// -----------------------------------------------------------------------------
// ProgressionTests.cs —— P1-6「玩家成长曲线」纯逻辑层的 NUnit 验收套件
//
// 【为什么新开一个测试文件，而不是往 CombatKernelTests.cs 里加】
// 同目录的 CombatKernelTests.cs 是**红线文件**：它锁着 T1 的 88 条既有断言，
// 一个字都不许动。新功能的断言另起一个文件，QA 在 diff 里就能一眼看清
// "旧断言零改动、新断言全新增"。这条规矩沿用 RunPhaseTests.cs 的先例。
//
// 【本文件当前的验证状态 —— 请如实理解】
// 编写环境**没有 Unity、也没有 dotnet**，因此本文件**未经编译、未经运行**。
// 它的正确性目前只由"人工比对 Progression.cs 源码签名"保证。
// 真正的验证请在本地 Unity 编辑器里跑：
//     Window → General → Test Runner → EditMode → Run All。
// 与它配套的静态护栏（Tests/t1_selfcheck.py、Tests/t3_selfcheck.py）在无 Unity
// 环境可以跑，但它们只覆盖"数值基线没被改动 + 语法/类型解析健全"，
// **不覆盖**本文件的任何一条成长断言。
//
// 【它顺带证明了 AC-28】
// 本文件从头到尾**没有构造过 Encounter，也没有构造过 Combatant**：
// PlayerProgression 是 new 出来就能测的。这就是"可脱离 Encounter 单测"，
// 与 RunPhase.Evaluate 三参数重载是同一个范式。
//
// 【覆盖的用例 —— 对应 PRD 验收编号】
//   PG-01  AC-01  1 级血上限 == 260.0f（严格相等）
//   PG-02  AC-02  1 级 AtkBonus == 0.0f（严格相等）
//   PG-03  AC-03  1→10 级血上限逐级钉死
//   PG-04  AC-04  1→10 级 AtkBonus 逐级钉死（0..9）
//   PG-05  AC-05  1→9 级"升至下一级所需"逐级钉死
//   PG-06  AC-06  累计经验逐级钉死（0..790）
//   PG-07  AC-07  表长 == 10，且越界索引有防护
//   PG-08  AC-08  初始状态：1 级 / 0 经验
//   PG-09  AC-09  +19 → 仍 1 级，进度 19
//   PG-10  AC-10  +20 → 2 级，进度 0
//   PG-11  AC-11  +21 → 2 级，进度 1（溢出保留）
//   PG-12  AC-12  1 级一次 +50 → 3 级，余 5（连升两级）
//   PG-13  AC-13  连升 N 级时升级事件恰好 N 次
//   PG-14  AC-14  1 级一次 +790 → 10 级封顶
//   PG-15  AC-15  已 10 级再吃经验 → 等级不变、累计不增
//   PG-16  AC-16  +0 / 负数 → 状态完全不变
//   PG-17  AC-29  Reset 后 1 级 / 0 进度 / 0 累计
//   PG-18  AC-30  10 级 Reset → 血上限回 260、AtkBonus 回 0
//   PG-19  AC-31  重开后状态与首次开局严格相等
//   PG-20  AC-35  同一 sourceId 重复入账只结算一次（幂等）
//   PG-21  ——     LevelUpInfo 载荷字段逐项正确（Delta / New 都对）
//   PG-22  ——     ExpGained 载荷是"实际入账量"，入账 0 时不抛
//   PG-23  ——     Reset() 不清订阅者（沿用 RunPhase 约定）
//   PG-24  ——     封顶时溢出被丢弃，ExpInLevel 归零、ExpTotal 不含残渣
//   PG-25  AC-21  BASE_HP_MAX 与 HpMaxAt(1) 自洽（跨 asmdef 断言的本侧一半）
//   PG-26  AC-33/34  精英/BOSS 的 ExpValue 原样入账，不做二次相乘
//
// 【禁止事项】不得引用 UnityEngine。本文件所在 asmdef 设了 noEngineReferences=true，
// 只允许 BCL + NUnit。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using NUnit.Framework;

namespace Xianxia.Combat.Tests
{
    /// <summary>
    /// P1-6 玩家成长曲线验收（纯逻辑层）。
    ///
    /// 【新手解释】每个带 [Test] 的方法都是一条独立的检查。
    /// NUnit 会各自新建一个 <see cref="ProgressionTests"/> 实例来跑，
    /// 所以方法之间不共享状态，不用担心互相污染。
    /// </summary>
    [TestFixture]
    public class ProgressionTests
    {
        // =====================================================================
        // 测试辅助
        // =====================================================================

        /// <summary>
        /// 造一个全新的 1 级成长状态机。
        /// 注意这里**不需要任何参数**——没有 Encounter、没有 Combatant，
        /// 这本身就是 AC-28「可脱离 Encounter 单测」的直接证据。
        /// </summary>
        private static PlayerProgression MakeProgression()
        {
            return new PlayerProgression();
        }

        /// <summary>
        /// 挂一个升级事件收集器，返回按发生顺序记录的载荷列表。
        /// 用列表而不是计数器，是为了让 PG-13 既能验"次数"，也能验"每次的内容"。
        /// </summary>
        private static List<LevelUpInfo> RecordLevelUps(PlayerProgression p)
        {
            List<LevelUpInfo> log = new List<LevelUpInfo>();
            p.LeveledUp += delegate (LevelUpInfo info) { log.Add(info); };
            return log;
        }

        /// <summary>挂一个经验入账收集器，记录每次 ExpGained 的载荷。</summary>
        private static List<int> RecordExpGains(PlayerProgression p)
        {
            List<int> log = new List<int>();
            p.ExpGained += delegate (int amount) { log.Add(amount); };
            return log;
        }

        // =====================================================================
        // 一、曲线表（AC-01 ~ AC-07）
        // =====================================================================

        /// <summary>PG-01 / AC-01：1 级血上限严格等于 260.0f。</summary>
        [Test]
        public void PG01_HpMaxAtLevel1_IsExactly260()
        {
            // 用 EqualTo 而非 Within：这是数值红线，必须严格位等价。
            Assert.That(ProgressionCurve.HpMaxAt(1), Is.EqualTo(260.0f));
        }

        /// <summary>PG-02 / AC-02：1 级攻击加成严格等于 0.0f（保证 raw = 12 + 0 ≡ 12）。</summary>
        [Test]
        public void PG02_AtkBonusAtLevel1_IsExactlyZero()
        {
            Assert.That(ProgressionCurve.AtkBonusAt(1), Is.EqualTo(0.0f));
        }

        /// <summary>PG-03 / AC-03：1→10 级血上限逐级钉死。</summary>
        [Test]
        public void PG03_HpMaxCurve_MatchesTableExactly()
        {
            float[] expected =
            {
                260.0f, 286.0f, 312.0f, 338.0f, 364.0f,
                390.0f, 416.0f, 442.0f, 468.0f, 494.0f
            };

            for (int lv = 1; lv <= ProgressionCurve.MAX_LEVEL; lv++)
            {
                Assert.That(
                    ProgressionCurve.HpMaxAt(lv),
                    Is.EqualTo(expected[lv - 1]),
                    "等级 " + lv + " 的血上限不符");
            }
        }

        /// <summary>PG-04 / AC-04：1→10 级攻击加成逐级钉死（0..9，加法非乘法）。</summary>
        [Test]
        public void PG04_AtkBonusCurve_MatchesTableExactly()
        {
            for (int lv = 1; lv <= ProgressionCurve.MAX_LEVEL; lv++)
            {
                Assert.That(
                    ProgressionCurve.AtkBonusAt(lv),
                    Is.EqualTo((float)(lv - 1)),
                    "等级 " + lv + " 的攻击加成不符");
            }
        }

        /// <summary>PG-05 / AC-05：1→9 级「升至下一级所需」逐级钉死；10 级为 0（封顶）。</summary>
        [Test]
        public void PG05_ExpToNextCurve_MatchesTableExactly()
        {
            int[] expected = { 20, 25, 35, 50, 65, 90, 120, 165, 220 };

            for (int lv = 1; lv <= 9; lv++)
            {
                Assert.That(
                    ProgressionCurve.ExpToNext(lv),
                    Is.EqualTo(expected[lv - 1]),
                    "等级 " + lv + " 的升级所需经验不符");
            }

            Assert.That(ProgressionCurve.ExpToNext(10), Is.EqualTo(0), "10 级应为封顶，返回 0");
        }

        /// <summary>PG-06 / AC-06：达到各级的累计经验逐级钉死。</summary>
        [Test]
        public void PG06_CumulativeExpCurve_MatchesTableExactly()
        {
            int[] expected = { 0, 20, 45, 80, 130, 195, 285, 405, 570, 790 };

            for (int lv = 1; lv <= ProgressionCurve.MAX_LEVEL; lv++)
            {
                Assert.That(
                    ProgressionCurve.CumulativeExpAt(lv),
                    Is.EqualTo(expected[lv - 1]),
                    "达到等级 " + lv + " 的累计经验不符");
            }
        }

        /// <summary>
        /// PG-07 / AC-07：两张表长度都等于等级上限，且任何越界输入都被夹住不抛异常。
        /// 这条是防呆：以后有人把上限改成 12 却只补了一张表，这里立刻红。
        /// </summary>
        [Test]
        public void PG07_TableLengthsMatchMaxLevel_AndOutOfRangeIsClamped()
        {
            Assert.That(ProgressionCurve.EXP_TO_NEXT.Length, Is.EqualTo(ProgressionCurve.MAX_LEVEL));
            Assert.That(ProgressionCurve.EXP_CUMULATIVE.Length, Is.EqualTo(ProgressionCurve.MAX_LEVEL));

            // 下越界
            Assert.That(ProgressionCurve.Clamp(0), Is.EqualTo(1));
            Assert.That(ProgressionCurve.Clamp(-999), Is.EqualTo(1));
            Assert.That(ProgressionCurve.HpMaxAt(-999), Is.EqualTo(260.0f));
            Assert.That(ProgressionCurve.AtkBonusAt(0), Is.EqualTo(0.0f));
            Assert.That(ProgressionCurve.CumulativeExpAt(-5), Is.EqualTo(0));

            // 上越界
            Assert.That(ProgressionCurve.Clamp(11), Is.EqualTo(10));
            Assert.That(ProgressionCurve.Clamp(999), Is.EqualTo(10));
            Assert.That(ProgressionCurve.HpMaxAt(999), Is.EqualTo(494.0f));
            Assert.That(ProgressionCurve.AtkBonusAt(999), Is.EqualTo(9.0f));
            Assert.That(ProgressionCurve.ExpToNext(999), Is.EqualTo(0));
            Assert.That(ProgressionCurve.CumulativeExpAt(999), Is.EqualTo(790));
        }

        // =====================================================================
        // 二、升级判定（AC-08 ~ AC-16）
        // =====================================================================

        /// <summary>PG-08 / AC-08：初始状态 1 级、0 经验、0 累计、非满级。</summary>
        [Test]
        public void PG08_InitialState_IsLevel1WithZeroExp()
        {
            PlayerProgression p = MakeProgression();

            Assert.That(p.Level, Is.EqualTo(1));
            Assert.That(p.ExpInLevel, Is.EqualTo(0));
            Assert.That(p.ExpTotal, Is.EqualTo(0));
            Assert.That(p.ExpToNext, Is.EqualTo(20));
            Assert.That(p.IsMaxLevel, Is.False);
            Assert.That(p.HpMax, Is.EqualTo(260.0f));
            Assert.That(p.AtkBonus, Is.EqualTo(0.0f));
        }

        /// <summary>PG-09 / AC-09：+19 差一点，不升级。</summary>
        [Test]
        public void PG09_Gain19_StaysLevel1()
        {
            PlayerProgression p = MakeProgression();
            int credited = p.GainExp(19);

            Assert.That(credited, Is.EqualTo(19));
            Assert.That(p.Level, Is.EqualTo(1));
            Assert.That(p.ExpInLevel, Is.EqualTo(19));
            Assert.That(p.ExpTotal, Is.EqualTo(19));
        }

        /// <summary>PG-10 / AC-10：+20 刚好升级，进度归零。</summary>
        [Test]
        public void PG10_Gain20_LevelsUpToTwoWithZeroRemainder()
        {
            PlayerProgression p = MakeProgression();
            p.GainExp(20);

            Assert.That(p.Level, Is.EqualTo(2));
            Assert.That(p.ExpInLevel, Is.EqualTo(0));
            Assert.That(p.ExpTotal, Is.EqualTo(20));
            Assert.That(p.ExpToNext, Is.EqualTo(25), "2 级升 3 级需要 25");
            Assert.That(p.HpMax, Is.EqualTo(286.0f));
            Assert.That(p.AtkBonus, Is.EqualTo(1.0f));
        }

        /// <summary>PG-11 / AC-11：+21 升级后余 1，溢出经验必须保留。</summary>
        [Test]
        public void PG11_Gain21_KeepsOverflowExp()
        {
            PlayerProgression p = MakeProgression();
            p.GainExp(21);

            Assert.That(p.Level, Is.EqualTo(2));
            Assert.That(p.ExpInLevel, Is.EqualTo(1), "溢出的 1 点必须计入 2 级进度");
            Assert.That(p.ExpTotal, Is.EqualTo(21));
        }

        /// <summary>PG-12 / AC-12：1 级一口气 +50 → 直接 3 级，余 5。20 + 25 = 45，剩 5。</summary>
        [Test]
        public void PG12_Gain50AtLevel1_JumpsToLevel3WithRemainder5()
        {
            PlayerProgression p = MakeProgression();
            int credited = p.GainExp(50);

            Assert.That(p.Level, Is.EqualTo(3));
            Assert.That(p.ExpInLevel, Is.EqualTo(5));
            Assert.That(p.ExpTotal, Is.EqualTo(50));
            Assert.That(credited, Is.EqualTo(50));
        }

        /// <summary>
        /// PG-13 / AC-13：连升 N 级必须抛 N 次事件，不是 1 次。
        /// 顺带验证事件里的等级是**连续递增**的（1→2、2→3），不能出现跳号。
        /// </summary>
        [Test]
        public void PG13_MultiLevelUp_RaisesEventOncePerLevel()
        {
            PlayerProgression p = MakeProgression();
            List<LevelUpInfo> log = RecordLevelUps(p);

            p.GainExp(50); // 1 → 3，跨两级

            Assert.That(log.Count, Is.EqualTo(2), "连升两级必须抛两次事件");

            Assert.That(log[0].PrevLevel, Is.EqualTo(1));
            Assert.That(log[0].NewLevel, Is.EqualTo(2));
            Assert.That(log[1].PrevLevel, Is.EqualTo(2));
            Assert.That(log[1].NewLevel, Is.EqualTo(3));
        }

        /// <summary>PG-14 / AC-14：1 级一次吃满 790 → 正好 10 级封顶，进度归零。</summary>
        [Test]
        public void PG14_Gain790AtLevel1_ReachesMaxLevel()
        {
            PlayerProgression p = MakeProgression();
            List<LevelUpInfo> log = RecordLevelUps(p);

            int credited = p.GainExp(790);

            Assert.That(p.Level, Is.EqualTo(10));
            Assert.That(p.IsMaxLevel, Is.True);
            Assert.That(p.ExpInLevel, Is.EqualTo(0));
            Assert.That(p.ExpTotal, Is.EqualTo(790));
            Assert.That(credited, Is.EqualTo(790));
            Assert.That(log.Count, Is.EqualTo(9), "1→10 共升 9 级");
            Assert.That(p.HpMax, Is.EqualTo(494.0f));
            Assert.That(p.AtkBonus, Is.EqualTo(9.0f));
        }

        /// <summary>PG-15 / AC-15：已封顶后再吃经验，等级不变、累计不增、不抛事件。</summary>
        [Test]
        public void PG15_GainAfterMaxLevel_ChangesNothing()
        {
            PlayerProgression p = MakeProgression();
            p.GainExp(790);

            List<LevelUpInfo> levelLog = RecordLevelUps(p);
            List<int> expLog = RecordExpGains(p);

            int credited = p.GainExp(9999);

            Assert.That(credited, Is.EqualTo(0));
            Assert.That(p.Level, Is.EqualTo(10));
            Assert.That(p.ExpTotal, Is.EqualTo(790), "封顶后累计经验必须停止增长");
            Assert.That(p.ExpInLevel, Is.EqualTo(0));
            Assert.That(levelLog.Count, Is.EqualTo(0));
            Assert.That(expLog.Count, Is.EqualTo(0));
        }

        /// <summary>PG-16 / AC-16：0 与负数完全无副作用（防御性）。</summary>
        [Test]
        public void PG16_GainZeroOrNegative_IsNoOp()
        {
            PlayerProgression p = MakeProgression();
            p.GainExp(19);

            List<LevelUpInfo> levelLog = RecordLevelUps(p);
            List<int> expLog = RecordExpGains(p);

            Assert.That(p.GainExp(0), Is.EqualTo(0));
            Assert.That(p.GainExp(-100), Is.EqualTo(0));

            Assert.That(p.Level, Is.EqualTo(1));
            Assert.That(p.ExpInLevel, Is.EqualTo(19), "状态必须一个字节都不变");
            Assert.That(p.ExpTotal, Is.EqualTo(19));
            Assert.That(levelLog.Count, Is.EqualTo(0));
            Assert.That(expLog.Count, Is.EqualTo(0), "入账 0 时不得抛 ExpGained");
        }

        // =====================================================================
        // 三、重开与幂等（AC-29 ~ AC-31、AC-35）
        // =====================================================================

        /// <summary>PG-17 / AC-29：Reset 后回到 1 级、0 进度、0 累计。</summary>
        [Test]
        public void PG17_Reset_ClearsLevelAndExp()
        {
            PlayerProgression p = MakeProgression();
            p.GainExp(200);

            p.Reset();

            Assert.That(p.Level, Is.EqualTo(1));
            Assert.That(p.ExpInLevel, Is.EqualTo(0));
            Assert.That(p.ExpTotal, Is.EqualTo(0));
            Assert.That(p.IsMaxLevel, Is.False);
        }

        /// <summary>PG-18 / AC-30：10 级时 Reset，派生属性同步回落到 1 级基准。</summary>
        [Test]
        public void PG18_ResetAtMaxLevel_RestoresBaseStats()
        {
            PlayerProgression p = MakeProgression();
            p.GainExp(790);
            Assert.That(p.IsMaxLevel, Is.True, "前置条件：先到 10 级");

            p.Reset();

            Assert.That(p.HpMax, Is.EqualTo(260.0f));
            Assert.That(p.AtkBonus, Is.EqualTo(0.0f));
            Assert.That(p.ExpToNext, Is.EqualTo(20));
        }

        /// <summary>
        /// PG-19 / AC-31：重开一局后的状态与首次开局**严格相等**。
        /// 这条保证每局开局都等价于基线，是"局内成长不外溢"的核心。
        /// </summary>
        [Test]
        public void PG19_ResetProducesStateIdenticalToFreshInstance()
        {
            PlayerProgression fresh = MakeProgression();

            PlayerProgression reused = MakeProgression();
            reused.GainExpFrom(1, 500.0f);
            reused.GainExpFrom(2, 500.0f);
            reused.Reset();

            Assert.That(reused.Level, Is.EqualTo(fresh.Level));
            Assert.That(reused.ExpInLevel, Is.EqualTo(fresh.ExpInLevel));
            Assert.That(reused.ExpTotal, Is.EqualTo(fresh.ExpTotal));
            Assert.That(reused.ExpToNext, Is.EqualTo(fresh.ExpToNext));
            Assert.That(reused.HpMax, Is.EqualTo(fresh.HpMax));
            Assert.That(reused.AtkBonus, Is.EqualTo(fresh.AtkBonus));
            Assert.That(reused.IsMaxLevel, Is.EqualTo(fresh.IsMaxLevel));

            // 幂等集合也必须清空，否则第二局杀同一个 id 的怪会拿不到经验。
            int credited = reused.GainExpFrom(1, 500.0f);
            Assert.That(credited, Is.GreaterThan(0), "Reset 必须清空幂等集合");
        }

        /// <summary>
        /// PG-20 / AC-35：同一 sourceId 重复入账只结算一次。
        /// 对应"同一只怪的死亡事件被重复广播"的现实场景。
        /// </summary>
        [Test]
        public void PG20_GainExpFrom_IsIdempotentPerSourceId()
        {
            PlayerProgression p = MakeProgression();

            int first = p.GainExpFrom(42, 15.0f);
            int second = p.GainExpFrom(42, 15.0f);
            int third = p.GainExpFrom(42, 999.0f);

            Assert.That(first, Is.EqualTo(15));
            Assert.That(second, Is.EqualTo(0), "同一 id 第二次必须返回 0");
            Assert.That(third, Is.EqualTo(0), "同一 id 即使经验值不同也不再结算");
            Assert.That(p.ExpTotal, Is.EqualTo(15));

            // 不同 id 正常入账，证明去重是按 id 而不是"只收一次"。
            int other = p.GainExpFrom(43, 5.0f);
            Assert.That(other, Is.EqualTo(5));
            Assert.That(p.ExpTotal, Is.EqualTo(20));
            Assert.That(p.Level, Is.EqualTo(2));
        }

        // =====================================================================
        // 四、事件载荷与边界（无 AC 编号，但都是回归风险点）
        // =====================================================================

        /// <summary>
        /// PG-21：LevelUpInfo 的六个字段逐项正确。
        /// 特别是 DeltaHpMax —— CombatBridge 靠它做 <c>Hp += ΔHpMax</c>，
        /// 这个数错了就会出现"升级掉血"或"升级白送血"。
        /// </summary>
        [Test]
        public void PG21_LevelUpInfoPayload_IsCorrectPerLevel()
        {
            PlayerProgression p = MakeProgression();
            List<LevelUpInfo> log = RecordLevelUps(p);

            p.GainExp(45); // 1 → 3（20 + 25）

            Assert.That(log.Count, Is.EqualTo(2));

            LevelUpInfo a = log[0];
            Assert.That(a.PrevLevel, Is.EqualTo(1));
            Assert.That(a.NewLevel, Is.EqualTo(2));
            Assert.That(a.DeltaHpMax, Is.EqualTo(26.0f));
            Assert.That(a.NewHpMax, Is.EqualTo(286.0f));
            Assert.That(a.DeltaAtkBonus, Is.EqualTo(1.0f));
            Assert.That(a.NewAtkBonus, Is.EqualTo(1.0f));

            LevelUpInfo b = log[1];
            Assert.That(b.PrevLevel, Is.EqualTo(2));
            Assert.That(b.NewLevel, Is.EqualTo(3));
            Assert.That(b.DeltaHpMax, Is.EqualTo(26.0f));
            Assert.That(b.NewHpMax, Is.EqualTo(312.0f));
            Assert.That(b.DeltaAtkBonus, Is.EqualTo(1.0f));
            Assert.That(b.NewAtkBonus, Is.EqualTo(2.0f));
        }

        /// <summary>
        /// PG-22：ExpGained 的载荷是**实际入账量**，且每次 GainExp 至多抛一次。
        /// 连升多级也只抛一次经验事件（经验是一整笔，等级才是一级一次）。
        /// </summary>
        [Test]
        public void PG22_ExpGainedCarriesActualCreditedAmount()
        {
            PlayerProgression p = MakeProgression();
            List<int> log = RecordExpGains(p);

            p.GainExp(50);

            Assert.That(log.Count, Is.EqualTo(1));
            Assert.That(log[0], Is.EqualTo(50));

            // 重复 id 不抛
            p.GainExpFrom(7, 10.0f);
            p.GainExpFrom(7, 10.0f);
            Assert.That(log.Count, Is.EqualTo(2), "重复 id 不得抛第二次");
        }

        /// <summary>
        /// PG-23：Reset() 不清订阅者 —— 沿用 RunPhase 的全项目统一约定。
        /// 如果哪天有人"顺手"在 Reset 里清了订阅，这条会红，
        /// 提醒他那会造成"第二局升级了 HUD 没反应"这种极难复现的 bug。
        /// </summary>
        [Test]
        public void PG23_ResetDoesNotClearSubscribers()
        {
            PlayerProgression p = MakeProgression();
            List<LevelUpInfo> log = RecordLevelUps(p);

            p.GainExp(20);
            Assert.That(log.Count, Is.EqualTo(1));

            p.Reset();
            Assert.That(log.Count, Is.EqualTo(1), "Reset 本身不得抛事件");

            p.GainExp(20);
            Assert.That(log.Count, Is.EqualTo(2), "Reset 之后订阅必须依然有效");
        }

        /// <summary>
        /// PG-24：一次吃下远超封顶的经验时，余数被丢弃且不计入累计。
        /// 契约表规定 ExpInLevel ∈ [0, ExpToNext)，封顶时 ExpToNext = 0，
        /// 该区间为空 ⇒ 封顶时 ExpInLevel 只能是 0，不允许挂残渣。
        /// </summary>
        [Test]
        public void PG24_OverflowBeyondMaxLevel_IsDiscarded()
        {
            PlayerProgression p = MakeProgression();
            int credited = p.GainExp(1000);

            Assert.That(p.Level, Is.EqualTo(10));
            Assert.That(p.ExpInLevel, Is.EqualTo(0), "封顶后不得挂着用不掉的余数");
            Assert.That(p.ExpTotal, Is.EqualTo(790), "累计只统计真正入账的部分");
            Assert.That(credited, Is.EqualTo(790), "返回值必须是实际入账量而非传入量");
            Assert.That(p.ExpToNext, Is.EqualTo(0));
        }

        /// <summary>
        /// PG-25 / AC-21（本侧一半）：BASE_HP_MAX 与 HpMaxAt(1) 自洽。
        /// 另一半（与 CombatBridge.PlayerHpMax 跨程序集比对）在
        /// P1_6_ProgressionIntegrationTests 里 —— 那里能同时看到两个程序集，
        /// 这里看不到 Unity 层，所以只能先把本侧钉死。
        /// </summary>
        [Test]
        public void PG25_BaseHpMaxIsSelfConsistent()
        {
            Assert.That(ProgressionCurve.BASE_HP_MAX, Is.EqualTo(260.0f));
            Assert.That(ProgressionCurve.HpMaxAt(1), Is.EqualTo(ProgressionCurve.BASE_HP_MAX));
            Assert.That(ProgressionCurve.HP_PER_LEVEL, Is.EqualTo(26.0f));
            Assert.That(ProgressionCurve.ATK_PER_LEVEL, Is.EqualTo(1.0f));
            Assert.That(ProgressionCurve.MAX_LEVEL, Is.EqualTo(10));
        }

        /// <summary>
        /// PG-26 / AC-33 + AC-34：精英与 BOSS 的 ExpValue **原样入账**。
        /// 敌人出厂时（DifficultyBridge）已经乘过 ×2.0 / ×3.0，
        /// 成长系统绝不能再乘一次 —— 那会让精英给出 4 倍经验。
        /// 这里用"传进来多少就入账多少"来锁死"没有二次相乘"。
        /// </summary>
        [Test]
        public void PG26_ExpValueIsCreditedVerbatim_NoSecondMultiplier()
        {
            // 普通怪 10 → 入账 10
            PlayerProgression normal = MakeProgression();
            Assert.That(normal.GainExpFrom(1, 10.0f), Is.EqualTo(10));

            // 精英（出厂已 ×2.0）20 → 入账 20，不是 40
            PlayerProgression elite = MakeProgression();
            Assert.That(elite.GainExpFrom(1, 20.0f), Is.EqualTo(20));

            // BOSS（出厂已 ×3.0）30 → 入账 30，不是 90
            PlayerProgression boss = MakeProgression();
            Assert.That(boss.GainExpFrom(1, 30.0f), Is.EqualTo(30));

            // 浮点尾差按截断处理，方向保守（宁少勿多）。
            //
            // 🚨 这里的字面量必须挑一个**在 float32 下真的小于 35** 的数。
            // 原先写的 34.999999f 是个陷阱：float 在 [32,64) 区间的 ULP 是
            // 2^-18 ≈ 3.8147e-6，而 34.999999 距 35.0 只有 1e-6 < 半个 ULP，
            // 于是它会被编译器**舍入成精确的 35.0f**，(int) 截断得 35 而不是 34。
            // 那样这条断言测的就不是"截断"，而是一个必然失败的舍入误解。
            // 34.99f 落在 34.99000167f，稳定小于 35，才真正压住截断行为。
            PlayerProgression frac = MakeProgression();
            Assert.That(frac.GainExpFrom(1, 34.99f), Is.EqualTo(34));
        }
    }
}
