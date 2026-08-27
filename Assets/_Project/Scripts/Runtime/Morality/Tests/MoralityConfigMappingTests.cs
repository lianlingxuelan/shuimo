// -----------------------------------------------------------------------------
// MoralityConfigMappingTests.cs —— Unity 侧 ScriptableObject → 内核配置 映射自测
//
// 验证 MoralityPersonalityConfig（Unity 侧可调参数）能正确写进纯逻辑内核配置，
// 保证"编辑器调数 → 内核生效"这条链在编译期就有人盯着。
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using Xianxia.Morality;
using Xianxia.Personality;
using Xianxia.Unity.T2.Morality;

namespace Xianxia.Unity.T2.Morality.Tests
{
    [TestFixture]
    public sealed class MoralityConfigMappingTests
    {
        [Test]
        public void ScriptableObject_映射_产出默认内核配置()
        {
            var so = ScriptableObject.CreateInstance<MoralityPersonalityConfig>();
            MoralityConfig mc = so.ToMoralityConfig();
            PersonalityConfig pc = so.ToPersonalityConfig();

            Assert.AreEqual(8.0f, mc.HeavyKillMo, 1e-4f);
            Assert.AreEqual(100.0f, mc.EnchantThreshold, 1e-4f);
            Assert.AreEqual(10.0f, mc.DefenseBonusMax, 1e-4f);
            Assert.AreEqual(0.40f, pc.ChongdongFirstStrike, 1e-4f);
            Assert.AreEqual(100.0f, pc.Max, 1e-4f);
        }
    }
}
