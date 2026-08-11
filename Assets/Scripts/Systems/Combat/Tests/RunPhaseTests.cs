// -----------------------------------------------------------------------------
// RunPhaseTests.cs —— P0-3「胜负判定 / 对局阶段状态机」的 NUnit 验收套件
//
// 【为什么新开一个测试文件，而不是往 CombatKernelTests.cs 里加】
// 同目录的 CombatKernelTests.cs 是**红线文件**：它锁着 T1 的 88 条既有断言，
// 一个字都不许动（理由见 ICombatEventsT3.cs 文件头的偏离说明 D-1）。
// 新功能的断言另起一个文件，QA 在 diff 里就能一眼看清"旧断言零改动、新断言全新增"。
//
// 【本文件当前的验证状态 —— 请如实理解】
// 编写环境**没有 Unity、也没有 dotnet**，因此本文件**未经编译、未经运行**。
// 它的正确性目前只由"人工比对内核源码签名"保证。
// 真正的验证请在本地 Unity 编辑器里跑：Window → General → Test Runner → EditMode → Run All。
// 与它配套的静态护栏（Tests/t1_selfcheck.py、Tests/t3_selfcheck.py）在无 Unity 环境
// 可以跑，但它们只覆盖"数值基线没被改动 + 语法/类型解析健全"，
// **不覆盖**本文件的任何一条胜负断言。
//
// 【覆盖的用例】
//   RP-01  双方都活着            → Playing
//   RP-02  玩家 HP 空            → Lost
//   RP-03  敌人全部 HP 空        → Won
//   RP-04  判负后幂等（回满血也不退回 Playing）
//   RP-05  判胜后幂等（再刷怪也不退回 Playing）
//   RP-06  空场（从未出现过敌人）不自动判胜   ← 布防闸门
//   RP-07  同一步同归于尽 → 判负（Lost 优先于 Won）
//   RP-08  PhaseChanged 一局至多触发一次
//   RP-09  没有玩家时不做任何判定
//   RP-10  Reset 回到 Playing 且撤销布防
//   RP-11  端到端：Encounter.StepFixed 驱动下玩家被打死 → Lost
//   RP-12  端到端：Encounter.StepFixed 驱动下清光敌人 → Won（且尸体已被收走）
//   RP-13  端到端：Clear() 之后可以正常打第二局
//   RP-14  红线：判负之后内核照常空转，不提前 return（观察者不干预）
//
// 【P2-1 追加 —— BOSS 债（BossPending）不变量】RP01–RP14 一条未改。
//   RP-15  I-2：欠着 BOSS 债时连推 100 步也判不出 Won；但 Lost 不受影响
//   RP-16  零跨越竞态：一步带走全部敌人仍不判胜，销债后下一步才判胜（本切片核心回归）
//   RP-17  I-1：PendingAwareEnemyCount 与 AliveEnemyCount 的差恰为债的有无
//   RP-18  I-4 / R-3：Clear() 之后债与两个计时器全部归零
//   RP-19  Q-1 计时语义：Idle 只在"场上零敌人"时累加，有敌人立刻归零；Total 无条件累加
//
// 【禁止事项】不得引用 UnityEngine。本文件受 CI 的 grep 守卫约束，只允许 BCL + NUnit。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using NUnit.Framework;

namespace Xianxia.Combat.Tests
{
    /// <summary>
    /// P0-3 胜负判定状态机验收。
    ///
    /// 【新手解释】每个带 [Test] 的方法都是一条独立的检查。
    /// NUnit 会各自新建一个 <see cref="RunPhaseTests"/> 实例来跑，
    /// 所以用例之间不会互相污染状态 —— 不需要手动清理。
    /// </summary>
    [TestFixture]
    public sealed class RunPhaseTests
    {
        // ---------------------------------------------------------------------
        // 测试夹具（helper）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 造一个"已布防"的裁判：先喂一步「玩家活着 + 1 只活敌人」，
        /// 让 _armed 点亮，之后的用例才能正常测胜利分支。
        ///
        /// 【为什么要单独抽出来】几乎每条用例都要先布防，抄五遍就会有一遍抄错。
        /// </summary>
        /// <returns>处于 Playing、且已布防的裁判。</returns>
        private static RunPhaseTracker MakeArmedTracker()
        {
            RunPhaseTracker t = new RunPhaseTracker();
            t.Evaluate(true, true, 1);
            Assert.AreEqual(RunPhase.Playing, t.Phase, "布防那一步不该分出胜负");
            Assert.IsTrue(t.IsArmed, "喂过一只活敌人之后应当已布防");
            return t;
        }

        /// <summary>
        /// 搭一场最小可用的战斗：1 名玩家 + N 只敌人。
        ///
        /// 敌人被放在离玩家很远的地方（1000 px，远超 TouchRange 44），
        /// 这样默认情况下不会有接触伤害 —— 用例想让谁掉血就自己显式改 Hp，
        /// 避免"怪跑过来碰到我了"这种时序噪声混进胜负断言里。
        /// </summary>
        /// <param name="enemyCount">敌人数量。</param>
        /// <param name="playerHp">玩家血量上限（同时也是初始血量）。</param>
        /// <returns>已编好队的战场。</returns>
        private static Encounter MakeEncounter(int enemyCount, float playerHp)
        {
            Encounter enc = new Encounter();
            enc.SetPlayer(Combatant.CreatePlayer(1, playerHp, Vec2.Zero));

            for (int i = 0; i < enemyCount; i++)
            {
                // 沿 X 轴一字排开，彼此间隔 50 px，全都在 1000 px 之外。
                Combatant e = Combatant.CreateEnemy(100 + i, new Vec2(1000.0f + i * 50.0f, 0.0f),
                    30.0f, 4.0f, 70.0f);
                enc.Add(e);
            }
            return enc;
        }

        /// <summary>推进若干个固定步。</summary>
        /// <param name="enc">战场。</param>
        /// <param name="steps">步数。</param>
        private static void Step(Encounter enc, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                enc.StepFixed(CombatScheduler.FixedStep);
            }
        }

        /// <summary>把战场上所有敌人的血打空（不经过伤害公式，直接置 0）。</summary>
        /// <param name="enc">战场。</param>
        private static void KillAllEnemies(Encounter enc)
        {
            for (int i = 0; i < enc.Combatants.Count; i++)
            {
                Combatant c = enc.Combatants[i];
                if (c.IsEnemy)
                {
                    c.Hp = 0.0f;
                }
            }
        }

        // ---------------------------------------------------------------------
        // A. 纯查询重载（不需要搭战场）
        // ---------------------------------------------------------------------

        /// <summary>RP-01 双方都活着 → Playing。</summary>
        [Test]
        public void RP01_BothAlive_StaysPlaying()
        {
            RunPhaseTracker t = new RunPhaseTracker();

            Assert.AreEqual(RunPhase.Playing, t.Phase, "刚造出来就应该是 Playing");

            t.Evaluate(true, true, 3);
            Assert.AreEqual(RunPhase.Playing, t.Phase, "玩家活着且还有 3 只怪，必须仍在进行中");
            Assert.IsFalse(t.IsSettled, "IsSettled 应与 Phase 保持一致");
        }

        /// <summary>RP-02 玩家 HP 空 → Lost。</summary>
        [Test]
        public void RP02_PlayerDead_Lost()
        {
            RunPhaseTracker t = MakeArmedTracker();

            t.Evaluate(true, false, 2);

            Assert.AreEqual(RunPhase.Lost, t.Phase, "玩家死亡必须判负");
            Assert.IsTrue(t.IsSettled, "判负后 IsSettled 应为 true");
        }

        /// <summary>RP-03 敌人全部 HP 空 → Won。</summary>
        [Test]
        public void RP03_AllEnemiesDead_Won()
        {
            RunPhaseTracker t = MakeArmedTracker();

            t.Evaluate(true, true, 0);

            Assert.AreEqual(RunPhase.Won, t.Phase, "玩家活着且敌人清零必须判胜");
            Assert.IsTrue(t.IsSettled, "判胜后 IsSettled 应为 true");
        }

        /// <summary>
        /// RP-04 判负后幂等：哪怕血被加回来，也不许退回 Playing。
        /// 这是"死亡界面弹出来之后又自己消失"这类 bug 的防线。
        /// </summary>
        [Test]
        public void RP04_Lost_IsIdempotent()
        {
            RunPhaseTracker t = MakeArmedTracker();
            t.Evaluate(true, false, 2);
            Assert.AreEqual(RunPhase.Lost, t.Phase);

            // 模拟"复活/回血"：玩家又活了，敌人也还在。
            t.Evaluate(true, true, 2);
            Assert.AreEqual(RunPhase.Lost, t.Phase, "判负后不许翻回 Playing");

            // 再模拟"敌人被清光"：也不许变成 Won。
            t.Evaluate(true, true, 0);
            Assert.AreEqual(RunPhase.Lost, t.Phase, "判负后不许翻成 Won");
        }

        /// <summary>
        /// RP-05 判胜后幂等：再刷出怪来也不许退回 Playing。
        /// 防的是"结算界面消失又重弹、奖励领两遍"。
        /// </summary>
        [Test]
        public void RP05_Won_IsIdempotent()
        {
            RunPhaseTracker t = MakeArmedTracker();
            t.Evaluate(true, true, 0);
            Assert.AreEqual(RunPhase.Won, t.Phase);

            t.Evaluate(true, true, 5);
            Assert.AreEqual(RunPhase.Won, t.Phase, "判胜后不许翻回 Playing");

            t.Evaluate(true, false, 5);
            Assert.AreEqual(RunPhase.Won, t.Phase, "判胜后不许翻成 Lost");
        }

        /// <summary>
        /// RP-06 布防闸门：从未出现过敌人的空场，不许自动判胜。
        /// 没有这道门，游戏一进场就"通关"。
        /// </summary>
        [Test]
        public void RP06_NeverArmed_DoesNotAutoWin()
        {
            RunPhaseTracker t = new RunPhaseTracker();

            // 连喂 10 步"玩家活着、场上 0 只怪"。
            for (int i = 0; i < 10; i++)
            {
                t.Evaluate(true, true, 0);
            }

            Assert.IsFalse(t.IsArmed, "从未见过活敌人，不该布防");
            Assert.AreEqual(RunPhase.Playing, t.Phase, "空场不许自动判胜");

            // 一旦真的出现敌人再被清光，才允许判胜。
            t.Evaluate(true, true, 1);
            Assert.IsTrue(t.IsArmed, "见到活敌人后应当布防");
            t.Evaluate(true, true, 0);
            Assert.AreEqual(RunPhase.Won, t.Phase, "布防之后清零才判胜");
        }

        /// <summary>RP-07 同一步同归于尽 → 判负（Lost 优先于 Won）。</summary>
        [Test]
        public void RP07_MutualDeath_PrefersLost()
        {
            RunPhaseTracker t = MakeArmedTracker();

            // 玩家死了，同时敌人也全清零 —— 两条规则同时成立。
            t.Evaluate(true, false, 0);

            Assert.AreEqual(RunPhase.Lost, t.Phase, "同归于尽必须判负，不能判胜");
        }

        /// <summary>
        /// RP-08 PhaseChanged 一局至多触发一次，且参数是新阶段。
        /// 订阅方据此可以放心地在回调里发奖励 / 弹界面。
        /// </summary>
        [Test]
        public void RP08_PhaseChanged_FiresExactlyOnce()
        {
            RunPhaseTracker t = MakeArmedTracker();

            List<RunPhase> got = new List<RunPhase>(4);
            t.PhaseChanged += p => got.Add(p);

            // Playing 期间不该有任何广播。
            t.Evaluate(true, true, 2);
            Assert.AreEqual(0, got.Count, "还在打的时候不该抛事件");

            // 判负，抛一次。
            t.Evaluate(true, false, 2);
            Assert.AreEqual(1, got.Count, "分出胜负应当抛且只抛一次");
            Assert.AreEqual(RunPhase.Lost, got[0], "事件参数应为新阶段");

            // 后续无论怎么喂，都不许再抛。
            t.Evaluate(true, true, 0);
            t.Evaluate(true, false, 3);
            Assert.AreEqual(1, got.Count, "落定之后不许重复抛事件");
        }

        /// <summary>
        /// RP-09 没有玩家时不做任何判定，也不布防。
        /// 覆盖"先加怪、后设玩家"的装配顺序。
        /// </summary>
        [Test]
        public void RP09_NoPlayer_StaysPlayingAndNotArmed()
        {
            RunPhaseTracker t = new RunPhaseTracker();

            // 没有玩家，但场上有 3 只怪 —— 不判定、也不布防。
            t.Evaluate(false, false, 3);
            Assert.AreEqual(RunPhase.Playing, t.Phase, "没有玩家时不许判负");
            Assert.IsFalse(t.IsArmed, "没有玩家时不许布防");

            // 没有玩家、也没有怪 —— 更不许判胜。
            t.Evaluate(false, false, 0);
            Assert.AreEqual(RunPhase.Playing, t.Phase, "没有玩家时不许判胜");
        }

        /// <summary>RP-10 Reset 回到 Playing 且撤销布防，订阅者保留。</summary>
        [Test]
        public void RP10_Reset_RestoresPlayingAndDisarms()
        {
            RunPhaseTracker t = MakeArmedTracker();

            int fired = 0;
            t.PhaseChanged += p => fired++;

            t.Evaluate(true, false, 1);
            Assert.AreEqual(RunPhase.Lost, t.Phase);
            Assert.AreEqual(1, fired);

            t.Reset();
            Assert.AreEqual(RunPhase.Playing, t.Phase, "Reset 后应回到 Playing");
            Assert.IsFalse(t.IsSettled, "Reset 后 IsSettled 应为 false");
            Assert.IsFalse(t.IsArmed, "Reset 后应撤销布防");
            Assert.AreEqual(1, fired, "Reset 本身不抛事件");

            // 订阅者没有被清掉：第二局分出胜负时照样能收到。
            t.Evaluate(true, true, 1);
            t.Evaluate(true, true, 0);
            Assert.AreEqual(RunPhase.Won, t.Phase, "Reset 后第二局应能正常判胜");
            Assert.AreEqual(2, fired, "Reset 不该清掉订阅者");
        }

        // ---------------------------------------------------------------------
        // B. 端到端（经由 Encounter.StepFixed 驱动）
        // ---------------------------------------------------------------------

        /// <summary>RP-11 端到端：玩家血空 → 下一步末判负。</summary>
        [Test]
        public void RP11_Encounter_PlayerDies_Lost()
        {
            Encounter enc = MakeEncounter(2, 260.0f);

            Step(enc, 1);
            Assert.AreEqual(RunPhase.Playing, enc.Phase, "双方都活着，应仍在进行中");
            Assert.IsTrue(enc.RunState.IsArmed, "场上有 2 只活怪，应已布防");

            enc.Player.Hp = 0.0f;
            Step(enc, 1);

            Assert.AreEqual(RunPhase.Lost, enc.Phase, "玩家血空后应判负");
            Assert.IsTrue(enc.IsRunOver, "IsRunOver 快捷方式应同步");

            // 内核不会把玩家尸体删掉（死亡流程属于上层，见 RemoveDead 的注释）。
            Assert.IsTrue(enc.Combatants.Contains(enc.Player), "玩家不该被内核移出列表");
        }

        /// <summary>RP-12 端到端：清光敌人 → 判胜，且尸体已被 ⑥ 收走。</summary>
        [Test]
        public void RP12_Encounter_AllEnemiesDead_Won()
        {
            Encounter enc = MakeEncounter(3, 260.0f);

            Step(enc, 1);
            Assert.AreEqual(RunPhase.Playing, enc.Phase);
            Assert.AreEqual(3, enc.AliveEnemyCount, "初始应有 3 只活怪");

            KillAllEnemies(enc);
            Step(enc, 1);

            Assert.AreEqual(0, enc.AliveEnemyCount, "尸体应已在阶段 ⑥ 被收走");
            Assert.AreEqual(RunPhase.Won, enc.Phase, "敌人清零且玩家存活应判胜");
            Assert.IsTrue(enc.Player.IsAlive, "玩家应仍然活着");
        }

        /// <summary>
        /// RP-13 端到端：Clear() 之后可以正常打第二局。
        /// 这条守的是"复用同一个 Encounter 时裁判卡在上一局结果"的坑。
        /// </summary>
        [Test]
        public void RP13_Encounter_ClearAllowsSecondRun()
        {
            Encounter enc = MakeEncounter(1, 260.0f);
            Step(enc, 1);
            enc.Player.Hp = 0.0f;
            Step(enc, 1);
            Assert.AreEqual(RunPhase.Lost, enc.Phase, "第一局应判负");

            enc.Clear();
            Assert.AreEqual(RunPhase.Playing, enc.Phase, "Clear 后应回到 Playing");
            Assert.IsFalse(enc.RunState.IsArmed, "Clear 后应撤销布防");

            // 第二局：重新编队，这次打赢。
            enc.SetPlayer(Combatant.CreatePlayer(1, 260.0f, Vec2.Zero));
            enc.Add(Combatant.CreateEnemy(200, new Vec2(1000.0f, 0.0f), 30.0f, 4.0f, 70.0f));
            Step(enc, 1);
            Assert.AreEqual(RunPhase.Playing, enc.Phase, "第二局刚开始应为 Playing");

            KillAllEnemies(enc);
            Step(enc, 1);
            Assert.AreEqual(RunPhase.Won, enc.Phase, "第二局应能正常判胜");
        }

        /// <summary>
        /// RP-14 红线：判负之后内核**照常空转**，不提前 return。
        ///
        /// 【为什么要专门测这个】胜负判定是"观察者"，一旦有人手贱在 StepFixed 里
        /// 加一句 `if (IsRunOver) return;`，所有长时间模拟的对拍（T1 的 64 条断言）
        /// 都会整体错位。这条断言把"观察者不干预"从口头约定变成可执行的检查。
        ///
        /// 【QA 复核修正（严过关）】原版只断言 StepCount / ElapsedTime 继续累加。
        /// 但这两个自增写在 <c>Encounter.StepFixed</c> 的**最前面**（阶段 ① 之前，
        /// 见 Encounter.cs:421-422），因此只要短路语句插在它们之后（哪怕把 ①~⑦
        /// 全部跳过），原版断言依然全绿 —— 这条"红线守卫"本身是假绿的。
        /// 现补一条**只有真正执行到阶段 ⑥ 才会发生**的可观测副作用断言：
        /// 判负之后再打死一只怪，它的尸体仍须被 RemoveDead 收走。
        /// </summary>
        [Test]
        public void RP14_KernelKeepsTickingAfterSettled()
        {
            Encounter enc = MakeEncounter(2, 260.0f);
            Step(enc, 1);
            enc.Player.Hp = 0.0f;
            Step(enc, 1);
            Assert.AreEqual(RunPhase.Lost, enc.Phase);
            Assert.AreEqual(2, enc.AliveEnemyCount, "判负时两只怪都还活着");

            int stepsBefore = enc.StepCount;
            float timeBefore = enc.ElapsedTime;

            // ① 计数器仍在走 —— 守的是"整个 StepFixed 被从头 return 掉"。
            Step(enc, 10);

            Assert.AreEqual(stepsBefore + 10, enc.StepCount, "判负后步数仍须照常累加");
            Assert.Greater(enc.ElapsedTime, timeBefore, "判负后逻辑时长仍须照常累加");

            // ② ★真红线：步内各阶段也必须照常执行 —— 守的是"短路插在计数器之后"。
            Combatant victim = null;
            for (int i = 0; i < enc.Combatants.Count; i++)
            {
                if (enc.Combatants[i].IsEnemy)
                {
                    victim = enc.Combatants[i];
                    break;
                }
            }
            Assert.IsNotNull(victim, "场上应仍有敌人可供本断言使用");

            victim.Hp = 0.0f;
            Step(enc, 1);

            Assert.IsFalse(enc.Combatants.Contains(victim),
                "判负后阶段 ⑥ 收尸仍须执行 —— 尸体没被收走说明内核已被提前 return 短路");
            Assert.AreEqual(1, enc.AliveEnemyCount, "只应少掉被打死的那一只");
            Assert.AreEqual(RunPhase.Lost, enc.Phase, "空转期间阶段不许改变");
        }

        // ---------------------------------------------------------------------
        // E. ★P2-1 BOSS 债（BossPending）不变量
        //
        // 这一组守的是整个 P2-1 切片唯一的致命故障模式：
        // BOSS 是"清完杂兵之后才生成"的，若裁判按 AliveEnemyCount 判，
        // 清掉最后一只杂兵的那一步就会当场判 Won，而幂等闸门单向不可逆 ——
        // 这一局的 BOSS 战直接蒸发，玩家会以为"设计如此"，连报障都不会来。
        // ---------------------------------------------------------------------

        /// <summary>
        /// RP-15 不变量 I-2：欠着 BOSS 债时，即使场上一只敌人都没有，
        /// 连推 100 个固定步也**不可能**落定 Won；而 Lost 分支完全不受影响。
        ///
        /// 后半段同样重要：失败判定只看玩家死活，与敌人数无关。
        /// "欠玩家一只怪"绝不能变成"玩家死不了" —— 那会让 GameOverHud 永远弹不出来。
        /// </summary>
        [Test]
        public void RP15_BossPending_BlocksWinButNotLoss()
        {
            Encounter enc = MakeEncounter(2, 260.0f);

            // 先跑一步让裁判布防（见过活敌人才允许判胜）。
            Step(enc, 1);
            Assert.AreEqual(RunPhase.Playing, enc.Phase);
            Assert.IsTrue(enc.RunState.IsArmed, "见过活敌人之后应当已布防");
            Assert.IsFalse(enc.BossPending, "尚未置位时不该欠债");

            enc.MarkBossPending();
            Assert.IsTrue(enc.BossPending, "置位后应当欠着一只 BOSS");

            KillAllEnemies(enc);
            Step(enc, 100);

            Assert.AreEqual(0, enc.AliveEnemyCount, "杂兵尸体应已全部被阶段 ⑥ 收走");
            Assert.AreEqual(1, enc.PendingAwareEnemyCount, "给裁判看的敌人数应为 0 + 1（欠的那只 BOSS）");
            Assert.AreEqual(RunPhase.Playing, enc.Phase,
                "★核心红线：BOSS 债未销，连推 100 步也绝不许判胜");
            Assert.IsTrue(enc.Player.IsAlive, "玩家应仍然活着");

            // Lost 不受影响：失败分支只看 playerAlive。
            enc.Player.Hp = 0.0f;
            Step(enc, 1);

            Assert.AreEqual(RunPhase.Lost, enc.Phase,
                "欠着 BOSS 债不该妨碍判负 —— 否则玩家死了也弹不出结算面板");
        }

        /// <summary>
        /// RP-16 ★本切片的核心回归用例：零跨越竞态。
        ///
        /// 血莲 / 范围技一次性把最后 3 只杂兵全部带走，本步 ⑥ 收尸后
        /// AliveEnemyCount 直接从 3 跳到 0 —— 中间没有"1"这个中间态给上层反应。
        /// 有了 BOSS 债，本步 ⑦ 读到的是 0 + 1 = 1，判不出 Won；
        /// 且**幂等闸门未被触发**，Won 仍然可以在将来正确落定（这一点必须验，
        /// 否则"没早判"也可能是"永远判不了"）。
        /// </summary>
        [Test]
        public void RP16_BossPending_SurvivesZeroCrossingRace()
        {
            Encounter enc = MakeEncounter(3, 260.0f);
            Step(enc, 1);
            Assert.AreEqual(3, enc.AliveEnemyCount, "初始应有 3 只活怪");

            enc.MarkBossPending();

            // 一步之内全部置死 —— 模拟范围技一次带走。
            KillAllEnemies(enc);
            Step(enc, 1);

            Assert.AreEqual(0, enc.AliveEnemyCount, "3 只应在同一步被收尸");
            Assert.AreEqual(RunPhase.Playing, enc.Phase,
                "★零跨越：一步从 3 掉到 0，仍不许判胜");

            // 模拟 BOSS 真身入列后上层销债（不变量 I-3：Add 之后才清）。
            Assert.IsTrue(enc.ClearBossPending(), "首次销债应返回 true");
            Assert.IsFalse(enc.BossPending, "销债后不该再欠");

            Step(enc, 1);

            Assert.AreEqual(RunPhase.Won, enc.Phase,
                "债销完且场上无敌，下一固定步必须正常判胜 —— 否则就是从早判变成了永远判不了");
        }

        /// <summary>
        /// RP-17 不变量 I-1：<c>PendingAwareEnemyCount >= AliveEnemyCount</c> 恒成立，
        /// 且两者之差恰好等于"债的有无"（1 或 0）。
        ///
        /// 顺带验 <c>MarkBossPending</c> 的幂等性：重复置位不该把计数抬到 +2。
        /// </summary>
        [Test]
        public void RP17_PendingAwareCount_TracksDebtExactly()
        {
            Encounter enc = MakeEncounter(2, 260.0f);

            // 未置位：两个口径应完全一致。
            Assert.AreEqual(enc.AliveEnemyCount, enc.PendingAwareEnemyCount,
                "没欠债时两个口径必须相等");
            Assert.AreEqual(2, enc.PendingAwareEnemyCount);

            enc.MarkBossPending();
            Assert.AreEqual(3, enc.PendingAwareEnemyCount, "欠债时应比存活敌人数多 1");

            // 幂等：连调三次也只多 1。
            enc.MarkBossPending();
            enc.MarkBossPending();
            Assert.AreEqual(3, enc.PendingAwareEnemyCount, "重复置位不该把债累加成 2");

            // 场上敌人清零，差值仍恒为 1。
            KillAllEnemies(enc);
            Step(enc, 1);
            Assert.AreEqual(0, enc.AliveEnemyCount);
            Assert.AreEqual(1, enc.PendingAwareEnemyCount);
            Assert.GreaterOrEqual(enc.PendingAwareEnemyCount, enc.AliveEnemyCount, "I-1 恒成立");

            // 销债后恢复原语义。
            enc.ClearBossPending();
            Assert.AreEqual(enc.AliveEnemyCount, enc.PendingAwareEnemyCount,
                "销债后两个口径必须重新相等");

            // 重复销债返回 false，且不抛。
            Assert.IsFalse(enc.ClearBossPending(), "本来就没欠债时应返回 false");
        }

        /// <summary>
        /// RP-18 不变量 I-4 / 防线 R-3：<c>Clear()</c> 之后债与两个计时器全部归零。
        ///
        /// 当前重开路径走的是整场景重载（<c>Encounter</c> 必然重建），这行复位不会被触发；
        /// 但 <c>Clear()</c> 是公开 API，契约就是"清空整场战斗 = 重开一局"，
        /// 而 RP13 已经在用"复用同一个 Encounter 打第二局"的写法。
        /// 不复位的话，第二局起手就带着上一局欠下的 BOSS 债 —— 表现为"第二局永远赢不了"。
        /// </summary>
        [Test]
        public void RP18_Clear_ResetsBossPendingDebt()
        {
            Encounter enc = MakeEncounter(1, 260.0f);
            Step(enc, 1);

            enc.MarkBossPending();
            KillAllEnemies(enc);
            Step(enc, 30);

            Assert.IsTrue(enc.BossPending, "Clear 之前应当确实欠着债");
            Assert.Greater(enc.BossPendingTotalSeconds, 0.0f, "Clear 之前 Total 应已累加");
            Assert.Greater(enc.BossPendingIdleSeconds, 0.0f, "Clear 之前 Idle 应已累加");

            enc.Clear();

            Assert.IsFalse(enc.BossPending, "Clear 后不该再欠 BOSS 债");
            Assert.AreEqual(0.0f, enc.BossPendingTotalSeconds, 1e-6f, "Clear 后 Total 必须归零");
            Assert.AreEqual(0.0f, enc.BossPendingIdleSeconds, 1e-6f, "Clear 后 Idle 必须归零");
            Assert.AreEqual(0, enc.PendingAwareEnemyCount, "Clear 后判定口径应为 0");
            Assert.AreEqual(RunPhase.Playing, enc.Phase, "Clear 后裁判应回到 Playing");

            // 第二局：不再欠债，能正常判胜。
            enc.SetPlayer(Combatant.CreatePlayer(1, 260.0f, Vec2.Zero));
            enc.Add(Combatant.CreateEnemy(200, new Vec2(1000.0f, 0.0f), 30.0f, 4.0f, 70.0f));
            Step(enc, 1);
            Assert.AreEqual(RunPhase.Playing, enc.Phase, "第二局刚开始应为 Playing");

            KillAllEnemies(enc);
            Step(enc, 1);
            Assert.AreEqual(RunPhase.Won, enc.Phase,
                "第二局不该带着上一局的 BOSS 债 —— 否则永远赢不了");
        }

        /// <summary>
        /// RP-19 Q-1 计时语义：这是软锁兜底（4s 重试 / 12s 强制清位）的判据基础。
        ///
        /// <c>Idle</c> 只在"欠债 且 场上零敌人"时累加，场上一旦有敌人**立刻归零**；
        /// <c>Total</c> 无条件累加，仅供诊断。
        ///
        /// 为什么必须这么分：建场即置位的语义下，玩家正常清杂兵要花几十秒到几分钟，
        /// 拿"自置位起的总时长"做超时判据会 100% 误触发 ——
        /// 结果不是"防住软锁"，而是"BOSS 永远不出场"。
        /// </summary>
        [Test]
        public void RP19_BossPendingTimers_IdleCountsOnlyWhenFieldIsEmpty()
        {
            Encounter enc = MakeEncounter(2, 260.0f);
            enc.MarkBossPending();

            Assert.AreEqual(0.0f, enc.BossPendingTotalSeconds, 1e-6f, "置位瞬间 Total 应为 0");
            Assert.AreEqual(0.0f, enc.BossPendingIdleSeconds, 1e-6f, "置位瞬间 Idle 应为 0");

            // 第一段：场上有敌人，推 60 步（= 1 秒）。
            Step(enc, 60);

            Assert.AreEqual(2, enc.AliveEnemyCount, "这一段敌人应全都活着");
            Assert.AreEqual(1.0f, enc.BossPendingTotalSeconds, 0.02f, "Total 应无条件累加到约 1 秒");
            Assert.AreEqual(0.0f, enc.BossPendingIdleSeconds, 1e-6f,
                "场上有敌人时 Idle 必须恒为 0 —— 否则玩家慢慢清怪也会被兜底误杀");

            // 第二段：清空敌人，再推 60 步。
            KillAllEnemies(enc);
            Step(enc, 60);

            Assert.AreEqual(0, enc.AliveEnemyCount);
            Assert.AreEqual(2.0f, enc.BossPendingTotalSeconds, 0.03f, "Total 应继续累加到约 2 秒");
            Assert.AreEqual(1.0f, enc.BossPendingIdleSeconds, 0.02f,
                "场上清空后 Idle 应从 0 起跳，累加到约 1 秒");

            // 第三段：又冒出一只怪（模拟 BOSS 召唤物先于 BOSS 入列这类怪事）
            //         → Idle 必须**立刻归零**，而不是暂停累加。
            enc.Add(Combatant.CreateEnemy(300, new Vec2(1000.0f, 0.0f), 30.0f, 4.0f, 70.0f));
            Step(enc, 1);

            Assert.AreEqual(1, enc.AliveEnemyCount);
            Assert.AreEqual(0.0f, enc.BossPendingIdleSeconds, 1e-6f,
                "只要场上有敌人，Idle 必须立刻归零 —— 软锁判据要的是**连续**滞留时长");
            Assert.Greater(enc.BossPendingTotalSeconds, 2.0f, "Total 不受影响，继续累加");
        }
    }
}
