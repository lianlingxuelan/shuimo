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
            Transform root = null;

            try
            {
                FirstChapterLayout layout = FirstChapterLayout.Build(new Vector2(240f, 180f));
                NavigationLandmarkPlacement[] plan = NavigationLandmarkPlan.Create(layout, 44u);

                root = NavigationLandmarkView.BuildRoot(context.transform, layout, 44u, null, null);

                Assert.IsNull(root.parent);
                Assert.AreEqual(NavigationLandmarkView.RootName, root.name);
                Assert.AreEqual(new Vector3(0f, 0f, 0f), root.position);
                Assert.AreEqual(Vector3.one, root.lossyScale);
                Assert.AreEqual(plan.Length, root.childCount);
                Assert.AreEqual(plan[0].Position, (Vector2)root.GetChild(0).position);

                context.transform.position = new Vector3(-500f, 900f, 0f);
                context.transform.localRotation = Quaternion.Euler(0f, 0f, 37f);
                context.transform.localScale = new Vector3(3f, 0.5f, 2f);

                Assert.AreEqual(new Vector3(0f, 0f, 0f), root.position);
                Assert.AreEqual(Quaternion.Euler(0f, 0f, 0f), root.localRotation);
                Assert.AreEqual(Vector3.one, root.lossyScale);
            }
            finally
            {
                NavigationLandmarkView.DestroyOwnedRoot(root);
                Object.DestroyImmediate(context);
            }
        }

        [Test]
        public void BuildRoot_RectangleLayersUseTheirAuthoredWorldDimensions()
        {
            GameObject context = new GameObject("BambooSceneContext_Test");
            Transform root = null;

            try
            {
                root = NavigationLandmarkView.BuildRoot(
                    context.transform,
                    FirstChapterLayout.Build(Vector2.zero),
                    77u,
                    null,
                    null);

                NavigationLandmarkView[] views = root.GetComponentsInChildren<NavigationLandmarkView>(true);
                NavigationLandmarkView sign = views.First(view => view.Kind == NavigationLandmarkKind.Sign);
                NavigationLandmarkView building = views.First(
                    view => view.Kind == NavigationLandmarkKind.BuildingSilhouette);
                SpriteRenderer signPost = sign.transform.Find("Sign_Post").GetComponent<SpriteRenderer>();
                SpriteRenderer buildingBody = building.transform.Find("Building_Body").GetComponent<SpriteRenderer>();

                Assert.IsTrue(Mathf.Abs(signPost.bounds.size.x - 8f * sign.transform.localScale.x) < 0.01f);
                Assert.IsTrue(Mathf.Abs(signPost.bounds.size.y - 76f * sign.transform.localScale.y) < 0.01f);
                Assert.IsTrue(Mathf.Abs(buildingBody.bounds.size.x - 142f * building.transform.localScale.x) < 0.01f);
                Assert.IsTrue(Mathf.Abs(buildingBody.bounds.size.y - 92f * building.transform.localScale.y) < 0.01f);
            }
            finally
            {
                NavigationLandmarkView.DestroyOwnedRoot(root);
                Object.DestroyImmediate(context);
            }
        }

        [Test]
        public void SpriteFactoryClear_RebindsEveryLandmarkKindToLiveSprites()
        {
            GameObject context = new GameObject("BambooSceneContext_Test");
            Transform root = null;

            try
            {
                root = NavigationLandmarkView.BuildRoot(
                    context.transform,
                    FirstChapterLayout.Build(Vector2.zero),
                    88u,
                    null,
                    null);
                NavigationLandmarkView[] views = root.GetComponentsInChildren<NavigationLandmarkView>(true);
                SpriteRenderer[] renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
                Sprite[] before = renderers.Select(renderer => renderer.sprite).ToArray();

                SpriteFactory.Clear();

                Assert.AreEqual(6, views.Select(view => view.Kind).Distinct().Count());
                for (int i = 0; i < renderers.Length; i++)
                {
                    Assert.IsNotNull(renderers[i].sprite);
                    Assert.IsFalse(object.ReferenceEquals(before[i], renderers[i].sprite));
                }
            }
            finally
            {
                NavigationLandmarkView.DestroyOwnedRoot(root);
                Object.DestroyImmediate(context);
                SpriteFactory.Clear();
            }
        }

        [Test]
        public void BuildRoot_SameFrameRebuildRetiresThePreviousOwnedRoot()
        {
            GameObject context = new GameObject("BambooSceneContext_Test");
            Transform first = null;
            Transform second = null;
#if TASK5_SOURCE_HARNESS
            Application.isPlaying = true;
#endif

            try
            {
                FirstChapterLayout layout = FirstChapterLayout.Build(Vector2.zero);
                first = NavigationLandmarkView.BuildRoot(context.transform, layout, 101u, null, null);
                second = NavigationLandmarkView.BuildRoot(context.transform, layout, 102u, null, null);

                Assert.IsNull(context.transform.Find(NavigationLandmarkView.RootName));
                Assert.IsNull(second.parent);
                Assert.IsTrue(second.gameObject.activeSelf);
                Assert.IsFalse(object.ReferenceEquals(first, second));
#if TASK5_SOURCE_HARNESS
                Assert.IsFalse(first.gameObject.activeSelf);
#endif
            }
            finally
            {
#if TASK5_SOURCE_HARNESS
                Application.isPlaying = false;
#endif
                NavigationLandmarkView.DestroyOwnedRoot(first);
                NavigationLandmarkView.DestroyOwnedRoot(second);
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
            Transform root = null;

            try
            {
                root = NavigationLandmarkView.BuildRoot(
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
                NavigationLandmarkView.DestroyOwnedRoot(root);
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

        [Test]
        public void DestroyOwnedRoot_IgnoresTransformsOutsideLandmarkOwnership()
        {
            GameObject context = new GameObject("BambooSceneContext_Test");
            GameObject sibling = new GameObject("Unrelated_Context_Child");
            sibling.transform.SetParent(context.transform, false);
            Transform root = null;

            try
            {
                root = NavigationLandmarkView.BuildRoot(
                    context.transform,
                    FirstChapterLayout.Build(Vector2.zero),
                    67u,
                    null,
                    null);

                NavigationLandmarkView.DestroyOwnedRoot(sibling.transform);

                Assert.AreSame(sibling.transform, context.transform.Find(sibling.name));
                Assert.IsTrue(sibling.activeSelf);
            }
            finally
            {
                NavigationLandmarkView.DestroyOwnedRoot(root);
                Object.DestroyImmediate(context);
            }
        }
    }
}
