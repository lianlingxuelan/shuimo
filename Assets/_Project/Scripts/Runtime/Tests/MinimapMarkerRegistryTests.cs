using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class MinimapMarkerRegistryTests
    {
        [Test]
        public void Registry_RegisterThenUnregisterMaintainsUniqueLiveMarkers()
        {
            GameObject go = new GameObject("marker");
            MinimapMarker marker = go.AddComponent<MinimapMarker>();
            marker.Configure(MinimapMarkerKind.Npc, "guide", "竹市引路人", true);

            MinimapMarkerRegistry.Register(marker);
            MinimapMarkerRegistry.Register(marker);
            Assert.AreEqual(1, MinimapMarkerRegistry.Markers.Count);

            MinimapMarkerRegistry.Unregister(marker);
            Assert.AreEqual(0, MinimapMarkerRegistry.Markers.Count);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Registry_DisableThenEnableRestoresOneLiveMarker()
        {
            GameObject go = new GameObject("marker");
            MinimapMarker marker = go.AddComponent<MinimapMarker>();
            marker.Configure(MinimapMarkerKind.Enemy, "enemy_1", "山魈", false);

            go.SetActive(false);
            Assert.IsFalse(Contains(marker));

            go.SetActive(true);
            Assert.AreEqual(1, Count(marker));
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Registry_ChangedFiresOnlyWhenMembershipChanges()
        {
            GameObject go = new GameObject("marker");
            MinimapMarker marker = go.AddComponent<MinimapMarker>();
            marker.Configure(MinimapMarkerKind.Enemy, "enemy_1", "山魈", false);
            int changedCount = 0;
            System.Action changed = () => changedCount++;
            MinimapMarkerRegistry.Changed += changed;

            MinimapMarkerRegistry.Register(marker);
            MinimapMarkerRegistry.Unregister(marker);
            MinimapMarkerRegistry.Unregister(marker);

            MinimapMarkerRegistry.Changed -= changed;
            Assert.AreEqual(1, changedCount);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Marker_ConfigurePublishesOnlyFinalMarkerData()
        {
            GameObject go = new GameObject("marker");
            MinimapMarker marker = go.AddComponent<MinimapMarker>();
            MinimapMarker observed = null;
            System.Action changed = () => observed = Find(marker);
            MinimapMarkerRegistry.Changed += changed;

            marker.Configure(MinimapMarkerKind.Npc, "guide", "竹市引路人", true);

            MinimapMarkerRegistry.Changed -= changed;
            Assert.AreSame(marker, observed);
            Assert.AreEqual(MinimapMarkerKind.Npc, observed.Kind);
            Assert.AreEqual("guide", observed.StableId);
            Assert.AreEqual("竹市引路人", observed.DisplayName);
            Assert.IsTrue(observed.PermanentVisibility);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Marker_ReconfigureUpdatesDataWithoutChangingRegistryMembership()
        {
            GameObject go = new GameObject("marker");
            MinimapMarker marker = go.AddComponent<MinimapMarker>();
            marker.Configure(MinimapMarkerKind.Enemy, "enemy_1", "山魈", false);
            int changedCount = 0;
            System.Action changed = () => changedCount++;
            MinimapMarkerRegistry.Changed += changed;

            marker.Configure(MinimapMarkerKind.Npc, "guide", "竹市引路人", true);

            MinimapMarkerRegistry.Changed -= changed;
            Assert.AreEqual(0, changedCount);
            Assert.AreEqual(1, MinimapMarkerRegistry.Markers.Count);
            Assert.AreEqual(MinimapMarkerKind.Npc, marker.Kind);
            Assert.AreEqual("guide", marker.StableId);
            Assert.AreEqual("竹市引路人", marker.DisplayName);
            Assert.IsTrue(marker.PermanentVisibility);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Registry_DestroyImmediateRemovesMarkerAndRaisesChangedOnce()
        {
            GameObject go = new GameObject("marker");
            MinimapMarker marker = go.AddComponent<MinimapMarker>();
            marker.Configure(MinimapMarkerKind.Enemy, "enemy_1", "山魈", false);
            int changedCount = 0;
            System.Action changed = () => changedCount++;
            MinimapMarkerRegistry.Changed += changed;

            Object.DestroyImmediate(go);

            MinimapMarkerRegistry.Changed -= changed;
            Assert.IsFalse(Contains(marker));
            Assert.AreEqual(1, changedCount);
        }

        private static bool Contains(MinimapMarker expected)
        {
            return Count(expected) > 0;
        }

        private static int Count(MinimapMarker expected)
        {
            int count = 0;
            for (int i = 0; i < MinimapMarkerRegistry.Markers.Count; i++)
            {
                if (MinimapMarkerRegistry.Markers[i] == expected)
                {
                    count++;
                }
            }
            return count;
        }

        private static MinimapMarker Find(MinimapMarker expected)
        {
            for (int i = 0; i < MinimapMarkerRegistry.Markers.Count; i++)
            {
                if (MinimapMarkerRegistry.Markers[i] == expected)
                {
                    return MinimapMarkerRegistry.Markers[i];
                }
            }
            return null;
        }
    }
}
