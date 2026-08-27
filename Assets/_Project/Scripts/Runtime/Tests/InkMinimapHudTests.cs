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
                Assert.IsNotNull(host.transform.Find("InkMinimapCanvas/Scroll/PlayerArrow"));
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
                Object.DestroyImmediate(markerHost);
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
    }
}
