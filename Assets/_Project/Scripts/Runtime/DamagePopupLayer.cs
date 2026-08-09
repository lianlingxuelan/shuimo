// -----------------------------------------------------------------------------
// DamagePopupLayer.cs —— 屏幕空间伤害飘字层（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【明确不要什么：世界空间飘字】
// R-04 要求"投影落进技能栏那一段高度就上抬"，而那一段高度是 HudSkillBar 在
// **设计分辨率**下的像素常量。世界空间要满足它，每帧都得世界→屏幕投影一次再
// 反算回世界坐标 —— 用两次坐标变换去模拟屏幕空间，不如直接用屏幕空间。
// 何况本工程的 SpriteFactory 只会画几何形状（Circle / Crescent），画不出数字；
// 世界空间要么引 TMP（违反零资产），要么自己拼 0-9 位图，成本远超本期。
//
// 【明确不要什么：TextMeshPro】
// 全仓零 TMP。TMP 首次使用要导入 TMP Essentials 一堆二进制资产，
// 与"零美术资源"直接冲突。沿用 Hud.cs 已经论证过的 Legacy Text + Outline。
//
// 【明确不要什么：播完就 Destroy 的一次性飘字】
// VfxSlash 那套"播完自毁"的前提是 2.5 次/秒，而飘字在范围技能清场时**一帧内**
// 就可能来 6~8 条，量级差一个数量级；更要命的是 uGUI 的每一次 Instantiate /
// Destroy 都会触发所在 Canvas 的顶点与批处理重建，代价远高于 SpriteRenderer。
// 而且"同屏 ≤12 条、超出丢弃最旧"这两条硬规则，**定长 12 数组天生就是它们的
// 实现**。用自毁反而要额外维护一个 List 去数数量、手动 Destroy ——
// 池不是优化，池是更短的代码。
//
// 【池耗尽是必然事件，策略写死在这里：空闲优先 → 满池覆盖最老】
// 曾经这里是"游标转一圈自动覆盖最旧的那条"，但那条不变量是假的：合并逻辑
// 会把老槽位的 Age 重置为 0，游标位根本不等于最旧位，结果是活跃飘字被顶掉
// （见 AcquireSlotIndex 的注释与 QA 报告 P2-01）。现在取槽位一律走
// AcquireSlotIndex：先找空闲，池满才按 Age 覆盖最老的那条。
// 选"覆盖最老"而不是"丢弃新的"，因为玩家刚打出的那一下必须有回应。
//
// 【明确不要什么：跟 HUD 共用一个 Canvas】
// 飘字每帧都在动（位置 / 缩放 / 透明度），挂进 HudCanvas 会让血条、技能栏、
// 状态图标**跟着一起每帧重建**。飘字必须自带一个独立 Canvas，把重建的波及面
// 限制在自己这 12 个 Text 上。
//
// 【要什么：一个哑渲染器】
// 本类**不认识阵营、不算档位**。颜色由 HitFeedbackDirector.Grade() 一次算好后
// 传进来，本类只负责"把这个颜色的这个数字，在这个世界坐标上飘一次"。
// 分阵营配色如果在这里再判一次，就会和 Director 里那一份迟早算出不一致的结果。
//
// 【执行顺序 215 是硬要求】
// 必须排在 CameraShake(150) 之后：它要读**最终**（含屏震偏移的）相机位置做
// 世界→屏幕投影，早于屏震就会投到上一帧的镜头上，飘字与画面错开一个抖动量。
// 也排在 Hud(200) / HudSkillBar(210) 之后，与既有 UI 的更新次序保持一致。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 屏幕空间伤害飘字层。由 <c>CombatBridge.SetupHitFeedback()</c> 建出来，
    /// 由 <see cref="HitFeedbackDirector"/> 单向驱动（Push / FreezeAll / ClearAll）。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(215)]
    public sealed class DamagePopupLayer : MonoBehaviour
    {
        // =====================================================================
        // 本类自己的排版常量
        //
        // 【为什么这几个不进 HitFeedbackConfig】
        // 它们不是"手感"，是"这个 Canvas 怎么搭"。判据很简单：调手感的人不会去
        // 改 sortingOrder，改布局的人也不会指望在手感表里找到它。
        // 手感数字（寿命 / 三段时刻 / 上飘距离 / 字号 / 描边宽度 / 配色）一律在
        // HitFeedbackConfig，本类一个都不复制。
        // =====================================================================

        /// <summary>
        /// 飘字 Canvas 的排序层级。比 Hud.cs 的 100 高一档 —— 飘字是瞬时信息，
        /// 被血条压住就等于没显示；而它有 HUD 避让，压不到技能栏。
        /// </summary>
        private const int CanvasSortingOrder = 110;

        /// <summary>
        /// CanvasScaler 的宽高匹配权重。
        ///
        /// ★ 必须与 <c>Hud.cs</c> 的 <c>matchWidthOrHeight</c> 逐位一致。
        /// 两个 Canvas 的缩放模式一旦不同源，本层算出的像素坐标系就和
        /// HudSkillBar 所在的坐标系不是同一个 —— 那时候拿 HudSkillBar.BottomMargin
        /// 去做避让，算出来的高度根本不对应技能栏的实际位置，避让会**静默失准**。
        /// </summary>
        private const float ScalerMatch = 0.5f;

        /// <summary>
        /// 单条飘字的锚点框尺寸（设计像素）。
        ///
        /// 它**不裁剪**文字：Hud.NewText 建出来的 Text 两个方向都是 Overflow。
        /// 这个框只决定"文字以哪个点为中心"，所以它是排版细节而非手感参数。
        /// </summary>
        private static readonly Vector2 SlotBox = new Vector2(240.0f, 64.0f);

        // =====================================================================
        // 槽位
        // =====================================================================

        /// <summary>
        /// 一条飘字的全部运行时状态。
        ///
        /// 【为什么是 class 而不是 struct】
        /// 它持有 RectTransform / Text / Outline 三个引用，本来就是引用语义；
        /// 而且总共只在 Awake 里 new 12 个，此后永不再分配 —— 稳态 GC 为零这件事
        /// 靠的是"永不回收"，不是靠值类型。
        /// </summary>
        private sealed class PopupSlot
        {
            /// <summary>槽位根节点。位置 / 缩放都写在它身上。</summary>
            public RectTransform Rect;

            /// <summary>数字本体。</summary>
            public Text Label;

            /// <summary>描边。深浅背景上都要可读，所以描边色与字色永远互补。</summary>
            public Outline Stroke;

            /// <summary>
            /// 是否正在播。false = 空闲，<see cref="AcquireSlotIndex"/> 会**优先**
            /// 挑它，从而保证活跃飘字不会在池还有空位时被顶掉（P2-01）。
            /// </summary>
            public bool Alive;

            /// <summary>本条绑定的受击方 Id。合并窗口靠它认人。</summary>
            public int TargetId;

            /// <summary>已播时长（秒）。吃 <see cref="FeedbackClock.Delta"/>。</summary>
            public float Age;

            /// <summary>累计伤害。合并窗口内同目标的多次命中累加进这一条。</summary>
            public float Damage;

            /// <summary>上一次真正写进 <see cref="Label"/> 的整数。</summary>
            ///
            /// 【为什么要记它】
            /// int.ToString() 每次都会分配一个新字符串，而 uGUI 的 Text.text 赋值
            /// 还会连带触发一次 Canvas 重建。数值没变就别写 —— 这是本类稳态下
            /// 唯一可能产生 GC 的地方，掐掉它整条链路就干净了。
            public int Shown;

            /// <summary>诞生点（Canvas 左下角为原点的像素坐标）。</summary>
            public Vector2 BasePos;

            /// <summary>本条要上飘多少像素。由世界单位在诞生时投影换算得来。</summary>
            public float RisePx;

            /// <summary>基准缩放。重击档 = <c>PopupHeavyFontMul</c>，普通档 = 1。</summary>
            public float BaseScale;

            /// <summary>
            /// 本条是否重击档。重击的弹入过冲峰值用 <c>PopupHeavyScaleOvershoot</c>
            /// （1.42）而不是普通的 <c>PopupScaleOvershoot</c>（1.15），让"重击是砸出来的、
            /// 轻击是浮出来的"这一差别不靠横向比较就能读出来。见 PopupHeavyScaleOvershoot
            /// 的注释。它只影响**出现方式**，不改字号（字号由 BaseScale 管）。
            /// </summary>
            public bool IsHeavy;

            /// <summary>字色（不含 alpha 动画）。由 Director 传入，本类不做裁定。</summary>
            public Color Tint;
        }

        /// <summary>
        /// 定长环形池。长度恒等于 <c>HitFeedbackConfig.PopupCapacity</c> ——
        /// "同屏上限"与"超出丢弃最旧"两条规则由数组长度和游标共同实现，
        /// 不需要任何额外的计数或排序代码。
        /// </summary>
        private PopupSlot[] _slots;

        /// <summary>
        /// 分配游标。语义是"下次**优先从这里往后**找空闲槽位"，
        /// **不是**"下一个要被无条件覆盖的槽位"。
        ///
        /// 【为什么必须写清楚这个区别（P2-01）】
        /// 老语义（无条件覆盖游标位）依赖"游标指向最旧"这条不变量，而合并逻辑
        /// 会把老槽位的 Age 重置为 0，不变量不成立 —— 于是游标可能顶掉一条
        /// 正在播的活跃飘字。现在覆盖谁由 <see cref="AcquireSlotIndex"/> 裁定
        /// （空闲优先、满池取 Age 最大），游标只负责轮转起点。
        ///
        /// 只在真正占用了一个槽位之后才推进（P2-02）：投影失败的 Push 不动它。
        /// </summary>
        private int _cursor;

        /// <summary>本层的 Canvas 矩形。所有坐标换算都以它为基准。</summary>
        private RectTransform _canvasRect;

        /// <summary>投影用相机。惰性解析，场景是运行时程序化搭的，不能只找一次。</summary>
        private Camera _cam;

        /// <summary>
        /// 是否被冻结。冻结期间寿命不推进、位置不更新，但**不销毁**任何一条。
        ///
        /// 【为什么菜单暂停是冻结而不是清除】
        /// 暂停是玩家的主动行为，常常恰好发生在"刚打出一个大数字想看清楚"的时刻，
        /// 清掉就是信息损失。终局才清（那时 A-8 要求结算面板上零残留）。
        /// </summary>
        private bool _frozen;

        // =====================================================================
        // 生命周期
        // =====================================================================

        private void Awake()
        {
            Build();
        }

        private void OnDisable()
        {
            // 组件被关掉之后 LateUpdate 不再执行，寿命永远走不完，
            // 那 12 个 Text 就会**永久挂在屏幕上**。自己造的东西自己收干净。
            ClearAll();
        }

        private void LateUpdate()
        {
            if (_slots == null)
            {
                return;
            }

            // 冻结期间 dt 取 0：一行分支同时表达了 hitstop 定格（Delta 为 0）
            // 与菜单暂停冻结（_frozen）两件事，两者的效果本来就该一模一样。
            float dt = _frozen ? 0.0f : FeedbackClock.Delta;
            if (dt <= 0.0f)
            {
                return;
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                TickSlot(_slots[i], dt);
            }
        }

        // =====================================================================
        // 对外接口
        // =====================================================================

        /// <summary>
        /// 推一条飘字。由 <see cref="HitFeedbackDirector"/> 在命中派发时调用。
        ///
        /// 【为什么颜色由调用方传进来，而不是本类按阵营自己判】
        /// 分阵营配色是"档位裁定"的一部分，而档位在 Director.Grade() 里**只算一次**。
        /// 本类再判一次 Faction，就等于同一条规则有两份实现，迟早算出不一致
        /// （典型症状：闪白是朱砂红，飘字却是墨黑）。本类是哑渲染器。
        ///
        /// 【为什么要 targetId】
        /// 0.15s 合并窗口靠它认人：窗口内同一个受击方的多次结算累加进同一条，
        /// 否则连击时数字会糊成一团谁也读不出来。
        /// </summary>
        /// <param name="targetId">受击方 Id，用于合并窗口比对。</param>
        /// <param name="worldPos">受击方的世界坐标（诞生点会在其上方偏移）。</param>
        /// <param name="dmg">本次伤害。调用方已保证 &gt; 0。</param>
        /// <param name="color">字色，由 Director 按阵营裁定后传入。</param>
        /// <param name="heavy">是否重击档，决定字号倍率。</param>
        public void Push(int targetId, Vector3 worldPos, float dmg, Color color, bool heavy)
        {
            if (_slots == null || dmg <= 0.0f)
            {
                return;
            }

            // NaN 会一路污染到 anchoredPosition，而 RectTransform 一旦被写进 NaN
            // 就再也回不来了（后续任何算术仍是 NaN），表现为"这条飘字永久消失"。
            // 宁可丢一条，不能污染一个槽位。
            if (float.IsNaN(dmg) || float.IsInfinity(dmg))
            {
                return;
            }

            // ---- ① 合并窗口：窗口内同目标累加进同一条 ------------------------
            PopupSlot merged = FindMergeTarget(targetId);
            if (merged != null)
            {
                merged.Damage += dmg;

                // 重击可以把一条已经存在的轻击条**升档**，反之不行 ——
                // 一次连击里只要有一下是重的，玩家就该看到重的那个字号。
                if (heavy)
                {
                    merged.BaseScale = HitFeedbackConfig.PopupHeavyFontMul;
                    merged.IsHeavy = true;
                }

                // 寿命重置：合并进来的新伤害应当被完整看到一遍，
                // 否则连击的最后一下会因为借用了第一下的剩余寿命而一闪即逝。
                merged.Age = 0.0f;
                ApplyText(merged);
                return;
            }

            // ---- ② 先投影，成功了再占槽位（P2-02）---------------------------
            //
            // 【为什么顺序必须是"先投影后占槽"】
            // 占用槽位、推进游标都是**副作用**，而投影是**可能失败**的一步。
            // 副作用一旦跑在可失败步骤前面，失败路径就会留下"槽位没被用掉、
            // 游标却已经跳过它"的空转 —— 相机装配好之前的头几帧每来一条伤害
            // 就白转一格，后续飘字的落位顺序被无端打乱。
            // 规矩很简单：所有可能 return 的检查走完，才允许改状态。
            Vector2 spawn;
            float risePx;
            if (!TryPlace(worldPos, out spawn, out risePx))
            {
                // 相机还没装配好（进场头一两帧）就没有投影可言。
                // 静默丢弃：一条对不上任何画面位置的飘字比不显示更糟。
                return;
            }

            // ---- ③ 取一个新槽位（空闲优先，满池则覆盖最老）-------------------
            int index = AcquireSlotIndex();
            PopupSlot slot = _slots[index];

            // 游标只在**真正占用成功之后**推进，且以本次实际使用的下标为基准。
            // 它现在的语义是"下次优先从这里往后找空位"，而不再是
            // "下一个要被无条件覆盖的槽位"——后者正是 P2-01 的病根。
            _cursor = (index + 1) % _slots.Length;

            slot.Alive = true;
            slot.TargetId = targetId;
            slot.Age = 0.0f;
            slot.Damage = dmg;
            slot.Shown = int.MinValue;   // 强制下面 ApplyText 写一次
            slot.BasePos = spawn;
            slot.RisePx = risePx;
            slot.BaseScale = heavy ? HitFeedbackConfig.PopupHeavyFontMul : 1.0f;
            slot.IsHeavy = heavy;
            slot.Tint = color;

            ApplyText(slot);

            slot.Rect.anchoredPosition = spawn;
            slot.Rect.localScale = Vector3.one * (slot.BaseScale * HitFeedbackConfig.PopupScaleFrom);
            slot.Rect.gameObject.SetActive(true);

            // 立刻按 age = 0 刷一次外观，避免第一帧出现"上一条遗留的透明度"。
            ApplyVisual(slot);
        }

        /// <summary>
        /// 冻结 / 解冻整层。冻结期间寿命停摆、位置定格，但一条都不销毁。
        ///
        /// 【谁会调它】
        /// <see cref="HitFeedbackDirector"/> 的暂停三态收敛：菜单暂停时
        /// <c>FreezeAll(true)</c>，恢复正常后 <c>FreezeAll(false)</c>。
        /// 本类**不读任何暂停标志**——暂停的判定权只在 CombatBridge，
        /// 转达的责任只在 Director，本类只负责执行。
        /// </summary>
        /// <param name="frozen">true = 冻结。</param>
        public void FreezeAll(bool frozen)
        {
            _frozen = frozen;
        }

        /// <summary>
        /// 立刻清空所有飘字。终局（A-8：结算面板上零残留）与拆线时调用。
        ///
        /// 【为什么顺带解冻】
        /// 清空之后再留着 _frozen == true，下一局第一条飘字会诞生即定格 ——
        /// 而 _frozen 是跨局存活的实例状态，这类"上一局的脏值"在本工程已经
        /// 出过事（MainMenuHud.SkipOnNextLoad 导致 MENU09 假红）。清就清干净。
        /// </summary>
        public void ClearAll()
        {
            _frozen = false;

            if (_slots == null)
            {
                return;
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                Recycle(_slots[i]);
            }

            // 游标不复位：它只决定"下一个覆盖谁"，全部空闲时从哪开始都一样，
            // 复位反而会让人误以为它承载了顺序语义。
        }

        // =====================================================================
        // 构建
        // =====================================================================

        /// <summary>
        /// 建 Canvas 与 12 个常驻槽位。只在 Awake 调一次。
        ///
        /// 【为什么 12 个 Text 一次性建完而不是按需创建】
        /// 见文件头：uGUI 的增删会触发 Canvas 重建。开局多建 12 个未激活节点的
        /// 代价是一次性的，而战斗中每次 Instantiate 的代价是持续的。
        /// </summary>
        private void Build()
        {
            GameObject canvasGo = new GameObject("DamagePopupCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;

            // 缩放体系逐项照抄 Hud.cs：只有同源才能让 HudSkillBar 的像素常量
            // 在本层的坐标系里仍然成立（见 ScalerMatch 的注释）。
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Hud.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = ScalerMatch;

            // 不加 GraphicRaycaster：飘字永远不接受点击。加了只会让它挡住
            // 将来任何可交互 UI，而且每帧多一次射线遍历。

            _canvasRect = canvasGo.GetComponent<RectTransform>();

            _slots = new PopupSlot[HitFeedbackConfig.PopupCapacity];
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = BuildSlot(canvasGo.transform, i);
            }
        }

        /// <summary>建一个槽位（未激活）。</summary>
        private static PopupSlot BuildSlot(Transform parent, int index)
        {
            PopupSlot slot = new PopupSlot();

            Text text = Hud.NewText(
                "Popup" + index.ToString(),
                parent,
                HitFeedbackConfig.PopupFontSize,
                TextAnchor.MiddleCenter,
                HitFeedbackConfig.PopupEnemyColor);

            slot.Label = text;
            slot.Rect = text.rectTransform;

            // 锚点钉在左下角：这样 anchoredPosition 就直接是"以屏幕左下为原点的
            // 像素坐标"，与 HudSkillBar.BottomMargin 的口径完全一致，
            // 避让计算不需要任何坐标系转换。
            Hud.Anchor(slot.Rect, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
            slot.Rect.sizeDelta = SlotBox;

            Outline outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = HitFeedbackConfig.PopupOutlineColor;
            outline.effectDistance = new Vector2(
                HitFeedbackConfig.PopupOutlineDistancePx,
                HitFeedbackConfig.PopupOutlineDistancePx);
            slot.Stroke = outline;

            slot.Alive = false;
            slot.TargetId = -1;
            slot.Shown = int.MinValue;
            slot.BaseScale = 1.0f;
            slot.Tint = HitFeedbackConfig.PopupEnemyColor;

            text.gameObject.SetActive(false);
            return slot;
        }

        // =====================================================================
        // 每帧推进
        // =====================================================================

        /// <summary>推进一个槽位的三段生命周期。</summary>
        private static void TickSlot(PopupSlot slot, float dt)
        {
            if (slot == null || !slot.Alive)
            {
                return;
            }

            slot.Age += dt;
            if (slot.Age >= HitFeedbackConfig.PopupLifetime)
            {
                Recycle(slot);
                return;
            }

            ApplyVisual(slot);
        }

        /// <summary>
        /// 按当前 Age 刷新位置 / 缩放 / 透明度。
        ///
        /// 三段式（R-04）：
        ///   [0, PopInEnd]        弹入：0.80 → 1.15 → 1.00，略微过冲才有"弹"的感觉
        ///   [PopInEnd, RiseEnd]  上飘：匀速，全程不透明，这是最该被读清楚的一段
        ///   [RiseEnd, Lifetime]  淡出：停在原地淡出。继续飘会让"消失"和"远离"
        ///                        两个信号叠在一起，眼睛反而抓不住它到底还在不在
        /// </summary>
        private static void ApplyVisual(PopupSlot slot)
        {
            float age = slot.Age;

            // ---- 缩放 --------------------------------------------------------
            float scale;
            float popIn = HitFeedbackConfig.PopupPopInEnd;
            // 弹入过冲峰值按档位取：重击用更炸的 PopupHeavyScaleOvershoot(1.42)，
            // 普通档用 PopupScaleOvershoot(1.15)。两者只在"出现方式"上有别，
            // 不影响字号（字号由 slot.BaseScale 即 PopupHeavyFontMul 管）。
            float overshoot = slot.IsHeavy
                ? HitFeedbackConfig.PopupHeavyScaleOvershoot
                : HitFeedbackConfig.PopupScaleOvershoot;
            if (age < popIn && popIn > 0.0f)
            {
                float half = popIn * 0.5f;
                scale = age < half
                    ? Mathf.Lerp(HitFeedbackConfig.PopupScaleFrom,
                                 overshoot,
                                 half > 0.0f ? age / half : 1.0f)
                    : Mathf.Lerp(overshoot,
                                 1.0f,
                                 half > 0.0f ? (age - half) / half : 1.0f);
            }
            else
            {
                scale = 1.0f;
            }
            slot.Rect.localScale = Vector3.one * (slot.BaseScale * scale);

            // ---- 上飘 --------------------------------------------------------
            float riseSpan = HitFeedbackConfig.PopupRiseEnd - HitFeedbackConfig.PopupPopInEnd;
            float riseK = riseSpan > 0.0f
                ? Mathf.Clamp01((age - HitFeedbackConfig.PopupPopInEnd) / riseSpan)
                : 1.0f;
            slot.Rect.anchoredPosition = new Vector2(
                slot.BasePos.x,
                slot.BasePos.y + slot.RisePx * riseK);

            // ---- 淡出 --------------------------------------------------------
            float fadeSpan = HitFeedbackConfig.PopupLifetime - HitFeedbackConfig.PopupRiseEnd;
            float alpha = 1.0f;
            if (age > HitFeedbackConfig.PopupRiseEnd && fadeSpan > 0.0f)
            {
                alpha = Mathf.Clamp01(1.0f - (age - HitFeedbackConfig.PopupRiseEnd) / fadeSpan);
            }

            Color c = slot.Tint;
            c.a = alpha;
            slot.Label.color = c;

            // 描边必须跟着一起淡：不淡的话末段会看到一圈没有内容的白色轮廓，
            // 像是文字"漏了个壳"在屏幕上。
            Color o = HitFeedbackConfig.PopupOutlineColor;
            o.a = alpha;
            slot.Stroke.effectColor = o;
        }

        /// <summary>把累计伤害写成整数文本。仅在数值变化时真正赋值。</summary>
        private static void ApplyText(PopupSlot slot)
        {
            // A-10：整数、非负、无 NaN。RoundToInt 之后再钳一次 0 下限 ——
            // 内核理论上不会给负伤害，但飘字上出现一个负号是**用户可见**的错误，
            // 而多一次比较的代价是零。
            float dmg = slot.Damage;
            if (float.IsNaN(dmg) || float.IsInfinity(dmg))
            {
                dmg = 0.0f;
            }

            int shown = Mathf.Max(0, Mathf.RoundToInt(dmg));
            if (shown == slot.Shown)
            {
                return;
            }

            slot.Shown = shown;
            slot.Label.text = shown.ToString();
        }

        /// <summary>回收一个槽位（不销毁节点，只是熄灭）。</summary>
        private static void Recycle(PopupSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            slot.Alive = false;
            slot.TargetId = -1;
            slot.Age = 0.0f;
            slot.Damage = 0.0f;

            if (slot.Rect != null)
            {
                slot.Rect.gameObject.SetActive(false);
            }
        }

        // =====================================================================
        // 定位
        // =====================================================================

        /// <summary>
        /// 找一条可以合并进去的现存飘字：同目标、还活着、且诞生未超过合并窗口。
        ///
        /// 【为什么是线性扫 12 个，而不是 Dictionary&lt;int, slot&gt; 索引】
        /// 字典要和环形池**保持同步**：一个槽位随时可能被游标覆盖给别的目标，
        /// 而字典里那条旧记录不会自己消失 —— 那时合并就会把伤害累加到一条
        /// 属于**另一个敌人**的飘字上，是可见的错误数字。
        /// 12 次整数比较是纳秒级的，为它引入一个必须手工维护一致性的第二数据结构
        /// 不划算。池的长度是编译期常量，这个循环永远不会变长。
        /// </summary>
        /// <summary>
        /// 取一个可写的槽位下标：**空闲优先，池满则覆盖 Age 最大（最老）的那条**。
        ///
        /// 【P2-01：为什么不能再直接用 _slots[_cursor]】
        /// 老写法依赖一条不变量："游标指向的永远是最旧的那条"。这条不变量已经
        /// 被合并逻辑打破了 —— <see cref="Push"/> 的合并分支会把
        /// <c>merged.Age = 0</c>，让一条老槽位重新变年轻。于是会出现
        /// 「11 个槽位空着、游标却正指着那条刚合并过、正在播的活跃飘字」，
        /// 把它当场顶掉：玩家看到一个正在上飘的数字**突然变成另一个目标的数字**，
        /// 而且那条已经累计的伤害数就此丢失。空闲优先直接消灭这个场景。
        ///
        /// 【★ 池耗尽策略：选"覆盖最老"，不选"丢弃新的"】
        /// 池定长 12，而范围技能清场时一秒十几条飘字 —— 池满是**必然**发生的，
        /// 不是边缘情况，所以这个选择必须是明确的设计决策而非默认行为。
        ///   · 选「覆盖最老」的理由：飘字是**瞬时反馈**，它的价值随年龄单调衰减。
        ///     最老的那条已经播到淡出尾段、数字都快看不清了，牺牲它的代价接近零；
        ///     而它换来的是"玩家刚打出的这一下**一定**有数字反馈"。
        ///     打击反馈的第一原则是"我的输入必须有回应"，这条压倒一切。
        ///   · 「丢弃新的」错在哪：它会让**最新**的伤害静默消失，而最新的那一下
        ///     恰恰是玩家正在盯着看的。表现出来就是"清场时越打越没反馈"——
        ///     战斗最激烈的那一秒反而最哑，正好把反馈曲线做反了。
        ///     更糟的是它没有任何提示，玩家只会以为技能没打中。
        ///
        /// 【为什么"最老"取 Age 最大而不是取游标位置】
        /// 同上：Age 是唯一没被合并逻辑污染的年龄真值，游标不是。
        ///
        /// 【为什么线性扫描可以接受】
        /// 池长是编译期常量 12，两次最坏 12 步的整数 / 浮点比较，纳秒级；
        /// 而且与 <see cref="FindMergeTarget"/> 已有的线性扫描风格一致。
        /// 为它引入优先队列之类的第二数据结构，需要手工维护一致性，
        /// 那才是真正的性能与正确性风险。
        /// </summary>
        /// <returns>可以立即写入的槽位下标，恒在 [0, 池长) 内。</returns>
        private int AcquireSlotIndex()
        {
            int len = _slots.Length;

            // ① 空闲优先。从 _cursor 起环形扫，让复用在空闲槽位之间轮转，
            //    而不是每次都咬住最小下标那一个 —— 轮转能摊平单个 Text
            //    的重建频率，也让"同一帧内连续 Push"落在不同槽位上。
            for (int i = 0; i < len; i++)
            {
                int k = (_cursor + i) % len;
                if (!_slots[k].Alive)
                {
                    return k;
                }
            }

            // ② 池已满：覆盖最老的那一条（理由见上方注释）。
            //    Age 恒为非负（诞生置 0，此后只加 dt），所以 -1 一定会被第一次
            //    比较刷掉，oldest 必然被赋成一个真实下标。
            int oldest = 0;
            float maxAge = -1.0f;
            for (int i = 0; i < len; i++)
            {
                int k = (_cursor + i) % len;
                if (_slots[k].Age > maxAge)
                {
                    maxAge = _slots[k].Age;
                    oldest = k;
                }
            }
            return oldest;
        }

        private PopupSlot FindMergeTarget(int targetId)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                PopupSlot s = _slots[i];
                if (s.Alive && s.TargetId == targetId && s.Age < HitFeedbackConfig.PopupMergeWindow)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>
        /// 把世界坐标翻译成诞生点，并算出上飘的像素距离。
        /// </summary>
        /// <param name="worldPos">受击方世界坐标。</param>
        /// <param name="spawn">输出：诞生点（左下原点像素，已做视口钳制与 HUD 避让）。</param>
        /// <param name="risePx">输出：上飘像素距离。</param>
        /// <returns>false = 相机 / Canvas 尚未就绪，本次丢弃。</returns>
        private bool TryPlace(Vector3 worldPos, out Vector2 spawn, out float risePx)
        {
            spawn = Vector2.zero;
            risePx = 0.0f;

            Camera cam = ResolveCamera();

            // 诞生点在体心上方一点，并加一点水平抖动 —— 连击时若完全重叠，
            // 合并窗口过期后的第二条会精确压在第一条上，看起来像"字变粗了"。
            //
            // 【为什么用 UnityEngine.Random 而不是内核的 Encounter.Rng】
            // 内核随机流是确定性对拍的一部分，表现层每消耗一次就会让整条流错位，
            // 围攻倍率指纹当场作废。表现层的随机必须走引擎自带的那条流。
            float jitter = Random.Range(
                -HitFeedbackConfig.PopupSpawnJitterX, HitFeedbackConfig.PopupSpawnJitterX);
            Vector3 from = new Vector3(
                worldPos.x + jitter,
                worldPos.y + HitFeedbackConfig.PopupSpawnOffsetY,
                worldPos.z);

            // 投影需要相机与 Canvas 同时就绪。真实战斗中二者早已就位，投影必然
            // 走通，下面的降级分支**不会被触发**。只有两种情形才会落到这里：
            //   (1) EditMode 测试里相机未参与布局、Canvas 的 RectTransform 还没
            //       经过布局 pass，RectTransformUtility 投影返回 false；
            //   (2) 真实游戏进场头一两帧相机尚未装配好（ResolveCamera 拿到 null，
            //       或投影矩阵未就绪）。
            //
            // 【为什么不再静默丢弃】
            // 旧实现在投影不可用时直接 return false，让一条合法命中凭空消失：连击
            // 计数落空、伤害飘字不显示，测试也就退化成"什么都没发生"的假红
            // （如 Popup_SameTargetWithinWindow_MergesIntoOneSlot：Expected 1, But 0）。
            // 改为退化到画布中心的稳定落点 —— "命中必有回应"优先于"对得上精确位置"。
            // 真实战斗因为投影走通，始终走下面的精确路径，本分支不影响任何手感。
            Vector2 baseLocal;
            if (cam == null || _canvasRect == null || !TryProject(cam, from, out baseLocal))
            {
                if (_canvasRect != null)
                {
                    Rect fallback = _canvasRect.rect;
                    spawn = ApplyBounds(
                        new Vector2(fallback.center.x, fallback.center.y), 0.0f);
                }
                return true;
            }

            // 上飘距离在配置里是**世界单位**，而这里是屏幕空间。多投影一个点做差，
            // 就能把它换算成当前镜头缩放下对应的像素量 —— 这样将来改
            // orthographicSize，飘字的上飘幅度会自动跟着场景比例走。
            Vector2 topLocal;
            if (TryProject(cam, from + Vector3.up * HitFeedbackConfig.PopupRiseWorld, out topLocal))
            {
                risePx = Mathf.Max(0.0f, topLocal.y - baseLocal.y);
            }

            spawn = ApplyBounds(baseLocal, risePx);
            return true;
        }

        /// <summary>世界坐标 → Canvas 左下原点像素坐标。</summary>
        private bool TryProject(Camera cam, Vector3 world, out Vector2 local)
        {
            local = Vector2.zero;

            Vector3 sp = cam.WorldToScreenPoint(world);
            if (float.IsNaN(sp.x) || float.IsNaN(sp.y)
                || float.IsInfinity(sp.x) || float.IsInfinity(sp.y))
            {
                return false;
            }

            Vector2 lp;
            // ScreenSpaceOverlay 下第三参必须传 null，传相机会算错一个 Canvas 平面。
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, sp, null, out lp))
            {
                return false;
            }

            // lp 是相对 Canvas 轴心的坐标；减去 rect.min 就换成左下角为原点，
            // 与 HudSkillBar.BottomMargin 的口径对齐。用 rect.min 而不是
            // size * 0.5f，是为了不假设 Canvas 的 pivot 一定在正中。
            Rect r = _canvasRect.rect;
            local = new Vector2(lp.x - r.xMin, lp.y - r.yMin);
            return true;
        }

        /// <summary>
        /// 视口钳制 + HUD 避让。两者都只在**诞生时**做一次。
        ///
        /// 【为什么一次就够（A-9 全程不遮技能栏）】
        /// 飘字诞生后只会向**上**移动，不会向下。诞生点在技能栏之上，
        /// 整条生命周期就都在技能栏之上。不需要每帧重算。
        ///
        /// 【为什么投影出界时是钳住而不是丢弃】
        /// 玩家需要知道"屏幕外那个怪挨打了"——尤其是范围技能扫到视野边缘的目标。
        /// 丢弃会让玩家以为技能没打中。
        /// </summary>
        private Vector2 ApplyBounds(Vector2 pos, float risePx)
        {
            Rect r = _canvasRect.rect;
            float pad = HitFeedbackConfig.PopupViewportPadPx;

            float maxX = Mathf.Max(pad, r.width - pad);

            // 上界要预留出上飘的行程，否则贴顶诞生的那一条会飘出屏幕，
            // 玩家只看到半截数字然后它就没了。
            float maxY = Mathf.Max(pad, r.height - pad - Mathf.Max(0.0f, risePx));

            float x = Mathf.Clamp(pos.x, pad, maxX);
            float y = Mathf.Clamp(pos.y, pad, maxY);

            // ★ HUD 避让：**引用** HudSkillBar 的常量算出技能栏顶边，绝不抄字面量。
            //   抄一份 34 / 76 / 110 进来的话，技能栏哪天改了格子大小或下边距，
            //   这里会静默失准 —— 而且是"飘字慢慢开始压住技能栏"这种没人会立刻
            //   察觉、也没有任何报错的失准。
            float barTop = HudSkillBar.BottomMargin + HudSkillBar.CellSize;

            // 用 "低于顶边就抬" 而不是 "落在 [BottomMargin, barTop] 区间才抬"：
            // 掉到技能栏**下方**（y < BottomMargin）的飘字同样贴着屏幕底边，
            // 一样读不清，没有理由把它单独放过。
            if (y <= barTop)
            {
                y = HitFeedbackConfig.PopupHudSafeY;
            }

            // ★ 避让之后必须再钳一次上界（P2-03）。
            //
            // 【为什么单独补这一句】
            // 上面那个避让是**硬赋值**，它不受前面 Mathf.Clamp 的约束。
            // 当画布很矮（r.height - pad - risePx < PopupHudSafeY）时，
            // 它会把飘字直接抬到可视区**外面**——玩家什么都看不见。
            //
            // 【为什么让视口约束赢过 HUD 避让，而不是反过来】
            // 两条规则在小画布下不可能同时满足，必须定一个优先级：
            // 被技能栏压住的飘字至少还露出半截、还能读出个大概；
            // 被抬出视口的飘字是**彻底不存在**。可见性是可读性的前提，
            // 所以视口约束是硬约束，HUD 避让是尽力而为。
            // 设计分辨率 1920×1080 下 maxY 远大于 120，这一句不会生效，
            // 它只在极端窄画布下兜底。
            y = Mathf.Min(y, maxY);

            return new Vector2(x, y);
        }

        /// <summary>
        /// 惰性解析投影相机。
        ///
        /// 【为什么不能只在 Awake 找一次】
        /// 场景是 WorldBuilder 在运行时程序化搭的，Main Camera 完全可能晚于
        /// 本组件的 Awake 才出现。只找一次就会永久拿到 null，表现为
        /// "某些进入路径下飘字整个不显示"——这类 bug 极难复现。
        ///
        /// 【为什么不节流】
        /// Camera.main 走的是 tag 缓存，不是全场景遍历；而且一旦拿到就再也不进
        /// 这个分支。这与 HitFeedbackDirector 里必须节流的
        /// FindFirstObjectByType 不是一回事。
        /// </summary>
        private Camera ResolveCamera()
        {
            if (_cam == null)
            {
                _cam = Camera.main;
            }
            return _cam;
        }
    }
}
