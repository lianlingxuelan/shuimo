using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class BambooForegroundDressingPlanTests
    {
        [Test]
        public void Create_AlwaysAddsSeveralDistinctInkBambooClumps()
        {
            BambooForegroundDressingPlacement[] placements = BambooForegroundDressingPlan.Create(700f);

            Assert.GreaterOrEqual(placements.Length, 6);
            Assert.AreNotEqual(placements[0].Position, placements[1].Position);
        }

        [Test]
        public void Create_KeepsTheCentralCombatLaneVisuallyOpen()
        {
            BambooForegroundDressingPlacement[] placements = BambooForegroundDressingPlan.Create(700f);

            for (int i = 0; i < placements.Length; i++)
            {
                Assert.Greater(Mathf.Abs(placements[i].Position.x), 180f);
                Assert.Greater(placements[i].Position.magnitude, 210f);
            }
        }

        [Test]
        public void Create_ProvidesLayerAndScaleVariation()
        {
            BambooForegroundDressingPlacement[] placements = BambooForegroundDressingPlan.Create(700f);

            Assert.AreNotEqual(placements[0].Scale, placements[1].Scale);
            Assert.Less(placements[0].SortingOrder, placements[placements.Length - 1].SortingOrder);
        }
    }
}
