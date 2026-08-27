// -----------------------------------------------------------------------------
// P2_3_EnemyContactAttackTests.cs —— 敌人接触伤害「是否该结算」决策核验收
//                                         （asmdef: Xianxia.Unity.T2.Tests）
//
// 【为什么值得单独测】
// 接触伤害最容易出「隐性」bug，而且这些 bug 在 PlayMode 里靠肉眼极难定位：
//   · 掉血频率失控：冷却没生效，玩家贴身后每帧都在掉血；
//   · 贴身却砍空：距离判定边界差一个 epsilon，明明贴脸却判定为「不在攻击距离内」；
//   · 冷却回填错：开火后回填的冷却不是 attackInterval，导致下一刀来得过早或过晚。
// 这三类都必须用单测把边界逐条钉死，而不是「跑一次看一眼血条」。
//
// EnemyContactAttack.Decide 是纯函数（无 MonoBehaviour、不碰内核、不读时钟、无副作用），
// 所以全部用例都是 EditMode 纯 [Test]，不需要场景、不需要 PlayMode、不需要真实时间推进。
//
// 【怎么跑】
//   Window → General → Test Runner → EditMode → 选 Xianxia.Unity.T2.Tests → Run All
//   （或搜索框输入 P2_3 只看这一组）
//
// 【验证状态 —— 请如实理解】
// 编写环境**没有 Unity、也没有 dotnet**，本文件**未经编译、未经运行**。
// 它的正确性目前由：① 人工比对生产代码签名；② Node.js 复刻脚本 ai_contact_check.js
// 跑全部用例 PASS 16 / FAIL 0，双重保证。
//
// 【红线】只用公开 API（EnemyContactInput / EnemyContactDecision / EnemyContactAttack.Decide），
// 不反射任何私有字段。
//
// 【统一基准参数】attackRange 46，attackInterval 1.1。半径关系沿用在 EnemyAiBrain 的推荐值。
//
// 【覆盖的用例】
//   EC-01  贴身 + 冷却到点 → 开火，回填 attackInterval
//   EC-02  贴身 + 冷却未到点 → 不开火，冷却原样回传
//   EC-03  圈外 + 冷却到点 → 不开火（距离门槛优先于冷却）
//   EC-04  圈外 + 冷却未到点 → 不开火
//   EC-05  正好在攻击距离边界（dist == attackRange）→ 算贴身 → 开火
//   EC-06  刚出边界一格（dist == attackRange + 1e-4）→ 不开火
//   EC-07  冷却恰好为 0 → 开火（边界）
//   EC-08  冷却为极小正数 → 不开火
//   EC-09  attackInterval 为 0 → 开火后回填下限 0.1（Mathf.Max 兜底）
//   EC-10  确定性：同一输入连续两次调用输出逐字段相同
//   EC-11  attackRange 0：dist 0 算贴身开火；dist>0 不开火
//   EC-12  负冷却输入被夹到 0 → 开火
//   EC-13  dist 为 NaN/Infinity → 视为圈外，不开火（防呆）
//   EC-14  非贴身但冷却到点且开火后回填值 == attackInterval（不污染其他字段）
//
// 【本文件明确未覆盖】
//   · EnemyPatrol 把请求经 IDamageRequester 抛给 CombatBridge、Bridge 再调内核结算伤害
//     （属集成层，且依赖内核 Combatant，留待 CombatBridge 接好后的 PlayMode / 内核单测）；
//   · 伤害数值（contactDamage 取值、内核减伤结果）—— 那是内核 DamageResolver 的验收范围。
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>敌人接触伤害决策核（EnemyContactAttack）的 EditMode 验收套件。</summary>
    public sealed class P2_3_EnemyContactAttackTests
    {
        private const float AttackRange = 46.0f;
        private const float AttackInterval = 1.1f;

        private static EnemyContactInput Make(float dist, float cd, float range = AttackRange, float interval = AttackInterval)
        {
            return new EnemyContactInput
            {
                DistanceToPlayer = dist,
                AttackRange = range,
                CooldownRemaining = cd,
                AttackInterval = interval,
            };
        }

        [Test]
        public void EC01_InRange_CooldownReady_Fires()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(10.0f, 0.0f));
            Assert.IsTrue(d.ShouldFire);
            Assert.AreEqual(AttackInterval, d.CooldownRemaining, 1e-5f);
        }

        [Test]
        public void EC02_InRange_CooldownPending_NoFire()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(10.0f, 0.5f));
            Assert.IsFalse(d.ShouldFire);
            Assert.AreEqual(0.5f, d.CooldownRemaining, 1e-5f);
        }

        [Test]
        public void EC03_OutOfRange_CooldownReady_NoFire()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(200.0f, 0.0f));
            Assert.IsFalse(d.ShouldFire);
            Assert.AreEqual(0.0f, d.CooldownRemaining, 1e-5f);
        }

        [Test]
        public void EC04_OutOfRange_CooldownPending_NoFire()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(200.0f, 0.5f));
            Assert.IsFalse(d.ShouldFire);
            Assert.AreEqual(0.5f, d.CooldownRemaining, 1e-5f);
        }

        [Test]
        public void EC05_ExactlyAtBoundary_Fires()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(AttackRange, 0.0f));
            Assert.IsTrue(d.ShouldFire);
        }

        [Test]
        public void EC06_JustOutsideBoundary_NoFire()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(AttackRange + 1e-4f, 0.0f));
            Assert.IsFalse(d.ShouldFire);
        }

        [Test]
        public void EC07_CooldownExactlyZero_Fires()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(0.0f, 0.0f));
            Assert.IsTrue(d.ShouldFire);
        }

        [Test]
        public void EC08_CooldownTinyPositive_NoFire()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(0.0f, 1e-6f));
            Assert.IsFalse(d.ShouldFire);
        }

        [Test]
        public void EC09_ZeroInterval_FlooredToPointOne()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(0.0f, 0.0f, AttackRange, 0.0f));
            Assert.IsTrue(d.ShouldFire);
            Assert.AreEqual(0.1f, d.CooldownRemaining, 1e-5f);
        }

        [Test]
        public void EC10_Deterministic_SameInputSameOutput()
        {
            EnemyContactInput a = Make(10.0f, 0.3f);
            EnemyContactInput b = Make(10.0f, 0.3f);
            EnemyContactDecision da = EnemyContactAttack.Decide(a);
            EnemyContactDecision db = EnemyContactAttack.Decide(b);
            Assert.AreEqual(da.ShouldFire, db.ShouldFire);
            Assert.AreEqual(da.CooldownRemaining, db.CooldownRemaining, 1e-5f);
        }

        [Test]
        public void EC11_ZeroRange_DistZeroFires_DistPositiveNoFire()
        {
            Assert.IsTrue(EnemyContactAttack.Decide(Make(0.0f, 0.0f, 0.0f)).ShouldFire);
            Assert.IsFalse(EnemyContactAttack.Decide(Make(1.0f, 0.0f, 0.0f)).ShouldFire);
        }

        [Test]
        public void EC12_NegativeCooldown_ClampedToZero_Fires()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(0.0f, -5.0f));
            Assert.IsTrue(d.ShouldFire);
            Assert.AreEqual(AttackInterval, d.CooldownRemaining, 1e-5f);
        }

        [Test]
        public void EC13_NonFiniteDistance_TreatedAsOutOfRange()
        {
            Assert.IsFalse(EnemyContactAttack.Decide(Make(float.NaN, 0.0f)).ShouldFire);
            Assert.IsFalse(EnemyContactAttack.Decide(Make(float.PositiveInfinity, 0.0f)).ShouldFire);
        }

        [Test]
        public void EC14_FireRefillsExactInterval()
        {
            EnemyContactDecision d = EnemyContactAttack.Decide(Make(10.0f, 0.0f, 80.0f, 2.5f));
            Assert.IsTrue(d.ShouldFire);
            Assert.AreEqual(2.5f, d.CooldownRemaining, 1e-5f);
        }
    }
}
