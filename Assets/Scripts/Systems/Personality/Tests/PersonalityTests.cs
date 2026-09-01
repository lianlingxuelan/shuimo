// -----------------------------------------------------------------------------
// PersonalityTests.cs —— Xianxia.Personality 的 EditMode 自测（无引擎依赖）
//
// 覆盖蓝图 1.5 的核心不变量：
//   · 性格值随战斗选择变化（玩出来），且钳制在 0–Max
//   · 性格 → 战斗系数（冲动先手 / 隐忍反击 / 冷静命中 / 贪婪拾取与反噬）
//   · 性格 × 正魔联动：悟性低 + 魔道高 → 入魔风险高
// -----------------------------------------------------------------------------

using NUnit.Framework;
using Xianxia.Personality;

namespace Xianxia.Personality.Tests
{
    [TestFixture]
    public sealed class PersonalityTests
    {
        private static PersonalityConfig DefaultCfg() => new PersonalityConfig();

        [Test]
        public void 初始中性_全为0()
        {
            var p = new PersonalityProfile(DefaultCfg());
            Assert.AreEqual(0f, p.Wuxing, 1e-4f);
            Assert.AreEqual(0f, p.Tanlan, 1e-4f);
        }

        [Test]
        public void 克己普攻_悟性升()
        {
            var p = new PersonalityProfile(DefaultCfg());
            p.RegisterRestrainedHit();
            Assert.AreEqual(0.3f, p.Wuxing, 1e-4f);
        }

        [Test]
        public void 杀招击杀_冲动升_隐忍降但不破零()
        {
            var p = new PersonalityProfile(DefaultCfg());
            p.RegisterKill(heavy: true);
            Assert.AreEqual(0.6f, p.Chongdong, 1e-4f);
            Assert.AreEqual(0f, p.Yinren, 1e-4f); // -0.3 被钳在 0
            Assert.AreEqual(0.2f, p.Lengjing, 1e-4f); // 任意击杀冷静+
        }

        [Test]
        public void 性格值钳制在0到Max()
        {
            var p = new PersonalityProfile(DefaultCfg());
            p.ApplyRaw(PersonalityTrait.Tanlan, 999f);
            Assert.AreEqual(100f, p.Tanlan, 1e-4f);
            p.ApplyRaw(PersonalityTrait.Tanlan, -5000f);
            Assert.AreEqual(0f, p.Tanlan, 1e-4f);
        }

        [Test]
        public void 冲动满_先手加成封顶()
        {
            var p = new PersonalityProfile(DefaultCfg());
            p.ApplyRaw(PersonalityTrait.Chongdong, 100f);
            var m = p.ComputeCombat();
            Assert.AreEqual(1.40f, m.FirstStrike, 1e-4f); // 1 + 0.40
        }

        [Test]
        public void 隐忍满_反击倍率封顶()
        {
            var p = new PersonalityProfile(DefaultCfg());
            p.ApplyRaw(PersonalityTrait.Yinren, 100f);
            var m = p.ComputeCombat();
            Assert.AreEqual(1.50f, m.CounterMul, 1e-4f); // 1 + 0.50
        }

        [Test]
        public void 冷静满_命中加成封顶()
        {
            var p = new PersonalityProfile(DefaultCfg());
            p.ApplyRaw(PersonalityTrait.Lengjing, 100f);
            var m = p.ComputeCombat();
            Assert.AreEqual(0.30f, m.HitRateBonus, 1e-4f);
        }

        [Test]
        public void 贪婪满_拾取与反噬封顶()
        {
            var p = new PersonalityProfile(DefaultCfg());
            p.ApplyRaw(PersonalityTrait.Tanlan, 100f);
            var m = p.ComputeCombat();
            Assert.AreEqual(1.50f, m.PickupMul, 1e-4f);
            Assert.AreEqual(0.30f, m.BacklashExtra, 1e-4f);
        }

        [Test]
        public void 入魔风险_悟性低魔道高_风险最高()
        {
            var p = new PersonalityProfile(DefaultCfg(), wuxing: 0f);
            Assert.AreEqual(1.0f, p.EnchantRisk(mo: 100f), 1e-4f);
        }

        [Test]
        public void 入魔风险_悟性满_风险为0()
        {
            var p = new PersonalityProfile(DefaultCfg(), wuxing: 100f);
            Assert.AreEqual(0.0f, p.EnchantRisk(mo: 100f), 1e-4f);
        }

        [Test]
        public void 入魔风险_中等()
        {
            var p = new PersonalityProfile(DefaultCfg(), wuxing: 50f);
            Assert.AreEqual(0.25f, p.EnchantRisk(mo: 50f), 1e-4f); // 0.5 * 0.5
        }
    }
}
