using System;
using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>单株竹子的纯视觉状态；掉落与战斗判定将在下一步接入。</summary>
    public enum BambooHarvestStage
    {
        Intact,
        Stump,
    }

    /// <summary>场景内可采集植物的视觉与掉落类别。</summary>
    public enum BambooPlantKind
    {
        Normal,
        Young,
        Shoot,
    }

    /// <summary>相对角色出生点的固定资源点，保证每次进入场景构图一致。</summary>
    public readonly struct BambooPlantPlacement
    {
        public readonly BambooPlantKind Kind;
        public readonly Vector2 Position;
        public readonly float Scale;

        public BambooPlantPlacement(BambooPlantKind kind, Vector2 position, float scale)
        {
            Kind = kind;
            Position = position;
            Scale = scale;
        }
    }

    /// <summary>独立于 Unity 对象的状态规则，供单元测试和玩法层复用。</summary>
    public static class BambooHarvestStageRules
    {
        public static BambooHarvestStage Next(BambooHarvestStage current)
        {
            return current == BambooHarvestStage.Intact
                ? BambooHarvestStage.Stump
                : BambooHarvestStage.Stump;
        }
    }

    /// <summary>独立单株竹的扇形命中规则，保持与普攻的半径/朝向语义一致。</summary>
    public static class BambooHarvestHitRules
    {
        public static bool IsHit(Vector2 origin, Vector2 facing, Vector2 target,
            float radius, float arcDeg)
        {
            Vector2 delta = target - origin;
            float distanceSqr = delta.sqrMagnitude;
            if (distanceSqr > radius * radius)
            {
                return false;
            }

            if (distanceSqr < 1e-6f)
            {
                return true;
            }

            if (facing.sqrMagnitude < 1e-6f)
            {
                facing = Vector2.right;
            }
            facing.Normalize();

            Vector2 direction = delta / Mathf.Sqrt(distanceSqr);
            float halfArcCos = Mathf.Cos(arcDeg * 0.5f * Mathf.Deg2Rad);
            return Vector2.Dot(direction, facing) >= halfArcCos;
        }
    }

    /// <summary>首轮可玩竹林的固定布局：可连续砍伐，又不会挤成一团。</summary>
    public static class BambooHarvestLayout
    {
        public static Vector2[] CreateTestGrovePositions()
        {
            const float radius = 60.0f;
            return new[]
            {
                new Vector2(radius, 0.0f),
                new Vector2(radius * 0.5f, radius * 0.8660254f),
                new Vector2(-radius * 0.5f, radius * 0.8660254f),
                new Vector2(-radius, 0.0f),
                new Vector2(-radius * 0.5f, -radius * 0.8660254f),
                new Vector2(radius * 0.5f, -radius * 0.8660254f),
            };
        }

        /// <summary>第一张竹林地图的固定构图：中央留白，资源沿林缘和石旁分布。</summary>
        public static BambooPlantPlacement[] CreateNaturalGrovePositions()
        {
            return new[]
            {
                new BambooPlantPlacement(BambooPlantKind.Normal, new Vector2(-155.0f, 92.0f), 5.3f),
                new BambooPlantPlacement(BambooPlantKind.Normal, new Vector2(-94.0f, 155.0f), 4.9f),
                new BambooPlantPlacement(BambooPlantKind.Normal, new Vector2(-145.0f, -105.0f), 5.1f),
                new BambooPlantPlacement(BambooPlantKind.Normal, new Vector2(170.0f, 110.0f), 5.4f),
                new BambooPlantPlacement(BambooPlantKind.Normal, new Vector2(158.0f, -82.0f), 4.8f),
                new BambooPlantPlacement(BambooPlantKind.Young, new Vector2(58.0f, 158.0f), 4.0f),
                new BambooPlantPlacement(BambooPlantKind.Young, new Vector2(222.0f, 22.0f), 3.7f),
                new BambooPlantPlacement(BambooPlantKind.Young, new Vector2(-225.0f, -22.0f), 3.8f),
                new BambooPlantPlacement(BambooPlantKind.Shoot, new Vector2(86.0f, -156.0f), 3.0f),
                new BambooPlantPlacement(BambooPlantKind.Shoot, new Vector2(-48.0f, -188.0f), 2.7f),
                new BambooPlantPlacement(BambooPlantKind.Shoot, new Vector2(232.0f, -122.0f), 2.8f),
                new BambooPlantPlacement(BambooPlantKind.Shoot, new Vector2(-230.0f, 124.0f), 2.6f),
            };
        }
    }

    /// <summary>
    /// 单株可砍竹的显示控制器。当前先负责完整竹与竹桩的状态切换；
    /// 后续攻击系统只需调用 <see cref="TryCut"/>，不用知道贴图细节。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ChoppableBambooView : MonoBehaviour
    {
        private SpriteRenderer _renderer;
        private Sprite _intactSprite;
        private Sprite _stumpSprite;
        private float _regrowRemaining;

        public BambooHarvestStage Stage { get; private set; }

        [Tooltip("砍成竹桩后多久重新长成完整竹（秒）。")]
        public float regrowSeconds = 18.0f;

        /// <summary>完整竹第一次被砍成竹桩时触发；重复挥砍不会重复给奖励。</summary>
        public event Action<ChoppableBambooView> OnCut;

        public void Configure(SpriteRenderer renderer, Sprite intactSprite, Sprite stumpSprite)
        {
            _renderer = renderer;
            _intactSprite = intactSprite;
            _stumpSprite = stumpSprite;
            Stage = BambooHarvestStage.Intact;
            _regrowRemaining = 0.0f;
            RefreshSprite();
        }

        /// <returns>只有第一次砍中会返回 true。</returns>
        public bool TryCut()
        {
            if (Stage == BambooHarvestStage.Stump)
            {
                return false;
            }

            Stage = BambooHarvestStageRules.Next(Stage);
            _regrowRemaining = Mathf.Max(0.0f, regrowSeconds);
            RefreshSprite();
            if (OnCut != null)
            {
                OnCut(this);
            }
            return true;
        }

        public void TickRegrowth(float delta)
        {
            if (Stage != BambooHarvestStage.Stump || _regrowRemaining <= 0.0f || delta <= 0.0f)
            {
                return;
            }

            _regrowRemaining -= delta;
            if (_regrowRemaining <= 0.0f)
            {
                _regrowRemaining = 0.0f;
                Stage = BambooHarvestStage.Intact;
                RefreshSprite();
            }
        }

        private void Update()
        {
            TickRegrowth(FeedbackClock.Delta);
        }

        private void RefreshSprite()
        {
            if (_renderer == null)
            {
                return;
            }

            _renderer.sprite = Stage == BambooHarvestStage.Intact ? _intactSprite : _stumpSprite;
        }
    }

    /// <summary>嫩笋是一击采集物：采走后隐藏，不会再重复给奖励。</summary>
    [DisallowMultipleComponent]
    public sealed class BambooShootView : MonoBehaviour
    {
        private SpriteRenderer _renderer;
        private bool _harvested;
        private float _regrowRemaining;

        public bool IsHarvested { get { return _harvested; } }

        [Tooltip("采走后多久重新长出嫩笋（秒）。")]
        public float regrowSeconds = 24.0f;

        public event Action<BambooShootView> OnHarvest;

        public void Configure(SpriteRenderer renderer, Sprite sprite)
        {
            _renderer = renderer;
            _harvested = false;
            _regrowRemaining = 0.0f;
            if (_renderer != null)
            {
                _renderer.sprite = sprite;
                _renderer.enabled = true;
            }
        }

        public bool TryHarvest()
        {
            if (_harvested)
            {
                return false;
            }

            _harvested = true;
            _regrowRemaining = Mathf.Max(0.0f, regrowSeconds);
            if (_renderer != null)
            {
                _renderer.enabled = false;
            }
            if (OnHarvest != null)
            {
                OnHarvest(this);
            }
            return true;
        }

        public void TickRegrowth(float delta)
        {
            if (!_harvested || _regrowRemaining <= 0.0f || delta <= 0.0f)
            {
                return;
            }

            _regrowRemaining -= delta;
            if (_regrowRemaining <= 0.0f)
            {
                _regrowRemaining = 0.0f;
                _harvested = false;
                if (_renderer != null)
                {
                    _renderer.enabled = true;
                }
            }
        }

        private void Update()
        {
            TickRegrowth(FeedbackClock.Delta);
        }
    }
}
