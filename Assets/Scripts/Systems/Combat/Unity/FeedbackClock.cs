// -----------------------------------------------------------------------------
// Unity/FeedbackClock.cs —— 表现层时钟闸门（asmdef: Xianxia.Combat.Unity）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【明确不要什么：Time.timeScale】
// 做 hitstop 最省事的写法是 Time.timeScale = 0.05f，本工程**永久禁止**这么写。
// 理由是硬的：CombatController.cs:313 是 Scheduler.Tick(Time.deltaTime)，
// timeScale 会连内核一起缩放 → 同一段输入在"开反馈/关反馈"两种情况下跑出不同
// 的战斗结果 → 确定性对拍失守，围攻倍率指纹 2.5294x 当场作废。
// 顿帧是**看起来停了**，不是**真的停了**，这两件事必须分清。
//
// 【明确不要什么：给每个表现组件发一个"我冻住了"的事件】
// N 个组件 N 份订阅、N 份状态，解冻时序对不齐就会看到"人停了但剑气还在飞"。
// 单一静态闸门是唯一能保证同一帧内所有表现层取到同一个答案的做法。
//
// 【要什么：一个零策略的读数原语】
// 本类**不做任何决策**——什么时候冻、冻多久，全部由 HitFeedbackDirector 决定。
// 它只回答一个问题："这一帧，表现层应该推进多少秒？"
//
// 【为什么落在 Xianxia.Combat.Unity 而不是 Xianxia.Unity.T2】
// 依赖方向是单向的：Xianxia.Unity.T2 ──► Xianxia.Combat.Unity ──► Xianxia.Combat。
// CombatView 住在 Combat.Unity，它必须**读**这个闸门；而 Combat.Unity 的
// asmdef 里没有、也不允许有 Xianxia.Unity.T2（加上就成环）。
// 所以闸门只能放在两边都够得着的下层。四个真正的"反馈组件"
// （Director / CameraShake / DamagePopupLayer / HitFeedbackConfig）仍然全部在 T2。
//
// 【★ 静态状态的复位责任——本工程有前科】
// MainMenuHud.SkipOnNextLoad 同样是 static，曾经因为跨 PlayMode 存活导致
// MENU09 用例假红。Frozen 的复位责任在下面的 XML 注释里写死，别再犯第二次。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Combat.UnityBridge
{
    /// <summary>
    /// 表现层（渲染 / 动画 / 特效 / 相机 / 飘字）专用的时钟闸门。
    ///
    /// 【三层时钟的分工，写死在这里免得下次又搞混】
    ///   内核层   Time.deltaTime  —— 顿帧期间照常推进，一个字不能改（确定性）
    ///   状态机层 Time.deltaTime  —— CombatView 的 _stepAge / _catchUpAge，
    ///                               必须跟着内核走，落后就会在解冻瞬间瞬移
    ///   渲染层   FeedbackClock.Delta —— 只有它在顿帧期间归零
    /// </summary>
    public static class FeedbackClock
    {
        /// <summary>
        /// 表现层是否处于顿帧冻结中。为 true 时 <see cref="Delta"/> 恒返回 0。
        ///
        /// 【谁写它】
        /// 唯一写入者是 <c>HitFeedbackDirector</c>（T2）：命中时置 true，
        /// 倒计时归零 / 菜单暂停 / 终局时置 false。其他任何地方都不许写。
        ///
        /// 【谁负责复位——三重保险，缺一不可】
        ///   1. 本类的 <see cref="ResetStatics"/>：带
        ///      [RuntimeInitializeOnLoadMethod(SubsystemRegistration)]，
        ///      专治 Unity「Enter Play Mode without Domain Reload」下静态字段
        ///      跨 PlayMode 存活——上一次 Play 正卡在顿帧里退出，下一次 Play
        ///      开局就会全局冻结，画面像死机。
        ///   2. HitFeedbackDirector.OnEnable / OnDisable / OnDestroy：组件被
        ///      禁用或销毁时，倒计时不会再有人递减，必须在这里兜底放闸。
        ///   3. CombatBridge.TeardownHitFeedback()：拆线时最后再写一次（批次 2）。
        ///
        /// 【它和 Scheduler.Paused 没有任何关系】
        /// Scheduler.Paused 是"玩法冻结"，唯一写入者是 CombatBridge.ApplyPauseState()。
        /// 两个标志位物理隔离、互不读写。方向是单向的：暂停会去清反馈，
        /// 反馈永远不会去动暂停。
        /// </summary>
        public static bool Frozen;

        /// <summary>
        /// 这一帧表现层应当推进的秒数。顿帧期间为 0。
        ///
        /// 【什么该用它】角色渲染位置、闪白衰减、VFX 年龄、动画帧推进、
        /// 相机跟随阻尼、屏震计时、飘字寿命。
        ///
        /// 【什么绝不能用它】
        ///   - 内核 Scheduler.Tick —— 用了就破坏确定性；
        ///   - CombatView 的插值状态机 —— 用了会落后于内核，解冻瞬间瞬移；
        ///   - HitFeedbackDirector 自己的 hitstop 倒计时 —— 用了就是拿被自己
        ///     冻住的时钟去数自己还要冻多久，永远数不完，画面**永久冻结**。
        /// </summary>
        public static float Delta
        {
            get { return Frozen ? 0.0f : Time.deltaTime; }
        }

        /// <summary>
        /// 把静态状态复位到"未冻结"。由 Unity 在每次进入运行时自动调用，
        /// 也可以被测试显式调用来隔离用例之间的污染。
        ///
        /// 【为什么是 SubsystemRegistration 而不是 AfterSceneLoad】
        /// SubsystemRegistration 是 RuntimeInitializeLoadType 里最早的一档，
        /// 早于任何 Awake。放晚了会出现"第一个 Awake 已经读到脏的 Frozen"的窗口。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetStatics()
        {
            Frozen = false;
        }
    }
}
