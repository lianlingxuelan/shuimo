using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class ConditioningRulesTests
    {
        [Test]
        public void QingQiRecipe_RequiresBambooAndShoot()
        {
            ConditioningRecipe recipe = ConditioningRules.GetRecipe(ConditioningKind.QingQi);

            Assert.AreEqual(2, recipe.BambooWoodCost);
            Assert.AreEqual(1, recipe.BambooShootCost);
            Assert.AreEqual("清气散", recipe.DisplayName);
        }

        [Test]
        public void CanCraft_WhenShootIsMissing_ReturnsFalse()
        {
            ConditioningRecipe recipe = ConditioningRules.GetRecipe(ConditioningKind.QingQi);

            Assert.IsFalse(ConditioningRules.CanCraft(99, 0, recipe));
        }

        [Test]
        public void NoConditioning_HasNoCombatModifiers()
        {
            Assert.AreEqual(1.0f, ConditioningRules.SkillCooldownMultiplier(ConditioningKind.None));
            Assert.AreEqual(1.0f, ConditioningRules.DodgeDamageMultiplier(ConditioningKind.None));
            Assert.AreEqual(1.0f, ConditioningRules.OutOfCombatRecoveryMultiplier(ConditioningKind.None));
        }

        [Test]
        public void EachConditioning_HasOneClearCombatDirection()
        {
            Assert.Less(ConditioningRules.SkillCooldownMultiplier(ConditioningKind.QingQi), 1.0f);
            Assert.Greater(ConditioningRules.DodgeDamageMultiplier(ConditioningKind.StrongSinew), 1.0f);
            Assert.Greater(ConditioningRules.OutOfCombatRecoveryMultiplier(ConditioningKind.NourishOrigin), 1.0f);
        }

        [Test]
        public void QingQiCooldown_UsesFewerFramesThanTheBaseSkill()
        {
            int adjusted = ConditioningRules.AdjustCooldownFrames(24, ConditioningKind.QingQi);

            Assert.AreEqual(20, adjusted);
        }

        [Test]
        public void OtherConditioning_DoesNotChangeSkillCooldownFrames()
        {
            Assert.AreEqual(24, ConditioningRules.AdjustCooldownFrames(24, ConditioningKind.StrongSinew));
            Assert.AreEqual(0, ConditioningRules.AdjustCooldownFrames(0, ConditioningKind.QingQi));
        }

        [Test]
        public void TrySpend_WhenMaterialsAreEnough_DeductsTheWholeRecipe()
        {
            int bambooWood = 3;
            int bambooShoot = 1;
            ConditioningRecipe recipe = ConditioningRules.GetRecipe(ConditioningKind.StrongSinew);

            bool spent = ConditioningRules.TrySpend(ref bambooWood, ref bambooShoot, recipe);

            Assert.IsTrue(spent);
            Assert.AreEqual(0, bambooWood);
            Assert.AreEqual(0, bambooShoot);
        }

        [Test]
        public void TrySpend_WhenMaterialsAreInsufficient_LeavesBothCountsUntouched()
        {
            int bambooWood = 2;
            int bambooShoot = 1;
            ConditioningRecipe recipe = ConditioningRules.GetRecipe(ConditioningKind.StrongSinew);

            bool spent = ConditioningRules.TrySpend(ref bambooWood, ref bambooShoot, recipe);

            Assert.IsFalse(spent);
            Assert.AreEqual(2, bambooWood);
            Assert.AreEqual(1, bambooShoot);
        }

        [Test]
        public void Controller_WhenRecipeCanBePaid_ConsumesMaterialsAndActivatesOneKind()
        {
            GameObject host = new GameObject("ConditioningControllerTest");
            try
            {
                PlayerInventory inventory = host.AddComponent<PlayerInventory>();
                inventory.AddMaterial(PlayerInventory.BambooWood, 2);
                inventory.AddMaterial(PlayerInventory.BambooShoot, 1);
                ConditioningController controller = host.AddComponent<ConditioningController>();

                bool activated = controller.TryActivate(ConditioningKind.QingQi);

                Assert.IsTrue(activated);
                Assert.AreEqual(ConditioningKind.QingQi, controller.ActiveKind);
                Assert.AreEqual(0, inventory.Count(PlayerInventory.BambooWood));
                Assert.AreEqual(0, inventory.Count(PlayerInventory.BambooShoot));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Controller_WhenRecipeCannotBePaid_LeavesInventoryAndStateUntouched()
        {
            GameObject host = new GameObject("ConditioningControllerInsufficientTest");
            try
            {
                PlayerInventory inventory = host.AddComponent<PlayerInventory>();
                inventory.AddMaterial(PlayerInventory.BambooWood, 1);
                ConditioningController controller = host.AddComponent<ConditioningController>();

                bool activated = controller.TryActivate(ConditioningKind.QingQi);

                Assert.IsFalse(activated);
                Assert.AreEqual(ConditioningKind.None, controller.ActiveKind);
                Assert.AreEqual(1, inventory.Count(PlayerInventory.BambooWood));
                Assert.AreEqual(0, inventory.Count(PlayerInventory.BambooShoot));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
