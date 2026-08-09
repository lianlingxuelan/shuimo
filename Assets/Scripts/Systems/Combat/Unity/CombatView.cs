// -----------------------------------------------------------------------------
// Unity/CombatView.cs —— Combatant ↔ GameObject 的表现层镜像
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【为什么位置是"内核 → 视图"单向的】
// 内核以 1/60 固定步推进，Unity 以真实帧率渲染。如果让 Transform 也参与
// 决定内核位置，两个时钟就会互相拉扯，出现"怪在原地抽搐"。这里定死单向：
//     敌人：内核算 → 写进 Transform（本文件）
//     玩家：Unity 算 → 读进内核（CombatController.SyncPlayerIntoKernel）
// 各自只有一个权威来源，永远不会打架。
//
// 【插值】
// 60 步/秒在 144Hz 屏上会看到轻微阶梯。这里提供可选的位置插值：把上一逻辑步
// 与当前逻辑步的位置按渲染时间做 Lerp。注意插值**只改渲染**，不回写内核。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Combat.UnityBridge
{
    /// <summary>
    /// 一个内核实体的表现层代表。挂在敌人 / BOSS 预制体根节点上。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatView : MonoBehaviour
    {
        [Header("表现")]
        [Tooltip("开启后在两个逻辑步之间插值渲染位置，消除 60Hz 阶梯感。")]
        [SerializeField] private bool interpolate = true;

        [Tooltip("血条 / 韧性条等 UI 根节点，可留空。")]
        [SerializeField] private Transform uiRoot;

        [Tooltip("受击闪白用的 SpriteRenderer，可留空。")]
        [SerializeField] private SpriteRenderer bodyRenderer;

        [Tooltip("蓄力预警的挂点（STRIKE windup 期间显形），可留空。")]
        [SerializeField] private GameObject windupTell;

        [Tooltip("状态特效（中毒 / 灼烧染色）的挂点，可留空，留空时按需自建。")]
        [SerializeField] private Transform statusAnchor;

        /// <summary>绑定的内核实体。未绑定时为 null。</summary>
        public Combatant Model { get; private set; }

        /// <summary>
        /// 状态特效挂点。首次访问时按需创建一个位于体心的空子节点。
        ///
        /// 【为什么要独立挂点而不是直接挂在根节点下】
        /// 根节点会被翻转 / 缩放（KeepUiUpright 里那段 lossyScale 补偿就是证据），
        /// 状态染色跟着翻会出现"中毒光晕镜像抖动"。单独一个不参与翻转的挂点，
        /// 表现层无论怎么折腾本体都不会波及它。
        /// </summary>
        public Transform StatusAnchor
        {
            get
            {
                if (statusAnchor == null)
                {
                    GameObject go = new GameObject("StatusAnchor");
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    statusAnchor = go.transform;
                }
                return statusAnchor;
            }
        }

        // 插值用的前后两个逻辑步位置。
        private Vector3 _prevLogicPos;
        private Vector3 _currLogicPos;
        private float _stepAge;

        private Color _baseColor = Color.white;
        private float _flashTimer;

        /// <summary>
        /// 无参 <see cref="PlayHitFlash()"/> 的闪白时长（秒）。
        ///
        /// 【为什么它是 public，而其余手感数字都在 HitFeedbackConfig】
        /// 它定义的是无参重载的**行为本身**，是本类对外契约的一部分。
        /// 而 HitFeedbackConfig 住在 Xianxia.Unity.T2，本程序集
        /// （Xianxia.Combat.Unity）**引用不到它** —— 依赖方向是单向的
        /// T2 ──► Combat.Unity ──► Combat，反向引用会让 asmdef 成环。
        /// 所以这个数字只能留在这里，由 T2 侧**反过来读它**（见
        /// HitFeedbackDirector.Grade() 里敌人轻击档的取值）。
        /// 这是 HitFeedbackConfig 类头注释里唯一承认的例外，不要再抄第二份。
        /// </summary>
        public const float DefaultFlashDuration = 0.08f;

        // --- 闪白的当前参数（每次 PlayHitFlash 覆盖一遍）---------------------
        //
        // 【为什么要把这三项存成字段，而不是每帧回头去问 Director】
        // 闪白衰减发生在本类的 LateUpdate，而 Director 住在上层程序集，
        // 本类既够不到它、也不该认识它。命中那一刻把"长什么样"一次性交接过来，
        // 之后本类就完全自洽 —— 这也是 fallback 路径（Director 尚未接线时）
        // 仍能正常闪白的原因。

        /// <summary>本次闪白混合到的目标色。敌人纯白 / 玩家朱砂红。</summary>
        private Color _flashColor = Color.white;

        /// <summary>本次闪白的总时长（秒）。作为衰减插值的分母，恒 &gt; 0。</summary>
        private float _flashDuration = DefaultFlashDuration;

        /// <summary>
        /// 本次闪白的峰值混合强度 [0,1]。1 = 完全变成 <see cref="_flashColor"/>。
        /// 玩家侧取 0.80，只混到八成，保留角色本身的墨色底子 —— 混到 1.0
        /// 会变成一块纯色剪影，那就"塑料"了。
        /// </summary>
        private float _flashPeak = 1.0f;

        // --- hitstop（表现层顿帧）相关状态 -----------------------------------
        //
        // 【为什么这几个字段属于"渲染层"而不是"状态机层"】
        // 上面的 _prevLogicPos / _currLogicPos / _stepAge 是插值状态机，它们
        // 永远吃真实 Time.deltaTime，跟着内核一步不落。下面这几个只影响
        // "最终写进 transform.position 的那个值"，是顿帧唯一被允许干预的地方。

        /// <summary>是否正处在"被钉住"状态。用于识别顿帧的**第一帧**，只在那一帧取锚点。</summary>
        private bool _stopHeld;

        /// <summary>顿帧开始瞬间的渲染位置。顿帧期间每帧原样写回它。</summary>
        private Vector3 _stopAnchor;

        /// <summary>
        /// 解冻瞬间"欠"的位移 = 锚点 - 状态机算出的真实目标位置。
        /// 顿帧期间每帧刷新，所以解冻那一刻它自然就是最终欠账，不需要额外记账。
        /// </summary>
        private Vector3 _catchUpOffset;

        /// <summary>归位进度计时。初始值等于 <see cref="CatchUpDuration"/>，表示"没有欠账要还"。</summary>
        private float _catchUpAge = CatchUpDuration;

        /// <summary>
        /// 解冻后把欠账抹平所需的时长（秒）。
        ///
        /// 【为什么必须有这一段，不能解冻就直接跳回真实位置】
        /// 顿帧 0.05~0.12s ＝ 内核走了 3~7 步（FixedStep = 1/60）。内核一直在动，
        /// 而渲染被钉住了，解冻瞬间两者相差一大截。直接赋值就是**肉眼可见的瞬移**，
        /// 这正是验收项 B-4 明文禁止的现象。
        ///
        /// 【为什么是 0.08 而不是别的数】
        /// 略短于 CameraFollow.DefaultSmoothTime = 0.14f。归位必须比镜头跟随
        /// **先**完成，否则两个平滑会互相拖影，看起来像画面在"游"。
        ///
        /// 【为什么放在这里而不是 HitFeedbackConfig】
        /// 它是插值状态机的内部实现细节，不是策划要调的手感参数；
        /// 而且 Xianxia.Combat.Unity 引用不到 Xianxia.Unity.T2（依赖方向单向）。
        /// </summary>
        private const float CatchUpDuration = 0.08f;

        private void Awake()
        {
            if (bodyRenderer == null)
            {
                bodyRenderer = GetComponentInChildren<SpriteRenderer>();
            }
            if (bodyRenderer != null)
            {
                _baseColor = bodyRenderer.color;
            }
            if (windupTell != null)
            {
                windupTell.SetActive(false);
            }
        }

        /// <summary>
        /// 绑定内核实体。会立刻把 Transform 对齐到内核位置，
        /// 避免生成后的第一帧从原点飞过去。
        /// </summary>
        public void Bind(Combatant model)
        {
            Model = model;
            if (model == null)
            {
                return;
            }
            Vector3 p = new Vector3(model.Position.X, model.Position.Y, transform.position.z);
            transform.position = p;
            _prevLogicPos = p;
            _currLogicPos = p;
            _stepAge = 0.0f;

            // 顿帧状态一并清干净。
            //
            // 【为什么绑定时必须复位而不能"反正下次顿帧会覆盖"】
            // 敌人视图会被复用（EnemySpawner 的模板 Instantiate + 池化路径）。
            // 上一只怪死在顿帧里，_stopHeld 就会留着 true、_catchUpOffset 留着一段
            // 属于**上一只怪**的位移欠账。新怪一绑定就会先被拖着走一小段，
            // 表现为"刚刷出来的敌人从旁边滑过来"。
            _stopHeld = false;
            _stopAnchor = p;
            _catchUpOffset = Vector3.zero;
            _catchUpAge = CatchUpDuration;   // == 已还清，归位分支不生效
        }

        /// <summary>
        /// 由 <see cref="CombatController"/> 在每个渲染帧调用：
        /// 拉取内核最新位置作为插值终点。
        ///
        /// 【★ 顿帧在这里只拦最后一步，绝不去动插值状态机】
        /// 被否掉的两个"更省事"的写法，以及它们错在哪：
        ///
        ///   ① 冻结 _stepAge（只停插值的 t）
        ///      _prevLogicPos / _currLogicPos 仍在滚动，t 卡在 0，解冻瞬间 t 从 0
        ///      跳到 1 → 瞬移。而且冻结期内核走的 3~7 个中间步会被反复覆盖丢失，
        ///      位置轨迹上出现跳点。
        ///
        ///   ② 本方法开头判断 Frozen 就直接 return
        ///      比 ① 更糟：_currLogicPos 在解冻后一口吞掉 7 步位移，与
        ///      _prevLogicPos 相距极远，插值出一段现实中根本不存在的直线滑行。
        ///
        /// 所以状态机（_prevLogicPos / _currLogicPos / _stepAge）**永远吃真实
        /// Time.deltaTime**，一刻不落后于内核；被冻住的只有"最终写进
        /// transform.position 的那个值"。这是"顿帧后不瞬移"（B-4）的根。
        /// </summary>
        public void SyncFromKernel()
        {
            if (Model == null)
            {
                return;
            }

            Vector3 next = new Vector3(Model.Position.X, Model.Position.Y, transform.position.z);

            // 位置变了 ⇒ 内核推进过至少一步，滚动插值区间。
            if ((next - _currLogicPos).sqrMagnitude > 1e-8f)
            {
                _prevLogicPos = _currLogicPos;
                _currLogicPos = next;
                _stepAge = 0.0f;
            }

            // ---- 状态机层：真实时间，与内核同步 ----------------------------
            Vector3 target;
            if (!interpolate)
            {
                target = _currLogicPos;
            }
            else
            {
                _stepAge += Time.deltaTime;   // ★ 这里绝不能换成 FeedbackClock.Delta
                float t = CombatScheduler.FixedStep > 0.0f ? _stepAge / CombatScheduler.FixedStep : 1.0f;
                target = Vector3.Lerp(_prevLogicPos, _currLogicPos, Mathf.Clamp01(t));
            }

            // ---- 渲染层：顿帧期间钉住，同时持续记账 ------------------------
            if (FeedbackClock.Frozen)
            {
                if (!_stopHeld)
                {
                    _stopHeld = true;
                    _stopAnchor = transform.position;
                }

                // 每帧刷新欠账。因为 target 一直在往前跑，解冻那一帧读到的
                // 就自然是最终欠账，不需要在解冻时点上额外做一次结算。
                _catchUpOffset = _stopAnchor - target;
                _catchUpAge = 0.0f;

                transform.position = _stopAnchor;
                return;
            }
            _stopHeld = false;

            // ---- 解冻：把欠账在 CatchUpDuration 内抹平，不瞬移（B-4）-------
            //
            // 用 SmoothStep 而不是线性：线性归位在起点和终点都有速度突变，
            // 看起来像"角色被拽了一把"；SmoothStep 两端速度为 0，衔接不出痕迹。
            if (_catchUpAge < CatchUpDuration && _catchUpOffset.sqrMagnitude > 1e-6f)
            {
                _catchUpAge += Time.deltaTime;   // ★ 归位属于状态机层，同样吃真实时间
                float k = 1.0f - Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp01(_catchUpAge / CatchUpDuration));
                transform.position = target + _catchUpOffset * k;
                return;
            }

            transform.position = target;
        }

        private void LateUpdate()
        {
            if (Model == null)
            {
                return;
            }
            UpdateTell();
            UpdateFlash();
            KeepUiUpright();
        }

        /// <summary>蓄力预警：只在 STRIKE 的 windup 相位显形，这是玩家的反应窗口。</summary>
        private void UpdateTell()
        {
            if (windupTell == null || Model.AI == null)
            {
                return;
            }
            bool winding = Model.AI.State == AIState.STRIKE
                           && Model.AI.StrikePhase == EnemyAI.STRIKE_PHASE_WINDUP;
            if (windupTell.activeSelf != winding)
            {
                windupTell.SetActive(winding);
            }
        }

        /// <summary>
        /// 受击闪白衰减。
        ///
        /// 【★ 这里吃 FeedbackClock.Delta，而上面的插值状态机吃 Time.deltaTime】
        /// 两者不是笔误，是分层：闪白是**纯渲染**，顿帧期间就该跟画面一起定格；
        /// 插值状态机是**跟内核对齐的记账**，落后一帧就会在解冻瞬间瞬移（B-4）。
        ///
        /// 定格的效果恰恰是想要的：命中瞬间 hitstop 与闪白同时开始，于是闪白会
        /// 稳稳停在**峰值**上，直到顿帧结束才开始衰减 —— "打中了"这个信号被
        /// 顿帧的整个时长托住，而不是在冻结的画面上自顾自地淡掉。
        ///
        /// 【为什么不怕 Frozen 卡死导致闪白永不消退】
        /// FeedbackClock 有三重复位保险（见其 Frozen 字段注释），
        /// 而且 Director 的倒计时用的是 Time.deltaTime，不会自锁。
        /// </summary>
        private void UpdateFlash()
        {
            if (bodyRenderer == null || _flashTimer <= 0.0f)
            {
                return;
            }

            _flashTimer -= FeedbackClock.Delta;
            if (_flashTimer <= 0.0f)
            {
                _flashTimer = 0.0f;
                bodyRenderer.color = _baseColor;
                return;
            }

            // 分母走字段而不是常量：三参重载可以传任意时长（重击 0.13 / 玩家 0.16
            // / 濒死 ×1.4），继续用常量当分母的话，k 会大于 1，
            // Color.Lerp 虽会自行钳制，但衰减曲线会被整段截断 ——
            // 表现为"长时长的闪白反而更早消失"，越重的攻击闪得越短。
            float k = _flashDuration > 0.0f ? _flashTimer / _flashDuration : 0.0f;
            bodyRenderer.color = Color.Lerp(
                _baseColor, _flashColor, _flashPeak * Mathf.Clamp01(k));
        }

        /// <summary>血条不跟随本体旋转/翻转，否则会跟着镜像成反的。</summary>
        private void KeepUiUpright()
        {
            if (uiRoot == null)
            {
                return;
            }
            uiRoot.rotation = Quaternion.identity;
            Vector3 s = transform.lossyScale;
            uiRoot.localScale = new Vector3(
                s.x != 0.0f ? 1.0f / Mathf.Sign(s.x) : 1.0f, 1.0f, 1.0f);
        }

        /// <summary>
        /// 播一次默认受击闪白（纯白、全强度、<see cref="DefaultFlashDuration"/>）。
        /// 由 <see cref="CombatEventsUnity"/> 在 OnHit 的 fallback 分支调用 ——
        /// 也就是 <c>HitFeedbackDirector</c> 还没接线时的兜底路径。
        ///
        /// 【★ 为什么保留这个无参重载，而不是给三参版加默认参数】
        /// C# 的默认参数是在**调用方**编译期内联的。本方法的调用方
        /// （CombatEventsUnity）与将来可能改这些默认值的人分处两个程序集，
        /// 只重编译被改的那一个，调用方会继续用**旧的**默认值，
        /// 而且不报任何错。这类"改了没生效"的坑排查成本极高。
        /// 转调一层是零成本的显式契约。
        /// </summary>
        public void PlayHitFlash()
        {
            PlayHitFlash(Color.white, DefaultFlashDuration, 1.0f);
        }

        /// <summary>
        /// 播一次参数化受击闪白。由 <c>HitFeedbackDirector</c> 按分档结果调用，
        /// 分阵营配色（R-03）与濒死加成（R-08）都已在 Director 侧算好。
        ///
        /// 【本类不认识阵营】
        /// 它只负责"把这个颜色按这个强度混上去，再用这个时长退回来"。
        /// 判阵营的活留在 Director 的 Grade() 一处，抄第二份必然算出不一致。
        /// </summary>
        /// <param name="tint">混合到的目标色。</param>
        /// <param name="duration">总时长（秒）。&lt;= 0 时回落到 <see cref="DefaultFlashDuration"/>。</param>
        /// <param name="peak">峰值混合强度，内部钳到 [0,1]。</param>
        public void PlayHitFlash(Color tint, float duration, float peak)
        {
            if (bodyRenderer == null)
            {
                return;
            }

            // duration 会成为 UpdateFlash 里的除数。上游若因为 FeedbackIntensity
            // 被改成 0、或者某个配置写成负数而传进 0，这里回落到默认值 ——
            // 直接 return 会留下一个"已经上了色但永远不衰减"的渲染器，
            // 那才是真正回不来的状态。
            if (duration <= 0.0f || float.IsNaN(duration))
            {
                duration = DefaultFlashDuration;
            }

            _flashColor = tint;
            _flashDuration = duration;
            _flashPeak = Mathf.Clamp01(peak);
            _flashTimer = duration;

            // 立刻上到峰值，不等下一帧的 UpdateFlash —— 差这一帧，
            // 快速连击时会看到"闪白比顿帧晚一拍"。
            bodyRenderer.color = Color.Lerp(_baseColor, _flashColor, _flashPeak);
        }

        /// <summary>当前血量比例，供外部血条读取。</summary>
        public float HpRatio()
        {
            return Model != null ? Model.HpRatio() : 0.0f;
        }

        /// <summary>当前韧性比例，供外部霸体条读取。</summary>
        public float PoiseRatio()
        {
            if (Model == null || Model.PoiseMax <= 0.0f)
            {
                return 0.0f;
            }
            return Mathf.Clamp01(Model.Poise / Model.PoiseMax);
        }
    }
}
