// -----------------------------------------------------------------------------
// ActionState.cs —— 动作帧机（引擎无关）
//
// 【为什么必须是整数帧而不是 float 计时器】
// T3 的第一条验收（P0-01 ①）是「同一动作在 30/60/144fps 下经历的逻辑帧数完全相同」。
// float 累减做不到这件事：0.4f 累减 24 次不等于 0，累积舍入会让某些帧多走 / 少走一步。
// 整数游标 _cursor++ 是精确运算，帧数一致是**结构保证**，不需要靠容差去论证。
// 秒 → 帧的换算只发生在 SkillConfig / StatusConfig 的常量定义处一次，运行时不再换算。
//
// 【为什么技能 / 连招 / 闪避必须共用这一个帧机】
// 三者都需要「前摇不结算、判定只结算一次、后摇可被取消」。各写一套计时器的结果是
// 「闪避能取消普攻但取消不了技能」这类只有靠手测才能发现的错位。收敛成一个类之后，
// 取消规则只有 CanCancelInto 一处答案。
//
// 【EnteredActive 是命中结算的唯一触发点】
// TickFrame 只在「本帧刚跨入 Active 段」时返回 ENTERED_ACTIVE。Encounter 收到它才去
// QueryHits + ResolveSkillHit。于是「一次动作只结算一次」不靠标志位，靠状态跃迁。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】看不懂上面那段？先读这里，读完再回去看就懂了。
// =============================================================================
// 一句话：这个文件负责回答「角色现在正在做什么动作、做到第几帧了」。
//
// · 什么是「帧」？
//   游戏世界不是连续流动的，而是像翻页动画一样一格一格往前跳。我们这个游戏规定
//   每秒钟固定跳 60 格，每一格就叫「一帧」（1 帧 ≈ 0.0167 秒）。
//   所以「24 帧」= 24 ÷ 60 = 0.4 秒。文件里所有时间都用「多少帧」来写。
//
// · 什么是「前摇 / 判定 / 后摇」？拿现实中的挥剑来打比方：
//     前摇(Startup)  = 举起剑的过程。这时候剑还没碰到怪，打不到人。
//     判定(Active)   = 剑刃扫过去的瞬间。**只有这一下会真的造成伤害**。
//     后摇(Recovery) = 收剑、站稳的过程。也打不到人，而且这段时间你动不了，
//                      这就是为什么"乱按技能会被怪抓住空档"。
//   三段加起来 = 一个完整动作。
//
// · 什么是「取消窗 CancelFrom」？
//   后摇本来是不能动的，但我们允许在后摇的后半段提前用别的动作把它"打断"。
//   比如砍完一刀，剑还没收回来时就可以直接翻滚跑掉。允许打断的那个起始帧号，
//   就叫取消窗。这是动作游戏"手感顺不顺"的核心，没有它玩起来就会很粘手。
//
// · 什么是「无敌帧 iframe」？（i = invincible，无敌）
//   翻滚的中间一小段时间里，角色被打也不掉血，好像穿过了敌人的攻击。
//   这段时间就叫无敌帧。本文件只负责记录"第几帧到第几帧算无敌"，
//   真正让角色免疫伤害的开关在 Encounter.cs 里拧。
//
// · 这个文件里有哪几个东西？
//     ActionKind        —— 动作的种类（发呆 / 普攻 / 放技能 / 翻滚 / 被打硬直）
//     ActionPhase       —— 动作演到哪一段了（前摇 / 判定 / 后摇）
//     ActionTickResult  —— 这一帧发生了什么（没事 / 刚进判定段 / 动作演完了）
//     FrameData         —— 一份"动作说明书"：前摇几帧、判定几帧、后摇几帧……
//     ActionState       —— 真正的"播放器"：拿着说明书，一帧一帧往下演
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// 动作大类。玩家与（P1 之后的）敌人共用。
    ///
    /// 【新手解释】就是「角色现在在干嘛」的分类标签。同一时刻只能是其中一种，
    /// 比如你不可能一边翻滚一边放技能 —— 这正是我们要用状态机的原因。
    /// </summary>
    public enum ActionKind
    {
        /// <summary>空闲。可接受任意输入。</summary>
        Idle = 0,

        /// <summary>普攻（水剑斩）。可被闪避全程取消。</summary>
        Attack = 1,

        /// <summary>施法（法阵冲击 / 血莲侵蚀）。</summary>
        Cast = 2,

        /// <summary>闪避翻滚。携带 i-frame 窗口。</summary>
        Dodge = 3,

        /// <summary>受击硬直。强制打断一切，且不可被任何输入取消。</summary>
        HitStun = 4
    }

    /// <summary>
    /// 动作内部相位。
    ///
    /// 【新手解释】一个动作被切成三段来演：举剑（前摇）→ 劈中（判定）→ 收剑（后摇）。
    /// 这个枚举就是告诉你"现在演到第几段了"。只有判定段会真的打到怪。
    /// </summary>
    public enum ActionPhase
    {
        /// <summary>无（Idle 时）。</summary>
        None = 0,

        /// <summary>前摇。不结算命中。</summary>
        Startup = 1,

        /// <summary>判定。跨入本段的那一帧结算命中。</summary>
        Active = 2,

        /// <summary>后摇。CancelFrom 之后可被新输入取消。</summary>
        Recovery = 3
    }

    /// <summary>
    /// 单帧推进的结果。
    ///
    /// 【新手解释】每过一帧，动作播放器都会汇报一句"这一帧发生了啥"。
    /// 战斗系统就靠听这句汇报来决定"要不要现在结算伤害"。
    /// </summary>
    public enum ActionTickResult
    {
        /// <summary>无事发生（含 Idle 空转）。</summary>
        None = 0,

        /// <summary>本帧刚跨入 Active 段 —— **命中结算的唯一触发点**。</summary>
        EnteredActive = 1,

        /// <summary>本帧动作走完，已回到 <see cref="ActionKind.Idle"/>。</summary>
        Finished = 2
    }

    /// <summary>
    /// 一个动作的帧数据（全部为 60Hz 整数逻辑帧）。
    ///
    /// 【新手解释】把它想成一张"动作说明书"：这一招举剑要几帧、劈中要几帧、
    /// 收剑要几帧、第几帧开始可以被打断、第几帧到第几帧是无敌的。
    /// 每个技能都有自己的一张说明书，全都写在 SkillConfig.cs 里。
    /// 改说明书上的数字 = 改这一招的手感，完全不用改任何代码逻辑。
    ///
    /// 游标语义：<c>TryBegin</c> 时 <c>Cursor = 0</c>，之后每个逻辑帧 <c>Cursor++</c>。
    /// <code>
    /// Cursor ∈ [1, Startup]                        → Startup
    /// Cursor ∈ (Startup, Startup+Active]           → Active
    /// Cursor ∈ (Startup+Active, Total]             → Recovery
    /// Cursor &gt; Total                            → 结束，回 Idle
    /// </code>
    /// <c>Startup = 0</c> 是合法且必要的：水剑斩要保住 T2「按下当帧即出伤害」的手感，
    /// 此时第一次 TickFrame 就会直接跨入 Active。
    /// </summary>
    public struct FrameData
    {
        /// <summary>前摇帧数。允许为 0。</summary>
        public int Startup;

        /// <summary>判定帧数。至少为 1，否则动作永远不会产出 ENTERED_ACTIVE。</summary>
        public int Active;

        /// <summary>后摇帧数。允许为 0。</summary>
        public int Recovery;

        /// <summary>i-frame 起始绝对帧号（Cursor 值）。<see cref="IframeLen"/> 为 0 时忽略。</summary>
        public int IframeStart;

        /// <summary>i-frame 持续帧数。0 = 该动作无无敌帧。</summary>
        public int IframeLen;

        /// <summary>
        /// 取消窗起始绝对帧号：<c>Cursor &gt;= CancelFrom</c> 时可被新动作打断。
        /// 取 <see cref="int.MaxValue"/> 表示全程不可取消（硬直用）。
        /// </summary>
        public int CancelFrom;

        /// <summary>动作总帧数 = 前摇 + 判定 + 后摇。</summary>
        public int Total
        {
            get { return Startup + Active + Recovery; }
        }

        /// <summary>动作总时长（秒）。仅供 UI / 日志展示，逻辑层一律用帧。</summary>
        public float TotalSeconds
        {
            get { return Total * CombatScheduler.FixedStep; }
        }

        /// <summary>构造一段无 i-frame、不可取消的帧数据。</summary>
        /// <param name="startup">前摇帧数。</param>
        /// <param name="active">判定帧数。</param>
        /// <param name="recovery">后摇帧数。</param>
        public FrameData(int startup, int active, int recovery)
        {
            Startup = startup < 0 ? 0 : startup;
            Active = active < 1 ? 1 : active;
            Recovery = recovery < 0 ? 0 : recovery;
            IframeStart = 0;
            IframeLen = 0;
            CancelFrom = int.MaxValue;
        }

        /// <summary>构造完整帧数据。</summary>
        /// <param name="startup">前摇帧数。</param>
        /// <param name="active">判定帧数。</param>
        /// <param name="recovery">后摇帧数。</param>
        /// <param name="cancelFrom">取消窗起始绝对帧号。</param>
        /// <param name="iframeStart">i-frame 起始绝对帧号。</param>
        /// <param name="iframeLen">i-frame 持续帧数（0 = 无）。</param>
        public FrameData(int startup, int active, int recovery, int cancelFrom, int iframeStart, int iframeLen)
        {
            Startup = startup < 0 ? 0 : startup;
            Active = active < 1 ? 1 : active;
            Recovery = recovery < 0 ? 0 : recovery;
            CancelFrom = cancelFrom < 0 ? 0 : cancelFrom;
            IframeStart = iframeStart < 0 ? 0 : iframeStart;
            IframeLen = iframeLen < 0 ? 0 : iframeLen;
        }

        /// <summary>硬直专用帧数据：无前摇无判定，整段后摇，全程不可取消。</summary>
        /// <param name="frames">硬直帧数（至少 1）。</param>
        /// <returns>帧数据。</returns>
        public static FrameData HitStun(int frames)
        {
            FrameData f = new FrameData();
            f.Startup = 0;
            f.Active = 0;          // 刻意为 0：硬直不产出 ENTERED_ACTIVE
            f.Recovery = frames < 1 ? 1 : frames;
            f.IframeStart = 0;
            f.IframeLen = 0;
            f.CancelFrom = int.MaxValue;
            return f;
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("{0}/{1}/{2} cancel@{3} if[{4},{5})",
                Startup, Active, Recovery,
                CancelFrom == int.MaxValue ? -1 : CancelFrom,
                IframeStart, IframeStart + IframeLen);
        }
    }

    /// <summary>
    /// 每单位一个的动作帧机。**可空组件**：未挂载时 <see cref="Encounter"/> 的 ①-A 阶段
    /// 直接跳过，执行路径与 T2 逐指令一致（这是「既有断言一字不改」的机制保证）。
    ///
    /// 【新手解释】这是「动作播放器」。它手里拿着一张说明书（FrameData），
    /// 每过一帧就把进度往前推一格，并告诉外面"现在演到哪了、该不该打伤害了"。
    /// 玩家身上挂一个，将来敌人也会挂一个 —— 大家用的是同一套播放器。
    ///
    /// 【什么叫"可空组件"】这个播放器是可以不装的。不装的时候（比如跑老的自动测试），
    /// 整个战斗流程会自动跳过所有 T3 新功能，表现得和 T3 之前一模一样。
    /// 这是我们保证"新功能不会偷偷改坏老数值"的核心手段。
    /// </summary>
    public sealed class ActionState
    {
        /// <summary>当前动作大类。</summary>
        public ActionKind Kind = ActionKind.Idle;

        /// <summary>当前相位。</summary>
        public ActionPhase Phase = ActionPhase.None;

        /// <summary>当前帧游标（1 基；0 表示动作刚开始尚未推进）。</summary>
        public int Cursor;

        /// <summary>当前动作的帧数据。</summary>
        public FrameData Frames;

        /// <summary>
        /// 当前动作对应的技能槽（<see cref="IntentSlot"/> 的整数值）。
        /// -1 表示与技能表无关（硬直）。
        /// </summary>
        public int SkillSlot = -1;

        /// <summary>
        /// 起手瞬间锁定的朝向（已含软索敌吸附）。命中判定一律用它，
        /// 而不是"结算那一帧的当前朝向" —— 否则前摇期间转身会把 AOE 甩到背后。
        /// </summary>
        public Vec2 LockedFacing = Vec2.Right;

        /// <summary>本帧是否处于无敌帧窗口内。</summary>
        public bool IsIframeActive
        {
            get
            {
                if (Frames.IframeLen <= 0)
                {
                    return false;
                }
                return Cursor >= Frames.IframeStart && Cursor < Frames.IframeStart + Frames.IframeLen;
            }
        }

        /// <summary>是否处于非 Idle 状态（正在做某个动作或硬直中）。</summary>
        public bool IsBusy
        {
            get { return Kind != ActionKind.Idle; }
        }

        /// <summary>剩余帧数（Idle 时为 0）。</summary>
        public int RemainFrames
        {
            get
            {
                if (Kind == ActionKind.Idle)
                {
                    return 0;
                }
                int left = Frames.Total - Cursor;
                return left < 0 ? 0 : left;
            }
        }

        /// <summary>
        /// 取消规则（P0-01 验收 ②③）：
        /// <list type="bullet">
        /// <item>Idle → 全放行；</item>
        /// <item>HitStun → 全拒（硬直不可用输入挣脱）；</item>
        /// <item>Attack 被 Dodge 取消 → 全程放行（闪避不产出伤害，放宽不影响平衡，手感更好）；</item>
        /// <item>其余 → 仅当 <c>Cursor &gt;= Frames.CancelFrom</c>（后摇取消窗内）。</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// 【新手解释】这个方法回答一个问题：「我现在能不能改做另一个动作？」
        /// 举例：发呆时想干嘛都行；砍到一半想翻滚 → 允许（翻滚是保命的，放宽手感更好）；
        /// 被怪打得踉跄（HitStun）时想翻滚 → 不行，只能挨打，这就是所谓"被抓住空档"。
        /// 什么时候被调用：每次你按键、系统准备起手一个新动作之前，都会先问它一次。
        /// </remarks>
        /// <param name="next">想要进入的动作。</param>
        /// <returns>true = 允许打断当前动作。</returns>
        public bool CanCancelInto(ActionKind next)
        {
            if (Kind == ActionKind.Idle)
            {
                return true;
            }
            if (Kind == ActionKind.HitStun)
            {
                return false;
            }
            if (Kind == ActionKind.Attack && next == ActionKind.Dodge)
            {
                return true;
            }
            if (Frames.CancelFrom == int.MaxValue)
            {
                return false;
            }
            return Cursor >= Frames.CancelFrom;
        }

        /// <summary>
        /// 尝试开始一个新动作。失败时**不产生任何副作用**（调用方据此决定不扣资源、不进 CD）。
        /// </summary>
        /// <remarks>
        /// 【新手解释】「起手」一个新动作，也就是让播放器从头开始播一张新说明书。
        /// 什么时候被调用：你按下攻击/技能/翻滚键，并且资源够、冷却好了之后。
        /// 关键点：如果返回 false（当前动作不许被打断），外面就**什么都不会做** ——
        /// 不扣蓝、不进冷却。这就是"技能放不出来时不会白白浪费资源"的实现方式。
        /// </remarks>
        /// <param name="kind">动作大类。</param>
        /// <param name="slot">技能槽（无对应技能传 -1）。</param>
        /// <param name="frames">帧数据（这一招的说明书）。</param>
        /// <param name="facing">起手锁定朝向（零向量时保留上一次朝向）。
        /// 之所以要"锁定"，是因为如果结算时才读朝向，前摇期间转身会把攻击范围甩到背后去。</param>
        /// <returns>true = 已进入新动作。</returns>
        public bool TryBegin(ActionKind kind, int slot, FrameData frames, Vec2 facing)
        {
            if (kind == ActionKind.Idle)
            {
                return false;
            }
            if (!CanCancelInto(kind))
            {
                return false;
            }

            Kind = kind;
            Frames = frames;
            Cursor = 0;
            SkillSlot = slot;
            Phase = ActionPhase.Startup;
            if (!facing.IsZero())
            {
                LockedFacing = facing.Normalized();
            }
            return true;
        }

        /// <summary>
        /// 推进一个逻辑帧（纯整数运算，无浮点）。
        /// </summary>
        /// <remarks>
        /// 【新手解释】把动作往前播一格。每秒会被调用 60 次，由 Encounter.StepFixed 统一驱动。
        /// 它返回的结果是整个战斗系统的"发令枪"：一旦返回 EnteredActive，
        /// 就说明"剑刃正好扫到了"，外面才会去找哪些怪被打中、扣多少血。
        /// 因为"刚进入判定段"这件事在一次动作里只可能发生一次，
        /// 所以"一刀只打一次伤害"是天然成立的，不需要额外加什么"已结算"标记。
        /// </remarks>
        /// <returns>本帧的跃迁结果。</returns>
        public ActionTickResult TickFrame()
        {
            if (Kind == ActionKind.Idle)
            {
                Phase = ActionPhase.None;
                return ActionTickResult.None;
            }

            ActionPhase before = Phase;
            Cursor++;

            int startupEnd = Frames.Startup;
            int activeEnd = Frames.Startup + Frames.Active;
            int total = Frames.Total;

            if (Cursor > total)
            {
                Reset();
                return ActionTickResult.Finished;
            }

            if (Cursor <= startupEnd)
            {
                Phase = ActionPhase.Startup;
            }
            else if (Cursor <= activeEnd)
            {
                Phase = ActionPhase.Active;
            }
            else
            {
                Phase = ActionPhase.Recovery;
            }

            if (Phase == ActionPhase.Active && before != ActionPhase.Active)
            {
                return ActionTickResult.EnteredActive;
            }
            return ActionTickResult.None;
        }

        /// <summary>
        /// 破韧硬直强制打断（P0-01 验收 ③）。
        ///
        /// 【为什么不回滚已扣资源】
        /// 已经放出去的技能不退蓝，这是 ARPG 惯例；退蓝会让「被打断 = 白嫖一次试探」
        /// 成为最优解，博弈直接退化。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"硬直"就是被打得踉跄、短时间动不了的状态。
        /// 什么时候被调用：怪物把你的"韧性"打空的那一刻（韧性 = 抗打断能力条）。
        /// 注意它是**强制**的：不管你正在放多华丽的技能，都会被当场打断。
        /// 但已经花掉的蓝不会退给你 —— 否则"故意被打断来白嫖试探"就成了最优打法。
        /// </remarks>
        /// <param name="frames">硬直帧数（多少帧内动不了）。</param>
        public void ForceHitStun(int frames)
        {
            Kind = ActionKind.HitStun;
            Frames = FrameData.HitStun(frames);
            Cursor = 0;
            SkillSlot = -1;
            Phase = ActionPhase.Recovery;
        }

        /// <summary>复位到 Idle。</summary>
        public void Reset()
        {
            Kind = ActionKind.Idle;
            Phase = ActionPhase.None;
            Cursor = 0;
            SkillSlot = -1;
            Frames = new FrameData(0, 1, 0);
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("{0}/{1} cur={2}/{3} slot={4}{5}",
                Kind, Phase, Cursor, Frames.Total, SkillSlot, IsIframeActive ? " [IFRAME]" : string.Empty);
        }
    }
}
