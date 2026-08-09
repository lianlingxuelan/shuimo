// -----------------------------------------------------------------------------
// Progression.cs —— 玩家成长曲线 / 等级状态机（引擎无关）
//
// 【它解决什么问题】（PM 缺口清单 P1-6，PRD §1.1「打完没有回报」）
// 在此之前，经验这条链路**只有发送方，没有接收方**：
// 敌人身上早就挂着 Combatant.ExpValue（DifficultyBridge.cs:330 出厂时就填好了，
// 精英 ×2.0 / BOSS ×3.0 也都算进去了），敌人死亡事件也一直在正常广播
// （CombatEventsUnity.cs:113 EnemyDied）。但**玩家这一侧空无一物** ——
// 没有等级、没有经验、没有任何东西去接住这个数。
// 于是玩家辛辛苦苦清完一个区，屏幕上什么都不会发生：不变强、不升级、
// 连个数字都不跳。战斗系统做得再精细，玩家也感觉不到「我在变强」。
// 本文件补的就是这个**接收方**：一张曲线表 + 一个状态机。
//
// 【为什么单独开一个文件 / 一个类，而不是往 Combatant 或 Encounter 里塞几个 int】
//   1. 🚨 **Combatant.Level 这个名字已经被敌人占用了**，而且语义完全不同。
//      看 Combatant.cs:88 `public int Level = 1;` —— 它是**敌人的区域等级**，
//      由 DifficultyBridge.cs:328 `c.Level = lv;` 在刷怪时写入，用来查难度曲线。
//      如果玩家等级复用这个字段，"等级"这个词在同一个类里会有两种含义，
//      而且玩家升级会顺手污染难度查表的输入 —— 这是本期最容易踩的坑，
//      所以在架构阶段就被否决（AD-1），玩家等级另起 PlayerProgression。
//   2. Encounter 已经 1000+ 行，是全项目最不该继续膨胀的文件。
//   3. 成长是**纯算术**：输入只有「击杀了谁、他值多少经验」两个数，
//      输出只有「几级、多少血上限、加多少攻击」。它完全可以脱离 Encounter
//      单独构造、单独单测（AC-28），不需要搭一整场战斗。
//      这一点和 RunPhase.cs 的 Evaluate 三参数重载是同一个思路。
//
// 【★ 最重要的设计决定：只算数，不碰战场】
// PlayerProgression **不持有任何 Combatant / Encounter 引用**，
// 一个字段、一个参数、一个局部变量都没有。它算完之后只做一件事：抛事件。
// 真正去改玩家血上限、改技能伤害的，是 Unity 层的 CombatBridge 在回调里做的。
//
// 这不是洁癖，是两条硬理由：
//   · 数值红线。U1 平衡口径（HP 260 / d_eff 4.0 / raw 12 / CD 0.4s / 围攻 4.300 次/s）
//     被 t1_selfcheck.py 的 64 条断言锁死。成长系统只要碰了战场上任何一个
//     不该碰的字段，那些长时间对拍就会整体错位。不碰 = 零风险。
//   · 编译期保证。GainExpFrom 的签名是 (int sourceId, float expValue)
//     两个基元，而不是更省事的 (Combatant e)。这样一来「不写敌人字段」
//     从一条**靠自觉遵守的规约**，变成了一条**编译器帮你把关的性质**：
//     类里根本没有 Combatant 这个类型，物理上写不进去（AC-26）。
//     拆包（e.Id / e.ExpValue）由 CombatBridge.OnEnemyDied 在 Unity 层完成。
//
// 【禁止事项】
//   · 不得引用 UnityEngine。本文件在 Xianxia.Combat 程序集下，
//     该 asmdef 设了 noEngineReferences=true，加上 t3_selfcheck.py 的红线扫描，
//     是**双重机械保证**。加一行 using UnityEngine 会同时炸编译和护栏。
//   · 不得引入任何新的伤害乘区。血上限和攻击加成都是**加法项**
//     （260 + 26×(L-1) / 1×(L-1)），26×(L-1) 是编译期就能算出的加数，
//     它不与任何伤害量相乘。境界压制、五行克制那类乘法加成不在本期范围。
//   · 不得写任何敌人字段（见上一段）。
//   · 不得在 Reset() 里抛事件、不得在 Reset() 里清订阅者（理由见 Reset 的注释）。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是**记分员**。他坐在场边记「你打死了谁、攒了多少分、现在几级」，
// 然后喊一嗓子"升级了！"。他自己不下场，也不会伸手去改你的血条 ——
// 改血条是场边另一个人（CombatBridge）听到喊声之后去做的。
//
// · 什么叫「溢出经验保留」？
//   假设你 4 级，升 5 级还差 2 点经验（进度 48/50）。这时你一刀砍死一只怪，
//   拿到 18 点。那多出来的 16 点去哪了？
//   —— 不能扔掉。扔掉的话，玩家会发现「我打死大怪和打死小怪升级速度一样」，
//   努力被吞了。正确做法是：用掉 2 点升到 5 级，剩下 16 点计入 5 级的进度
//   （变成 16/65）。这就是"溢出保留"。
//
// · 为什么「连升两级要抛两次事件」，不能合并成一次？
//   因为每一级都有它自己的奖励（+26 血上限、+1 攻击）。如果只抛一次事件，
//   订阅方就得自己算"这次跳了几级、一共该加多少"，逻辑就从这里泄漏出去了。
//   更实际的原因：以后要做升级动画/音效，玩家一次连升两级应该看到两次特效，
//   听到两声"叮"。抛两次事件，表现层什么都不用改就自然对了。
//   （这也是为什么 RefreshSkillRawFromLevel 每级都重算一次 —— 冗余但对称。）
//
// · 为什么「升级不回满血」？
//   如果升级回满血，玩家就会学会一个很怪的玩法：残血了不撤退、不喝药，
//   而是硬扛着去找怪刷经验，赌升级回血。这会让整个战斗的紧张感崩掉。
//   所以规则是 Hp += ΔHpMax：上限 +26，当前血也 +26，**比例不变、缺口不变**。
//   260/260 升级后是 286/286（本来就满，还是满）；
//   100/260 升级后是 126/286（还是残血，缺口依然是 160）。
//
// · 🚨 为什么「标尺不该跟着被测量的人一起变」？（全项目最需要防呆的一条）
//   游戏里有一个反调机制：系统会根据"玩家有多少血"去倒推"怪该打多疼"，
//   目标是让玩家大约挨 4 下就死（这个 4 就是 d_eff = 4.0）。
//   现在问题来了 —— 如果玩家升级把血从 260 涨到 286，而反调的分子
//   也跟着变成 286，那系统就会想：「哦，他血变多了，那怪也得打得更疼」，
//   于是怪的伤害同步涨了 10%。结果：**玩家白升了这一级**，
//   体感强度一点没变，甚至因为量化误差还会变难。这叫"用橡皮尺量身高"。
//   正确做法：反调的分子**永久钉死在 260**（1 级血量，也就是平衡基准值），
//   玩家实际血上限该涨涨该跌跌，两者从此**解耦**。
//   代码上的落地就是 CombatBridge 把常量 PlayerHpMax 直接写给反调，
//   而不是把玩家实时的 p.HpMax 回写过去。
//   这条如果做反了，整个 P1-6 就等于没做 —— PRD 里 R-11 标了「最高优先级」。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Xianxia.Combat
{
    /// <summary>
    /// 玩家成长曲线的**唯一数据源**（静态查表，无状态）。
    ///
    /// 【为什么血/攻用公式，经验用数组】
    /// 血量和攻击力是**等差**的（每级固定 +26 / +1），
    /// 用公式 <c>260 + 26*(L-1)</c> 写出来既无舍入风险，也比手抄十个数字更难写错。
    /// 经验需求是**非等差**的（20/25/35/50/65/90/120/165/220，越往后越陡），
    /// 没有公式可言，只能用数组。
    /// 两者都会在 ProgressionTests 里被逐项钉死（AC-03 / AC-04 / AC-05 / AC-06），
    /// 所以实现方式的差异不影响断言强度。
    /// </summary>
    public static class ProgressionCurve
    {
        /// <summary>等级上限。到顶之后不再累计经验（R-08 / AC-14 / AC-15）。</summary>
        public const int MAX_LEVEL = 10;

        /// <summary>
        /// 1 级血上限 = 平衡基准血量 = 260。
        ///
        /// 🚨 【跨程序集常量，必须与 CombatBridge.PlayerHpMax 同值】
        /// Unity 层的 <c>CombatBridge.cs:40 public const float PlayerHpMax = 260.0f;</c>
        /// 和这里是同一个数。它们物理上无法共享 —— CombatBridge 在
        /// Xianxia.Unity.T2 程序集，本文件在 Xianxia.Combat 程序集，
        /// 而后者设了 noEngineReferences=true，两边互相引用不到。
        ///
        /// 所以采用「接受重复声明 + 用断言锁死 + 两边注释互相点名」这个标准解法：
        /// P1_6_ProgressionIntegrationTests 的 AC-21 会断言
        /// <c>CombatBridge.PlayerHpMax == ProgressionCurve.HpMaxAt(1)</c>。
        /// **改动其中任何一个而不改另一个，那条测试立刻红。**
        /// </summary>
        public const float BASE_HP_MAX = 260.0f;

        /// <summary>每升一级血上限的增量（加法项，不是乘区）。</summary>
        public const float HP_PER_LEVEL = 26.0f;

        /// <summary>每升一级攻击力的增量（**加法**，不是百分比 —— 见文件头「禁止事项」）。</summary>
        public const float ATK_PER_LEVEL = 1.0f;

        /// <summary>
        /// 「从本级升到下一级」所需经验。索引 = 等级 - 1。
        /// 末位（10 级）是 0，表示已封顶、没有下一级。
        /// 用 0 而不是 int.MaxValue 表示封顶，是为了让「还差多少」这个数
        /// 在 HUD 上有唯一确定值；代价是 HUD 算百分比时必须先判零（防除零）。
        /// </summary>
        public static readonly int[] EXP_TO_NEXT = { 20, 25, 35, 50, 65, 90, 120, 165, 220, 0 };

        /// <summary>
        /// 「达到该等级」所需的累计经验。索引 = 等级 - 1。
        /// 它是 EXP_TO_NEXT 的前缀和，冗余存一份是为了让 AC-06 能直接逐项断言，
        /// 而不必在测试里再算一遍前缀和（测试里重算 = 用同样的逻辑验证自己）。
        /// </summary>
        public static readonly int[] EXP_CUMULATIVE = { 0, 20, 45, 80, 130, 195, 285, 405, 570, 790 };

        /// <summary>
        /// 把任意整数夹到合法等级区间 <c>[1, MAX_LEVEL]</c>（AC-07 的越界防护）。
        /// 所有查表函数都先过这一道，所以传 0、负数、999 进来都不会抛数组越界。
        /// </summary>
        /// <param name="level">任意整数，允许越界。</param>
        /// <returns>夹紧后的等级，保证落在 [1, 10]。</returns>
        public static int Clamp(int level)
        {
            if (level < 1)
            {
                return 1;
            }

            if (level > MAX_LEVEL)
            {
                return MAX_LEVEL;
            }

            return level;
        }

        /// <summary>
        /// 查该等级的血上限：<c>260 + 26 × (L - 1)</c>。
        /// 1 级恒为 260.0f（加成为 0），这是数值红线「PlayerHpMax = 260」
        /// 严格位等价的依据 —— IEEE-754 下 <c>x + 0.0f ≡ x</c>。
        /// </summary>
        public static float HpMaxAt(int level)
        {
            return BASE_HP_MAX + HP_PER_LEVEL * (Clamp(level) - 1);
        }

        /// <summary>
        /// 查该等级的攻击力**加成**（不是攻击力本身）：<c>1 × (L - 1)</c>。
        /// 1 级恒为 0.0f，所以 1 级时 raw = 12 + 0 ≡ 12，两条伤害路径都逐位不变。
        /// </summary>
        public static float AtkBonusAt(int level)
        {
            return ATK_PER_LEVEL * (Clamp(level) - 1);
        }

        /// <summary>
        /// 查「从该等级升到下一级」所需经验。已封顶（或越界到封顶）返回 0。
        /// </summary>
        public static int ExpToNext(int level)
        {
            int lv = Clamp(level);
            if (lv >= MAX_LEVEL)
            {
                return 0;
            }

            return EXP_TO_NEXT[lv - 1];
        }

        /// <summary>
        /// 查「达到该等级」所需的累计经验（AC-06）。1 级为 0，10 级为 790。
        /// </summary>
        public static int CumulativeExpAt(int level)
        {
            return EXP_CUMULATIVE[Clamp(level) - 1];
        }
    }

    /// <summary>
    /// 一次升级的完整描述，作为 <see cref="PlayerProgression.LeveledUp"/> 的载荷。
    ///
    /// 【为什么带 Delta，而不让订阅方自己算】
    /// R-07 的规则是 <c>Hp += ΔHpMax</c>，订阅方需要的是**增量**。
    /// 如果让 CombatBridge 自己用 <c>HpMaxAt(新) - HpMaxAt(旧)</c> 去减，
    /// 曲线逻辑就泄漏到 Unity 层了；连升多级时还特别容易把基准搞错
    /// （拿 1 级去减 3 级，一次性 +52，看起来对，但事件语义就乱了）。
    /// 由曲线的主人把增量算好递出去，订阅方只管加，是最不容易错的分工。
    ///
    /// 【为什么用 struct 而不是 class】
    /// 一局最多抛 9 次（1→10），完全没有 GC 压力；
    /// 而值语义天然防止订阅方把这个包裹改了再传给下一个订阅方。
    /// </summary>
    public readonly struct LevelUpInfo
    {
        /// <summary>升级前的等级。</summary>
        public readonly int PrevLevel;

        /// <summary>升级后的等级。恒为 <see cref="PrevLevel"/> + 1（每级各抛一次）。</summary>
        public readonly int NewLevel;

        /// <summary>血上限的增量，恒为 +26.0f。订阅方用它做 <c>Hp += ΔHpMax</c>。</summary>
        public readonly float DeltaHpMax;

        /// <summary>升级后的血上限绝对值。订阅方用它直接赋给 <c>Player.HpMax</c>。</summary>
        public readonly float NewHpMax;

        /// <summary>攻击加成的增量，恒为 +1.0f。</summary>
        public readonly float DeltaAtkBonus;

        /// <summary>升级后的攻击加成绝对值（相对 1 级基础值的加数）。</summary>
        public readonly float NewAtkBonus;

        /// <summary>
        /// 由 <see cref="PlayerProgression"/> 在升级瞬间构造。
        /// 外部一般不需要手动 new，但测试里可以，用来单独验证订阅方的反应。
        /// </summary>
        public LevelUpInfo(
            int prevLevel,
            int newLevel,
            float deltaHpMax,
            float newHpMax,
            float deltaAtkBonus,
            float newAtkBonus)
        {
            PrevLevel = prevLevel;
            NewLevel = newLevel;
            DeltaHpMax = deltaHpMax;
            NewHpMax = newHpMax;
            DeltaAtkBonus = deltaAtkBonus;
            NewAtkBonus = newAtkBonus;
        }
    }

    /// <summary>
    /// 玩家等级 / 经验状态机（**纯观察者**，见文件头「★ 最重要的设计决定」）。
    ///
    /// 【生命周期】跟随**一局**。生产路径上它随 CombatBridge 一起被创建和销毁，
    /// 场景重载后天然回到 1 级 —— 所以 <see cref="Reset"/> 在生产路径上
    /// 其实一次都不会被调用，它主要服务于测试和未来的「不重载场景直接重开」。
    ///
    /// 【线程模型】事件在逻辑线程同步抛出，与 RunPhaseTracker 一致，订阅方无需切线程。
    /// </summary>
    public sealed class PlayerProgression
    {
        /// <summary>当前等级，初值 1，恒落在 [1, MAX_LEVEL]。</summary>
        private int _level = 1;

        /// <summary>当前等级内的经验进度，恒落在 [0, ExpToNext)；封顶时恒为 0。</summary>
        private int _expInLevel = 0;

        /// <summary>本局实际入账的经验总量。**封顶后停止增长**（AC-15）。</summary>
        private int _expTotal = 0;

        /// <summary>
        /// 已结算过经验的击杀源 id 集合（幂等去重，AC-35）。
        ///
        /// 【为什么可以拿 Combatant.Id 当键】
        /// 它由 DifficultyBridge.AllocId() 分配，那里的注释原文是
        /// 「W-CORE 冷却表以它为 key，必须全局唯一」—— 既然冷却表敢用它当键，
        /// 我们也敢。同一只怪的死亡事件被重复广播时（比如同帧多处判死），
        /// 第二次进来会被这个集合拦下，经验只结算一次。
        ///
        /// 【为什么存 int 而不是存 Combatant 引用】
        /// 存引用等于让本类"认识"Combatant，文件头承诺的编译期保证就没了；
        /// 顺带还会把死掉的敌人对象一直吊在内存里不让 GC 回收。
        /// </summary>
        private readonly HashSet<int> _credited = new HashSet<int>();

        /// <summary>
        /// 升级事件，**每升一级抛一次**（AC-13）。连升两级 = 抛两次，不合并。
        /// 抛出时机：等级与所有派生属性**都已更新完毕之后**，
        /// 所以订阅方在回调里读 <see cref="Level"/> / <see cref="HpMax"/> 拿到的都是新值。
        /// </summary>
        public event Action<LevelUpInfo> LeveledUp;

        /// <summary>
        /// 经验入账事件，载荷是**实际入账量**（不是传入量）。
        /// 入账 0 时不抛 —— 重复击杀、封顶后再吃经验、传 0 或负数，
        /// 这几种情况下 UI 不该有任何抖动。
        /// </summary>
        public event Action<int> ExpGained;

        /// <summary>当前等级。</summary>
        public int Level
        {
            get { return _level; }
        }

        /// <summary>当前等级内已攒的经验。封顶后恒为 0。</summary>
        public int ExpInLevel
        {
            get { return _expInLevel; }
        }

        /// <summary>升到下一级还需要的**总量**（不是差值）。封顶时为 0。</summary>
        public int ExpToNext
        {
            get { return ProgressionCurve.ExpToNext(_level); }
        }

        /// <summary>本局累计实际入账的经验。封顶后不再增长。</summary>
        public int ExpTotal
        {
            get { return _expTotal; }
        }

        /// <summary>是否已到等级上限。</summary>
        public bool IsMaxLevel
        {
            get { return _level >= ProgressionCurve.MAX_LEVEL; }
        }

        /// <summary>当前等级对应的血上限（查曲线，本类不持有玩家引用，只是报数）。</summary>
        public float HpMax
        {
            get { return ProgressionCurve.HpMaxAt(_level); }
        }

        /// <summary>
        /// 当前等级对应的攻击力加成。
        /// Unity 层通过 <c>CombatBridge.PlayerAtkBonus</c> 转发读取，
        /// 那是 AttackController 的**唯一读取出口**。
        /// </summary>
        public float AtkBonus
        {
            get { return ProgressionCurve.AtkBonusAt(_level); }
        }

        /// <summary>
        /// 击杀入账（**生产路径的唯一入口**），带幂等去重。
        ///
        /// 【为什么参数是 (int, float) 而不是 (Combatant)】
        /// 见文件头「★ 最重要的设计决定」：两个基元参数让「不写敌人字段」
        /// 从规约升级成编译期性质。拆包由 CombatBridge.OnEnemyDied 负责。
        ///
        /// 【为什么在入口处就把 float 截成 int】
        /// Combatant.ExpValue 是 float，但整条曲线全是整数（20/25/35…）。
        /// 在入口一次性转 int，之后全程整数运算 ——
        /// 这样 AC-05 / AC-06 才能用 <c>==</c> 逐项断言，而不必退化成 Within(ε)。
        /// 用 (int) 截断而非四舍五入：ExpValue 出厂时已经是整数值
        /// （基础值 × 2.0 / × 3.0 都是整倍），截断只在浮点表示误差
        /// （如 34.999999）时起兜底作用，方向偏保守。
        /// </summary>
        /// <param name="sourceId">击杀源的 <c>Combatant.Id</c>，用作幂等键。</param>
        /// <param name="expValue">该敌人的 <c>ExpValue</c>，**已含精英/BOSS 倍率**，此处不再二次相乘（R-02 / AC-33 / AC-34）。</param>
        /// <returns>实际入账的经验；重复 id、非正数、已封顶均返回 0。</returns>
        public int GainExpFrom(int sourceId, float expValue)
        {
            // 先去重再转发。注意顺序：即便 expValue 是 0，也要把 id 记下来，
            // 否则一只"零经验怪"会每次死亡都走一遍完整流程，白白浪费。
            if (_credited.Contains(sourceId))
            {
                return 0;
            }

            _credited.Add(sourceId);

            int amount = (int)expValue;
            return GainExp(amount);
        }

        /// <summary>
        /// 直接入账经验（**测试与调试路径**，无去重）。
        ///
        /// 连升多级用 while 循环逐级结算，每级各抛一次 <see cref="LeveledUp"/>（AC-13）。
        /// 溢出经验保留到下一级进度（AC-11 / AC-12），封顶后丢弃余数。
        /// </summary>
        /// <param name="amount">要入账的经验量。小于等于 0 时状态完全不变（AC-16）。</param>
        /// <returns>实际入账量。已封顶或参数非正时返回 0。</returns>
        public int GainExp(int amount)
        {
            // AC-16：防御性早退。注意必须在任何状态写入之前，且不抛任何事件。
            if (amount <= 0)
            {
                return 0;
            }

            // AC-15：已封顶就彻底不动，累计经验也不再增长。
            if (IsMaxLevel)
            {
                return 0;
            }

            int credited = 0;
            int remaining = amount;

            // 【为什么用 credited 逐级累加，而不是先 _expTotal += amount 再循环扣】
            // 因为封顶时余数要**丢弃**，而丢弃的部分不该算进"累计入账"。
            // 契约表规定 ExpInLevel ∈ [0, ExpToNext)，封顶时 ExpToNext = 0，
            // 该区间为空 ⇒ 封顶时 ExpInLevel 只能是 0，不允许挂着一截用不掉的余数。
            // 一次性先加再扣的写法，会在"1 级一口气吃 1000 点"时留下 210 点
            // 永远无法消化的残渣，让 HUD 显示 "210/0"。
            while (remaining > 0 && !IsMaxLevel)
            {
                int need = ProgressionCurve.ExpToNext(_level);

                // 理论上进不来（!IsMaxLevel ⇒ need > 0），留作防御，避免死循环。
                if (need <= 0)
                {
                    break;
                }

                int room = need - _expInLevel;

                if (remaining < room)
                {
                    // 攒不满这一级，全部计入当前进度，结束。
                    _expInLevel += remaining;
                    _expTotal += remaining;
                    credited += remaining;
                    remaining = 0;
                    break;
                }

                // 攒满了这一级：先把状态推到新等级，再抛事件。
                _expTotal += room;
                credited += room;
                remaining -= room;
                _expInLevel = 0;

                LevelUpOnce();
            }

            // 走到这里若 remaining > 0，说明已经封顶，余数按 R-08 丢弃。

            if (credited > 0)
            {
                // 【标准写法】先取本地变量再判空。
                // 直接 if (ExpGained != null) ExpGained(x) 在多线程下有 TOCTOU 窗口；
                // 本项目单线程也照此写，保持全项目一致（同 RunPhase.cs）。
                Action<int> handler = ExpGained;
                if (handler != null)
                {
                    handler(credited);
                }
            }

            return credited;
        }

        /// <summary>
        /// 升一级并抛出一次 <see cref="LeveledUp"/>。
        ///
        /// 【顺序约束】<c>_level++</c> **必须先于**事件抛出。
        /// 订阅方（Hud）会在回调里直接读 <see cref="Level"/>，
        /// 先抛后加会让界面稳定显示错一级 —— 这类 bug 极难在肉眼下发现。
        /// </summary>
        private void LevelUpOnce()
        {
            int prev = _level;
            int next = ProgressionCurve.Clamp(prev + 1);

            float prevHpMax = ProgressionCurve.HpMaxAt(prev);
            float prevAtkBonus = ProgressionCurve.AtkBonusAt(prev);

            _level = next;

            float newHpMax = ProgressionCurve.HpMaxAt(next);
            float newAtkBonus = ProgressionCurve.AtkBonusAt(next);

            LevelUpInfo info = new LevelUpInfo(
                prev,
                next,
                newHpMax - prevHpMax,
                newHpMax,
                newAtkBonus - prevAtkBonus,
                newAtkBonus);

            Action<LevelUpInfo> handler = LeveledUp;
            if (handler != null)
            {
                handler(info);
            }
        }

        /// <summary>
        /// 清空本局成长状态：等级回 1、进度归零、累计归零、幂等集合清空。
        ///
        /// 🚨 【Reset() 不足以完成"重开"——这是本类最容易被误用的地方】
        /// 本类是**纯观察者**，不持有 Combatant 引用，所以它**改不了、也不该改**
        /// 玩家身上的任何东西。调用方在调完 Reset() 之后**必须自己补做**：
        ///   1. <c>Player.HpMax = ProgressionCurve.HpMaxAt(1)</c>  // 回 260
        ///   2. <c>Player.Hp    = ProgressionCurve.HpMaxAt(1)</c>  // 且 HpMax 必须先赋值
        ///   3. <c>RefreshSkillRawFromLevel()</c>                  // 技能 raw 退回基础常量
        /// 只调 Reset() 就以为万事大吉的话，会得到一个"1 级但有 494 血、21 攻"的玩家。
        ///
        /// 【为什么不清空 LeveledUp / ExpGained 的订阅者】
        /// 订阅者是上层（HUD）在装配阶段挂上的，生命周期跟随**场景**；
        /// 而 Reset 的生命周期跟随**一局**。在这里顺手清订阅，等于每次重开
        /// 都要求上层重新挂一遍回调，漏挂一次就是"第二局升级了没反应"这种
        /// 极难复现的 bug。谁订阅谁负责退订，是本项目统一的约定（同 RunPhase.cs）。
        ///
        /// 【为什么不抛任何事件】
        /// Reset 一定是上层主动调的 —— 它自己就是那个"要重开"的人，
        /// 不需要内核再通知它一遍。反过来，在清场/析构路径上抛事件，
        /// 很容易让还没来得及退订的 UI 在被销毁的过程中收到回调而崩溃。
        /// </summary>
        public void Reset()
        {
            _level = 1;
            _expInLevel = 0;
            _expTotal = 0;
            _credited.Clear();
        }
    }
}
