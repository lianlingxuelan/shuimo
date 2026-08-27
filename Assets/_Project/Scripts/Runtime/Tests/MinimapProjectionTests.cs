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

        [TestCase(float.NaN, 800f)]
        [TestCase(1000f, float.NaN)]
        [TestCase(float.PositiveInfinity, 800f)]
        [TestCase(1000f, float.PositiveInfinity)]
        public void Project_NonFiniteWorldSizeReturnsMapCenter(float width, float height)
        {
            Assert.AreEqual(new Vector2(0.5f, 0.5f),
                MinimapProjection.Project(new Vector2(10f, 20f), new Vector2(width, height), 0.05f));
        }

        [Test]
        public void Project_ClampsWorldPositionToMapBounds()
        {
            Assert.AreEqual(new Vector2(0.05f, 0.95f),
                MinimapProjection.Project(new Vector2(-100f, 900f), new Vector2(1000f, 800f), 0.05f));
        }

        [Test]
        public void Project_ClampsPaddingToSupportedRange()
        {
            Assert.AreEqual(new Vector2(0f, 0f),
                MinimapProjection.Project(Vector2.zero, new Vector2(1000f, 800f), -1f));
            Assert.AreEqual(new Vector2(0.49f, 0.49f),
                MinimapProjection.Project(Vector2.zero, new Vector2(1000f, 800f), 1f));
        }
    }
}
