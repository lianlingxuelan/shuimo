using System;
using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    public static class MinimapMarkerRegistry
    {
        private static readonly List<MinimapMarker> LiveMarkers = new List<MinimapMarker>();

        public static event Action Changed;

        public static IReadOnlyList<MinimapMarker> Markers
        {
            get
            {
                if (RemoveNullMarkers())
                {
                    NotifyChanged();
                }
                return LiveMarkers;
            }
        }

        public static void Register(MinimapMarker marker)
        {
            bool removedNull = RemoveNullMarkers();
            if (marker == null || LiveMarkers.Contains(marker))
            {
                if (removedNull)
                {
                    NotifyChanged();
                }
                return;
            }

            LiveMarkers.Add(marker);
            NotifyChanged();
        }

        public static void Unregister(MinimapMarker marker)
        {
            bool removed = RemoveNullMarkers();
            if (marker != null)
            {
                removed |= LiveMarkers.Remove(marker);
            }
            if (removed)
            {
                NotifyChanged();
            }
        }

        public static void Refresh(MinimapMarker marker)
        {
            bool removedNull = RemoveNullMarkers();
            if (marker != null && LiveMarkers.Contains(marker))
            {
                NotifyChanged();
            }
            else if (removedNull)
            {
                NotifyChanged();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            LiveMarkers.Clear();
            Changed = null;
        }

        private static bool RemoveNullMarkers()
        {
            return LiveMarkers.RemoveAll(marker => marker == null) > 0;
        }

        private static void NotifyChanged()
        {
            Action changed = Changed;
            if (changed != null)
            {
                changed();
            }
        }
    }
}
