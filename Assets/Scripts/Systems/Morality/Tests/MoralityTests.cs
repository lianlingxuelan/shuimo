// -----------------------------------------------------------------------------
// MoralityTests.cs —— Xianxia.Morality 的 EditMode 自测（无引擎依赖）
//
// 覆盖蓝图 1.4 的核心不变量：
//   · 此消彼长与钳制（0–100）
//   · 路线推导（正 / 侠 / 魔 / 入魔）
//   · 战斗系数（正道防高攻抑 / 魔道攻高防低）
//   · 魔道反噬（概率随魔道升、入魔后消失、伤害按最大生命比例）
// -----------------------------------------------------------------------------

using NUnit.Framework;
using Xianxia.Morality;

namespace Xianxia.Morality.Tests
{
    [TestFixture]
    public sealed class MoralityTests
    {
        private static MoralityConfig DefaultCfg() => new MoralityConfig();

        [Test]
        public void 初始默认偏正道_路线为正()
        {
            var m = new MoralityManager(DefaultCfg());
            Assert.AreEqual(50f, m.Zheng, 1e-4f);
            Assert.AreEqual(0f, m.Mo, 1e-4f);
            Assert.AreEqual(MoralityRoute.Zheng, m.Route);
        }

        [Test]
        public void 克己普攻_正道升_魔道不破零()
        {
            var m = new MoralityManager(DefaultCfg());
            m.ApplyRestrainedHit();
            Assert.AreEqual(51.5f, m.Zheng, 1e-4f);
            Assert.AreEqual(0f, m.Mo, 1e-4f); // 回落被钳在 0
        }

        [Test]
        public void 连续杀招_魔道累积_最终入魔()
        {
            var m = new MoralityManager(DefaultCfg());
            for (int i = 0; i < 20; i++) m.ApplyHeavyKill();
            Assert.GreaterOrEqual(m.Mo, 100f);
            Assert.IsTrue(m.IsEnchanted);
            Assert.AreEqual(MoralityRoute.Enchanted, m.Route);
        }

        [Test]
        public void 杀招足够多_转向魔道路线()
        {
            var m = new MoralityManager(DefaultCfg());
            for (int i = 0; i < 6; i++) m.ApplyHeavyKill(); // Mo +48, Zheng -24 → Balance 48-(-26)=74? 算一下
            // Zheng=50-24=26, Mo=0+48=48 → Balance=22 < 30 → 仍侠道
            Assert.AreEqual(MoralityRoute.Xia, m.Route);
            m.ApplyHeavyKill(); // +8 Mo=56, -4 Zheng=22 → Balance=34 ≥30 → 魔道
            Assert.AreEqual(MoralityRoute.Mo, m.Route);
        }

        [Test]
        public void 正道满_防御高攻击被压制()
        {
            var m = new MoralityManager(DefaultCfg(), zheng: 100f, mo: 0f);
            var mods = m.ComputeCombat();
            Assert.AreEqual(0.80f, mods.AttackMul, 1e-4f);  // 1 - 0.20
            Assert.AreEqual(1.50f, mods.DefenseMul, 1e-4f); // 1 + 0.50
        }

        [Test]
        public void 魔道满_攻击高防御低()
        {
            var m = new MoralityManager(DefaultCfg(), zheng: 0f, mo: 100f);
            var mods = m.ComputeCombat();
            Assert.AreEqual(1.50f, mods.AttackMul, 1e-4f);  // 1 + 0.50
            Assert.AreEqual(0.70f, mods.DefenseMul, 1e-4f); // 1 - 0.30
        }

        [Test]
        public void 反噬_魔道为0_概率命中必触发()
        {
            var cfg = DefaultCfg();
            var r = BacklashResolver.Resolve(cfg, mo: 0f, playerMaxHp: 1000f, rng: () => 0f);
            Assert.IsTrue(r.Triggered);
            Assert.AreEqual(40f, r.Damage, 1e-3f); // 1000 * 0.04
        }

        [Test]
        public void 反噬_入魔后消失()
        {
            var cfg = DefaultCfg();
            var r = BacklashResolver.Resolve(cfg, mo: 100f, playerMaxHp: 1000f, rng: () => 0f);
            Assert.IsFalse(r.Triggered);
            Assert.AreEqual(0f, r.Damage, 1e-3f);
        }

        [Test]
        public void 反噬_随机为1_永不触发()
        {
            var cfg = DefaultCfg();
            var r = BacklashResolver.Resolve(cfg, mo: 80f, playerMaxHp: 1000f, rng: () => 1f);
            Assert.IsFalse(r.Triggered);
        }

        [Test]
        public void 反噬_魔道越高伤害越高()
        {
            var cfg = DefaultCfg();
            var low = BacklashResolver.Resolve(cfg, mo: 20f, playerMaxHp: 1000f, rng: () => 0f);
            var high = BacklashResolver.Resolve(cfg, mo: 90f, playerMaxHp: 1000f, rng: () => 0f);
            Assert.Greater(high.Damage, low.Damage);
        }
    }
}
