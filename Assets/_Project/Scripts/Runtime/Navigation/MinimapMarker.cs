using UnityEngine;

namespace Xianxia.Unity.T2
{
    [DisallowMultipleComponent]
    public sealed class MinimapMarker : MonoBehaviour
    {
        public MinimapMarkerKind Kind { get; private set; }

        public string MarkerId { get; private set; } = string.Empty;

        public string DisplayName { get; private set; } = string.Empty;

        public bool IsPermanentlyVisible { get; private set; }

        public Transform MarkerTransform
        {
            get { return transform; }
        }

        public void Configure(MinimapMarkerKind kind, string markerId, string displayName, bool isPermanentlyVisible)
        {
            Kind = kind;
            MarkerId = markerId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            IsPermanentlyVisible = isPermanentlyVisible;
        }

        private void OnEnable()
        {
            MinimapMarkerRegistry.Register(this);
        }

        private void OnDisable()
        {
            MinimapMarkerRegistry.Unregister(this);
        }

        private void OnDestroy()
        {
            MinimapMarkerRegistry.Unregister(this);
        }
    }
}
