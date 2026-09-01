using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class InkUiThemeTests
    {
        [Test]
        public void FallbackSprite_IsReadableAndOpaque()
        {
            Sprite sprite = InkUiTheme.CreateFallbackSprite();

            Assert.That(sprite, Is.Not.Null);
            Assert.That(InkUiTheme.Paper.a, Is.GreaterThan(0.95f));
            Assert.That(InkUiTheme.Ink.grayscale, Is.LessThan(InkUiTheme.Paper.grayscale));
        }
    }
}
