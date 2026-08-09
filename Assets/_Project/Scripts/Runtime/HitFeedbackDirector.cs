// -----------------------------------------------------------------------------
// HitFeedbackDirector.cs —— 受击反馈总调度（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【本文件已是批次 2 完整版：四件套 + R-07 击杀强调 + R-08 低血量加强】
// 批次 1 只做 hitstop + 屏震，闪白与飘字的派发在本批接上。分档结果 HitGrade
// 从"只有强度"扩成"强度 + 这一档长什么样"（配色 / 峰值 / 时长），
// 让四件套共享**同一次**裁定 —— 这正是本类存在的理由。
//
// 【明确不要什么：让四件套各自订阅命中事件】
// 四件套是同一次命中的四种表达。各自订阅意味着"轻/重、谁挨打、伤害占比"这套
// 分档逻辑要抄四遍，而且四份实现迟早算出不一致的档位 —— 玩家会看到"飘字说是
// 重击，屏震却是轻的"。所以命中事件只有一个订阅者：本类。它算一次档，派发四次。
//
// 【明确不要什么：把去重/冷却下沉给各个表现组件自己做】
// R-01 的"同一时刻多次命中只取最长"和"两次 hitstop 起始间隔 ≥ 0.15s"是**跨表现**
// 的规则。下沉之后，屏震按自己的窗口去重、顿帧按自己的窗口去重，一次范围技能
// 就会出现"震了 3 下但只顿了 1 次"。节流必须发生在分叉之前，也就是这里。
//
// 【明确不要什么：碰 Scheduler.Paused】
// 本类对暂停是**只读**的：读 CombatBridge.IsGameplayBlocked / IsRunOver，
// 用来决定要不要清掉自己的 FeedbackClock.Frozen。方向永远单向（暂停 → 反馈），
// 绝不反向。两个标志位物理隔离，满足 PRD §6.2「不得复用同一个标志位」。
//
// 【★ 本文件最危险的一行：_stopRemain 的递减】
// 它必须用 Time.deltaTime，**绝不能**用 FeedbackClock.Delta。因为顿帧期间
// FeedbackClock.Delta 恒为 0 —— 拿被自己冻住的时钟去数自己还要冻多久，
// 永远数不完，画面**永久冻结**。这是死锁，不是掉帧。见 §6.3 最后一行。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 一次命中的分档结果。
    ///
    /// 【为什么是 struct】
    /// 每次命中都会产生一个，范围技能清场时一帧内十几个。class 的话就是十几次
    /// 堆分配喂给 GC，而这里全是值类型字段，struct 零分配。
    /// </summary>
    public struct HitGrade
    {
        /// <summary>受击方是否玩家。决定用哪一套阈值、哪一套配色。</summary>
        public bool IsPlayerVictim;

        /// <summary>是否重击。轻/重决定基准档位，连续系数再在其上微调。</summary>
        public bool IsHeavy;

        /// <summary>dmg / defender.HpMax，已钳到 [0,1]。</summary>
        public float Ratio;

        /// <summary>R-06 连续强度系数，**已乘过** HitFeedbackConfig.FeedbackIntensity。</summary>
        public float Scale;

        /// <summary>
        /// R-08：受击方是否处于濒死（仅玩家侧可能为 true）。
        /// 为 true 时闪白时长 ×LowHpFlashMul、屏震幅度 ×LowHpShakeMul。
        /// </summary>
        public bool IsLowHp;

        // ---------------------------------------------------------------------
        // 以下四项是"这一档长什么样"。
        //
        // 【为什么把它们塞进 HitGrade，而不是让 DispatchFlash / DispatchPopup
        //   各自去查 HitFeedbackConfig】
        // 分阵营配色是**档位裁定的一部分**。让两个派发函数各判一次 IsPlayerVictim，
        // 就是同一条规则的第二、第三份实现 —— 迟早出现"闪白是朱砂红、飘字是墨黑"
        // 这种自相矛盾的画面。裁定只发生在 Grade() 一处，派发只负责搬运。
        // ---------------------------------------------------------------------

        /// <summary>R-03 闪白色。敌人纯白 / 玩家朱砂红。</summary>
        public Color FlashTint;

        /// <summary>R-03 闪白峰值混合强度 [0,1]。</summary>
        public float FlashPeak;

        /// <summary>R-03 闪白时长（秒）。已含 R-08 濒死加成。</summary>
        public float FlashDuration;

        /// <summary>R-04 飘字色。与 <see cref="FlashTint"/> 同一套语言（玩家=朱砂、敌人=墨）。</summary>
        public Color PopupColor;
    }

    /// <summary>
    /// 受击反馈四件套（hitstop / 屏震 / 闪白 / 飘字）的唯一调度者。
    /// 挂在场景里的任意常驻对象上，通常与 <c>CombatBridge</c> 同一个 GameObject。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(120)]
    public sealed class HitFeedbackDirector : MonoBehaviour
    {
        // =====================================================================
        // 依赖
        // =====================================================================

        /// <summary>暂停状态来源。只读，永不写。</summary>
        private CombatBridge _bridge;

        /// <summary>屏震执行者。</summary>
        private CameraShake _shake;

        /// <summary>
        /// 伤害飘字层。本类是它**唯一**的驱动者（Push / FreezeAll / ClearAll）。
        ///
        /// 【为什么飘字层不自己订阅命中事件】
        /// 见文件头："四件套是同一次命中的四种表达"。飘字若自己订阅，就得自己
        /// 判阵营配色、自己判轻重字号 —— 那是把 Grade() 抄第四遍。
        /// </summary>
        private DamagePopupLayer _popup;

        /// <summary>
        /// 玩家受击染色器（批次 3 / R-03 玩家侧）。挂在玩家 GameObject 上。
        ///
        /// 【为什么玩家闪白不走 ViewOf，而要单独持有一个组件】
        /// ViewOf 查的是 CombatController._views，那个字典只在 SpawnView 里被写，
        /// 也就是**只有敌人**有 CombatView。要让玩家进这个字典，就得给玩家
        /// AttachView 一个 CombatView —— 而 CombatView.SyncFromKernel() 每帧写
        /// transform.position，会和 PlayerController 抢同一个 transform，
        /// 结果是玩家持续抽搐或被拽回内核位置。这条路已被主理人裁定否决，
        /// 完整理由写在 PlayerHitFlash.cs 的文件头，不要再走回头路。
        ///
        /// 所以玩家侧的闪白由一个只染色、不碰 transform 的轻组件承担，
        /// 本字段就是它的引用。它可能为 null（主菜单场景 / 玩家还没建好），
        /// 与 _popup 一样属于合法状态。
        /// </summary>
        private PlayerHitFlash _playerFlash;

        /// <summary>
        /// 命中粒子 / 光效通道（第五路）。与 _popup 同一地位：本类是它**唯一**的驱动者。
        ///
        /// <para>【为什么不订阅事件，而由本类持有并转发】见 HitEffects.cs 文件头：
        /// 自己订阅会逼着它重抄轻/重分档、同目标去重、暂停闸门、禁用守卫，
        /// 迟早和本类算出不一致的档位。本类在 Grade() 之后顺手调一次它的哑接口，
        /// 副作用最小——事件订阅数不变（仍然只有本类一个），三道闸自动继承。</para>
        ///
        /// <para>【为什么由本类自行解析，而不是在 CombatBridge.SetupHitFeedback 里 Bind】
        /// HitEffects 与本类同体（都挂在 Combat 那个 GameObject 上），一个
        /// GetComponent 即可，比 FindFirstObjectByType 更省、更稳；而且本类在
        /// ResolveDependencies 里用"GetOrAdd"自愈，万一场景里只有本类没有 HitEffects
        /// （或 PlayMode 测试手工摆场景忘挂），也会自动补一个，第五路永远在线。
        /// 它可能为 null 的唯一窗口是 Instantiate 当帧、且禁用守卫已拦下入口，
        /// 所以所有调用点都走 <c>?.</c> 哨兵。</para>
        /// </summary>
        private HitEffects _hitFx;

        // =====================================================================
        // hitstop 状态
        // =====================================================================

        /// <summary>当前顿帧还剩多少秒。&gt; 0 即表示 FeedbackClock.Frozen 应为 true。</summary>
        private float _stopRemain;

        /// <summary>
        /// 上一次 hitstop **起始**的时刻（<see cref="Time.unscaledTime"/>）。
        /// 用于 R-01 的 0.15s 起始冷却。
        ///
        /// 【为什么用 unscaledTime 而不是 Time.time】
        /// 本工程全仓禁用 Time.timeScale，两者理论上恒等；用 unscaledTime 是把
        /// "反馈计时与任何时间缩放无关"这件事写进代码，将来万一有人（比如做慢镜头
        /// 演出）动了 timeScale，冷却也不会跟着被拉长。
        /// </summary>
        private float _lastStopAt = float.NegativeInfinity;

        /// <summary>
        /// 起帧号。用于跳过"顿帧开始那一帧"的递减。
        ///
        /// 【为什么需要它】
        /// OnHitFeedback 在 Update 阶段（内核 Tick 中）被调用，而递减在 LateUpdate。
        /// 不跳过的话，起始那一帧会立刻被扣掉一整个 dt —— 0.05s 的轻击顿帧
        /// @60fps 只有 3 帧，白扣 1 帧就是 33% 的误差，轻重两档会明显糊在一起。
        /// 那一帧的时间实际上是在命中**之前**流逝的，本就不该算进顿帧时长。
        /// </summary>
        private int _stopKickFrame = -1;

        /// <summary>上一次派发反馈的目标 Id。配合下面的时刻做同目标去重。</summary>
        private int _lastHitId = -1;

        /// <summary>上一次派发反馈的时刻（unscaledTime）。</summary>
        private float _lastHitAt = float.NegativeInfinity;

        /// <summary>上一次派发反馈时那一档的 Scale，用于"同窗口内只取最强"的比较。</summary>
        private float _lastHitScale;

        /// <summary>惰性解析的重试倒计时（秒，吃真实时间）。</summary>
        private float _resolveRetry;

        /// <summary>
        /// 惰性解析的重试间隔（秒）。沿用 <c>HeroineAnimator.BridgeRetryInterval</c> 的量级。
        /// 场景装配最迟也就在进场后一两帧内完成，0.25s 一次足够快，
        /// 而全场景遍历一秒 4 次是可以接受的开销。
        /// </summary>
        private const float ResolveRetryInterval = 0.25f;

        // =====================================================================
        // 生命周期
        // =====================================================================

        private void Awake()
        {
            ResolveDependencies();
        }

        private void OnEnable()
        {
            // 【复位纪律 · 保险 2/3】
            // 组件被重新启用时，静态闸门可能还留着上一次运行的脏值
            // （Enter Play Mode without Domain Reload 下静态字段跨 PlayMode 存活）。
            // 本工程已经因为 MainMenuHud.SkipOnNextLoad 这个同样是 static 的字段
            // 吃过一次测试污染的亏（MENU09 假红），这里不重蹈覆辙。
            ClearHitstop();
        }

        private void OnDisable()
        {
            // 【复位纪律 · 保险 2/3】
            // ★ 这一句是防死锁的最后一道闸：组件一旦被禁用，LateUpdate 不再执行，
            // _stopRemain 就永远没人递减了。此时若 Frozen 还是 true，
            // 整个表现层会**永久冻结**——画面看起来和崩溃没有区别。
            //
            // 走 ClearAll 而不是只走 ClearHitstop：屏震也一样没人推进了，
            // 相机会永久停在最后那一帧的偏移位上。ClearAll 的第一句就是
            // ClearHitstop()，Frozen 在那里被放开。
            ClearAll();
        }

        private void OnDestroy()
        {
            // 与 OnDisable 同理。销毁路径（换场景 / Destroy(gameObject)）不一定
            // 走 OnDisable 之外的清理，两处都写才叫兜底。
            ClearHitstop();
        }

        private void LateUpdate()
        {
            ResolveDependencies();

            // ① 暂停 / 终局收敛优先于一切：它会直接清掉正在进行的顿帧与屏震。
            if (ApplyPauseConvergence())
            {
                return;
            }

            // ② 正常态：显式解冻飘字层。
            //
            // ★ 这一句是三态收敛的第三态，漏了它就是"暂停过一次之后飘字永久定格"。
            //   冻结是 ApplyPauseConvergence 里置上的，而那个分支在恢复后**不再执行**，
            //   所以放开的责任只能落在这里。
            //   每帧无条件写一个 bool 字段，代价可以忽略；换成"记一个上一帧状态再比较"
            //   反而多一个会走岔的状态，不划算。
            if (_popup != null)
            {
                _popup.FreezeAll(false);
            }

            // 第五路粒子与飘字**必须**对称解冻。冻结是 ApplyPauseConvergence 的
            // menuPaused 分支置上的（_hitFx.FreezeAll(true)），而那个分支恢复后
            // 不再执行，放开的责任同样只能落在这里。
            //
            // ★ 漏了这一句的后果比飘字定格更隐蔽：HitEffects.Tick 里
            //   `float dt = _frozen ? 0.0f : FeedbackClock.Delta;`
            //   会让 Age 永不增长 → 粒子永不按寿命到期 → 看板被永久占满，
            //   之后每一次新命中都要先挤掉最老的一个，最终表现为"暂停过一次
            //   以后特效越来越少"。
            if (_hitFx != null)
            {
                _hitFx.FreezeAll(false);
            }

            // ③ hitstop 倒计时。
            TickHitstop();
        }

        // =====================================================================
        // 对外入口
        // =====================================================================

        /// <summary>
        /// 注入依赖。由 <c>CombatBridge.SetupHitFeedback()</c>（批次 2）或
        /// 装配代码调用。传 null 表示"这一项保持现状"，方便分批接线。
        /// </summary>
        /// <param name="bridge">暂停状态来源。</param>
        /// <param name="shake">屏震执行者，通常在 Main Camera 上。</param>
        /// <param name="popup">伤害飘字层。</param>
        public void Bind(CombatBridge bridge, CameraShake shake, DamagePopupLayer popup)
        {
            Bind(bridge, shake, popup, null);
        }

        /// <summary>
        /// 注入依赖（批次 3 扩参版，多一个玩家染色器）。
        ///
        /// 【为什么是加重载而不是直接改三参签名】
        /// 三参版的调用方不止 CombatBridge.SetupHitFeedback 一处，还包括
        /// PlayMode 测试里手工摆场景的代码。直接改签名会让那些调用点全部
        /// 编译失败，而它们的正确修法只是"多传一个 null" —— 用重载把这件事
        /// 变成零改动，同时新代码仍然拿得到显式注入的能力。
        /// 三参版转调本方法并补 null，语义就是"这一项保持现状"，
        /// 与其余三项的 null 语义完全一致。
        /// </summary>
        /// <param name="bridge">暂停状态来源。</param>
        /// <param name="shake">屏震执行者，通常在 Main Camera 上。</param>
        /// <param name="popup">伤害飘字层。</param>
        /// <param name="playerFlash">玩家受击染色器，挂在玩家 GameObject 上。</param>
        public void Bind(CombatBridge bridge, CameraShake shake, DamagePopupLayer popup,
                         PlayerHitFlash playerFlash)
        {
            if (bridge != null)
            {
                _bridge = bridge;
            }
            if (shake != null)
            {
                _shake = shake;
            }
            if (popup != null)
            {
                _popup = popup;
            }
            if (playerFlash != null)
            {
                _playerFlash = playerFlash;
            }
        }

        /// <summary>
        /// 命中反馈的唯一入口。由 <see cref="CombatEventsUnity.HitFeedback"/> 委托驱动。
        ///
        /// 【为什么这里不用再判 applied】
        /// CombatEventsUnity.OnHit 开头那道 <c>if (!applied) return;</c> 已经拦过了，
        /// 委托根本不会被调用。闸门只该有一处，抄第二份的下场是将来改了一处忘了另一处。
        /// </summary>
        /// <param name="attacker">攻击方，可能为 null（环境伤害 / DOT 结算）。</param>
        /// <param name="defender">受击方，调用方已保证非 null。</param>
        /// <param name="dmg">本次实际结算的伤害。</param>
        public void OnHitFeedback(Combatant attacker, Combatant defender, float dmg)
        {
            if (defender == null)
            {
                return;
            }

            // ★★★ 死锁守卫（P1-01）——这一行不是"顺手判一下激活状态" ★★★
            //
            // 【为什么委托挡不住】
            // C# 委托持有的是**实例引用**，它完全不认识 MonoBehaviour 的 enabled /
            // activeInHierarchy —— 一个被禁用的组件，它的委托照样会被调用。
            //
            // 【为什么退订也挡不住】
            // 唯一的退订发生在 CombatBridge.OnDestroy → TeardownHitFeedback()。
            // 也就是说"被禁用但未销毁"这个窗口里，订阅关系**依然有效**。
            //
            // 【漏掉它的后果是画面死机，不是掉一次反馈】
            // 事件打进来 → KickHitstop → FeedbackClock.Frozen = true，
            // 而 _stopRemain 的唯一递减者是本组件的 LateUpdate —— 组件已禁用，
            // LateUpdate 停摆，没有任何人会把闸门放开 ⇒ 表现层**永久冻结**。
            // 置位者和递减者是同一个组件，这个结构决定了守卫必须在入口，
            // 不能指望下游兜底。
            //
            // 【为什么放在这个位置】
            // 在 defender 判空之后（空引用是更基础的拒绝理由），在
            // ResolveDependencies() 之前（禁用状态下没必要再去找依赖）。
            if (!isActiveAndEnabled)
            {
                return;
            }

            ResolveDependencies();

            // R-05 第一条：暂停 / 终局期间**不新起任何反馈**。
            // 注意是 return 而不是"记下来等解除暂停再播"——延迟播放的反馈对不上
            // 任何画面事件，比不播更糟。
            if (_bridge != null && _bridge.IsGameplayBlocked)
            {
                return;
            }

            HitGrade grade = Grade(defender, dmg);

            // R-01/R-02 并发去重：同一目标在极短窗口内的多次结算（典型是范围技能
            // 一步内的多段伤害）只表现最强的一次，不叠加、不排队。
            if (!PassDedupe(defender.Id, grade.Scale))
            {
                return;
            }

            // 四件套在这里分叉。顺序无关（四者互不读写），但固定成
            // "顿帧 → 屏震 → 闪白 → 飘字"这个从粗到细的次序，方便对着读日志。
            KickHitstop(grade);
            KickShake(grade);
            DispatchFlash(defender, grade);
            DispatchPopup(defender, dmg, grade);

            // 第五路：命中粒子 / 光效。不订阅事件、不抄分档——
            // 直接复用本类刚刚算好的 grade（轻/重、是否玩家、连续强度系数），
            // 把"这一下该出多大的粒子"交给 HitEffects 自己算。
            // _hitFx 可能为 null（禁用守卫已拦入口的窗口），所以走 ?. 哨兵。
            if (_hitFx != null)
            {
                Vector3 world = new Vector3(defender.Position.X, defender.Position.Y, 0.0f);
                _hitFx.PlayHit(world, grade.IsHeavy, grade.IsPlayerVictim, grade.Scale);
            }
        }

        /// <summary>
        /// 敌人死亡回调（R-07 击杀强调）。由 <see cref="CombatEventsUnity.EnemyDied"/> 驱动。
        ///
        /// 【为什么击杀要单独一档，而不是靠"最后那一刀自然就重"】
        /// 致命一击的伤害往往极小 —— 怪只剩 2 点血时一发普攻也能杀。按伤害占比
        /// 分档会把它判成最轻的一档，于是"杀死一只怪"和"挠了它一下"手感完全一样，
        /// 玩家最在意的事件反而是全场最没存在感的。击杀是**事件**，不是伤害。
        ///
        /// 【为什么不复用 OnHitFeedback 的去重】
        /// 去重是"同一目标的多次伤害结算只表现一次"，而击杀与致命伤是**两件事**
        /// （前者是状态跃迁，后者是数值变化），本来就该各表现一次。
        /// </summary>
        /// <param name="e">刚死亡的敌人。可能为 null（防御性）。</param>
        public void OnEnemyDied(Combatant e)
        {
            if (e == null)
            {
                return;
            }

            // ★★★ 死锁守卫（P1-01）—— 与 OnHitFeedback 入口同因同治 ★★★
            //
            // 本方法通向 KickKillEmphasis()，它同样会写 FeedbackClock.Frozen。
            // 委托不认 enabled、退订只在 OnDestroy，两条前提在这里一字不差地成立：
            // 组件被禁用后 LateUpdate 停摆，_stopRemain 再无人递减，
            // 这里放进去一次击杀强调就足以让表现层**永久冻结**。
            //
            // 两个入口必须**都**加。只堵一个等于没堵 —— 击杀事件与命中事件
            // 是两条独立的委托链路，任何一条都能单独把闸门置起来。
            if (!isActiveAndEnabled)
            {
                return;
            }

            ResolveDependencies();

            // R-05：暂停 / 终局期间不新起任何反馈。
            //
            // 【顺带解释一个看起来像 bug 的现象】
            // 杀掉**最后一只**敌人时，内核可能在同一 Tick 内把 RunPhase 翻成 Won，
            // 此时 IsGameplayBlocked 已为 true，这一次击杀强调会被吃掉。
            // 这是**正确**行为：A-8 要求结算面板上零残留，让相机在结算界面上
            // 抖 0.22s 才是真的错。
            if (_bridge != null && _bridge.IsGameplayBlocked)
            {
                return;
            }

            KickKillEmphasis();

            // 第五路：击杀爆发粒子。与 R-07 击杀强调同因——致命一击伤害往往极小，
            // 按占比分档会判成最轻一档，击杀是"事件"不是"数值"，视觉上该有自己的一档。
            // 这里复用 KickKillEmphasis 已经通过的暂停 / 禁用闸，不会在结算面板背后喷粒子。
            if (_hitFx != null)
            {
                Vector3 world = new Vector3(e.Position.X, e.Position.Y, 0.0f);
                _hitFx.PlayKill(world);
            }
        }

        /// <summary>
        /// 立刻收敛全部正在进行的反馈：顿帧结束、屏震归零回正。
        /// 供暂停 / 终局 / 拆线调用。
        /// </summary>
        public void ClearAll()
        {
            ClearHitstop();

            if (_shake != null)
            {
                _shake.StopAndRecenter();
            }

            // 玩家闪白一并收敛回底色（批次 3）。
            //
            // 【为什么必须在这里还原，而不是让它自然退完】
            // ClearAll 的调用方是 OnDisable / OnDestroy / 拆线 / 终局 —— 全都是
            // "本组件不会再工作"的路径。闪白的衰减由 PlayerHitFlash 自己的
            // LateUpdate 推进，它确实还活着，理论上能退完；但终局那一刻画面会被
            // 结算面板接管、角色定格，定格成朱砂红就是一处肉眼可见的脏画面
            // （A-8 要求结算面板上零残留）。与 _shake.StopAndRecenter() 同理，
            // 显式收敛永远比"相信它会自己好"可靠。
            if (_playerFlash != null)
            {
                _playerFlash.StopAndRestore();
            }

            // 去重记录一并清掉：暂停可能持续数秒，恢复后的第一次命中不该被
            // 暂停之前那次的记录误判为"窗口内重复"。
            _lastHitId = -1;
            _lastHitAt = float.NegativeInfinity;
            _lastHitScale = 0.0f;

            // 飘字全清（顺带解冻，见 DamagePopupLayer.ClearAll 的注释）。
            //
            // 【为什么这里是"清"而不是"冻"】
            // ClearAll 的调用方是 OnDisable / OnDestroy / 拆线 / 终局 —— 全都是
            // "不会再恢复"的路径。菜单暂停那条**会恢复**的路径不走这里，
            // 它在 ApplyPauseConvergence 里单独走 FreezeAll(true)。
            if (_popup != null)
            {
                _popup.ClearAll();
            }

            // 第五路粒子一并收敛。与 _popup 同一条纪律：ClearAll 的调用方都是
            // "不会再恢复"的路径，残留的粒子实例必须当场销毁，否则它们会
            // 永久挂在场景里（A-8 要求结算界面零残留，这里同样适用）。
            if (_hitFx != null)
            {
                _hitFx.ClearAll();
            }
        }

        // =====================================================================
        // 分档
        // =====================================================================

        /// <summary>
        /// 把一次命中翻译成强度档位。
        ///
        /// 【为什么阈值必须分玩家 / 敌人两套】
        /// DifficultyBridge.cs:52 敌人基准 Hp = 22.0f，:151 玩家 PlayerHpMax = 260.0f，
        /// 相差 11.8 倍。共用一条 8% 线的话，敌人侧 8% × 22 = 1.76 点伤害，
        /// 任何一次普攻都越线 ⇒ 敌人侧的轻/重分档**退化成常量**，等于没分。
        /// 具体两套数字在 HitFeedbackConfig 里，这里只负责按阵营分派。
        /// </summary>
        private HitGrade Grade(Combatant defender, float dmg)
        {
            HitGrade g = new HitGrade();
            g.IsPlayerVictim = defender.Faction == Faction.Player;

            // HpMax 有可能是 0（实体刚构造完还没填数值），除零会产生 NaN 并一路
            // 污染到屏震幅度 —— 相机位置一旦变成 NaN 就再也回不来了。
            float hpMax = defender.HpMax > 0.0f ? defender.HpMax : 1.0f;
            float safeDmg = dmg > 0.0f ? dmg : 0.0f;

            g.Ratio = Mathf.Clamp01(safeDmg / hpMax);
            g.IsHeavy = g.Ratio >= HitFeedbackConfig.HeavyRatio(g.IsPlayerVictim);
            g.Scale = HitFeedbackConfig.ScaleOf(g.Ratio, g.IsPlayerVictim);

            // ---- R-08 低血量加强 ---------------------------------------------
            //
            // 【为什么读的是"挨打之后"的血量】
            // CombatEventsUnity.OnHit 在伤害**已结算**之后才喊，所以这里的 HpRatio()
            // 已经是扣完血的值。这正是想要的：把玩家打进濒死的**那一下**就该加强，
            // 而不是等下一次挨打才反应过来 —— 后者会晚整整一个攻击周期，
            // 而濒死状态往往撑不过一个攻击周期。
            //
            // 【为什么只对玩家生效】敌人濒死不需要这个信号，它的表达是"下一刀就倒"。
            g.IsLowHp = g.IsPlayerVictim && defender.HpRatio() < HitFeedbackConfig.LowHpRatio;

            // ---- 分阵营外观（本方法是这套配色的唯一裁定处）--------------------
            if (g.IsPlayerVictim)
            {
                g.FlashTint = HitFeedbackConfig.PlayerTint;
                g.FlashPeak = HitFeedbackConfig.FlashPeakPlayer;
                g.FlashDuration = HitFeedbackConfig.FlashDurPlayer;
                // 飘字色经 PopupColorOf 取：当前 isCrit 恒为 false（命中事件无 crit 标志），
                // 结果等同旧写法；将来内核接入 crit 标志时只改这一行。
                g.PopupColor = HitFeedbackConfig.PopupColorOf(g.IsPlayerVictim, false);
            }
            else
            {
                g.FlashTint = HitFeedbackConfig.EnemyTint;
                g.FlashPeak = HitFeedbackConfig.FlashPeakEnemy;

                // 敌人轻击档刻意**不进** HitFeedbackConfig，而是取
                // CombatView.DefaultFlashDuration —— 那是无参 PlayHitFlash() 的
                // 行为本身，两处必须同值，否则"内核 fallback 闪一下"和
                // "Director 派发闪一下"会长得不一样。这是 HitFeedbackConfig
                // 类头注释里唯一承认的例外。
                g.FlashDuration = g.IsHeavy
                    ? HitFeedbackConfig.FlashDurEnemyHeavy
                    : CombatView.DefaultFlashDuration;

                g.PopupColor = HitFeedbackConfig.PopupColorOf(g.IsPlayerVictim, false);
            }

            // 濒死拉长闪白而不是加深：加深会把角色本体盖成一块纯色，
            // 玩家反而看不清自己站在哪 —— 濒死时这恰恰是最要命的信息。
            if (g.IsLowHp)
            {
                g.FlashDuration *= HitFeedbackConfig.LowHpFlashMul;
            }

            return g;
        }

        /// <summary>
        /// 同目标短窗口去重。窗口内重复命中只放行更强的那一次。
        /// </summary>
        /// <param name="defenderId">受击方 Id。</param>
        /// <param name="scale">本次的强度系数。</param>
        /// <returns>true = 放行并记录；false = 被去重吃掉。</returns>
        private bool PassDedupe(int defenderId, float scale)
        {
            float now = Time.unscaledTime;
            bool sameTarget = defenderId == _lastHitId;
            bool inWindow = now - _lastHitAt < HitFeedbackConfig.StopDedupeWindow;

            if (sameTarget && inWindow && scale <= _lastHitScale)
            {
                return false;
            }

            _lastHitId = defenderId;
            _lastHitAt = now;
            _lastHitScale = scale;
            return true;
        }

        // =====================================================================
        // hitstop
        // =====================================================================

        /// <summary>按档位起一次顿帧。受 R-01 的起始冷却约束。</summary>
        private void KickHitstop(HitGrade g)
        {
            float now = Time.unscaledTime;

            // 起始冷却：连击时每一下都顿，画面会从"有打击感"退化成"在掉帧"。
            // 注意冷却拦下的命中是**直接丢弃**，不延长当前顿帧——延长等价于
            // 把连击的顿帧累加起来，那正是这条冷却要防的事。
            if (now - _lastStopAt < HitFeedbackConfig.StopCooldown)
            {
                return;
            }

            float baseSec = g.IsPlayerVictim
                ? HitFeedbackConfig.StopPlayer
                : (g.IsHeavy ? HitFeedbackConfig.StopEnemyHeavy : HitFeedbackConfig.StopEnemyLight);

            float sec = Mathf.Min(baseSec * g.Scale, HitFeedbackConfig.StopMaxSeconds);
            if (sec <= 0.0f)
            {
                // FeedbackIntensity 被调到 0 就是"关掉反馈"，此时不该冻任何一帧。
                return;
            }

            // 并发取最强：已有更长的顿帧在跑就别缩短它。
            _stopRemain = Mathf.Max(_stopRemain, sec);
            _lastStopAt = now;
            _stopKickFrame = Time.frameCount;

            FeedbackClock.Frozen = true;
        }

        /// <summary>每帧推进顿帧倒计时。</summary>
        private void TickHitstop()
        {
            if (_stopRemain <= 0.0f)
            {
                // 兜底：状态与闸门必须始终一致。哪怕有别的路径把 _stopRemain 清了，
                // 闸门也不能留在 true 上。
                if (FeedbackClock.Frozen)
                {
                    FeedbackClock.Frozen = false;
                }
                return;
            }

            // 起帧不扣：那一帧的时间是在命中之前流逝的，见 _stopKickFrame 的注释。
            if (Time.frameCount == _stopKickFrame)
            {
                return;
            }

            // ★★★ 这里必须是 Time.deltaTime，绝不能是 FeedbackClock.Delta ★★★
            // 顿帧期间 FeedbackClock.Delta 恒为 0：用它递减就是拿被自己冻住的时钟
            // 去数自己还要冻多久，永远数不完 → 表现层**永久冻结**，等同于死机。
            // 这是死锁，不是掉帧。谁都不许改这一行。
            _stopRemain -= Time.deltaTime;

            if (_stopRemain <= 0.0f)
            {
                ClearHitstop();
            }
        }

        /// <summary>结束顿帧并放开静态闸门。可重复调用。</summary>
        private void ClearHitstop()
        {
            _stopRemain = 0.0f;
            _stopKickFrame = -1;
            FeedbackClock.Frozen = false;
        }

        // =====================================================================
        // 屏震
        // =====================================================================

        /// <summary>按档位起一次屏震。</summary>
        private void KickShake(HitGrade g)
        {
            if (_shake == null)
            {
                return;
            }

            float amp;
            float dur;

            if (g.IsPlayerVictim)
            {
                // 玩家侧有第三档：ShakeHeavyRatioPlayer(15%) 比重击线(8%) 更高。
                // 重击是"疼"，这一档是"要命"，US-4 要求它必须被单独感知到。
                bool crushing = g.Ratio >= HitFeedbackConfig.ShakeHeavyRatioPlayer;
                amp = crushing ? HitFeedbackConfig.ShakeAmpPlayerHeavy : HitFeedbackConfig.ShakeAmpPlayer;
                dur = crushing ? HitFeedbackConfig.ShakeDurPlayerHeavy : HitFeedbackConfig.ShakeDurPlayer;
            }
            else
            {
                amp = g.IsHeavy ? HitFeedbackConfig.ShakeAmpEnemyHeavy : HitFeedbackConfig.ShakeAmpEnemyLight;
                dur = g.IsHeavy ? HitFeedbackConfig.ShakeDurEnemyHeavy : HitFeedbackConfig.ShakeDurEnemyLight;
            }

            // R-08 濒死加强。与闪白同理，只放大幅度不动时长。
            if (g.IsLowHp)
            {
                amp *= HitFeedbackConfig.LowHpShakeMul;
            }

            // 只缩放幅度，不缩放时长：把时长也乘上去，弱击的抖动会短到只有一两帧，
            // 采样不足会让它看起来像画面"跳了一下"而不是"抖了一下"。
            // Scale 里已经含了 FeedbackIntensity，此处不要重复乘。
            _shake.Kick(amp * g.Scale, dur);
        }

        // =====================================================================
        // R-07 击杀强调
        // =====================================================================

        /// <summary>
        /// 起一次击杀强调：固定档顿帧 + 固定档屏震。
        ///
        /// 【★ 这里故意绕过 StopCooldown，不是漏写】
        /// 致命一击会**先**走 OnHitFeedback 起一次普通顿帧，紧接着（同一帧内）
        /// 内核才喊 EnemyDied。0.15s 的起始冷却此刻必然处于生效状态 ——
        /// 老老实实过闸的结果是：击杀强调 100% 被吃掉，R-07 等于没做。
        ///
        /// 绕过是安全的，因为这里同时做了两件事：
        ///   ① 走 Mathf.Max 取最长，不累加，所以不会把两段顿帧接成 0.05+0.14；
        ///   ② 把 _lastStopAt 推到当下，于是击杀**之后**的 0.15s 内不会再顿 ——
        ///      冷却对"后续连击"依然完整生效，被豁免的只有击杀这一个事件。
        ///
        /// 【为什么不看伤害占比】见 OnEnemyDied 的注释：击杀是事件不是数值。
        /// 但仍然乘全局强度系数 —— R-09 的语义是"整体调弱"，
        /// 留一个不受调节的档位会让 FeedbackIntensity = 0（无障碍全关）失效。
        /// </summary>
        private void KickKillEmphasis()
        {
            float k = HitFeedbackConfig.Intensity();
            if (k <= 0.0f)
            {
                // 反馈被整体关闭。这不是异常，直接什么都不做。
                return;
            }

            float sec = Mathf.Min(
                HitFeedbackConfig.KillStopSeconds * k, HitFeedbackConfig.StopMaxSeconds);

            if (sec > 0.0f)
            {
                _stopRemain = Mathf.Max(_stopRemain, sec);
                _lastStopAt = Time.unscaledTime;
                _stopKickFrame = Time.frameCount;
                FeedbackClock.Frozen = true;
            }

            if (_shake != null)
            {
                _shake.Kick(HitFeedbackConfig.KillShakeAmp * k, HitFeedbackConfig.KillShakeDur);
            }
        }

        // =====================================================================
        // R-03 闪白 / R-04 飘字派发
        // =====================================================================

        /// <summary>
        /// 派发一次分阵营闪白。敌我两条路，**宿主不同，参数完全相同**。
        ///
        /// <para>【★ 批次 3：玩家侧已点亮，不再是空操作】
        /// 此前玩家侧恒为空操作，因为 <c>ViewOf</c> 查的是
        /// <c>CombatController._views</c>，而该字典只在 <c>SpawnView</c> 里被写
        /// （CombatController.cs:475），也就是**只有敌人**有 CombatView；
        /// 玩家对象从未注册过，<c>ViewOf(player.Id)</c> 恒为 null。</para>
        ///
        /// <para>【★ 为什么修法不是"给玩家也注册一个 CombatView"】
        /// 那是最省事但**错误**的解法，已被主理人裁定否决：
        /// <c>CombatView.SyncFromKernel()</c> 每帧写 <c>transform.position</c>
        /// （含插值与批次 1 的顿帧钉位 / 归位逻辑），而玩家的位置由
        /// <c>PlayerController</c> 驱动。两者同帧抢写同一个 transform，
        /// 会导致玩家角色持续抽搐或被反复拽回内核位置 —— 这个回归比
        /// "没有闪白"严重得多。完整论证见 <c>PlayerHitFlash.cs</c> 文件头，
        /// 不要再走回头路。</para>
        ///
        /// <para>正解是玩家走一个只染色、不碰 transform 的轻组件
        /// <see cref="PlayerHitFlash"/>，它的 <c>Play</c> 签名与
        /// <c>CombatView.PlayHitFlash</c> 逐字一致，所以下面两条分支
        /// **只在"找谁"上有区别，传参一模一样**。</para>
        ///
        /// <para>【为什么用 g.IsPlayerVictim 而不是在这里重判 defender.Faction】
        /// 阵营裁定只发生在 <c>Grade()</c> 一处（那里写的是
        /// <c>defender.Faction == Faction.Player</c>，与 CombatEventsUnity.cs:89-92
        /// 同款）。在这里抄第二遍，就是同一条规则的第二份实现 —— 迟早出现
        /// "配色按玩家算、宿主按敌人找"这种自相矛盾的派发。本方法只搬运。</para>
        ///
        /// <para>【为什么两条路都是 return 而不是 LogWarning】
        /// 敌人侧：死亡到视图回收之间有几帧窗口，此时 ViewOf 返回 null 是**正常**的，
        /// 刷警告只会在清场时刷屏，把真正的问题埋掉。
        /// 玩家侧：主菜单场景根本没有玩家对象，_playerFlash 为 null 同样合法。</para>
        /// </summary>
        private void DispatchFlash(Combatant defender, HitGrade g)
        {
            if (g.IsPlayerVictim)
            {
                // ★ 玩家路径**不依赖 _bridge、也不依赖 ViewOf**。
                //   这是本批的关键：把玩家闪白从那个恒为 null 的查询里彻底摘出来。
                if (_playerFlash == null)
                {
                    return;
                }

                _playerFlash.Play(g.FlashTint, g.FlashDuration, g.FlashPeak);
                return;
            }

            // 敌人路径：维持原样，逐字未改。
            if (_bridge == null)
            {
                return;
            }

            CombatView view = _bridge.ViewOf(defender.Id);
            if (view == null)
            {
                return;
            }

            view.PlayHitFlash(g.FlashTint, g.FlashDuration, g.FlashPeak);
        }

        /// <summary>
        /// 派发一条伤害飘字。
        ///
        /// 【为什么这里再判一次 dmg &gt; 0（DamagePopupLayer.Push 里已经判过）】
        /// 不是冗余，是**语义分层**：这里的判定是 A-10 的规则
        /// "0 伤害（免疫 / 无敌帧 / 完全减免）不飘字"；Push 里那一判是渲染层
        /// 对脏输入的自我保护。两者恰好同形，但改动理由完全不同 ——
        /// 将来若要加"MISS"字样，改的是这一处，而不是 Push。
        ///
        /// 【为什么 z 传 0】
        /// 相机是正交的（WorldBuilder.cs:639 cam.orthographic = true），
        /// 正交投影下屏幕 x/y 与世界 z 无关，传 0 与传实际 z 的投影结果逐位相同。
        /// 而 Combatant.Position 本身只有 X/Y 两个分量，编不出第三个来。
        /// </summary>
        private void DispatchPopup(Combatant defender, float dmg, HitGrade g)
        {
            if (_popup == null || dmg <= 0.0f)
            {
                return;
            }

            Vector3 world = new Vector3(defender.Position.X, defender.Position.Y, 0.0f);
            _popup.Push(defender.Id, world, dmg, g.PopupColor, g.IsHeavy);
        }

        // =====================================================================
        // 暂停 / 终局收敛
        // =====================================================================

        /// <summary>
        /// 三态收敛。返回 true 表示本帧已被暂停 / 终局接管，调用方应跳过常规推进。
        ///
        /// 【为什么 hitstop 在暂停时是"立即结束"而不是"冻结保留"】
        /// 菜单暂停时 Scheduler.Paused == true，内核已经停了 —— 此时"冻结表现层"
        /// 是个无意义的空操作，没有东西在动，冻什么？更糟的是 FeedbackClock 是
        /// **全局**的，把 Frozen 留着会连暂停面板自己的过场动画一起按住。
        /// hitstop 本身是个 ≤0.12s 的瞬时效果，暂停通常持续数秒，
        /// "解除后接着播"没有任何可感知价值，却引入一个跨暂停边界的状态泄漏。
        /// </summary>
        private bool ApplyPauseConvergence()
        {
            if (_bridge == null || !_bridge.IsGameplayBlocked)
            {
                return false;
            }

            // 逐字沿用 HeroineAnimator.cs 已经建立的三态写法：
            // IsGameplayBlocked = IsRunOver || _menuPaused。
            bool menuPaused = !_bridge.IsRunOver;

            ClearHitstop();

            if (_shake != null)
            {
                _shake.StopAndRecenter();
            }

            if (_popup != null)
            {
                if (menuPaused)
                {
                    // 冻结但**不清除**。暂停常发生在"刚打出一个大数字想看清楚"
                    // 的时刻，清掉就是信息损失。恢复后由 LateUpdate 的正常态
                    // 显式 FreezeAll(false) 放开，那一条飘字接着播完。
                    _popup.FreezeAll(true);
                }
                else
                {
                    // 终局：A-8 要求结算界面上零残留。IsRunOver 不可逆，
                    // "恢复后接着播"的语义根本不存在，冻着只会在结算面板背后
                    // 挂 12 个永不消失的数字。
                    _popup.ClearAll();
                }
            }

            // 第五路粒子与飘字同一套三态收敛语义：菜单暂停冻结（保留正在播的爆发），
            // 终局清空（零残留）。_hitFx 走 ?. 哨兵——它可能还没被 ResolveDependencies
            // 解析出来（比如暂停发生在进场头 0.25s 内、且场景里没有挂 HitEffects），
            // 那种情况下没有粒子可收敛，静默跳过即可。
            if (_hitFx != null)
            {
                if (menuPaused)
                {
                    _hitFx.FreezeAll(true);
                }
                else
                {
                    _hitFx.ClearAll();
                }
            }

            return true;
        }

        // =====================================================================
        // 内部
        // =====================================================================

        /// <summary>
        /// 惰性补齐依赖。
        ///
        /// 【为什么不能只在 Awake 里找一次】
        /// WorldBuilder 是运行时程序化建场景的，Main Camera 上的 CameraShake
        /// 完全可能晚于本组件的 Awake 才被 AddComponent。只找一次就会永久拿到 null，
        /// 表现为"屏震在某些进入路径下静默失效"——这类 bug 极难复现。
        ///
        /// 【★ 但绝不能每帧去找】
        /// FindFirstObjectByType 会遍历整个场景。两个字段都拿到之后是两次 null
        /// 比较，代价为零；但在**永远找不到**的场景里（比如主菜单里残留了一个
        /// Director），不节流就是每帧两次全场景遍历。HeroineAnimator.cs:379 已经
        /// 因为同一个原因加了节流，这里沿用同一套做法。
        ///
        /// 【为什么倒计时吃 Time.deltaTime 而不是 FeedbackClock.Delta】
        /// 这是**记账**，不是表现。用被自己冻住的时钟去数记账周期，
        /// 顿帧期间就永远轮不到下一次重试——和 _stopRemain 是同一类坑。
        /// </summary>
        private void ResolveDependencies()
        {
            if (_bridge != null && _shake != null && _popup != null
                && _playerFlash != null && _hitFx != null)
            {
                return;
            }

            _resolveRetry -= Time.deltaTime;
            if (_resolveRetry > 0.0f)
            {
                return;
            }
            _resolveRetry = ResolveRetryInterval;

            if (_bridge == null)
            {
#if UNITY_2023_1_OR_NEWER
                _bridge = Object.FindFirstObjectByType<CombatBridge>();
#else
                _bridge = Object.FindObjectOfType<CombatBridge>();
#endif
            }

            if (_shake == null)
            {
#if UNITY_2023_1_OR_NEWER
                _shake = Object.FindFirstObjectByType<CameraShake>();
#else
                _shake = Object.FindObjectOfType<CameraShake>();
#endif
            }

            // 飘字层正常由 CombatBridge.SetupHitFeedback() 直接 Bind 进来，
            // 这条兜底是给"Director 先于 Bridge 装配"以及 PlayMode 测试里
            // 手工摆场景的路径用的。找不到就下一轮再找，永远不报错 ——
            // 没有飘字层的场景（比如主菜单）是一条合法路径。
            if (_popup == null)
            {
#if UNITY_2023_1_OR_NEWER
                _popup = Object.FindFirstObjectByType<DamagePopupLayer>();
#else
                _popup = Object.FindObjectOfType<DamagePopupLayer>();
#endif
            }

            // 玩家染色器（批次 3）。正常由 CombatBridge.SetupHitFeedback() 显式
            // Bind 进来，这条兜底覆盖两种情况：
            //   ① 玩家对象比 CombatBridge 晚建（WorldBuilder 与 Bridge 的装配
            //      次序不由本类保证），Bind 那一刻还找不到；
            //   ② PlayMode 测试里手工摆场景，根本没人调 Bind。
            //
            // 找不到就下一轮再找，永远不报错 —— 没有玩家对象的场景
            // （主菜单）是一条合法路径，和上面 _popup 的处理逻辑一致。
            //
            // 【代价】本方法带 0.25s 节流，最坏情况是进场后头 0.25s 内玩家
            // 挨的第一下没有闪白。这个窗口在实际对局里够不着——玩家出生时
            // 敌人还没进入攻击距离。不为它加一条"每次命中都全场景扫一遍"的
            // 快路径：那会让"场景里确实没有玩家染色器"的情况退化成每帧全场景遍历。
            if (_playerFlash == null)
            {
#if UNITY_2023_1_OR_NEWER
                _playerFlash = Object.FindFirstObjectByType<PlayerHitFlash>();
#else
                _playerFlash = Object.FindObjectOfType<PlayerHitFlash>();
#endif
            }

            // 第五路粒子通道：与 HitEffects 同体（都在 Combat 这个 GameObject 上），
            // 一个 GetComponent 就够，不必全场景扫。找不到就 GetOrAdd 自愈——
            // 保证"只有 Director、没有 HitEffects"的场景里第五路也在线。
            // 注意：这里**不**走 0.25s 节流之外的额外查找失败降级，
            // 因为 GetComponent / AddComponent 是本 GameObject 上的即时操作，零遍历成本。
            if (_hitFx == null)
            {
                _hitFx = GetComponent<HitEffects>();
                if (_hitFx == null)
                {
                    _hitFx = gameObject.AddComponent<HitEffects>();
                }
            }
        }
    }
}
