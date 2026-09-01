using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    public sealed class NavigationAssemblyTests
    {
        [Test]
        public void BuildScene_AttachesOneNavigationStackToGeneratedWorld()
        {
            WorldBuilder.BuildScene();
            try
            {
                Assert.AreEqual(1, Object.FindObjectsOfType<InkMinimapHud>().Length);
                Assert.AreEqual(1, Object.FindObjectsOfType<WorldBoundaryFeedback>().Length);
                Assert.AreEqual(1, Object.FindObjectsOfType<FirstChapterRuntime>().Length);
                PlayerController player = Object.FindObjectOfType<PlayerController>();
                Assert.IsNotNull(player);
                Assert.IsNotNull(player.GetComponent<MinimapMarker>());
            }
            finally
            {
                WorldBuilder.DestroyGeneratedRoots();
            }
        }

        [Test]
        public void BuildNavigation_IsIdempotentAndBindsExplicitReferences()
        {
            GameObject world = new GameObject("navigation-world");
            GameObject playerHost = new GameObject("navigation-player");
            try
            {
                PlayerController player = playerHost.AddComponent<PlayerController>();
                WorldBuilder.BuildNavigation(world.transform, player.transform);
                WorldBuilder.BuildNavigation(world.transform, player.transform);

                Assert.AreEqual(1, player.GetComponents<MinimapMarker>().Length);
                Assert.AreEqual(1, world.GetComponents<WorldBoundaryFeedback>().Length);
                Assert.AreEqual(1, world.GetComponents<InkMinimapHud>().Length);
                Assert.AreEqual(1, world.GetComponents<FirstChapterRuntime>().Length);
                Assert.AreSame(player, ReadField<PlayerController>(world.GetComponent<InkMinimapHud>(), "player"));
                Assert.AreSame(world.GetComponent<WorldBoundaryFeedback>(),
                    ReadField<WorldBoundaryFeedback>(world.GetComponent<InkMinimapHud>(), "boundaryFeedback"));
            }
            finally
            {
                Object.DestroyImmediate(playerHost);
                Object.DestroyImmediate(world);
            }
        }

        [Test]
        public void FirstChapter_BuildsOneTeachingEnemyAndSeparatedPatrolsOutsideShopSafeRadius()
        {
            GameObject world = new GameObject("chapter-world");
            GameObject playerHost = new GameObject("chapter-player");
            try
            {
                PlayerController player = playerHost.AddComponent<PlayerController>();
                EnemyNpcSpawner spawner = world.AddComponent<EnemyNpcSpawner>();
                FirstChapterRuntime chapter = world.AddComponent<FirstChapterRuntime>();
                chapter.Bind(player.transform, spawner);
                chapter.Build();

                Assert.IsNotNull(chapter.TeachingEnemy);
                Assert.AreEqual(2, chapter.PatrolEnemies.Count);
                Assert.AreEqual(MinimapMarkerKind.ChapterEnemy,
                    chapter.TeachingEnemy.GetComponent<MinimapMarker>().Kind);
                Assert.Greater(Vector2.Distance(chapter.PatrolEnemies[0].position, chapter.PatrolEnemies[1].position),
                    FirstChapterRuntime.MinimumPatrolSeparation);
                for (int i = 0; i < chapter.PatrolEnemies.Count; i++)
                {
                    Assert.GreaterOrEqual(Vector2.Distance(chapter.PatrolEnemies[i].position, chapter.Layout.Shop),
                        FirstChapterRuntime.ShopSafeRadius);
                }
            }
            finally
            {
                Object.DestroyImmediate(playerHost);
                Object.DestroyImmediate(world);
            }
        }

        [Test]
        public void FirstChapter_RegistersStableUniqueGuideShopBuildingAndQuestMarkers()
        {
            GameObject world = new GameObject("chapter-markers-world");
            GameObject playerHost = new GameObject("chapter-markers-player");
            try
            {
                PlayerController player = playerHost.AddComponent<PlayerController>();
                EnemyNpcSpawner spawner = world.AddComponent<EnemyNpcSpawner>();
                FirstChapterRuntime chapter = world.AddComponent<FirstChapterRuntime>();
                chapter.Bind(player.transform, spawner);
                chapter.Build();
                player.transform.position = chapter.Layout.RoadEncounter;
                InvokeUpdate(chapter);
                Object.DestroyImmediate(chapter.TeachingEnemy.gameObject);
                InvokeUpdate(chapter);
                player.transform.position = chapter.GuideNpc.position;
                InvokeUpdate(chapter);

                MinimapMarker[] markers = world.GetComponentsInChildren<MinimapMarker>(true);
                HashSet<string> stableIds = new HashSet<string>();
                bool guide = false;
                bool shop = false;
                bool building = false;
                bool quest = false;
                for (int i = 0; i < markers.Length; i++)
                {
                    MinimapMarker marker = markers[i];
                    if (marker == null || string.IsNullOrEmpty(marker.StableId)) continue;
                    Assert.IsTrue(stableIds.Add(marker.StableId), "duplicate id: " + marker.StableId);
                    guide |= marker.Kind == MinimapMarkerKind.Npc && marker.StableId == FirstChapterRuntime.GuideMarkerId;
                    shop |= marker.Kind == MinimapMarkerKind.Shop && marker.StableId == FirstChapterRuntime.ShopMarkerId;
                    building |= marker.Kind == MinimapMarkerKind.Building && marker.StableId == FirstChapterRuntime.BuildingMarkerId;
                    quest |= marker.Kind == MinimapMarkerKind.QuestTarget && marker.StableId == FirstChapterRuntime.QuestMarkerId;
                }
                Assert.IsTrue(guide && shop && building && quest);
            }
            finally
            {
                Object.DestroyImmediate(playerHost);
                Object.DestroyImmediate(world);
            }
        }

        private static T ReadField<T>(object target, string fieldName) where T : class
        {
            return (T)target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static void InvokeUpdate(FirstChapterRuntime chapter)
        {
            typeof(FirstChapterRuntime).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(chapter, null);
        }
    }
}
