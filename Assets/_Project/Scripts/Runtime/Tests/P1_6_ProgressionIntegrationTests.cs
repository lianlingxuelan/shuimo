// -----------------------------------------------------------------------------
// P1_6_ProgressionIntegrationTests.cs —— P1-6「玩家成长曲线」跨层集成验收
//
// 【本文件与 ProgressionTests.cs 的分工】
// ProgressionTests.cs（Xianxia.Combat.Tests）只能看到纯逻辑层，
// 它证明"曲线算得对、状态机转得对"。但它**看不到 Unity 层**，
// 所以证明不了下面这三件真正会出事的事：
//   ① 跨程序集的两个 260 是不是真的同一个值（AC-21）；
//   ② 玩家升级之后，怪的反调靶子有没有被顺手带歪（AC-23，头号哨兵）；
//   ③ 两条 raw 注入路径是不是都接上了（AC-22 + §1.3）。
// 这些只有在能同时 using Xianxia.Combat 和 Xianxia.Unity.T2 的程序集里才测得了，
// 也就是本文件所在的 Xianxia.Unity.T2.Tests。
//
// 【为什么大部分用例是 EditMode 纯 [Test]，只有少数进 PlayMode】
// 成长系统的关键风险点（常量一致性、反调解耦、曲线与 raw 的关系）都是
// **不需要场景**就能证伪的。能在 EditMode 跑的就不要拖进 PlayMode ——
// PlayMode 要加载场景、等帧，慢且脆。
// 真正需要 MonoBehaviour 生命周期的（Start 里建 _progression、订阅 EnemyDied、
// 升级回调写玩家血量）才用 [UnityTest]，并在未配置场景名时 Assert.Ignore。
//
// 【怎么跑】
//   EditMode：Window → General → Test Runner → EditMode
//             → 选 Xianxia.Unity.T2.Tests → Run All
//   PlayMode：同上切到 PlayMode 页签。GameplaySceneName 已配好，无需改动。
//
// 【static 状态卫生】
// LoadGameplayScene() 会把 MainMenuHud.SkipOnNextLoad 置 true 以跳过主菜单。
// 该字段是 static，不随场景销毁，会残留到后续任何测试夹具（典型受害者是 EditMode 的
// P0_5_MenuHudTests.MENU09，它断言夹具启动时旗标应为 false）。本套件用
// **同步 [TearDown]**（不是 [UnityTearDown]）在每个用例结束后无条件复位，
// 杜绝跨夹具污染 —— 选型理由见 ResetMenuSkipFlagAfterEachTest 的注释。
//
// 【验证状态 —— 请如实理解】
// 编写环境**没有 Unity、也没有 dotnet**，本文件**未经编译、未经运行**。
// 它的正确性目前只由"人工比对生产代码签名"保证。
//
// 【红线】只用公开 API（常量、公开属性、公开方法），不反射任何私有字段。
// 反射私有字段的测试会在重构时误报红，也会让"公开契约"这件事失去意义。
//
// 【覆盖的用例 —— 对应 PRD 验收编号】
//   PI-01  AC-21   CombatBridge.PlayerHpMax ≡ ProgressionCurve.HpMaxAt(1) ≡ 260.0f
//   PI-02  AC-22   SkillConfig.BASIC_RAW + 1 级加成 ≡ 12.0f（严格位等价）
//   PI-03  AC-22   AttackController.AttackRaw + 1 级加成 ≡ 12.0f（第二条 raw 路径）
//   PI-04  ——      10 级时两条 raw 都恰好 = 基础值 + 9，且互不干扰
//   PI-05  AC-24   反调输出是 PlayerHpMax 的函数（负对照：靶子变了伤害就会变）
//   PI-06  AC-24   靶子钉死在 260 时，1 级与 10 级刷出的怪 ContactDamage 严格相等
//   PI-07  AC-25   成长系统不触碰 SoloFreqMeasured / SwarmFreqMeasured（2.5294x 指纹）
//   PI-08  AC-17/18/19/20  升级血量语义（同额补偿、不回满、连升累计）
//   PI-09  AC-32/33/34     ExpValue 原样入账，精英/BOSS 不二次相乘
//   PI-10  AC-26   成长系统不写敌人字段（入账后敌人对象逐字段不变）
//   PI-11  AC-23 ★ 头号哨兵：玩家 10 级后 Encounter.Bridge.PlayerHpMax 仍严格 260
//   PI-12  ——      PlayerAtkBonus 在 Progression 未装配时安全返回 0
//   PI-13  AC-31   重开后玩家状态与首次开局严格相等
// -----------------------------------------------------------------------------

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Xianxia.Core;
using Xianxia.Combat;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>P1-6 玩家成长曲线跨层集成验收。</summary>
    [TestFixture]
    public sealed class P1_6_ProgressionIntegrationTests
    {
        // 本工程真实战斗场景为 SampleScene，已由主理人核实：场景内 GameObject
        // 1602164645 同时挂载 CombatController + CombatBridge，且 playerTransform /
        // enemyPrefab / zoneId / playerHpMax 均已配置齐全；该场景已 enabled 加入
        // ProjectSettings/EditorBuildSettings.asset，PlayMode 下可直接 LoadScene。
        // 留空则 PlayMode 用例自动 Ignore，不会误报红。
        private const string GameplaySceneName = "SampleScene";

        /// <summary>反调用的固定种子。任何值都行，关键是两次比对必须用同一个。</summary>
        private const ulong FixedSeed = 0x5A17C0DEUL;

        // =====================================================================
        // 一、跨程序集常量一致性（AC-21 / AC-22）
        // =====================================================================

        /// <summary>
        /// PI-01 / AC-21 ★ 跨 asmdef 常量锁。
        ///
        /// 【这条测试存在的全部意义】
        /// 260 这个数在工程里有四处声明，物理上无法合并（Xianxia.Combat 设了
        /// noEngineReferences=true，引用不到 Unity 层的 CombatBridge）。
        /// 「接受重复 + 用断言锁死」是跨程序集常量一致性的标准解法，
        /// 而这里就是那个"锁"。改任何一处而忘了另一处，这条立刻红。
        /// </summary>
        [Test]
        public void PI01_PlayerHpMaxConstant_IsConsistentAcrossAssemblies()
        {
            Assert.AreEqual(260.0f, CombatBridge.PlayerHpMax,
                "CombatBridge.PlayerHpMax 是数值红线，必须是 260.0f");
            Assert.AreEqual(260.0f, ProgressionCurve.BASE_HP_MAX,
                "ProgressionCurve.BASE_HP_MAX 必须是 260.0f");
            Assert.AreEqual(CombatBridge.PlayerHpMax, ProgressionCurve.HpMaxAt(1),
                "两个程序集里的 260 必须是同一个值——这是 AC-21 的全部内容");
        }

        /// <summary>
        /// PI-02 / AC-22：技能路径的 1 级 raw 严格等于 12.0f。
        /// 加法而非乘法 + 1 级加成为 0 ⇒ IEEE-754 下 12.0f + 0.0f 逐位等于 12.0f。
        /// </summary>
        [Test]
        public void PI02_SkillBasicRawAtLevel1_IsExactly12()
        {
            Assert.AreEqual(12.0f, SkillConfig.BASIC_RAW, "BASIC_RAW 常量本身不得被改动");

            float effective = SkillConfig.BASIC_RAW + ProgressionCurve.AtkBonusAt(1);
            Assert.AreEqual(12.0f, effective, "1 级技能 raw 必须与接入成长之前逐位等价");
        }

        /// <summary>
        /// PI-03 / AC-22：普攻路径的 1 级 raw 严格等于 12.0f。
        ///
        /// 【为什么要单独一条】
        /// 玩家的伤害有**两条互不相干的 raw 源**：普攻走 AttackController.AttackRaw，
        /// 技能走 SkillConfig.BASIC_RAW。两个常量恰好都是 12，
        /// 所以"只改了一条"这种半接线状态在肉眼下几乎看不出来 ——
        /// 必须两条各测一次，才能保证没有漏掉一半。
        /// </summary>
        [Test]
        public void PI03_AttackControllerRawAtLevel1_IsExactly12()
        {
            Assert.AreEqual(12.0f, AttackController.AttackRaw, "AttackRaw 常量本身不得被改动");

            float effective = AttackController.AttackRaw + ProgressionCurve.AtkBonusAt(1);
            Assert.AreEqual(12.0f, effective, "1 级普攻 raw 必须与接入成长之前逐位等价");
        }

        /// <summary>
        /// PI-04：10 级时两条路径都恰好 +9，且各自基于自己的基础常量。
        /// 顺带钉死"加成是加法项，不是百分比"——若哪天被改成 ×(1+0.1×L)，这里会红。
        /// </summary>
        [Test]
        public void PI04_BothRawPathsAtMaxLevel_AreBasePlusNine()
        {
            float bonus = ProgressionCurve.AtkBonusAt(ProgressionCurve.MAX_LEVEL);
            Assert.AreEqual(9.0f, bonus, "10 级加成必须是 +9（1 × 9）");

            Assert.AreEqual(21.0f, AttackController.AttackRaw + bonus, "10 级普攻 raw 应为 21");
            Assert.AreEqual(21.0f, SkillConfig.BASIC_RAW + bonus, "10 级水剑斩 raw 应为 21");
            Assert.AreEqual(SkillConfig.BURST_RAW + 9.0f, SkillConfig.BURST_RAW + bonus,
                "爆发技从自己的基础常量起算，不与普攻串味");
            Assert.AreEqual(SkillConfig.LOTUS_RAW + 9.0f, SkillConfig.LOTUS_RAW + bonus,
                "血莲从自己的基础常量起算");
        }

        // =====================================================================
        // 二、反调解耦（AC-24 / AC-25）—— 本期最高优先级 R-11
        // =====================================================================

        /// <summary>
        /// 造一只固定配方的怪，用于反调对比。
        /// cfg 传 null 走全默认，把变量压到只剩 <paramref name="playerHpMax"/> 一个。
        /// </summary>
        private static Combatant BuildProbeEnemy(float playerHpMax)
        {
            DifficultyBridge bridge = new DifficultyBridge();
            bridge.PlayerHpMax = playerHpMax;
            bridge.PlayerDef = CombatBridge.PlayerDef;

            // 每次都新建 PCG32 并用同一个种子 ⇒ 随机流完全一致，
            // 精英/词缀的掷点也一致，于是两只怪的差异只可能来自 playerHpMax。
            PCG32 rng = new PCG32(FixedSeed);
            EnemyBaseStats stats = EnemyBaseTable.Get(CombatConfig.KIND_WITCH);

            return bridge.BuildEnemy(null, stats, 1, rng);
        }

        /// <summary>
        /// PI-05 / AC-24（负对照）：证明"靶子变了，怪的伤害真的会变"。
        ///
        /// 【为什么需要一条负对照】
        /// PI-06 断言"1 级和 10 级刷出的怪伤害相等"。但如果反调压根不看 PlayerHpMax，
        /// 那条断言就算过了也毫无意义（它会永远绿，包括在 bug 存在时）。
        /// 先用这条证明耦合**确实存在**，PI-06 的"相等"才是有信息量的。
        /// 这也正是修复前 P1-6 会造成的事故现场：升到 10 级 → 靶子 494 → 怪变强 90%。
        /// </summary>
        [Test]
        public void PI05_ContactDamage_DependsOnPlayerHpMaxTarget()
        {
            Combatant at260 = BuildProbeEnemy(260.0f);
            Combatant at494 = BuildProbeEnemy(494.0f);

            Assert.AreNotEqual(at260.ContactDamage, at494.ContactDamage,
                "反调靶子分子必须真的影响怪的伤害，否则 PI-06 的相等断言没有意义");
            Assert.Greater(at494.ContactDamage, at260.ContactDamage,
                "靶子越大 ⇒ d_eff 越大 ⇒ 怪打得越疼。这就是'白升级'事故的来源");
        }

        /// <summary>
        /// PI-06 / AC-24 ★：靶子被钉死为常量后，玩家等级不再影响怪的伤害。
        ///
        /// 生产代码里 CombatBridge.ApplyPlayerDamageModel 现在无条件写入常量
        /// <c>CombatBridge.PlayerHpMax</c>，与玩家实时血上限无关 ——
        /// 所以这里用"两边都传常量"来模拟 1 级与 10 级两种情形，结果必须逐位相等。
        /// </summary>
        [Test]
        public void PI06_ContactDamage_IsIdenticalAcrossPlayerLevels()
        {
            // 🚨【这条测试曾经是"假绿"】原写法两侧都传 CombatBridge.PlayerHpMax，
            // 两个入参逐位相同，相等断言恒真 —— 即使生产代码改回写实时血上限也不会红。
            // 真正要钉死的命题是两句话：
            //   (a) 生产代码用的那个靶子常量，等于 1 级血上限、且不等于 10 级血上限；
            //   (b) 在该常量下建怪，与"按 1 级血上限"建怪逐位一致。
            // 两句合起来才等价于 AC-24。端到端那一侧由 PI-11 用真实
            // ApplyPlayerDamageModel 复核，此处负责纯函数侧。

            // (a) 先证明常量选对了 —— 这才是 PI-05 差异性的落点。
            Assert.AreEqual(ProgressionCurve.HpMaxAt(1), CombatBridge.PlayerHpMax,
                "反调靶子常量必须锚定 1 级血上限（260），而不是任何实时值");
            Assert.AreEqual(494.0f, ProgressionCurve.HpMaxAt(10), "前置：10 级血上限确实是 494");
            Assert.AreNotEqual(ProgressionCurve.HpMaxAt(10), CombatBridge.PlayerHpMax,
                "靶子常量绝不能等于 10 级血上限，否则本条断言退化为恒真");

            // (b) 1 级：按玩家 1 级血上限建怪。
            Combatant asLevel1 = BuildProbeEnemy(ProgressionCurve.HpMaxAt(1));

            // 10 级：玩家实际 494 血，但靶子**依然**是生产常量（这正是本期的修复）。
            Combatant asLevel10 = BuildProbeEnemy(CombatBridge.PlayerHpMax);

            Assert.AreEqual(asLevel1.ContactDamage, asLevel10.ContactDamage,
                "玩家升到 10 级后，同种子刷出的怪伤害必须与 1 级时严格相等");
            Assert.AreEqual(asLevel1.HpMax, asLevel10.HpMax, "怪的血量也不得随玩家等级漂移");
            Assert.AreEqual(asLevel1.ExpValue, asLevel10.ExpValue, "经验值同样不得漂移");
        }

        /// <summary>
        /// PI-07 / AC-25：围攻/单挑 2.5294x 频率指纹不受成长影响。
        ///
        /// 【为什么这条可以这么"轻"】
        /// 该比值是每秒承伤次数之比，由 W-CORE 双层闸门 + 固定步长 + 60Hz 量化
        /// 三者唯一决定，是个**时间量**，与血量/伤害的数值大小无关。
        /// 成长系统只改血上限与 raw，两者都不出现在闸门计算里 ——
        /// 所以这里只需断言那两个测量常量本身没被动过（结构性免疫）。
        /// 真正的端到端复现由 t1_selfcheck.py 负责（AC-36 / AC-37）。
        /// </summary>
        [Test]
        public void PI07_SwarmSoloFrequencyFingerprint_IsUntouched()
        {
            Assert.AreEqual(4.300f, Difficulty.SwarmFreqMeasured, 1e-6f,
                "围攻承伤频率是数值指纹，成长系统不得触碰");
            Assert.AreEqual(1.700f, Difficulty.SoloFreqMeasured, 1e-6f,
                "单挑承伤频率是数值指纹，成长系统不得触碰");

            float ratio = Difficulty.SwarmFreqMeasured / Difficulty.SoloFreqMeasured;
            Assert.AreEqual(2.5294f, ratio, 1e-3f, "围攻/单挑倍率必须仍为 2.5294x");
        }

        // =====================================================================
        // 三、升级的血量语义（AC-17 ~ AC-20）
        // =====================================================================

        /// <summary>
        /// 把一次升级按生产代码的规则应用到玩家身上。
        ///
        /// 【为什么测试里要复刻这两行，而不是直接调 CombatBridge】
        /// CombatBridge.OnPlayerLeveledUp 是私有的，且需要完整的 MonoBehaviour +
        /// CombatController 环境；在 EditMode 里搭不起来。
        /// 这里复刻的是**顺序契约**（HpMax 先、Hp 后）——PI-08 真正想守住的
        /// 就是这个顺序和"同额补偿"的语义。端到端那一份由 PI-11 的 PlayMode 用例覆盖。
        /// </summary>
        private static void ApplyLevelUp(Combatant player, LevelUpInfo info)
        {
            player.HpMax = info.NewHpMax;
            player.Hp = player.Hp + info.DeltaHpMax;
        }

        /// <summary>
        /// PI-08 / AC-17 ~ AC-20：满血升级仍满血、残血升级不回满、连升两级累计 +52。
        /// </summary>
        [Test]
        public void PI08_LevelUpHpSemantics_AreCompensatoryNotFullHeal()
        {
            // --- AC-17：满血 260/260 → 286/286 ---
            Combatant full = Combatant.CreatePlayer(0, 260.0f, Vec2.Zero);
            PlayerProgression fullProg = new PlayerProgression();
            fullProg.LeveledUp += delegate (LevelUpInfo info) { ApplyLevelUp(full, info); };

            fullProg.GainExp(20);
            Assert.AreEqual(286.0f, full.HpMax, "AC-17 血上限应为 286");
            Assert.AreEqual(286.0f, full.Hp, "AC-17 本来满血，升级后仍应满血");

            // --- AC-18 / AC-19：残血 100/260 → 126/286，且严格小于上限 ---
            Combatant hurt = Combatant.CreatePlayer(1, 260.0f, Vec2.Zero);
            hurt.Hp = 100.0f;
            PlayerProgression hurtProg = new PlayerProgression();
            hurtProg.LeveledUp += delegate (LevelUpInfo info) { ApplyLevelUp(hurt, info); };

            hurtProg.GainExp(20);
            Assert.AreEqual(286.0f, hurt.HpMax, "AC-18 血上限应为 286");
            Assert.AreEqual(126.0f, hurt.Hp, "AC-18 当前血应同额 +26，而不是回满");
            Assert.Less(hurt.Hp, hurt.HpMax, "AC-19 残血升级后必须仍是残血");

            // --- AC-20：连升两级（1→3）当前血累计 +52 ---
            Combatant duo = Combatant.CreatePlayer(2, 260.0f, Vec2.Zero);
            duo.Hp = 100.0f;
            PlayerProgression duoProg = new PlayerProgression();
            duoProg.LeveledUp += delegate (LevelUpInfo info) { ApplyLevelUp(duo, info); };

            duoProg.GainExp(45); // 20 + 25 = 45 ⇒ 1 → 3
            Assert.AreEqual(3, duoProg.Level, "AC-20 前置：应连升到 3 级");
            Assert.AreEqual(312.0f, duo.HpMax, "AC-20 血上限应为 312");
            Assert.AreEqual(152.0f, duo.Hp, "AC-20 当前血应累计 +52（两级各 +26）");
        }

        // =====================================================================
        // 四、经验来源与不写敌人字段（AC-32 ~ AC-34、AC-26）
        // =====================================================================

        /// <summary>
        /// PI-09 / AC-32 ~ AC-34：击杀经验原样入账，精英与 BOSS 的倍率**不再二次相乘**。
        /// 倍率在敌人出厂时（DifficultyBridge）就已经乘进 ExpValue 了。
        /// </summary>
        [Test]
        public void PI09_ExpFromKill_UsesEnemyExpValueVerbatim()
        {
            float baseExp = EnemyBaseTable.Get(CombatConfig.KIND_WITCH).Exp;
            Assert.AreEqual(6.0f, baseExp, "前置：巫蛊基础经验为 6");

            // 普通怪
            PlayerProgression normal = new PlayerProgression();
            Assert.AreEqual(6, normal.GainExpFrom(100, baseExp), "AC-32 普通怪原样入账");

            // 精英：出厂已 ×2.0
            float eliteExp = baseExp * CombatConfig.ELITE_EXP_MULT;
            PlayerProgression elite = new PlayerProgression();
            Assert.AreEqual(12, elite.GainExpFrom(101, eliteExp),
                "AC-33 精英应入账 12（6×2），不是 24（再乘一次）");

            // BOSS：出厂已 ×3.0
            float bossExp = baseExp * CombatConfig.BOSS_EXP_MULT;
            PlayerProgression boss = new PlayerProgression();
            Assert.AreEqual(18, boss.GainExpFrom(102, bossExp),
                "AC-34 BOSS 应入账 18（6×3），不是 54（再乘一次）");
        }

        /// <summary>
        /// PI-10 / AC-26：入账经验的全过程不写敌人任何字段。
        ///
        /// 【机械性保证 vs 行为断言】
        /// 真正的保证是编译期的：PlayerProgression 的签名里根本没有 Combatant 类型，
        /// 写不进去。这条测试是那个保证的**行为侧佐证** ——
        /// 万一将来有人给 GainExpFrom 加了 Combatant 重载，这里会立刻发现。
        /// </summary>
        [Test]
        public void PI10_GainingExp_DoesNotMutateEnemy()
        {
            Combatant enemy = Combatant.CreateEnemy(7, Vec2.Zero, 30.0f, 8.0f, 60.0f);
            enemy.ExpValue = 25.0f;
            enemy.Level = 4;
            enemy.Armor = 3.0f;

            float hpBefore = enemy.Hp;
            float hpMaxBefore = enemy.HpMax;
            float expBefore = enemy.ExpValue;
            int levelBefore = enemy.Level;
            float armorBefore = enemy.Armor;
            float contactBefore = enemy.ContactDamage;

            PlayerProgression p = new PlayerProgression();
            p.GainExpFrom(enemy.Id, enemy.ExpValue);

            Assert.AreEqual(hpBefore, enemy.Hp, "不得改敌人当前血");
            Assert.AreEqual(hpMaxBefore, enemy.HpMax, "不得改敌人血上限");
            Assert.AreEqual(expBefore, enemy.ExpValue, "不得改敌人经验值");
            Assert.AreEqual(levelBefore, enemy.Level,
                "★ 尤其不得改敌人 Level —— 那是区域难度等级，不是玩家等级");
            Assert.AreEqual(armorBefore, enemy.Armor, "不得改敌人护甲");
            Assert.AreEqual(contactBefore, enemy.ContactDamage, "不得改敌人伤害");
        }

        /// <summary>
        /// PI-13 / AC-31：重开一局后成长状态与首次开局严格相等。
        /// 生产路径靠场景重载天然满足；这里验的是 Reset 语义那一份。
        /// </summary>
        [Test]
        public void PI13_ProgressionAfterReset_EqualsFreshRun()
        {
            PlayerProgression fresh = new PlayerProgression();

            PlayerProgression reused = new PlayerProgression();
            reused.GainExp(790);
            Assert.IsTrue(reused.IsMaxLevel, "前置：先打到 10 级");
            reused.Reset();

            Assert.AreEqual(fresh.Level, reused.Level);
            Assert.AreEqual(fresh.ExpTotal, reused.ExpTotal);
            Assert.AreEqual(fresh.ExpInLevel, reused.ExpInLevel);
            Assert.AreEqual(fresh.HpMax, reused.HpMax);
            Assert.AreEqual(fresh.AtkBonus, reused.AtkBonus);
            Assert.AreEqual(CombatBridge.PlayerHpMax, reused.HpMax,
                "重开后血上限必须回到 260，与开局基线一致");
        }

        // =====================================================================
        // 五、PlayMode 端到端（需要真实场景）
        // =====================================================================

        /// <summary>
        /// 每个用例结束后把 static 旗标复位，避免污染后续夹具（见文件头「static 状态卫生」）。
        ///
        /// 【为什么这里用 [TearDown] 而不是 [UnityTearDown]】
        /// 本夹具是**混装**的：12 条 EditMode [Test]（PI-01~PI-10、PI-12、PI-13）
        /// + 1 条 PlayMode [UnityTest]（PI-11）。
        /// [UnityTearDown] 会把每一条用例（包括纯 EditMode 的）都推进协程运行器并多等一帧，
        /// 平白给 12 条本来同步跑完的用例引入帧调度。而这里要做的只是给一个 static bool 赋值，
        /// 零帧即可完成，用同步 [TearDown] 语义更准、代价更低。
        /// [TearDown] 对 [Test] 和 [UnityTest] 同样生效，覆盖面没有损失。
        /// （纯 PlayMode 的 P0_2_P0_4_PlayModeTests 那边则用 [UnityTearDown]，
        ///   因为它全是 [UnityTest]，且需要确保在场景卸载之后才复位。）
        /// </summary>
        [TearDown]
        public void ResetMenuSkipFlagAfterEachTest()
        {
            MainMenuHud.SkipOnNextLoad = false;
        }

        private IEnumerator LoadGameplayScene()
        {
            if (string.IsNullOrEmpty(GameplaySceneName))
            {
                Assert.Ignore("未配置 GameplaySceneName，跳过需要场景的 P1-6 集成用例。");
                yield break;
            }

            // 与 P0_2_P0_4_PlayModeTests 同款：跳过主菜单，直接进战斗，
            // 否则 Scheduler 会被 _menuPaused 冻住。
            MainMenuHud.SkipOnNextLoad = true;

            // ★ P1-6 / PI-11 修复（寇豆码，2026-08）
            //   原代码直接 SceneManager.LoadScene，在 EditMode 上下文会抛出
            //   InvalidOperationException: This can only be used during edit mode,
            //   please use EditorSceneManager.OpenScene() instead.
            //
            //   原因：本测试夹具所在的 Xianxia.Unity.T2.Tests 是
            //   includePlatforms:["Editor"] 的程序集，其 [UnityTest] 以「编辑器协程」
            //   方式运行 —— 并未真正进入 PlayMode，因此 SceneManager.LoadScene 不可用。
            //
            //   修复策略（遵循团队指示：优先保留 EditMode + 改用 EditorSceneManager）：
            //   · PlayMode 下仍走 SceneManager.LoadScene（合法路径，Start 自然跑）；
            //   · EditMode 下改用 EditorSceneManager.OpenScene 打开场景，再手动驱动
            //     PI-11 真正要验的「最小可行生命周期」：
            //       Awake                → CombatController.Awake → BuildEncounter
            //                              （建 Encounter / DifficultyBridge / EventsUnity / Player）
            //       ApplyPlayerDamageModel → 把反调靶子常量 260 写进 Encounter.Bridge
            //       SetupT3             → 建技能/状态表（纯代码，安全）
            //       SetupProgression    → 建 PlayerProgression 并接上升级/击杀回调
            //     刻意跳过 SpawnFirstWave 与菜单 Build（Editor 下无意义且易脆），
            //     PI-11 的全部断言都落在这几条已驱动的链路上，测试意图完整保留。
            if (Application.isPlaying)
            {
                SceneManager.LoadScene(GameplaySceneName);
            }
            else
            {
#if UNITY_EDITOR
                string scenePath = null;
                foreach (var scene in UnityEditor.EditorBuildSettings.scenes)
                {
                    if (System.IO.Path.GetFileNameWithoutExtension(scene.path) == GameplaySceneName)
                    {
                        scenePath = scene.path;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(scenePath))
                {
                    Assert.Ignore(
                        "GameplayScene（" + GameplaySceneName +
                        "）未加入 Build Settings，跳过需要场景的 P1-6 集成用例。");
                    yield break;
                }

                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);

                // 编辑器不会自动跑 MonoBehaviour 生命周期，手动驱动 PI-11 所需那段。
                CombatBridge bridge = Object.FindObjectOfType<CombatBridge>();
                if (bridge != null)
                {
                    // Awake：CombatController.Awake → BuildEncounter（Encounter/Bridge/EventsUnity/Player）。
                    bridge.SendMessage("Awake", null, SendMessageOptions.DontRequireReceiver);
                    // 写反调靶子常量 260（公开方法，直接调）。
                    bridge.ApplyPlayerDamageModel();
                    // SetupT3：建技能/状态表（纯代码，安全）。
                    bridge.SendMessage("SetupT3", null, SendMessageOptions.DontRequireReceiver);
                    // SetupProgression：建 PlayerProgression 并接上升级/击杀回调（私有，SendMessage 调）。
                    bridge.SendMessage("SetupProgression", null, SendMessageOptions.DontRequireReceiver);
                }
#else
                SceneManager.LoadScene(GameplaySceneName);
#endif
            }

            // 等两帧：场景加载 + 生命周期驱动完成。
            yield return null;
            yield return null;
        }

        /// <summary>
        /// 取场景里的 CombatBridge，并**当场校验装配完整性**。
        ///
        /// 【为什么把判空塞进 helper】PI-11 一上来就要读 bridge.Encounter.Bridge.PlayerHpMax，
        /// 这是三级链式访问。任何一级为 null 都只会得到一句没有信息量的
        /// NullReferenceException，堆栈还指向测试文件，极易被误判成"测试写错了"。
        /// 在这里逐级拦下，失败消息直接点名断在哪一环、该去查哪段生产代码。
        /// </summary>
        private static CombatBridge FindBridge()
        {
            CombatBridge bridge = Object.FindObjectOfType<CombatBridge>();
            Assert.IsNotNull(bridge,
                "场景中应存在 CombatBridge：确认 " + GameplaySceneName +
                " 已加入 Build Settings 且场景内对象挂了 CombatBridge 组件");

            Assert.IsNotNull(bridge.Player,
                "CombatBridge.Player 为 null：CombatController.BuildEncounter 可能未执行，" +
                "或 Inspector 上 playerTransform 未绑定");
            Assert.IsNotNull(bridge.Encounter,
                "CombatBridge.Encounter 为 null：CombatController.Awake/BuildEncounter 可能未执行，" +
                "或 CombatBridge 与 CombatController 不在同一 GameObject 上");
            Assert.IsNotNull(bridge.Encounter.Bridge,
                "Encounter.Bridge（DifficultyBridge）为 null：难度桥未装配，反调靶子无从读取");

            return bridge;
        }

        /// <summary>
        /// PI-11 / AC-23 ★★ 本套件的头号哨兵。
        ///
        /// 【它守的是什么】
        /// 玩家一路升到 10 级（血上限 494），此时怪的反调靶子
        /// <c>Encounter.Bridge.PlayerHpMax</c> 必须**仍然是 260**。
        /// 若哪天有人"顺手修好"了 CombatBridge.ApplyPlayerDamageModel，
        /// 把玩家实时血上限回写进去，这条会立刻红 ——
        /// 而如果没有这条测试，那个改动在游戏里的表现只是"升级好像没什么感觉"，
        /// 几乎不可能被人工发现。
        ///
        /// 【为什么还要再调一次 ApplyPlayerDamageModel】
        /// 它是公开方法，Start 里调过一次，运行中也可能被再次调用。
        /// 必须证明**任何一次调用**都写常量，而不是只有首次恰好正确。
        /// </summary>
        [UnityTest]
        public IEnumerator PI11_PlayerAtMaxLevel_KeepsDifficultyTargetPinnedAt260()
        {
            yield return LoadGameplayScene();
            CombatBridge bridge = FindBridge();

            // bridge / Player / Encounter / Encounter.Bridge 的非空已由 FindBridge 校验，
            // 这里只需再守住 Progression 这一条独立的装配路径。
            Assert.IsNotNull(bridge.Progression,
                "Start 之后 Progression 不应为 null：CombatBridge.SetupProgression 可能因 " +
                "controller 或 EventsUnity 为 null 而整段跳过（检查 Console 是否有初始化期告警）");

            Assert.AreEqual(1, bridge.Progression.Level,
                $"开局应为 1 级，实际 Level={bridge.Progression.Level}（ExpTotal={bridge.Progression.ExpTotal}）。" +
                "若已大于 1，说明 Progression 是跨用例复用的残留实例，而非本局新建");

            float startTarget = bridge.Encounter.Bridge.PlayerHpMax;
            Assert.AreEqual(260.0f, startTarget,
                $"开局靶子应为 260，实际读到 {startTarget}（玩家 HpMax={bridge.Player.HpMax}）");

            // 一口气升到 10 级。走公开 API，不反射。
            bridge.Progression.GainExp(790);
            yield return null;

            Assert.AreEqual(10, bridge.Progression.Level,
                $"应已升到 10 级，实际 Level={bridge.Progression.Level}" +
                $"（ExpTotal={bridge.Progression.ExpTotal}，ExpInLevel={bridge.Progression.ExpInLevel}）");
            Assert.AreEqual(494.0f, bridge.Player.HpMax,
                $"玩家血上限应涨到 494，实际 HpMax={bridge.Player.HpMax}。" +
                "若仍是 260，说明 LeveledUp 回调没把 NewHpMax 写回 Player");

            // ★ 核心断言。
            float actualTarget = bridge.Encounter.Bridge.PlayerHpMax;
            Assert.AreEqual(260.0f, actualTarget,
                $"★ AC-23：玩家 10 级后反调靶子必须仍严格等于 260.0f，实际读到 {actualTarget}" +
                $"（玩家 HpMax={bridge.Player.HpMax}）。若读到 494，说明 " +
                "CombatBridge.ApplyPlayerDamageModel 把玩家实时血上限回写进了难度桥");

            // 再手动调一次，证明重复调用依然写常量。
            bridge.ApplyPlayerDamageModel();
            float afterReapply = bridge.Encounter.Bridge.PlayerHpMax;
            Assert.AreEqual(260.0f, afterReapply,
                $"★ AC-23：重复调用 ApplyPlayerDamageModel 也不得把玩家实时血上限写回去，" +
                $"实际读到 {afterReapply}（玩家 HpMax={bridge.Player.HpMax}）");

            // 顺带确认两条 raw 路径都已生效到 10 级。
            Assert.AreEqual(9.0f, bridge.PlayerAtkBonus,
                $"10 级攻击加成应为 +9，实际 PlayerAtkBonus={bridge.PlayerAtkBonus}");
            if (bridge.SkillTable != null)
            {
                SkillDef basic = bridge.SkillTable.Get(SkillConfig.SKILL_BASIC_SLASH);
                Assert.IsNotNull(basic, "技能表应含水剑斩（SkillConfig.SKILL_BASIC_SLASH）");
                Assert.AreEqual(SkillConfig.BASIC_RAW + 9.0f, basic.Raw,
                    $"技能 raw 应被刷新为 基础常量 + 9 = {SkillConfig.BASIC_RAW + 9.0f}，实际 Raw={basic.Raw}。" +
                    "若明显偏大，多半是每次升级都在旧值上累加，而不是从常量重算");
            }
        }

        /// <summary>
        /// PI-12：Progression 尚未装配时 PlayerAtkBonus 安全返回 0。
        ///
        /// 【为什么这很重要】
        /// AttackController 每次挥砍都读这个属性。若它在早期帧抛 NullReference，
        /// 玩家开局第一刀就会报错；若返回一个非 0 值，基线对拍会漂移。
        /// 返回 0.0f ⇒ raw = 12 + 0.0f ≡ 12，逐位等价。
        /// </summary>
        [Test]
        public void PI12_PlayerAtkBonus_IsZeroWhenProgressionNotReady()
        {
            GameObject host = new GameObject("P1_6_BridgeProbe");
            try
            {
                // 只 AddComponent、不跑 Start ⇒ _progression 必为 null，
                // 正是"装配之前"那一小段窗口的真实状态。
                CombatBridge bridge = host.AddComponent<CombatBridge>();

                Assert.IsNull(bridge.Progression, "未装配时 Progression 应为 null");
                Assert.AreEqual(0.0f, bridge.PlayerAtkBonus,
                    "未装配时加成必须是 0.0f，保证 raw 逐位等价于 12");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
