using NUnit.Framework;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class InkInventoryGridTests
    {
        [Test]
        public void MaterialEntries_UseStableFirstTwoSlots()
        {
            Assert.That(InkInventoryGrid.SlotFor(PlayerInventory.BambooWood), Is.EqualTo(0));
            Assert.That(InkInventoryGrid.SlotFor(PlayerInventory.BambooShoot), Is.EqualTo(1));
            Assert.That(InkInventoryGrid.Columns, Is.EqualTo(5));
            Assert.That(InkInventoryGrid.Rows, Is.EqualTo(4));
        }
    }
}
