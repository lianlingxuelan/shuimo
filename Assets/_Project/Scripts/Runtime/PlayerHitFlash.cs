// -----------------------------------------------------------------------------
// PlayerHitFlash.cs —— 玩家受击朱砂闪白（R-03 玩家侧）（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// =============================================================================
// 【★★ 明确不要什么：绝对不要给玩家挂 CombatView ★★】
//
// 这是主理人在 P1-2 批次 3 的架构裁定，**已被否决的方案，不要再评估第二次**。
//
// 点亮玩家闪白最"省事"的写法，是给玩家对象也 AttachView 一个 CombatView，
// 这样 ViewOf(player.Id) 就不再是 null，DispatchFlash 一行都不用改。
// 这条路是**错的**，理由是硬的：
//
//   CombatView.SyncFromKernel() 每帧会写 transform.position（还带插值，以及
//   批次 1 新加的顿帧钉位 _stopAnchor / 解冻归位逻辑）。而玩家的位置由
//   PlayerController 自己驱动 —— 它读输入、算位移、写 transform。
//   两个组件同帧抢写同一个 transform.position，结果不是"闪白亮了"，
//   而是**玩家角色持续抽搐，或者被反复拽回内核位置**。
//
// 这个回归比"玩家挨打没有闪白"严重一个数量级：前者是操作手感彻底损坏，
// 后者只是少一个提示。所以正确解法是本文件 —— 一个**只染色、不碰 transform**
// 的轻组件，与 CombatView 的闪白部分同构，但不继承它任何位置同步的包袱。
//
// 【本文件的红线，改代码前先读】
//   · 本文件**永远不许出现** transform.position / transform.localPosition
//     的写入。玩家的位置只有 PlayerController 一个主人。
//   · 本文件不认识 Combatant、不认识 Faction、不认识内核。判阵营的活在
//     HitFeedbackDirector.Grade() 一处，本类只负责"把这个颜色按这个强度混上去"。
//   · 数值（朱砂色 / 时长 / 峰值）一律由调用方从 HitFeedbackConfig 取好后传入，
//     本文件不写任何魔法数字（唯一的兜底默认值也是读 HitFeedbackConfig）。
// =============================================================================
//
// 【核实结论 1：HeroineAnimator 不会覆写 _sr.color —— 已逐行读代码确认】
// HeroineAnimator.Apply()（:362-381）只写 `_sr.sprite` 与 `_sr.flipX`，
// 全文件**没有任何** `_sr.color = ...`。它在 :215 / :235 / :371 传的那三个
// Color.white，是 SpriteFactory.TryLoadPng / PreloadPngSequence 的 `tint` 形参 ——
// 那个 tint 在**建 Sprite 时烘进像素**并参与缓存 key（SpriteFactory.PngKey），
// 与 SpriteRenderer.color 是两回事。所以本组件的染色不会被动画机每帧冲掉。
//
// 【那为什么仍然选 LateUpdate，而不是 Update】
// 不是因为"必须"，是因为这层保险不要钱，且能同时买到两个好处：
//   ① 与 CombatView.UpdateFlash 同相位。两边的衰减都发生在 LateUpdate，
//      敌我闪白在同一帧内推进同样的量，不会出现"敌人闪完了玩家还剩一帧"。
//   ② 防未来回归。今天动画机不写 color，不代表明天不写（例如有人要加"中毒
//      变绿""隐身半透明"）。凡是写在 Update 里的染色，都排在 LateUpdate 之前，
//      本组件的结果永远压在最后一层 —— 届时闪白**自动**继续生效，
//      不需要有人再想起来回头改这里。
// 换句话说：Update 今天也能跑，LateUpdate 今天和明天都能跑。选后者。
//
// 【核实结论 2：SpriteRenderer 何时就绪】
// WorldBuilder.BuildPlayer 在 :541 就 AddComponent<SpriteRenderer>() 并立刻设了
// 蓝方块占位 sprite，**早于** :561 的 PlayerController 与 :590 的 HeroineAnimator。
// 所以本组件在 :561 附近挂载时，Awake 里必然拿得到 SpriteRenderer。
// 但仍然实现了惰性解析（EnsureRenderer）：装配顺序是别人家的实现细节，
// 本组件不该因为将来有人调整 BuildPlayer 的行序就空引用崩掉。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 玩家专用的受击染色器：把玩家 <see cref="SpriteRenderer"/> 的颜色按衰减曲线
    /// 混向朱砂红，再精确还原。**只碰 color，不碰 transform**。
    ///
    /// <para>挂载点：玩家 GameObject（与 <see cref="PlayerController"/> /
    /// <see cref="HeroineAnimator"/> 同体），由 <c>WorldBuilder.BuildPlayer</c> 挂。</para>
    ///
    /// <para>驱动方：<see cref="HitFeedbackDirector.OnHitFeedback"/> 在受击方为
    /// 玩家阵营时调用 <see cref="Play(Color,float,float)"/>。签名与
    /// <c>CombatView.PlayHitFlash(Color,float,float)</c> 逐字一致，
    /// 让 Director 的敌我两条派发路径保持同构。</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerHitFlash : MonoBehaviour
    {
        // =====================================================================
        // 渲染器与底色
        // =====================================================================

        /// <summary>
        /// 被染色的渲染器。
        ///
        /// 【为什么是 GetComponent 而不是 GetComponentInChildren】
        /// 玩家对象下面还挂着一个 "FacingMarker" 子物体，它也有 SpriteRenderer
        /// （WorldBuilder.cs:555）。用 GetComponentInChildren 的话，一旦将来
        /// 本体的 SpriteRenderer 因为某次重构被移到别处，就会**静默**染到那根
        /// 朝向条上 —— 表现为"挨打时角色不变色，只有旁边一根小棍变红"，
        /// 这种 bug 极难联想到根因。
        /// 用 GetComponent 锁死在本体上，与 HeroineAnimator.cs:102 取的是
        /// **同一个**渲染器，拿不到就是拿不到，不会张冠李戴。
        /// </summary>
        private SpriteRenderer _sr;

        /// <summary>
        /// 染色前的原始颜色。结束时精确还原到它，不做任何近似。
        ///
        /// 【为什么必须缓存，而不是"结束时写回 Color.white"】
        /// 写死 white 等于假设"玩家底色永远是纯白"。这个假设今天成立
        /// （WorldBuilder 建 SpriteRenderer 时没动过 color，Unity 默认就是 white），
        /// 但只要将来有人给玩家加个"中毒偏绿""残血泛红"的常驻底色，
        /// 每挨一次打就会被闪白**顺手洗成白色**，且再也回不去。
        /// 缓存实际值是唯一不会随上下文腐烂的写法。
        /// </summary>
        private Color _baseColor = Color.white;

        /// <summary>
        /// 底色是否已经采样过。
        ///
        /// 【为什么需要这个布尔量，而不是靠 _sr != null 判断】
        /// 采样只能发生**一次**，且必须在任何染色之前。若每次 EnsureRenderer
        /// 都重采一遍，第二次挨打时读到的就是上一次闪白**还没退完**的中间色，
        /// 它会被当成"底色"记下来 —— 于是每挨一次打底色就朝朱砂红漂移一点，
        /// 打十下之后角色永久变成红的，再也不还原。这是典型的状态累积腐蚀。
        /// </summary>
        private bool _baseCaptured;

        // =====================================================================
        // 本次闪白的参数（每次 Play 覆盖一遍）
        //
        // 与 CombatView 的字段一一对应，语义逐字相同 —— 两个类做的是同一件事，
        // 只是宿主不同。保持同构是为了让"敌我表现不一致"这种 bug 在读代码时
        // 就能被一眼看出来，而不是等到跑起来对比画面。
        // =====================================================================

        /// <summary>本次闪白混合到的目标色。玩家侧恒为 <c>HitFeedbackConfig.PlayerTint</c>（朱砂红）。</summary>
        private Color _flashColor = Color.white;

        /// <summary>本次闪白的总时长（秒）。作为衰减插值的分母，恒 &gt; 0。</summary>
        private float _flashDuration = HitFeedbackConfig.FlashDurPlayer;

        /// <summary>本次闪白的峰值混合强度 [0,1]。玩家侧 0.80，保留角色本身的墨色底子。</summary>
        private float _flashPeak = HitFeedbackConfig.FlashPeakPlayer;

        /// <summary>剩余闪白时间（秒）。&gt; 0 表示正在闪。</summary>
        private float _flashTimer;

        /// <summary>是否正在闪白。供测试与调试读取，生产路径不依赖它。</summary>
        public bool IsFlashing
        {
            get { return _flashTimer > 0.0f; }
        }

        // =====================================================================
        // 生命周期
        // =====================================================================

        private void Awake()
        {
            // 正常路径下这一次就拿到了（见文件头"核实结论 2"）。
            // 拿不到也不报错、不禁用自己 —— 交给首次 Play 时再试。
            EnsureRenderer();
        }

        private void OnDisable()
        {
            // 【复位纪律 1/2】
            // 组件被禁用后 LateUpdate 不再执行，_flashTimer 就永远没人递减了。
            // 此时若正卡在峰值上，玩家会**永久保持朱砂红**——看起来像角色中了
            // 一个永不消失的 debuff。必须在这里把颜色还回去。
            StopAndRestore();
        }

        private void OnDestroy()
        {
            // 【复位纪律 2/2】
            // 与 OnDisable 同理。销毁路径（换场景 / Destroy(gameObject) / 退出
            // PlayMode）不保证一定先走 OnDisable，两处都写才叫兜底 ——
            // 这条纪律沿用 HitFeedbackDirector.OnDisable/OnDestroy 的既有写法。
            //
            // 注意：整个 GameObject 一起销毁时 _sr 可能已经先没了。
            // StopAndRestore 内部有 null 判断（Unity 重载过 == null，
            // 已销毁对象会正确返回 true），不会抛。
            StopAndRestore();
        }

        /// <summary>
        /// 每帧推进闪白衰减。
        ///
        /// 【为什么吃 FeedbackClock.Delta 而不是 Time.deltaTime】
        /// 顿帧（hitstop）期间画面是"看起来停住了"，此时闪白也必须跟着停。
        /// 用 Time.deltaTime 的话，0.12s 的玩家顿帧里闪白会**自己把 0.16s 跑掉大半**，
        /// 解冻瞬间玩家看到的是一个已经退色到尾巴的闪白 —— 节奏对不上，
        /// 表现为"顿帧结束后才反应过来自己挨了打"。
        /// 顿帧期间 FeedbackClock.Delta 恒为 0，闪白原地冻住，与顿帧同起同落。
        ///
        /// 【为什么这里可以安全地吃被冻住的时钟，而 Director 的 _stopRemain 不行】
        /// 本类**不是**冻结的发起者。FeedbackClock.Frozen 由
        /// HitFeedbackDirector 负责置位与清零，它的倒计时用的是真实
        /// Time.deltaTime，所以冻结一定会被解开。本类只是搭车的乘客，
        /// 不存在"拿被自己冻住的时钟数自己还要冻多久"那种死锁。
        /// </summary>
        private void LateUpdate()
        {
            if (_flashTimer <= 0.0f)
            {
                return;
            }

            // 渲染器可能在闪白进行中被换掉（理论上不会，但代价只有一次 null 判断）。
            if (_sr == null)
            {
                _flashTimer = 0.0f;
                return;
            }

            _flashTimer -= FeedbackClock.Delta;

            if (_flashTimer <= 0.0f)
            {
                // ★ 精确还原点之一：时间到，一次性写回缓存的底色。
                //   不是 Lerp 到 0，是**直接赋值** —— 浮点插值到 k=0 时
                //   可能留下 1/255 的色差，累积几十次之后肉眼可辨。
                _flashTimer = 0.0f;
                _sr.color = _baseColor;
                return;
            }

            // 分母走字段而不是常量：Director 会传入濒死加成后的时长
            // （FlashDurPlayer 0.16s × LowHpFlashMul 1.4 = 0.224s）。
            // 继续用常量当分母的话 k 会大于 1，Color.Lerp 虽会自行钳制，
            // 但衰减曲线整段被截断 —— 表现为"濒死时闪白反而更早消失"，
            // 恰好把 R-08 想强化的信号削弱了。这条坑 CombatView.cs:343 已经踩过一次。
            float k = _flashDuration > 0.0f ? _flashTimer / _flashDuration : 0.0f;
            _sr.color = Color.Lerp(_baseColor, _flashColor, _flashPeak * Mathf.Clamp01(k));
        }

        // =====================================================================
        // 对外入口
        // =====================================================================

        /// <summary>
        /// 播一次参数化受击闪白。由 <see cref="HitFeedbackDirector"/> 在受击方为
        /// 玩家阵营时调用，分阵营配色（R-03）与濒死加成（R-08）都已在 Director
        /// 的 <c>Grade()</c> 里算好。
        ///
        /// <para>【签名为什么与 <c>CombatView.PlayHitFlash(Color,float,float)</c> 一致】
        /// 让 Director 的 <c>DispatchFlash</c> 两条分支只在"找谁"上有区别，
        /// "传什么"完全相同。参数一旦分化，敌我两侧的闪白迟早长成两个样子，
        /// 而且改一边忘一边不会有任何编译错误。</para>
        ///
        /// <para>【本类不认识阵营】它只负责"把这个颜色按这个强度混上去，
        /// 再用这个时长退回来"。判阵营的活在 Director.Grade() 一处。</para>
        /// </summary>
        /// <param name="tint">混合到的目标色（玩家侧为 <c>HitFeedbackConfig.PlayerTint</c> 朱砂红）。</param>
        /// <param name="duration">总时长（秒）。&lt;= 0 或 NaN 时回落到 <c>HitFeedbackConfig.FlashDurPlayer</c>。</param>
        /// <param name="peak">峰值混合强度，内部钳到 [0,1]。</param>
        public void Play(Color tint, float duration, float peak)
        {
            // 惰性解析：Awake 时若渲染器还没就绪（装配顺序被人调整过），
            // 在这里补上。取不到就安静返回 —— 没有渲染器的玩家对象是一条
            // 合法但退化的路径（例如纯逻辑的 PlayMode 测试场景），
            // 刷警告只会在这类场景里刷屏。
            if (!EnsureRenderer())
            {
                return;
            }

            // duration 会成为 LateUpdate 里的除数。上游若因为 FeedbackIntensity
            // 被调成 0、或某个配置写成负数而传进 0，这里回落到配置里的玩家默认值 ——
            // 直接 return 会留下一个"已经上了色但永远不衰减"的渲染器，
            // 那才是真正回不来的状态。逻辑与 CombatView.cs:404 逐字对齐。
            if (duration <= 0.0f || float.IsNaN(duration))
            {
                duration = HitFeedbackConfig.FlashDurPlayer;
            }

            _flashColor = tint;
            _flashDuration = duration;
            _flashPeak = Mathf.Clamp01(peak);
            _flashTimer = duration;

            // 立刻上到峰值，不等下一帧的 LateUpdate —— 差这一帧，
            // 快速连击时会看到"闪白比顿帧晚一拍"。
            _sr.color = Color.Lerp(_baseColor, _flashColor, _flashPeak);
        }

        /// <summary>
        /// 立刻结束闪白并把颜色还原到底色。
        ///
        /// <para>供 <see cref="HitFeedbackDirector.ClearAll"/>（拆线 / 终局 /
        /// 组件禁用）以及本类的 OnDisable / OnDestroy 调用。语义与
        /// <c>CameraShake.StopAndRecenter()</c> 对齐：**收敛到什么都没发生**。</para>
        ///
        /// <para>【为什么终局要还原而不是让它自然退完】A-8 要求结算面板上零残留。
        /// 终局那一刻画面会被面板接管，角色定格 —— 定格成朱砂红就是一处
        /// 肉眼可见的脏画面，而且它永远不会自己退掉（Director 已经停止工作了）。</para>
        /// </summary>
        public void StopAndRestore()
        {
            _flashTimer = 0.0f;

            // 没采过底色就说明从来没染过，此时写回 _baseColor 的默认值 white
            // 反而会**凭空篡改**渲染器原本的颜色。什么都不做才是正确的。
            if (!_baseCaptured || _sr == null)
            {
                return;
            }

            _sr.color = _baseColor;
        }

        // =====================================================================
        // 内部
        // =====================================================================

        /// <summary>
        /// 确保 <see cref="_sr"/> 可用，并在**首次**拿到时采样底色。
        /// </summary>
        /// <returns>渲染器可用则 true。</returns>
        private bool EnsureRenderer()
        {
            if (_sr == null)
            {
                // GetComponent 不遍历子物体，开销是一次组件表查找，
                // 且只在 _sr 为 null 时才走 —— 正常情况下仅 Awake 一次命中。
                _sr = GetComponent<SpriteRenderer>();
            }

            if (_sr == null)
            {
                return false;
            }

            if (!_baseCaptured)
            {
                // ★ 采样必须发生在任何染色之前，且一辈子只发生一次（见 _baseCaptured 注释）。
                _baseColor = _sr.color;
                _baseCaptured = true;
            }

            return true;
        }
    }
}
