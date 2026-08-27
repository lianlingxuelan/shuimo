using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    public sealed class NavigationLandmarkPlanTests
    {
        [Test]
        public void Create_AddsEveryLandmarkKindWithinTheNavigationDensityRange()
        {
            NavigationLandmarkPlacement[] items = NavigationLandmarkPlan.Create(
                FirstChapterLayout.Build(Vector2.zero),
                20260827u);

            Assert.IsTrue(items.Length >= 12 && items.Length <= 18);
            Assert.AreEqual(6, items.Select(item => item.Kind).Distinct().Count());
            Assert.IsTrue(items.Any(item => item.Kind == NavigationLandmarkKind.BambooClump));
            Assert.IsTrue(items.Any(item => item.Kind == NavigationLandmarkKind.ScholarRock));
            Assert.IsTrue(items.Any(item => item.Kind == NavigationLandmarkKind.Sign));
            Assert.IsTrue(items.Any(item => item.Kind == NavigationLandmarkKind.Lantern));
            Assert.IsTrue(items.Any(item => item.Kind == NavigationLandmarkKind.Basket));
            Assert.IsTrue(items.Any(item => item.Kind == NavigationLandmarkKind.BuildingSilhouette));
        }

        [Test]
        public void Create_ReplaysExactlyForTheSameSeed()
        {
            FirstChapterLayout layout = FirstChapterLayout.Build(new Vector2(120f, -40f));

            NavigationLandmarkPlacement[] first = NavigationLandmarkPlan.Create(layout, 99u);
            NavigationLandmarkPlacement[] replay = NavigationLandmarkPlan.Create(layout, 99u);

            Assert.AreEqual(first.Length, replay.Length);
            for (int i = 0; i < first.Length; i++)
            {
                Assert.AreEqual(first[i].Kind, replay[i].Kind);
                Assert.AreEqual(first[i].Position, replay[i].Position);
                Assert.AreEqual(first[i].Scale, replay[i].Scale);
                Assert.AreEqual(first[i].SortingOrder, replay[i].SortingOrder);
            }
        }

        [Test]
        public void Create_UsesTheSeedForBoundedVisualVariation()
        {
            FirstChapterLayout layout = FirstChapterLayout.Build(Vector2.zero);

            NavigationLandmarkPlacement[] first = NavigationLandmarkPlan.Create(layout, 7u);
            NavigationLandmarkPlacement[] second = NavigationLandmarkPlan.Create(layout, 8u);

            Assert.IsTrue(first.Where((item, index) => !item.Position.Equals(second[index].Position)).Any());
            Assert.IsTrue(first.Where((item, index) => item.Scale != second[index].Scale).Any());
            Assert.IsTrue(first.All(item => item.Scale >= 0.82f && item.Scale <= 1.18f));
            Assert.IsTrue(second.All(item => item.Scale >= 0.82f && item.Scale <= 1.18f));
        }

        [Test]
        public void Create_DistributesLandmarksOnBothSidesWithoutBlockingTheRoad()
        {
            FirstChapterLayout layout = FirstChapterLayout.Build(new Vector2(75f, 30f));
            NavigationLandmarkPlacement[] items = NavigationLandmarkPlan.Create(layout, 20260827u);

            Assert.GreaterOrEqual(items.Count(item => item.Position.x < layout.RoadCenterX), 5);
            Assert.GreaterOrEqual(items.Count(item => item.Position.x > layout.RoadCenterX), 5);
            foreach (NavigationLandmarkPlacement item in items)
            {
                Assert.IsFalse(layout.IsInRoadLane(item.Position));
                Assert.IsFalse(item.BlocksRoadCenter);
            }
        }

        [Test]
        public void Create_KeepsEveryChapterStoryAnchorClear()
        {
            FirstChapterLayout layout = FirstChapterLayout.Build(Vector2.zero);
            Vector2[] anchors =
            {
                layout.RoadEncounter,
                layout.GuideNpc,
                layout.Shop,
                layout.InnPlaceholder,
                layout.HerbPlaceholder,
                layout.GatePlaceholder,
            };

            foreach (NavigationLandmarkPlacement item in NavigationLandmarkPlan.Create(layout, 20260827u))
            {
                Assert.IsFalse(layout.IsNearReservedStoryNode(item.Position));
                Assert.IsFalse(layout.IsNearAnyChapterNode(item.Position));
                foreach (Vector2 anchor in anchors)
                {
                    Assert.Greater(
                        (item.Position - anchor).sqrMagnitude,
                        FirstChapterLayout.StoryNodeReserveRadius * FirstChapterLayout.StoryNodeReserveRadius);
                }
            }
        }

        [Test]
        public void BuildRoot_UsesAStaticWorldRootAndBuildsEveryPlacement()
        {
            GameObject context = new GameObject("BambooSceneContext_Test");
            context.transform.position = new Vector3(240f, 180f, 0f);

            try
            {
                FirstChapterLayout layout = FirstChapterLayout.Build(new Vector2(240f, 180f));
                NavigationLandmarkPlacement[] plan = NavigationLandmarkPlan.Create(layout, 44u);

                Transform root = NavigationLandmarkView.BuildRoot(context.transform, layout, 44u, null, null);

                Assert.AreSame(context.transform, root.parent);
                Assert.AreEqual(NavigationLandmarkView.RootName, root.name);
                Assert.AreEqual(new Vector3(0f, 0f, 0f), root.position);
                Assert.AreEqual(plan.Length, root.childCount);
                Assert.AreEqual(plan[0].Position, (Vector2)root.GetChild(0).position);
            }
            finally
            {
                Object.DestroyImmediate(context);
            }
        }

        [Test]
        public void BuildRoot_ReusesEnvironmentSpritesAndNeverAddsColliders()
        {
            GameObject context = new GameObject("BambooSceneContext_Test");
            Sprite bamboo = SpriteFactory.Circle(
                "landmark_test_bamboo",
                new Color(0.2f, 0.4f, 0.2f, 1f),
                Color.white,
                0f);
            Sprite rock = SpriteFactory.Circle(
                "landmark_test_rock",
                new Color(0.3f, 0.3f, 0.3f, 1f),
                Color.white,
                0f);

            try
            {
                Transform root = NavigationLandmarkView.BuildRoot(
                    context.transform,
                    FirstChapterLayout.Build(Vector2.zero),
                    55u,
                    bamboo,
                    rock);

                NavigationLandmarkView[] views = root.GetComponentsInChildren<NavigationLandmarkView>(true);
                Assert.AreEqual(
                    NavigationLandmarkPlan.Create(FirstChapterLayout.Build(Vector2.zero), 55u).Length,
                    views.Length);
                Assert.IsTrue(views.Any(view => view.Kind == NavigationLandmarkKind.BambooClump
                    && view.GetComponentsInChildren<SpriteRenderer>(true)[0].sprite == bamboo));
                Assert.IsTrue(views.Any(view => view.Kind == NavigationLandmarkKind.ScholarRock
                    && view.GetComponentsInChildren<SpriteRenderer>(true)[0].sprite == rock));
                Assert.AreEqual(0, root.GetComponentsInChildren<Collider>(true).Length);
                Assert.AreEqual(0, root.GetComponentsInChildren<Collider2D>(true).Length);
            }
            finally
            {
                Object.DestroyImmediate(context);
            }
        }

        [Test]
        public void DestroyOwnedRoot_RemovesOnlyTheNavigationLandmarksRoot()
        {
            GameObject context = new GameObject("BambooSceneContext_Test");
            GameObject sibling = new GameObject("Unrelated_Context_Child");
            sibling.transform.SetParent(context.transform, false);

            try
            {
                Transform root = NavigationLandmarkView.BuildRoot(
                    context.transform,
                    FirstChapterLayout.Build(Vector2.zero),
                    66u,
                    null,
                    null);

                NavigationLandmarkView.DestroyOwnedRoot(root);

                Assert.IsNull(context.transform.Find(NavigationLandmarkView.RootName));
                Assert.AreSame(sibling.transform, context.transform.Find(sibling.name));
            }
            finally
            {
                Object.DestroyImmediate(context);
            }
        }
    }
}
