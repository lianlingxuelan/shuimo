using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class BambooBackdropLayoutTests
    {
        [Test]
        public void BackdropSize_PreservesArtworkAspectRatio()
        {
            Vector2 size = BambooSceneContext.CalculateBackdropSize(1040.0f, new Vector2(1670.0f, 941.0f));

            Assert.AreEqual(1040.0f, size.x, 1e-4f);
            Assert.AreEqual(586.0f, size.y, 1.0f,
                "底图必须保持原画比例，不能把水墨竹子拉成粗胖的柱子。");
        }

        [Test]
        public void BackdropSize_InvalidArtwork_ReturnsZeroSize()
        {
            Vector2 size = BambooSceneContext.CalculateBackdropSize(900.0f, Vector2.zero);

            Assert.AreEqual(Vector2.zero, size,
                "资源异常时应回退到现有程序地面，而不是生成不可见或无穷大的底图。");
        }
    }
}
