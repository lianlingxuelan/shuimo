using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Xianxia.Unity.T2.Tests
{
    public sealed class InkMinimapHudTests
    {
        [Test]
        public void Build_CreatesOneDedicatedCanvasAndPlayerMarker()
        {
            GameObject host = new GameObject("hud-host");
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();

                Assert.AreEqual(1, host.GetComponentsInChildren<Canvas>(true).Length);
                Assert.IsNotNull(host.transform.Find("InkMinimapCanvas/Scroll/MapClip/PlayerArrow"));
                Assert.IsNotNull(host.transform.Find("InkMinimapCanvas/Scroll/BoundaryWash"));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Build_ConfiguresReferenceScrollClipAndInactiveEdgeWashes()
        {
            GameObject host = new GameObject("hud-layout-host");
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();

                Transform canvasTransform = host.transform.Find("InkMinimapCanvas");
                Canvas canvas = canvasTransform.GetComponent<Canvas>();
                CanvasScaler scaler = canvasTransform.GetComponent<CanvasScaler>();
                RectTransform scroll = (RectTransform)canvasTransform.Find("Scroll");
                RectTransform mapClip = (RectTransform)scroll.Find("MapClip");
                Transform wash = scroll.Find("BoundaryWash");

                Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
                Assert.AreEqual(160, canvas.sortingOrder);
                Assert.AreEqual(Hud.ReferenceResolution, scaler.referenceResolution);
                Assert.AreEqual(new Vector2(310.0f, 220.0f), scroll.sizeDelta);
                Assert.AreEqual(new Vector2(278.0f, 174.0f), mapClip.sizeDelta);
                Assert.IsNotNull(mapClip.GetComponent<RectMask2D>());
                Assert.AreEqual(4, wash.childCount);
                for (int i = 0; i < wash.childCount; i++)
                {
                    Assert.IsFalse(wash.GetChild(i).gameObject.activeSelf);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Palette_UsesApprovedWaterInkTokens()
        {
            AssertColor(0xD9, 0xCE, 0xAA, 0xE8, InkMinimapPalette.Paper);
            AssertColor(0x25, 0x31, 0x2B, 0xEF, InkMinimapPalette.DeepInk);
            AssertColor(0x75, 0x6E, 0x5D, 0x99, InkMinimapPalette.RoadInk);
            AssertColor(0x2A, 0x8A, 0x78, 0xFF, InkMinimapPalette.PlayerJade);
            AssertColor(0xA7, 0x3A, 0x32, 0xFF, InkMinimapPalette.Cinnabar);
            AssertColor(0xB9, 0x98, 0x49, 0xFF, InkMinimapPalette.NpcGold);
        }

        [TestCase(1280.0f, 720.0f)]
        [TestCase(1920.0f, 1080.0f)]
        [TestCase(1200.0f, 900.0f)]
        public void Layout_MinimapAndDiagnosticsKeepClearGap(float width, float height)
        {
            float scale = Mathf.Pow(width / Hud.ReferenceResolution.x, 0.5f)
                        * Mathf.Pow(height / Hud.ReferenceResolution.y, 0.5f);
            float minimapBottom = (InkMinimapHud.TopMargin + InkMinimapHud.PanelSize.y) * scale;
            float diagnosticsTop = InkMinimapHud.DiagnosticsTopOffset * scale;

            Assert.Greater(diagnosticsTop - minimapBottom, InkMinimapHud.DiagnosticsGap * scale - 0.01f);
            Assert.Greater(width, (InkMinimapHud.RightMargin + InkMinimapHud.PanelSize.x) * scale);
            Assert.Greater(height, diagnosticsTop);
        }

        [Test]
        public void RefreshNow_ReusesReleasedMarkerImageForNewStableId()
        {
            GameObject host = new GameObject("hud-pool-host");
            GameObject firstHost = new GameObject("first-marker");
            GameObject secondHost = new GameObject("second-marker");
            Vector2 oldGrid = SetWorldGrid(32, 25);
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();

                MinimapMarker first = firstHost.AddComponent<MinimapMarker>();
                first.Configure(MinimapMarkerKind.Npc, "guide", "竹市引路人", true);
                hud.RefreshNow();
                Transform firstImage = host.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers/Marker-guide");
                Assert.IsNotNull(firstImage);
                Assert.IsTrue(firstImage.gameObject.activeSelf);

                firstHost.SetActive(false);
                hud.RefreshNow();
                Assert.IsFalse(firstImage.gameObject.activeSelf);

                MinimapMarker second = secondHost.AddComponent<MinimapMarker>();
                second.Configure(MinimapMarkerKind.QuestTarget, "chapter-goal", "问剑台", true);
                hud.RefreshNow();
                Transform secondImage = host.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers/Marker-chapter-goal");
                Assert.AreSame(firstImage, secondImage);
            }
            finally
            {
                RestoreWorldGrid(oldGrid);
                Object.DestroyImmediate(secondHost);
                Object.DestroyImmediate(firstHost);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void BoundaryTrigger_ShowsMatchingWashAndTemporaryNoRoadText()
        {
            GameObject host = new GameObject("hud-boundary-host");
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();

                MethodInfo trigger = typeof(InkMinimapHud).GetMethod(
                    "OnBoundaryTriggered",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                trigger.Invoke(hud, new object[] { Vector2.right });

                Transform scroll = host.transform.Find("InkMinimapCanvas/Scroll");
                Assert.IsTrue(scroll.Find("BoundaryWash/Right").gameObject.activeSelf);
                Assert.IsFalse(scroll.Find("BoundaryWash/Left").gameObject.activeSelf);
                Assert.AreEqual("前方无路", scroll.Find("TargetLabel").GetComponent<Text>().text);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Update_HidesScrollWhileAdventurePageIsOpen()
        {
            GameObject host = new GameObject("hud-modal-host");
            GameObject adventureHost = new GameObject("adventure-modal-host");
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();
                AdventurePanelsHud adventure = adventureHost.AddComponent<AdventurePanelsHud>();
                adventure.TogglePanel(AdventurePanelKind.Inventory);

                InvokeUpdate(hud);
                Transform scroll = host.transform.Find("InkMinimapCanvas/Scroll");
                Assert.IsFalse(scroll.gameObject.activeSelf);

                adventure.ClosePanel();
                InvokeUpdate(hud);
                Assert.IsTrue(scroll.gameObject.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(adventureHost);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void OnEnable_ResynchronizesRegistryChangesMissedWhileDisabled()
        {
            GameObject host = new GameObject("hud-reenable-host");
            GameObject markerHost = new GameObject("disabled-marker-host");
            Vector2 oldGrid = SetWorldGrid(32, 25);
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();
                hud.RefreshNow();

                host.SetActive(false);
                MinimapMarker marker = markerHost.AddComponent<MinimapMarker>();
                marker.Configure(MinimapMarkerKind.Npc, "late-guide", "迟来的引路人", true);
                host.SetActive(true);
                hud.RefreshNow();

                Assert.IsNotNull(host.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers/Marker-late-guide"));
            }
            finally
            {
                RestoreWorldGrid(oldGrid);
                Object.DestroyImmediate(markerHost);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RefreshNow_NoPlayerHidesArrowThenPlayerMarkerRestoresIt()
        {
            GameObject host = new GameObject("hud-player-resolution-host");
            GameObject playerMarkerHost = new GameObject("player-marker-host");
            Vector2 oldGrid = SetWorldGrid(32, 25);
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();
                hud.RefreshNow();
                Transform arrow = host.transform.Find("InkMinimapCanvas/Scroll/MapClip/PlayerArrow");
                Assert.IsFalse(arrow.gameObject.activeSelf);

                playerMarkerHost.transform.position = new Vector3(320.0f, 160.0f, 0.0f);
                MinimapMarker marker = playerMarkerHost.AddComponent<MinimapMarker>();
                marker.Configure(MinimapMarkerKind.Player, "player", "", true);
                hud.RefreshNow();

                Assert.IsTrue(arrow.gameObject.activeSelf);
            }
            finally
            {
                RestoreWorldGrid(oldGrid);
                Object.DestroyImmediate(playerMarkerHost);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RefreshNow_InvalidWorldShowsDrawingFallbackAndRestoresNormalMode()
        {
            GameObject host = new GameObject("hud-world-readiness-host");
            GameObject playerHost = new GameObject("world-ready-player");
            GameObject markerHost = new GameObject("world-ready-marker");
            Vector2 oldGrid = SetWorldGrid(0, 0);
            try
            {
                PlayerController controller = playerHost.AddComponent<PlayerController>();
                markerHost.transform.position = new Vector3(600.0f, 0.0f, 0.0f);
                MinimapMarker marker = markerHost.AddComponent<MinimapMarker>();
                marker.Configure(MinimapMarkerKind.QuestTarget, "goal", "问剑台", true);
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Bind(controller, null);
                hud.Build();
                hud.RefreshNow();

                Transform scroll = host.transform.Find("InkMinimapCanvas/Scroll");
                Assert.IsFalse(scroll.Find("MapClip/PlayerArrow").gameObject.activeSelf);
                Assert.IsFalse(scroll.Find("MapClip/Markers/Marker-goal").gameObject.activeSelf);
                Assert.IsFalse(scroll.Find("MapClip/Roads").gameObject.activeSelf);
                Assert.AreEqual("地图绘制中", scroll.Find("TargetLabel").GetComponent<Text>().text);

                SetWorldGrid(32, 25);
                hud.RefreshNow();
                Assert.IsTrue(scroll.Find("MapClip/PlayerArrow").gameObject.activeSelf);
                Assert.IsTrue(scroll.Find("MapClip/Markers/Marker-goal").gameObject.activeSelf);
                Assert.IsTrue(scroll.Find("MapClip/Roads").gameObject.activeSelf);
                Assert.AreEqual(string.Empty, scroll.Find("TargetLabel").GetComponent<Text>().text);
            }
            finally
            {
                RestoreWorldGrid(oldGrid);
                Object.DestroyImmediate(markerHost);
                Object.DestroyImmediate(playerHost);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RefreshNow_ReconfigurationRemapsStableIdAndVisualWithoutChangedEvent()
        {
            GameObject host = new GameObject("hud-reconfigure-host");
            GameObject markerHost = new GameObject("reconfigured-marker-host");
            Vector2 oldGrid = SetWorldGrid(32, 25);
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Build();
                MinimapMarker marker = markerHost.AddComponent<MinimapMarker>();
                marker.Configure(MinimapMarkerKind.Npc, "old-id", "旧引路人", true);
                hud.RefreshNow();
                Transform originalImage = host.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers/Marker-old-id");

                marker.Configure(MinimapMarkerKind.Shop, "new-id", "竹市商铺", true);
                hud.RefreshNow();
                Transform remappedImage = host.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers/Marker-new-id");

                Assert.IsNotNull(remappedImage);
                Assert.AreSame(originalImage, remappedImage);
                Assert.IsNull(host.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers/Marker-old-id"));
                Assert.AreEqual(new Vector2(14.0f, 7.0f), ((RectTransform)remappedImage).sizeDelta);
                Assert.AreEqual((Color32)InkMinimapPalette.RoadInk, (Color32)remappedImage.GetComponent<Image>().color);
            }
            finally
            {
                RestoreWorldGrid(oldGrid);
                Object.DestroyImmediate(markerHost);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ResolveBindings_MissingReferencesUseBoundedRetryCadence()
        {
            GameObject host = new GameObject("hud-binding-retry-host");
            try
            {
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                MethodInfo resolve = typeof(InkMinimapHud).GetMethod(
                    "ResolveBindings",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                resolve.Invoke(hud, new object[] { 0.0f });
                resolve.Invoke(hud, new object[] { 0.1f });
                resolve.Invoke(hud, new object[] { 0.9f });
                Assert.AreEqual(2, ReadLookupCount(hud));

                resolve.Invoke(hud, new object[] { 1.0f });
                Assert.AreEqual(4, ReadLookupCount(hud));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RefreshNow_MarkerKindsUseDistinctVisualSemantics()
        {
            GameObject host = new GameObject("hud-marker-semantics-host");
            GameObject playerHost = new GameObject("semantic-player-host");
            GameObject[] markerHosts = new GameObject[6];
            Vector2 oldGrid = SetWorldGrid(32, 25);
            try
            {
                PlayerController controller = playerHost.AddComponent<PlayerController>();
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Bind(controller, null);
                hud.Build();

                MinimapMarkerKind[] kinds =
                {
                    MinimapMarkerKind.Enemy,
                    MinimapMarkerKind.ChapterEnemy,
                    MinimapMarkerKind.QuestTarget,
                    MinimapMarkerKind.Npc,
                    MinimapMarkerKind.Shop,
                    MinimapMarkerKind.Building,
                };
                string[] ids = { "enemy", "chapter", "quest", "npc", "shop", "building" };
                for (int i = 0; i < kinds.Length; i++)
                {
                    markerHosts[i] = new GameObject(ids[i]);
                    markerHosts[i].transform.position = new Vector3(32.0f + i, 32.0f, 0.0f);
                    MinimapMarker marker = markerHosts[i].AddComponent<MinimapMarker>();
                    marker.Configure(kinds[i], ids[i], ids[i], kinds[i] != MinimapMarkerKind.Enemy);
                }
                hud.RefreshNow();

                Transform root = host.transform.Find("InkMinimapCanvas/Scroll/MapClip/Markers");
                Assert.AreEqual(new Vector2(8.0f, 8.0f), ((RectTransform)root.Find("Marker-enemy")).sizeDelta);
                Assert.AreNotEqual((Color32)InkMinimapPalette.Cinnabar, (Color32)root.Find("Marker-enemy").GetComponent<Image>().color);
                Assert.AreEqual(new Vector2(14.0f, 14.0f), ((RectTransform)root.Find("Marker-chapter")).sizeDelta);
                Assert.AreEqual(new Vector2(8.0f, 16.0f), ((RectTransform)root.Find("Marker-quest")).sizeDelta);
                Assert.AreEqual(new Vector2(10.0f, 10.0f), ((RectTransform)root.Find("Marker-npc")).sizeDelta);
                Assert.AreEqual((Color32)InkMinimapPalette.NpcGold, (Color32)root.Find("Marker-npc").GetComponent<Image>().color);
                Assert.AreEqual(new Vector2(14.0f, 7.0f), ((RectTransform)root.Find("Marker-shop")).sizeDelta);
                Assert.AreEqual(new Vector2(13.0f, 11.0f), ((RectTransform)root.Find("Marker-building")).sizeDelta);
            }
            finally
            {
                RestoreWorldGrid(oldGrid);
                for (int i = 0; i < markerHosts.Length; i++)
                {
                    if (markerHosts[i] != null) Object.DestroyImmediate(markerHosts[i]);
                }
                Object.DestroyImmediate(playerHost);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RefreshNow_TargetLabelIsEmptyUntilTargetIsNear()
        {
            GameObject host = new GameObject("hud-target-label-host");
            GameObject playerHost = new GameObject("target-player-host");
            GameObject targetHost = new GameObject("target-marker-host");
            Vector2 oldGrid = SetWorldGrid(32, 25);
            try
            {
                playerHost.transform.position = new Vector3(64.0f, 64.0f, 0.0f);
                PlayerController controller = playerHost.AddComponent<PlayerController>();
                targetHost.transform.position = new Vector3(600.0f, 64.0f, 0.0f);
                MinimapMarker target = targetHost.AddComponent<MinimapMarker>();
                target.Configure(MinimapMarkerKind.QuestTarget, "quest", "问剑台", true);
                InkMinimapHud hud = host.AddComponent<InkMinimapHud>();
                hud.Bind(controller, null);
                hud.Build();
                Text label = host.transform.Find("InkMinimapCanvas/Scroll/TargetLabel").GetComponent<Text>();
                Assert.AreEqual(string.Empty, label.text);

                hud.RefreshNow();
                Assert.AreEqual(string.Empty, label.text);

                targetHost.transform.position = new Vector3(100.0f, 64.0f, 0.0f);
                hud.RefreshNow();
                Assert.AreEqual("问剑台", label.text);
            }
            finally
            {
                RestoreWorldGrid(oldGrid);
                Object.DestroyImmediate(targetHost);
                Object.DestroyImmediate(playerHost);
                Object.DestroyImmediate(host);
            }
        }

        private static void AssertColor(byte r, byte g, byte b, byte a, Color actual)
        {
            Assert.AreEqual(new Color32(r, g, b, a), (Color32)actual);
        }

        private static void InvokeUpdate(InkMinimapHud hud)
        {
            typeof(InkMinimapHud).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hud, null);
        }

        private static int ReadLookupCount(InkMinimapHud hud)
        {
            FieldInfo field = typeof(InkMinimapHud).GetField("_sceneLookupCount", BindingFlags.Instance | BindingFlags.NonPublic);
            return (int)field.GetValue(hud);
        }

        private static Vector2 SetWorldGrid(int width, int height)
        {
            Vector2 old = new Vector2(WorldBuilder.Width, WorldBuilder.Height);
            SetWorldDimension("Width", width);
            SetWorldDimension("Height", height);
            return old;
        }

        private static void RestoreWorldGrid(Vector2 old)
        {
            SetWorldDimension("Width", (int)old.x);
            SetWorldDimension("Height", (int)old.y);
        }

        private static void SetWorldDimension(string propertyName, int value)
        {
            typeof(WorldBuilder).GetProperty(propertyName).GetSetMethod(true).Invoke(null, new object[] { value });
        }
    }
}
