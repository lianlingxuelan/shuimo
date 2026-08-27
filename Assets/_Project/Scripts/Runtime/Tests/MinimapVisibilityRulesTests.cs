using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class MinimapVisibilityRulesTests
    {
        [TestCase(MinimapMarkerKind.Player, 9999f, true)]
        [TestCase(MinimapMarkerKind.ChapterEnemy, 9999f, true)]
        [TestCase(MinimapMarkerKind.QuestTarget, 9999f, true)]
        [TestCase(MinimapMarkerKind.Enemy, 120f, true)]
        [TestCase(MinimapMarkerKind.Enemy, 121f, false)]
        public void ShouldShow_RespectsPermanentAndLocalMarkers(MinimapMarkerKind kind, float distance, bool expected)
        {
            bool actual = MinimapVisibilityRules.ShouldShow(kind, Vector2.zero, Vector2.right * distance, 120f);
            Assert.AreEqual(expected, actual);
        }

        [TestCase(MinimapMarkerKind.Npc)]
        [TestCase(MinimapMarkerKind.Shop)]
        [TestCase(MinimapMarkerKind.Building)]
        public void ShouldShow_PermanentWorldMarkersRemainVisible(MinimapMarkerKind kind)
        {
            Assert.IsTrue(MinimapVisibilityRules.ShouldShow(kind, Vector2.zero, Vector2.right * 9999f, 1f));
        }
    }
}
