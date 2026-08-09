// -----------------------------------------------------------------------------
// ActiveStatus.cs —— 在体状态实例与施加者快照（引擎无关）
//
// 【为什么要有 SourceSnapshot】（P0-06 验收③）
// 「施加者死亡后 DOT 仍按快照跳完」不是一个可选的打磨项，而是必须的正确性要求：
// 如果 DOT 每跳都回头去读 attacker.BreakDef，那么施加者一死（Combatant 被
// Encounter.RemoveDead 移出列表）就会出现空引用，或者更糟 —— 读到被对象池复用后的
// 新怪的数值。快照是值类型 struct，附着瞬间整体拷贝，之后与施加者彻底解耦。
//
// 【为什么 ActiveStatus 是 class 而不是 struct】
// 它住在 List<ActiveStatus> 里且每帧被就地修改（RemainFrames--）。struct 存 List 里
// 每次修改都要「取出 → 改 → 写回」，漏写回是这类代码最常见的 bug。用 class 换取
// 「拿到引用就能改」的确定性，代价是每次附着一次分配 —— 在 60Hz × 32 怪的量级下
// 完全可接受（状态附着是低频事件，不是每帧事件）。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：上一个文件是"Debuff 说明书"，这个文件是"某只怪身上实际中的那个 Debuff"。
//
// · 说明书 vs 实例，为什么要分开？
//   "蛊毒"这个 Debuff 的规则（5 秒、每 0.6 秒掉一次血、最多 5 层）全世界只有一份，
//   写在 StatusEffectDef 里。但场上 10 只怪可能都中了毒，各自剩余时间不同、
//   层数不同 —— 这 10 份各自的进度就是 10 个 ActiveStatus。
//   这在编程里叫"类 vs 对象"，是最基础也最重要的一组概念。
//
// · 什么是 SourceSnapshot（施加者快照）？为什么必须有？
//   考虑这个场景：你给怪下了毒，然后你的召唤兽把怪打死了，或者你自己切换了装备。
//   毒还剩 3 秒没跳完，这 3 秒的伤害该按谁的攻击力算？
//   如果每次跳伤都回头去查"下毒的那个人现在攻击力多少"，会出两个严重问题：
//     ① 那个人可能已经死了、已经被从战斗列表里移除了 → 程序崩溃（空引用）
//     ② 更阴险：对象在游戏里会被回收复用，你可能读到一只**新刷出来的怪**的数值
//   所以正确做法是：下毒的那一瞬间，把需要的数字整个抄一份存下来（这就是"快照"），
//   之后跳伤只看这份抄件，跟原施加者再无关系。
//   这正是 PRD P0-06 验收③「施加者死亡后 DOT 仍按快照跳完」的实现方式。
//
// · 为什么快照用 struct 而实例用 class？
//   struct（结构体）是"值"，赋值时整个复制一份，天然就是快照语义。
//   class（类）是"引用"，赋值时两个变量指向同一份数据，方便就地修改。
//   ActiveStatus 每帧都要改剩余时间，用 class 拿到引用直接改最省事；
//   SourceSnapshot 要的就是"复制一份和原件断开关系"，所以用 struct。
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// 施加者在附着瞬间的数值快照。**值类型**，与施加者生命周期彻底解耦。
    ///
    /// 【新手解释】"下毒那一刻，下毒者的能力数据的一份复印件"。
    /// 有了它，下毒者死了、变强了、变弱了，都不会影响这瓶已经生效的毒。
    /// </summary>
    public struct SourceSnapshot
    {
        /// <summary>施加者实例 id。玩家侧 DOT 需要它作为 W-CORE 冷却表的 key。</summary>
        public int SourceId;

        /// <summary>施加者的破防值（DOT 跳伤时按它穿甲）。</summary>
        public float BreakDef;

        /// <summary>
        /// 威力缩放。P0 恒为 1.0；T4 引入攻击力成长后，DOT 才能"按附着那一刻的强度"跳完，
        /// 而不是被中途的装备切换影响。
        /// </summary>
        public float PowerScale;

        /// <summary>构造。</summary>
        /// <param name="sourceId">施加者 id。</param>
        /// <param name="breakDef">施加者破防值。</param>
        /// <param name="powerScale">威力缩放（P0 传 1.0f）。</param>
        public SourceSnapshot(int sourceId, float breakDef, float powerScale)
        {
            SourceId = sourceId;
            BreakDef = breakDef;
            PowerScale = powerScale <= 0.0f ? 1.0f : powerScale;
        }

        /// <summary>无来源的默认快照（用于测试桩与环境伤害）。</summary>
        /// <returns>SourceId = -1、无破防、威力 1.0 的快照。</returns>
        public static SourceSnapshot None()
        {
            return new SourceSnapshot(-1, 0.0f, 1.0f);
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("src#{0} bd={1:F1} x{2:F2}", SourceId, BreakDef, PowerScale);
        }
    }

    /// <summary>
    /// 挂在某个单位身上的一条在体状态。
    ///
    /// 【新手解释】"这只怪身上正挂着的那个 Debuff"。
    /// 你在 HUD 上看到怪头顶的 [毒×3 5.2s] 小图标，
    /// 上面的"3"读的是 Stacks，"5.2s"读的是 RemainSeconds，就是这个类。
    /// </summary>
    public sealed class ActiveStatus
    {
        /// <summary>静态定义（只读引用，不拷贝）。</summary>
        public StatusEffectDef Def;

        /// <summary>当前层数。<see cref="StackRule.Refresh"/> 的状态恒为 1。</summary>
        public int Stacks = 1;

        /// <summary>剩余帧数。</summary>
        public int RemainFrames;

        /// <summary>距离下一次跳伤还有多少帧。非 DOT 状态恒为 0。</summary>
        public int NextTickFrames;

        /// <summary>施加者快照（附着瞬间锁定）。</summary>
        public SourceSnapshot Source;

        /// <summary>剩余时长（秒）。HUD 的收缩条读它。</summary>
        public float RemainSeconds
        {
            get { return RemainFrames * CombatScheduler.FixedStep; }
        }

        /// <summary>剩余时长占比，夹在 [0,1]。</summary>
        public float RemainRatio
        {
            get
            {
                if (Def == null || Def.DurationFrames <= 0)
                {
                    return 0.0f;
                }
                float r = (float)RemainFrames / Def.DurationFrames;
                if (r < 0.0f)
                {
                    return 0.0f;
                }
                return r > 1.0f ? 1.0f : r;
            }
        }

        /// <summary>
        /// 本次跳伤的伤害值 = 单层伤害 × 层数 × 快照威力。
        /// 「蛊毒 5 层各自跳伤」（P0-06 验收②）就落在这个乘法上。
        /// </summary>
        public float TickDamageNow
        {
            get
            {
                if (Def == null)
                {
                    return 0.0f;
                }
                return Def.TickDamage * Stacks * Source.PowerScale;
            }
        }

        /// <summary>状态 id 的便捷访问（Def 为 null 时返回空串）。</summary>
        public string Id
        {
            get { return Def != null ? Def.Id : string.Empty; }
        }

        /// <summary>
        /// 按定义初始化 / 重置本实例（附着与刷新共用）。
        /// </summary>
        /// <param name="def">状态定义。</param>
        /// <param name="src">施加者快照。</param>
        /// <param name="stacks">层数。</param>
        public void Init(StatusEffectDef def, SourceSnapshot src, int stacks)
        {
            Def = def;
            Source = src;
            Stacks = stacks < 1 ? 1 : stacks;
            RemainFrames = def != null ? def.DurationFrames : 0;
            NextTickFrames = def != null && def.IsDot ? def.TickIntervalFrames : 0;
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("{0}x{1} {2}f", Id, Stacks, RemainFrames);
        }
    }
}
