using UnityEngine;

namespace Xianxia.Unity.T2
{
    [DisallowMultipleComponent]
    public sealed class MinimapMarker : MonoBehaviour
    {
        private bool _isConfigured;

        public MinimapMarkerKind Kind { get; private set; }

        public string StableId { get; private set; } = string.Empty;

        public string DisplayName { get; private set; } = string.Empty;

        public bool PermanentVisibility { get; private set; }

        public string MarkerId
        {
            get { return StableId; }
        }

        public bool IsPermanentlyVisible
        {
            get { return PermanentVisibility; }
        }

        public Transform MarkerTransform
        {
            get { return transform; }
        }

        public void Configure(MinimapMarkerKind kind, string markerId, string displayName, bool isPermanentlyVisible)
        {
            Kind = kind;
            StableId = markerId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            PermanentVisibility = isPermanentlyVisible;

            bool wasConfigured = _isConfigured;
            _isConfigured = true;
            if (!isActiveAndEnabled)
            {
                return;
            }

            if (!wasConfigured)
            {
                MinimapMarkerRegistry.Register(this);
            }
        }

        private void OnEnable()
        {
            if (_isConfigured)
            {
                MinimapMarkerRegistry.Register(this);
            }
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
