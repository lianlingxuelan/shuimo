using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Xianxia.Unity.T2.Tests
{
    public sealed class NavigationPlayModeTests
    {
        [UnityTest]
        public IEnumerator NavigationLoop_MovementDeathAndBoundaryFeedbackRemainConnected()
        {
            WorldBuilder.BuildScene();
            try
            {
                yield return null;
                PlayerController player = Object.FindObjectOfType<PlayerController>();
                InkMinimapHud hud = Object.FindObjectOfType<InkMinimapHud>();
                FirstChapterRuntime chapter = Object.FindObjectOfType<FirstChapterRuntime>();
                WorldBoundaryFeedback boundary = Object.FindObjectOfType<WorldBoundaryFeedback>();
                Assert.IsNotNull(player);
                Assert.IsNotNull(hud);
                Assert.IsNotNull(chapter);
                Assert.IsNotNull(boundary);

                player.transform.position += new Vector3(96f, 0f, 0f);
                hud.RefreshNow();
                RectTransform arrow = (RectTransform)hud.transform.Find("InkMinimapCanvas/Scroll/MapClip/PlayerArrow");
                Assert.Greater(arrow.anchoredPosition.x, 0f);

                string enemyId = chapter.TeachingEnemy.GetComponent<MinimapMarker>().StableId;
                Object.Destroy(chapter.TeachingEnemy.gameObject);
                yield return null;
                hud.RefreshNow();
                Assert.IsNull(hud.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers/Marker-" + enemyId));

                int triggerCount = 0;
                boundary.Triggered += _ => triggerCount++;
                player.transform.position = new Vector3(WorldBuilder.WorldWidth - WorldBuilder.TileUnit * 0.5f, 160f, 0f);
                typeof(PlayerController).GetProperty("MoveDir").SetValue(player, Vector2.right, null);
                typeof(PlayerController).GetMethod("Move", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(player, new object[] { 1f });
                typeof(WorldBoundaryFeedback).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boundary, null);
                typeof(WorldBoundaryFeedback).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boundary, null);
                Assert.AreEqual(1, triggerCount);
            }
            finally
            {
                WorldBuilder.DestroyGeneratedRoots();
            }
        }
    }
}
