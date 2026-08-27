using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>右上角水墨卷轴小地图。静态画框只构建一次，动态图标复用池内 Image。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(230)]
    public sealed class InkMinimapHud : MonoBehaviour
    {
        public static readonly Vector2 PanelSize = new Vector2(310.0f, 220.0f);
        public static readonly Vector2 MapSize = new Vector2(278.0f, 174.0f);

        public const float RightMargin = 28.0f;
        public const float TopMargin = 24.0f;
        public const float DiagnosticsGap = 20.0f;
        public const float DiagnosticsTopOffset = TopMargin + 220.0f + DiagnosticsGap;
        public const float MarkerRevealRadius = 120.0f;
        public const float MarkerRefreshInterval = 0.1f;
        public const float MarkerMovementThreshold = 2.0f;
        public const float BoundaryWashDuration = 0.55f;
        public const float BoundaryTextDuration = 0.9f;

        private static readonly Vector2 MapOrigin = new Vector2(16.0f, 28.0f);

        [SerializeField] private PlayerController player;
        [SerializeField] private WorldBoundaryFeedback boundaryFeedback;

        private readonly Dictionary<string, Image> _markerImages = new Dictionary<string, Image>();
        private readonly Dictionary<string, MinimapMarker> _markerSources = new Dictionary<string, MinimapMarker>();
        private readonly Stack<Image> _markerPool = new Stack<Image>();
        private readonly List<string> _scratchIds = new List<string>();
        private readonly Image[] _washImages = new Image[4];

        private RectTransform _canvasRoot;
        private RectTransform _scroll;
        private RectTransform _markersRoot;
        private RectTransform _playerArrow;
        private Text _targetLabel;
        private AdventurePanelsHud _adventurePanels;
        private bool _registryDirty = true;
        private bool _hasMarkerSample;
        private Vector2 _lastMarkerPlayerPosition;
        private float _nextMarkerRefreshTime;
        private float _boundaryWashRemaining;
        private float _boundaryTextRemaining;
        private string _targetText = "未定目标";

        private void OnEnable()
        {
            // 禁用期间不会接收 Changed；重启时必须重新对齐一次真实注册表。
            _registryDirty = true;
            MinimapMarkerRegistry.Changed += OnRegistryChanged;
            if (boundaryFeedback != null)
            {
                boundaryFeedback.Triggered += OnBoundaryTriggered;
            }
        }

        private void OnDisable()
        {
            MinimapMarkerRegistry.Changed -= OnRegistryChanged;
            if (boundaryFeedback != null)
            {
                boundaryFeedback.Triggered -= OnBoundaryTriggered;
            }
        }

        private void Start()
        {
            Build();
        }

        private void Update()
        {
            if (_canvasRoot == null)
            {
                Build();
            }

            ResolveBindings();
            RefreshModalVisibility();
            Vector2 playerPosition = ResolvePlayerPosition();
            RefreshPlayer(playerPosition);

            bool moved = !_hasMarkerSample
                || (playerPosition - _lastMarkerPlayerPosition).sqrMagnitude
                    >= MarkerMovementThreshold * MarkerMovementThreshold;
            if (_registryDirty || moved || Time.unscaledTime >= _nextMarkerRefreshTime)
            {
                RefreshMarkers(playerPosition);
            }

            TickBoundaryFeedback(Time.unscaledDeltaTime);
        }

        /// <summary>供场景装配代码显式绑定玩家与 Task 3 的边界事件源。</summary>
        public void Bind(PlayerController controller, WorldBoundaryFeedback feedback)
        {
            player = controller;
            SetBoundaryFeedback(feedback);
        }

        /// <summary>一次性构建卷轴、裁剪区、道路、玩家箭头与边界水洗层。</summary>
        public void Build()
        {
            if (_canvasRoot != null)
            {
                return;
            }

            GameObject canvasGo = new GameObject("InkMinimapCanvas", typeof(RectTransform));
            canvasGo.layer = LayerMask.NameToLayer("UI");
            canvasGo.transform.SetParent(transform, false);
            _canvasRoot = canvasGo.GetComponent<RectTransform>();

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 160;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Hud.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            _scroll = Hud.NewRect("Scroll", _canvasRoot);
            Hud.Anchor(_scroll, Vector2.one, Vector2.one, Vector2.one);
            _scroll.anchoredPosition = new Vector2(-RightMargin, -TopMargin);
            _scroll.sizeDelta = PanelSize;

            BuildScrollLayers();
            BuildMapArea();
            BuildPlayerArrow();
            BuildBoundaryWash();

            _targetLabel = Hud.NewText("TargetLabel", _scroll, 15, TextAnchor.MiddleCenter, InkMinimapPalette.DeepInk);
            Hud.Anchor(_targetLabel.rectTransform, new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f));
            _targetLabel.rectTransform.anchoredPosition = new Vector2(16.0f, 3.0f);
            _targetLabel.rectTransform.sizeDelta = new Vector2(MapSize.x, 22.0f);
            _targetLabel.text = _targetText;

            _registryDirty = true;
        }

        /// <summary>立即同步注册表与位置；正常运行由 10 Hz 刷新节奏自动调用。</summary>
        public void RefreshNow()
        {
            Build();
            ResolveBindings();
            RefreshModalVisibility();
            Vector2 playerPosition = ResolvePlayerPosition();
            RefreshPlayer(playerPosition);
            RefreshMarkers(playerPosition);
        }

        private void BuildScrollLayers()
        {
            RectTransform offsetBorder = Hud.NewImageRect("InkBorderOffset", _scroll, InkMinimapPalette.RoadInk);
            Hud.Anchor(offsetBorder, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            offsetBorder.anchoredPosition = new Vector2(-2.0f, -2.0f);
            offsetBorder.sizeDelta = new Vector2(314.0f, 224.0f);

            RectTransform inkBorder = Hud.NewImageRect("InkBorder", _scroll, InkMinimapPalette.DeepInk);
            Hud.Anchor(inkBorder, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            inkBorder.anchoredPosition = new Vector2(1.0f, 1.0f);
            inkBorder.sizeDelta = new Vector2(312.0f, 222.0f);

            RectTransform paper = Hud.NewImageRect("Paper", _scroll, InkMinimapPalette.Paper);
            Hud.Anchor(paper, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            paper.anchoredPosition = Vector2.zero;
            paper.sizeDelta = PanelSize;
        }

        private void BuildMapArea()
        {
            RectTransform mapClip = Hud.NewImageRect("MapClip", _scroll, WithAlpha(InkMinimapPalette.Paper, 0.42f));
            Hud.Anchor(mapClip, Vector2.zero, Vector2.zero, Vector2.zero);
            mapClip.anchoredPosition = MapOrigin;
            mapClip.sizeDelta = MapSize;
            mapClip.gameObject.AddComponent<RectMask2D>();

            BuildRoadStroke("Road-WestEast", mapClip, new Vector2(139.0f, 87.0f), new Vector2(252.0f, 4.0f), -8.0f);
            BuildRoadStroke("Road-NorthSouth", mapClip, new Vector2(151.0f, 88.0f), new Vector2(148.0f, 3.0f), 68.0f);
            BuildRoadStroke("Road-Branch", mapClip, new Vector2(75.0f, 55.0f), new Vector2(94.0f, 3.0f), 28.0f);

            _markersRoot = Hud.NewRect("Markers", mapClip);
            Hud.Stretch(_markersRoot, 0.0f);
        }

        private static void BuildRoadStroke(string name, Transform parent, Vector2 position, Vector2 size, float degrees)
        {
            RectTransform road = Hud.NewImageRect(name, parent, InkMinimapPalette.RoadInk);
            Hud.Anchor(road, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
            road.anchoredPosition = position;
            road.sizeDelta = size;
            road.localRotation = Quaternion.Euler(0.0f, 0.0f, degrees);
        }

        private void BuildPlayerArrow()
        {
            _playerArrow = Hud.NewImageRect("PlayerArrow", _scroll, InkMinimapPalette.PlayerJade);
            Hud.Anchor(_playerArrow, Vector2.zero, Vector2.zero, new Vector2(0.35f, 0.5f));
            _playerArrow.sizeDelta = new Vector2(16.0f, 6.0f);

            RectTransform tip = Hud.NewImageRect("Tip", _playerArrow, InkMinimapPalette.PlayerJade);
            Hud.Anchor(tip, new Vector2(1.0f, 0.5f), new Vector2(1.0f, 0.5f), new Vector2(0.0f, 0.5f));
            tip.anchoredPosition = Vector2.zero;
            tip.sizeDelta = new Vector2(5.0f, 10.0f);
        }

        private void BuildBoundaryWash()
        {
            RectTransform washRoot = Hud.NewRect("BoundaryWash", _scroll);
            Hud.Anchor(washRoot, Vector2.zero, Vector2.zero, Vector2.zero);
            washRoot.anchoredPosition = MapOrigin;
            washRoot.sizeDelta = MapSize;

            _washImages[0] = BuildWash("Left", washRoot, Vector2.zero, new Vector2(18.0f, MapSize.y), new Vector2(0.0f, 0.0f));
            _washImages[1] = BuildWash("Right", washRoot, new Vector2(1.0f, 0.0f), new Vector2(18.0f, MapSize.y), new Vector2(1.0f, 0.0f));
            _washImages[2] = BuildWash("Top", washRoot, new Vector2(0.0f, 1.0f), new Vector2(MapSize.x, 18.0f), new Vector2(0.0f, 1.0f));
            _washImages[3] = BuildWash("Bottom", washRoot, Vector2.zero, new Vector2(MapSize.x, 18.0f), Vector2.zero);
        }

        private static Image BuildWash(string name, Transform parent, Vector2 anchor, Vector2 size, Vector2 pivot)
        {
            RectTransform rect = Hud.NewImageRect(name, parent, WithAlpha(InkMinimapPalette.Cinnabar, 0.52f));
            Hud.Anchor(rect, anchor, anchor, pivot);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            rect.gameObject.SetActive(false);
            return rect.GetComponent<Image>();
        }

        private void ResolveBindings()
        {
            if (player == null)
            {
                player = FindOne<PlayerController>();
            }
            if (boundaryFeedback == null)
            {
                SetBoundaryFeedback(FindOne<WorldBoundaryFeedback>());
            }
            if (_adventurePanels == null)
            {
                _adventurePanels = FindOne<AdventurePanelsHud>();
            }
        }

        private void RefreshModalVisibility()
        {
            if (_scroll != null)
            {
                bool modalOpen = _adventurePanels != null
                    && _adventurePanels.ActivePanel != AdventurePanelKind.None;
                _scroll.gameObject.SetActive(!modalOpen);
            }
        }

        private void SetBoundaryFeedback(WorldBoundaryFeedback feedback)
        {
            if (boundaryFeedback == feedback)
            {
                return;
            }
            if (boundaryFeedback != null && isActiveAndEnabled)
            {
                boundaryFeedback.Triggered -= OnBoundaryTriggered;
            }
            boundaryFeedback = feedback;
            if (boundaryFeedback != null && isActiveAndEnabled)
            {
                boundaryFeedback.Triggered += OnBoundaryTriggered;
            }
        }

        private Vector2 ResolvePlayerPosition()
        {
            if (player != null)
            {
                return player.transform.position;
            }

            IReadOnlyList<MinimapMarker> markers = MinimapMarkerRegistry.Markers;
            for (int i = 0; i < markers.Count; i++)
            {
                MinimapMarker marker = markers[i];
                if (marker != null && marker.Kind == MinimapMarkerKind.Player)
                {
                    return marker.transform.position;
                }
            }
            return Vector2.zero;
        }

        private void RefreshPlayer(Vector2 playerPosition)
        {
            if (_playerArrow == null)
            {
                return;
            }
            Vector2 projected = MinimapProjection.Project(
                playerPosition,
                new Vector2(WorldBuilder.WorldWidth, WorldBuilder.WorldHeight),
                0.035f);
            _playerArrow.anchoredPosition = MapOrigin + new Vector2(projected.x * MapSize.x, projected.y * MapSize.y);

            Vector2 facing = player != null ? player.LastFacing : Vector2.right;
            if (facing.sqrMagnitude <= 0.0f)
            {
                facing = Vector2.right;
            }
            _playerArrow.localRotation = Quaternion.Euler(0.0f, 0.0f, Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg);
        }

        private void RefreshMarkers(Vector2 playerPosition)
        {
            if (_registryDirty)
            {
                RebuildMarkerMembership();
            }

            Vector2 worldSize = new Vector2(WorldBuilder.WorldWidth, WorldBuilder.WorldHeight);
            MinimapMarker nearestTarget = null;
            float nearestTargetDistance = float.PositiveInfinity;
            foreach (KeyValuePair<string, MinimapMarker> pair in _markerSources)
            {
                MinimapMarker marker = pair.Value;
                Image image;
                if (marker == null || !_markerImages.TryGetValue(pair.Key, out image))
                {
                    continue;
                }

                Vector2 markerPosition = marker.transform.position;
                bool visible = MinimapVisibilityRules.ShouldShow(
                    marker.Kind,
                    playerPosition,
                    markerPosition,
                    MarkerRevealRadius);
                image.gameObject.SetActive(visible);
                if (!visible)
                {
                    continue;
                }

                Vector2 projected = MinimapProjection.Project(markerPosition, worldSize, 0.035f);
                RectTransform rect = (RectTransform)image.transform;
                rect.anchoredPosition = new Vector2(projected.x * MapSize.x, projected.y * MapSize.y);

                if (marker.Kind == MinimapMarkerKind.QuestTarget || marker.Kind == MinimapMarkerKind.ChapterEnemy)
                {
                    float distance = (markerPosition - playerPosition).sqrMagnitude;
                    if (distance < nearestTargetDistance)
                    {
                        nearestTargetDistance = distance;
                        nearestTarget = marker;
                    }
                }
            }

            _targetText = nearestTarget != null && !string.IsNullOrEmpty(nearestTarget.DisplayName)
                ? nearestTarget.DisplayName
                : "未定目标";
            if (_targetLabel != null && _boundaryTextRemaining <= 0.0f)
            {
                _targetLabel.text = _targetText;
            }

            _lastMarkerPlayerPosition = playerPosition;
            _hasMarkerSample = true;
            _nextMarkerRefreshTime = Time.unscaledTime + MarkerRefreshInterval;
        }

        private void RebuildMarkerMembership()
        {
            _markerSources.Clear();
            IReadOnlyList<MinimapMarker> markers = MinimapMarkerRegistry.Markers;
            for (int i = 0; i < markers.Count; i++)
            {
                MinimapMarker marker = markers[i];
                if (marker == null || marker.Kind == MinimapMarkerKind.Player || string.IsNullOrEmpty(marker.StableId))
                {
                    continue;
                }
                if (!_markerSources.ContainsKey(marker.StableId))
                {
                    _markerSources.Add(marker.StableId, marker);
                }
            }

            _scratchIds.Clear();
            foreach (string stableId in _markerImages.Keys)
            {
                if (!_markerSources.ContainsKey(stableId))
                {
                    _scratchIds.Add(stableId);
                }
            }
            for (int i = 0; i < _scratchIds.Count; i++)
            {
                string stableId = _scratchIds[i];
                Image released = _markerImages[stableId];
                _markerImages.Remove(stableId);
                released.gameObject.SetActive(false);
                _markerPool.Push(released);
            }

            foreach (KeyValuePair<string, MinimapMarker> pair in _markerSources)
            {
                Image image;
                if (!_markerImages.TryGetValue(pair.Key, out image))
                {
                    image = AcquireMarkerImage(pair.Key);
                    _markerImages.Add(pair.Key, image);
                }
                image.color = MarkerColor(pair.Value.Kind);
            }
            _registryDirty = false;
        }

        private Image AcquireMarkerImage(string stableId)
        {
            Image image;
            if (_markerPool.Count > 0)
            {
                image = _markerPool.Pop();
                image.gameObject.name = "Marker-" + stableId;
            }
            else
            {
                RectTransform rect = Hud.NewImageRect("Marker-" + stableId, _markersRoot, InkMinimapPalette.DeepInk);
                Hud.Anchor(rect, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
                rect.sizeDelta = new Vector2(10.0f, 10.0f);
                image = rect.GetComponent<Image>();
            }
            image.gameObject.SetActive(true);
            return image;
        }

        private static Color MarkerColor(MinimapMarkerKind kind)
        {
            if (kind == MinimapMarkerKind.QuestTarget || kind == MinimapMarkerKind.ChapterEnemy)
            {
                return InkMinimapPalette.Cinnabar;
            }
            if (kind == MinimapMarkerKind.Npc || kind == MinimapMarkerKind.Shop || kind == MinimapMarkerKind.Building)
            {
                return InkMinimapPalette.NpcGold;
            }
            return InkMinimapPalette.DeepInk;
        }

        private void OnRegistryChanged()
        {
            _registryDirty = true;
        }

        private void OnBoundaryTriggered(Vector2 direction)
        {
            int index;
            if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
            {
                index = direction.x >= 0.0f ? 1 : 0;
            }
            else
            {
                index = direction.y >= 0.0f ? 2 : 3;
            }

            for (int i = 0; i < _washImages.Length; i++)
            {
                if (_washImages[i] != null)
                {
                    _washImages[i].gameObject.SetActive(i == index);
                    _washImages[i].color = WithAlpha(InkMinimapPalette.Cinnabar, 0.52f);
                }
            }
            _boundaryWashRemaining = BoundaryWashDuration;
            _boundaryTextRemaining = BoundaryTextDuration;
            if (_targetLabel != null)
            {
                _targetLabel.text = "前方无路";
            }
        }

        private void TickBoundaryFeedback(float unscaledDeltaTime)
        {
            if (_boundaryWashRemaining > 0.0f)
            {
                _boundaryWashRemaining -= unscaledDeltaTime;
                float alpha = 0.52f * Mathf.Clamp01(_boundaryWashRemaining / BoundaryWashDuration);
                for (int i = 0; i < _washImages.Length; i++)
                {
                    Image wash = _washImages[i];
                    if (wash != null && wash.gameObject.activeSelf)
                    {
                        wash.color = WithAlpha(InkMinimapPalette.Cinnabar, alpha);
                        if (_boundaryWashRemaining <= 0.0f)
                        {
                            wash.gameObject.SetActive(false);
                        }
                    }
                }
            }

            if (_boundaryTextRemaining > 0.0f)
            {
                _boundaryTextRemaining -= unscaledDeltaTime;
                if (_boundaryTextRemaining <= 0.0f && _targetLabel != null)
                {
                    _targetLabel.text = _targetText;
                }
            }
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        private static T FindOne<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<T>();
#else
            return Object.FindObjectOfType<T>();
#endif
        }
    }
}
