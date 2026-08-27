using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class MinimapProjectionTests
    {
        [Test]
        public void Project_MapsWorldCornersAndCenterIntoNormalizedMapSpace()
        {
            Assert.AreEqual(new Vector2(0.05f, 0.05f), MinimapProjection.Project(Vector2.zero, new Vector2(1000f, 800f), 0.05f));
            Assert.AreEqual(new Vector2(0.5f, 0.5f), MinimapProjection.Project(new Vector2(500f, 400f), new Vector2(1000f, 800f), 0.05f));
            Assert.AreEqual(new Vector2(0.95f, 0.95f), MinimapProjection.Project(new Vector2(1000f, 800f), new Vector2(1000f, 800f), 0.05f));
        }

        [Test]
        public void Project_InvalidWorldSizeReturnsMapCenterWithoutNaN()
        {
            Vector2 result = MinimapProjection.Project(new Vector2(10f, 20f), Vector2.zero, 0.05f);
            Assert.AreEqual(new Vector2(0.5f, 0.5f), result);
            Assert.IsFalse(float.IsNaN(result.x) || float.IsNaN(result.y));
        }
    }
}
