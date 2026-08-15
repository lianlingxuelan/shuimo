// -----------------------------------------------------------------------------
// P2_2_EnemyAiBrainTests.cs —— 敌人感知/追击决策核验收（asmdef: Xianxia.Unity.T2.Tests）
//
// 【为什么值得单独测】
// AI 状态机是最容易出「隐性」bug 的地方，而这些 bug 在 PlayMode 里靠肉眼极难定位：
//   · 边界抖动：玩家站在警戒圈边缘，敌人在 Chase/Return 间每帧互切，看起来像抽风；
//   · 追出天边：玩家把怪一路拖走，整张地图的怪连成一串；
//   · 永久返巢：半径参数配反，敌人待在自家圈里却一直往圆心走，永远不巡逻。
// 这三类都不是「跑一次看一眼」能发现的，必须把转移边逐条钉死。
//
// EnemyAiBrain.Decide 是纯函数（无 MonoBehaviour、不碰 Transform、不读时钟、无副作用），
// 所以全部用例都是 EditMode 纯 [Test]，不需要场景、不需要 PlayMode、不需要真实时间推进。
//
// 【怎么跑】
//   Window → General → Test Runner → EditMode → 选 Xianxia.Unity.T2.Tests → Run All
//
// 【验证状态 —— 请如实理解】
// 编写环境**没有 Unity、也没有 dotnet**，本文件**未经编译、未经运行**。
// 它的正确性目前只由「人工比对生产代码签名」保证。
//
// 【红线】只用公开 API（EnemyAiInput / EnemyAiDecision / EnemyAiBrain.Decide），
// 不反射任何私有字段。
//
// 【统一基准参数】圆心原点，patrolRadius 300，alert 220，lose 340，leash 520，attackRange 46。
// 半径关系刻意设成「alert < lose < leash」，即真实推荐配置。
//
// 【覆盖的用例】
//   EA-01  玩家在警戒外 → 保持 Patrol，移动目标为巡逻目标
//   EA-02  玩家进入警戒半径 → 转 Chase，移动目标为玩家位置
//   EA-03  ★滞回：Chase 中玩家退到 alert 与 lose 之间 → 仍维持 Chase（防边界抖动）
//   EA-04  Chase 中玩家超出 lose → 转 Return，移动目标为圆心
//   EA-05  玩家无效（找不到/已亡）→ 巡逻态不起警
//   EA-06  Chase 中玩家失效 → 转 Return
//   EA-07  ★拴绳优先：离圆心超 leash → 强制 Return，玩家贴身也不追
//   EA-08  ★Return 不可打断：圈外且玩家贴身 → 仍 Return（不被警戒抢走）
//   EA-09  Return 回到巡逻圈内 → 恢复 Patrol
//   EA-10  Chase 贴身（<= attackRange）→ 停步且给出攻击信号
//   EA-11  ★防呆：lose 配得比 alert 小时被抬平，不产生抖动
//   EA-12  ★防呆：leash 配得比 patrolRadius 小时圈内不被误判返巢
//   EA-13  确定性：同一输入连续两次调用输出逐字段相同
//   EA-14  攻击信号只可能出现在 Chase；Patrol / Return 永不给攻击信号
//
// 【本文件明确未覆盖】
//   · EnemyPatrol 的位移/朝向/动画/攻击冷却节流（需 GameObject + CharacterView，
//     且冷却依赖调用方传入 dt，属集成层，留待 PlayMode 手测）；
//   · 敌人攻击造成伤害 —— 当前设计**不造成伤害**，ShouldAttack 仅为表现信号。
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>敌人感知/追击决策核（EnemyAiBrain）的 EditMode 验收套件。</summary>
    public sealed class P2_2_EnemyAiBrainTests
    {
        private const float PatrolRadius = 300.0f;
        private const float AlertRadius = 220.0f;
        private const float LoseRadius = 340.0f;
        private const float LeashRadius = 520.0f;
        private const float AttackRange = 46.0f;

        private static readonly Vector2 Home = Vector2.zero;
        private static readonly Vector2 PatrolTarget = new Vector2(100.0f, 0.0f);

        /// <summary>按统一基准参数构造输入快照。</summary>
        private static EnemyAiInput MakeInput(
            EnemyAiState state,
            Vector2 self,
            Vector2 player,
            bool playerValid)
        {
            EnemyAiInput i = new EnemyAiInput();
            i.State = state;
            i.SelfPos = self;
            i.PlayerPos = player;
            i.PlayerValid = playerValid;
            i.PatrolCenter = Home;
            i.PatrolRadius = PatrolRadius;
            i.PatrolTarget = PatrolTarget;
            i.AlertRadius = AlertRadius;
            i.LoseRadius = LoseRadius;
            i.LeashRadius = LeashRadius;
            i.AttackRange = AttackRange;
            return i;
        }

        private static void AssertVec(Vector2 expected, Vector2 actual, string msg)
        {
            Assert.AreEqual(expected.x, actual.x, 0.001f, msg + " (x)");
            Assert.AreEqual(expected.y, actual.y, 0.001f, msg + " (y)");
        }

        // =====================================================================
        // EA-01 / EA-02：基本起警
        // =====================================================================

        [Test]
        public void EA01_PlayerOutsideAlert_StaysPatrol()
        {
            EnemyAiInput i = MakeInput(EnemyAiState.Patrol, Home, new Vector2(400.0f, 0.0f), true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Patrol, d.State, "警戒外不应起警");
            Assert.IsTrue(d.Moving, "巡逻应继续移动");
            Assert.IsFalse(d.ShouldAttack, "巡逻不应给攻击信号");
            AssertVec(PatrolTarget, d.MoveTarget, "巡逻目标应原样沿用");
        }

        [Test]
        public void EA02_PlayerEntersAlert_TurnsChase()
        {
            Vector2 player = new Vector2(200.0f, 0.0f); // 200 <= alert 220
            EnemyAiInput i = MakeInput(EnemyAiState.Patrol, Home, player, true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Chase, d.State, "进入警戒半径应转追击");
            Assert.IsTrue(d.Moving, "追击应移动");
            Assert.IsFalse(d.ShouldAttack, "距离 200 远大于攻击距离 46，不应起手");
            AssertVec(player, d.MoveTarget, "追击目标应为玩家位置");
        }

        // =====================================================================
        // EA-03：滞回 —— 本套件最重要的一条
        // =====================================================================

        [Test]
        public void EA03_Hysteresis_ChaseHeldBetweenAlertAndLose()
        {
            // 距离 300：已超出 alert(220)，但未超出 lose(340)。
            // 若实现用同一个半径判进出，这里会掉回 Return，造成边界每帧抖动。
            Vector2 player = new Vector2(300.0f, 0.0f);
            EnemyAiInput i = MakeInput(EnemyAiState.Chase, Home, player, true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Chase, d.State,
                "alert 与 lose 之间必须维持追击，否则边界抖动");
            AssertVec(player, d.MoveTarget, "维持追击时目标仍为玩家");
        }

        [Test]
        public void EA04_PlayerBeyondLose_TurnsReturn()
        {
            Vector2 self = new Vector2(250.0f, 0.0f);   // 离家 250，未超 leash
            Vector2 player = new Vector2(650.0f, 0.0f); // 距敌 400 > lose 340
            EnemyAiInput i = MakeInput(EnemyAiState.Chase, self, player, true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Return, d.State, "超出脱战半径应返巢");
            Assert.IsFalse(d.ShouldAttack, "返巢不应给攻击信号");
            AssertVec(Home, d.MoveTarget, "返巢目标应为巡逻圆心");
        }

        // =====================================================================
        // EA-05 / EA-06：玩家失效
        // =====================================================================

        [Test]
        public void EA05_InvalidPlayer_NeverAlerts()
        {
            // 玩家坐标就在脚边，但 PlayerValid = false（找不到玩家 / 玩家已亡 / 终局）。
            EnemyAiInput i = MakeInput(EnemyAiState.Patrol, Home, new Vector2(10.0f, 0.0f), false);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Patrol, d.State, "玩家无效时不得起警");
            Assert.IsFalse(d.ShouldAttack, "玩家无效时不得给攻击信号");
        }

        [Test]
        public void EA06_ChaseLosesInvalidPlayer_TurnsReturn()
        {
            Vector2 self = new Vector2(250.0f, 0.0f);
            EnemyAiInput i = MakeInput(EnemyAiState.Chase, self, new Vector2(260.0f, 0.0f), false);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Return, d.State, "追击中目标失效应返巢");
            AssertVec(Home, d.MoveTarget, "返巢目标应为圆心");
        }

        // =====================================================================
        // EA-07 / EA-08：拴绳与返巢不可打断
        // =====================================================================

        [Test]
        public void EA07_BeyondLeash_ForcesReturnEvenIfPlayerAdjacent()
        {
            // 离家 600 > leash 520，且玩家就贴在身上（距离 5）。
            Vector2 self = new Vector2(600.0f, 0.0f);
            Vector2 player = new Vector2(605.0f, 0.0f);
            EnemyAiInput i = MakeInput(EnemyAiState.Patrol, self, player, true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Return, d.State,
                "超出拴绳半径必须强制返巢，玩家再近也不追");
            Assert.IsFalse(d.ShouldAttack, "拴绳返巢期间不得给攻击信号");
            AssertVec(Home, d.MoveTarget, "返巢目标应为圆心");
        }

        [Test]
        public void EA08_ReturnNotInterruptible_OutsidePatrolCircle()
        {
            // 离家 400：在巡逻圈(300)外，但在拴绳(520)内 —— 排除拴绳分支的干扰，
            // 单独验证「返巢途中不被警戒抢走」。玩家贴身（距离 10）。
            Vector2 self = new Vector2(400.0f, 0.0f);
            Vector2 player = new Vector2(410.0f, 0.0f);
            EnemyAiInput i = MakeInput(EnemyAiState.Return, self, player, true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Return, d.State,
                "返巢必须先回到圈内，否则会与追击在边界反复互切");
            Assert.IsFalse(d.ShouldAttack, "返巢途中不得给攻击信号");
            AssertVec(Home, d.MoveTarget, "返巢目标应为圆心");
        }

        [Test]
        public void EA09_ReturnReachesHome_RecoversPatrol()
        {
            // 离家 200 <= patrolRadius 300：已回到圈内。
            EnemyAiInput i = MakeInput(
                EnemyAiState.Return, new Vector2(200.0f, 0.0f), new Vector2(9000.0f, 0.0f), true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Patrol, d.State, "回到巡逻圈内应恢复巡逻");
            Assert.IsTrue(d.Moving, "恢复巡逻后应继续移动");
            AssertVec(PatrolTarget, d.MoveTarget, "恢复巡逻应走巡逻目标");
        }

        // =====================================================================
        // EA-10：贴身攻击
        // =====================================================================

        [Test]
        public void EA10_WithinAttackRange_StopsAndSignalsAttack()
        {
            Vector2 player = new Vector2(30.0f, 0.0f); // 30 <= attackRange 46
            EnemyAiInput i = MakeInput(EnemyAiState.Chase, Home, player, true);
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Chase, d.State, "贴身仍属追击态");
            Assert.IsFalse(d.Moving, "进入攻击距离应停步，避免糊脸重叠");
            Assert.IsTrue(d.ShouldAttack, "进入攻击距离应给出攻击表现信号");
        }

        // =====================================================================
        // EA-11 / EA-12：参数配错时的防呆
        // =====================================================================

        [Test]
        public void EA11_LoseSmallerThanAlert_ClampedNoFlicker()
        {
            // 故意把 lose 配成比 alert 小（100 < 220）。
            // 不做 clamp 的话，距离 200 会既满足「起警」又满足「脱战」，产生抖动。
            EnemyAiInput i = MakeInput(EnemyAiState.Chase, Home, new Vector2(200.0f, 0.0f), true);
            i.LoseRadius = 100.0f;
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Chase, d.State,
                "lose 小于 alert 时应被抬平到 alert，维持追击而不是抖动");
        }

        [Test]
        public void EA12_LeashSmallerThanPatrolRadius_ClampedNoPermanentReturn()
        {
            // 故意把 leash 配成比巡逻半径小（50 < 300）。
            // 不做 clamp 的话，敌人待在自家圈里(离家 100)也会被判「跑太远」而永久返巢。
            EnemyAiInput i = MakeInput(
                EnemyAiState.Patrol, new Vector2(100.0f, 0.0f), new Vector2(9000.0f, 0.0f), true);
            i.LeashRadius = 50.0f;
            EnemyAiDecision d = EnemyAiBrain.Decide(i);

            Assert.AreEqual(EnemyAiState.Patrol, d.State,
                "leash 小于巡逻半径时应被抬平，圈内不得被误判返巢");
            AssertVec(PatrolTarget, d.MoveTarget, "圈内应正常走巡逻目标");
        }

        // =====================================================================
        // EA-13 / EA-14：确定性与攻击信号边界
        // =====================================================================

        [Test]
        public void EA13_Deterministic_SameInputSameOutput()
        {
            EnemyAiInput i = MakeInput(EnemyAiState.Chase, Home, new Vector2(30.0f, 0.0f), true);
            EnemyAiDecision a = EnemyAiBrain.Decide(i);
            EnemyAiDecision b = EnemyAiBrain.Decide(i);

            Assert.AreEqual(a.State, b.State, "状态应确定");
            Assert.AreEqual(a.Moving, b.Moving, "移动标志应确定");
            Assert.AreEqual(a.ShouldAttack, b.ShouldAttack, "攻击标志应确定");
            AssertVec(a.MoveTarget, b.MoveTarget, "移动目标应确定");
        }

        [Test]
        public void EA14_AttackSignalOnlyInChase()
        {
            // 巡逻：玩家极远
            EnemyAiDecision patrol = EnemyAiBrain.Decide(
                MakeInput(EnemyAiState.Patrol, Home, new Vector2(9000.0f, 0.0f), true));
            Assert.IsFalse(patrol.ShouldAttack, "巡逻态不得给攻击信号");

            // 返巢：圈外且玩家贴身
            EnemyAiDecision ret = EnemyAiBrain.Decide(
                MakeInput(EnemyAiState.Return, new Vector2(400.0f, 0.0f), new Vector2(405.0f, 0.0f), true));
            Assert.IsFalse(ret.ShouldAttack, "返巢态不得给攻击信号");

            // 拴绳强制返巢：玩家贴身
            EnemyAiDecision leash = EnemyAiBrain.Decide(
                MakeInput(EnemyAiState.Patrol, new Vector2(600.0f, 0.0f), new Vector2(602.0f, 0.0f), true));
            Assert.IsFalse(leash.ShouldAttack, "拴绳返巢不得给攻击信号");

            // 对照：追击贴身才应给信号
            EnemyAiDecision chase = EnemyAiBrain.Decide(
                MakeInput(EnemyAiState.Chase, Home, new Vector2(20.0f, 0.0f), true));
            Assert.IsTrue(chase.ShouldAttack, "唯有追击贴身才给攻击信号");
        }
    }
}
