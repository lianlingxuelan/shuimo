using NUnit.Framework;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class InkUiDensityRulesTests
    {
        [Test]
        public void EmptyInventorySlot_UsesOnlyHairlineInsteadOfDecorativeFrame()
        {
            Assert.That(InkUiDensityRules.ShouldShowDecorativeSlotFrame(0), Is.False);
            Assert.That(InkUiDensityRules.ShouldShowDecorativeSlotFrame(1), Is.True);
        }

        [Test]
        public void BookPanel_UsesOnePaperSurfaceWithoutOuterFrame()
        {
            Assert.That(InkUiDensityRules.ShowOuterBookFrame, Is.False);
        }
    }
}
