using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class AdventurePanelRulesTests
    {
        [Test]
        public void Toggle_WhenOpeningAnotherPanel_SwitchesToRequestedPanel()
        {
            AdventurePanelKind result = AdventurePanelRules.Toggle(
                AdventurePanelKind.Inventory, AdventurePanelKind.Skills);

            Assert.AreEqual(AdventurePanelKind.Skills, result);
        }

        [Test]
        public void Toggle_WhenPressingTheOpenPanel_ClosesIt()
        {
            AdventurePanelKind result = AdventurePanelRules.Toggle(
                AdventurePanelKind.Quests, AdventurePanelKind.Quests);

            Assert.AreEqual(AdventurePanelKind.None, result);
        }

        [Test]
        public void Toggle_WhenOpeningFromClosedState_OpensRequestedPanel()
        {
            AdventurePanelKind result = AdventurePanelRules.Toggle(
                AdventurePanelKind.None, AdventurePanelKind.Inventory);

            Assert.AreEqual(AdventurePanelKind.Inventory, result);
        }

        [TestCase(AdventurePanelKind.Character)]
        [TestCase(AdventurePanelKind.Cultivation)]
        [TestCase(AdventurePanelKind.DaoHeart)]
        public void Toggle_EachNewShellEntry_UsesTheSameExclusiveRule(AdventurePanelKind requested)
        {
            AdventurePanelKind opened = AdventurePanelRules.Toggle(AdventurePanelKind.None, requested);
            AdventurePanelKind closed = AdventurePanelRules.Toggle(opened, requested);

            Assert.AreEqual(requested, opened);
            Assert.AreEqual(AdventurePanelKind.None, closed);
        }

        [Test]
        public void Hud_TogglePanel_UsesTheSameExclusiveOpenCloseRule()
        {
            GameObject host = new GameObject("AdventurePanelsHudTest");
            try
            {
                AdventurePanelsHud hud = host.AddComponent<AdventurePanelsHud>();

                hud.TogglePanel(AdventurePanelKind.Inventory);
                Assert.AreEqual(AdventurePanelKind.Inventory, hud.ActivePanel);

                hud.TogglePanel(AdventurePanelKind.Inventory);
                Assert.AreEqual(AdventurePanelKind.None, hud.ActivePanel);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
