using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class BambooHarvestStateTests
    {
        [Test]
        public void NextStage_AfterOneSuccessfulCut_BecomesStump()
        {
            BambooHarvestStage next = BambooHarvestStageRules.Next(BambooHarvestStage.Intact);

            Assert.AreEqual(BambooHarvestStage.Stump, next);
        }

        [Test]
        public void NextStage_AStump_RemainsAStump()
        {
            BambooHarvestStage next = BambooHarvestStageRules.Next(BambooHarvestStage.Stump);

            Assert.AreEqual(BambooHarvestStage.Stump, next);
        }

        [Test]
        public void HitRule_TargetAheadAndWithinRange_IsHit()
        {
            Assert.IsTrue(BambooHarvestHitRules.IsHit(
                Vector2.zero, Vector2.right, new Vector2(60.0f, 0.0f), 70.0f, 90.0f));
        }

        [Test]
        public void HitRule_TargetBehindPlayer_IsNotHit()
        {
            Assert.IsFalse(BambooHarvestHitRules.IsHit(
                Vector2.zero, Vector2.right, new Vector2(-30.0f, 0.0f), 70.0f, 90.0f));
        }

        [Test]
        public void ChoppableBamboo_FirstCut_NotifiesOnce()
        {
            GameObject host = new GameObject("HarvestBambooTest");
            Texture2D texture = new Texture2D(2, 2);
            Sprite intact = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            Sprite stump = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            try
            {
                ChoppableBambooView bamboo = host.AddComponent<ChoppableBambooView>();
                bamboo.Configure(host.AddComponent<SpriteRenderer>(), intact, stump);
                int notificationCount = 0;
                bamboo.OnCut += _ => notificationCount++;

                Assert.IsTrue(bamboo.TryCut());
                Assert.IsFalse(bamboo.TryCut());
                Assert.AreEqual(1, notificationCount,
                    "只有真正从完整竹变成竹桩的那一刀才应给材料和播放反馈。");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(intact);
                Object.DestroyImmediate(stump);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void TestGroveLayout_CreatesSixReachableNonOverlappingBamboos()
        {
            Vector2[] positions = BambooHarvestLayout.CreateTestGrovePositions();

            Assert.AreEqual(6, positions.Length);
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.LessOrEqual(positions[i].magnitude, AttackController.AttackRadius,
                    "首轮验收竹应放在角色普攻范围内，方便连续验证反馈。");
                for (int j = i + 1; j < positions.Length; j++)
                {
                    Assert.Greater(Vector2.Distance(positions[i], positions[j]), 40.0f,
                        "六根竹不能重叠成一团，必须能看清各自变成竹桩。");
                }
            }
        }

        [Test]
        public void BambooShoot_FirstHarvest_NotifiesOnceAndHidesShoot()
        {
            GameObject host = new GameObject("BambooShootTest");
            Texture2D texture = new Texture2D(2, 2);
            Sprite shootSprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            try
            {
                SpriteRenderer renderer = host.AddComponent<SpriteRenderer>();
                BambooShootView shoot = host.AddComponent<BambooShootView>();
                shoot.Configure(renderer, shootSprite);
                int notificationCount = 0;
                shoot.OnHarvest += _ => notificationCount++;

                Assert.IsTrue(shoot.TryHarvest());
                Assert.IsFalse(shoot.TryHarvest());
                Assert.IsFalse(renderer.enabled);
                Assert.AreEqual(1, notificationCount);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(shootSprite);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ChoppableBamboo_RegrowsOnlyAfterConfiguredDelay()
        {
            GameObject host = new GameObject("RegrowthBambooTest");
            Texture2D texture = new Texture2D(2, 2);
            Sprite intact = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            Sprite stump = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            try
            {
                ChoppableBambooView bamboo = host.AddComponent<ChoppableBambooView>();
                bamboo.regrowSeconds = 2.0f;
                bamboo.Configure(host.AddComponent<SpriteRenderer>(), intact, stump);
                bamboo.TryCut();

                bamboo.TickRegrowth(1.99f);
                Assert.AreEqual(BambooHarvestStage.Stump, bamboo.Stage);
                bamboo.TickRegrowth(0.02f);
                Assert.AreEqual(BambooHarvestStage.Intact, bamboo.Stage);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(intact);
                Object.DestroyImmediate(stump);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void BambooShoot_RegrowsAndBecomesHarvestableAgain()
        {
            GameObject host = new GameObject("RegrowthShootTest");
            Texture2D texture = new Texture2D(2, 2);
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            try
            {
                SpriteRenderer renderer = host.AddComponent<SpriteRenderer>();
                BambooShootView shoot = host.AddComponent<BambooShootView>();
                shoot.regrowSeconds = 1.0f;
                shoot.Configure(renderer, sprite);
                shoot.TryHarvest();

                shoot.TickRegrowth(0.99f);
                Assert.IsTrue(shoot.IsHarvested);
                shoot.TickRegrowth(0.02f);
                Assert.IsFalse(shoot.IsHarvested);
                Assert.IsTrue(renderer.enabled);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void NaturalGroveLayout_ContainsAllPlantKindsAndKeepsCentreClear()
        {
            BambooPlantPlacement[] plants = BambooHarvestLayout.CreateNaturalGrovePositions();
            int normal = 0;
            int young = 0;
            int shoots = 0;

            for (int i = 0; i < plants.Length; i++)
            {
                Assert.Greater(plants[i].Position.magnitude, 85.0f,
                    "角色和道路中央应留白，不能再围出测试用竹桩圆环。");
                if (plants[i].Kind == BambooPlantKind.Normal) normal++;
                if (plants[i].Kind == BambooPlantKind.Young) young++;
                if (plants[i].Kind == BambooPlantKind.Shoot) shoots++;
                for (int j = i + 1; j < plants.Length; j++)
                {
                    Assert.Greater(Vector2.Distance(plants[i].Position, plants[j].Position), 55.0f,
                        "自然竹林的资源点不能重叠。 ");
                }
            }

            Assert.GreaterOrEqual(normal, 4);
            Assert.GreaterOrEqual(young, 2);
            Assert.GreaterOrEqual(shoots, 3);
        }
    }
}
