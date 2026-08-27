using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class FirstChapterLayoutTests
    {
        [Test]
        public void Build_AlwaysPlacesEncounterAheadOfSpawn()
        {
            Vector2 spawn = new Vector2(100.0f, 200.0f);
            FirstChapterLayout layout = FirstChapterLayout.Build(spawn);

            Assert.Greater(layout.RoadEncounter.y, spawn.y);
            Assert.Greater(Vector2.Distance(layout.RoadEncounter, layout.GuideNpc), 100.0f);
        }

        [Test]
        public void Build_AlwaysKeepsShopOffTheCombatLane()
        {
            FirstChapterLayout layout = FirstChapterLayout.Build(Vector2.zero);

            Assert.Greater(Mathf.Abs(layout.Shop.x), FirstChapterLayout.RoadHalfWidth);
        }

        [Test]
        public void IsInRoadLane_LeavesEncounterAndGuideClearForCombatReadability()
        {
            FirstChapterLayout layout = FirstChapterLayout.Build(Vector2.zero);

            Assert.IsTrue(layout.IsInRoadLane(layout.RoadEncounter));
            Assert.IsTrue(layout.IsInRoadLane(layout.GuideNpc));
            Assert.IsFalse(layout.IsInRoadLane(layout.Shop));
        }

        [Test]
        public void IsNearReservedStoryNode_ProtectsCombatAndGuideFromDenseDressing()
        {
            FirstChapterLayout layout = FirstChapterLayout.Build(Vector2.zero);

            Assert.IsTrue(layout.IsNearReservedStoryNode(layout.RoadEncounter));
            Assert.IsTrue(layout.IsNearReservedStoryNode(layout.GuideNpc));
            Assert.IsFalse(layout.IsNearReservedStoryNode(layout.Shop));
        }
    }
}
