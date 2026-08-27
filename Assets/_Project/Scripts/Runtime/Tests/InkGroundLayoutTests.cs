using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class InkGroundLayoutTests
    {
        [Test]
        public void RoadAnchors_StayInsideTheRequestedGroveExtent()
        {
            Vector2[] anchors = InkGroundLayout.CreateRoadAnchors(420.0f);

            Assert.GreaterOrEqual(anchors.Length, 4);
            foreach (Vector2 anchor in anchors)
            {
                Assert.LessOrEqual(Mathf.Abs(anchor.x), 420.0f);
                Assert.LessOrEqual(Mathf.Abs(anchor.y), 420.0f);
            }
        }

        [Test]
        public void HeroRockPosition_SitsBesideTheRoadButInsideTheGrove()
        {
            Vector2 position = InkGroundLayout.CreateHeroRockPosition(420.0f);

            Assert.That(position.x, Is.EqualTo(142.8f).Within(0.01f));
            Assert.That(position.y, Is.EqualTo(-50.4f).Within(0.01f));
            Assert.Less(Mathf.Abs(position.x), 420.0f);
            Assert.Less(Mathf.Abs(position.y), 420.0f);
        }
    }
}
