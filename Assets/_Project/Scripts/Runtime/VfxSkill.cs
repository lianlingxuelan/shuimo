// -----------------------------------------------------------------------------
// VfxSkill.cs —— 技能特效：光环爆发、莲花绽放、闪避残影三种程序化特效
//
// 【本文件的特效全部由代码生成，没有用任何美术资源或粒子系统】
// 圆环、花瓣、残影都是把 SpriteFactory 造出来的简单几何图形，
// 按时间曲线不断改变缩放、旋转和透明度而形成的动画。
// 这么做的原因：原型阶段没有美术资源，但战斗手感必须先验证出来。
// 用几十行数学换一个能看的打击反馈，比等美术排期划算得多。
//
// 【阅读本文件需要的一个核心概念："归一化时间 t"】
// 下面所有 Layout* 方法都接收一个 t（0 → 1，表示特效播放进度）。
// 把动画写成"t 的函数"而不是"每帧加一点"，好处是：
//   · 任意时刻的画面完全由 t 决定，不会因为掉帧而累积误差；
//   · 想让特效变快变慢，只改总时长，曲线形状完全不变。
// 这是做程序化动画的标准姿势。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>特效形态。一个组件靠这个枚举复用三套表现，避免写三个几乎相同的类。</summary>
    public enum VfxSkillMode
    {
        Ring = 0,       // 向外扩散的光环（范围爆发类技能）
        Petals = 1,     // 扇形展开的花瓣（弧形攻击类技能）
        Trail = 2       // 拖在身后的残影（位移类）
    }

    /// <summary>程序化技能特效。播完自动销毁自身，调用方不需要管理生命周期。</summary>
    [DisallowMultipleComponent]
    public sealed class VfxSkill : MonoBehaviour
    {
        public const float RingDuration = 0.34f;
        public const float PetalDuration = 0.42f;
        public const float TrailDuration = 0.26f;

        public const int RingSortingOrder = 26;
        public const int PetalSortingOrder = 29;
        public const int TrailSortingOrder = 24;

        public const int PetalCount = 5;
        public const int TrailSegments = 5;

        public static readonly Color BurstTint = new Color(0.60f, 0.80f, 1.00f, 0.90f);
        public static readonly Color LotusTint = new Color(0.92f, 0.35f, 0.48f, 0.88f);
        public static readonly Color DodgeTint = new Color(0.86f, 0.92f, 0.98f, 0.55f);

        private VfxSkillMode _mode = VfxSkillMode.Ring;
        private SpriteRenderer[] _parts;
        private float _duration = RingDuration;
        private float _age;
        private float _radius = 100.0f;
        private float _arcDeg = 120.0f;
        private float _facingDeg;
        private Color _tint = Color.white;
        private Vector3 _origin;
        private Vector2 _dir = Vector2.right;
        private float _distance;

        public VfxSkillMode Mode
        {
            get { return _mode; }
        }

        public float Age
        {
            get { return _age; }
        }

        // 三个对外快捷入口，都是"建实例 + 按形态初始化"两步，方便别处一行调用。
        public static VfxSkill PlayBurst(Vector3 origin, Vector2 facing, float radius)
        {
            VfxSkill fx = NewInstance("VfxBurst", origin);
            fx.InitRing(facing, radius, BurstTint);
            return fx;
        }

        public static VfxSkill PlayLotus(Vector3 origin, Vector2 facing, float radius, float arcDeg)
        {
            VfxSkill fx = NewInstance("VfxLotus", origin);
            fx.InitPetals(facing, radius, arcDeg, LotusTint);
            return fx;
        }

        public static VfxSkill PlayDodge(Vector3 origin, Vector2 dir, float distance)
        {
            VfxSkill fx = NewInstance("VfxDodge", origin);
            fx.InitTrail(dir, distance, DodgeTint);
            return fx;
        }

        public static VfxSkill PlayForSkill(SkillDef def, Vector3 origin, Vector2 facing)
        {
            if (def == null)
            {
                return null;
            }

            // 水剑斩沿用 T2 的月牙剑气：同一招在 T2 / T3 两条路径下表现必须一致，
            // 否则老玩家会以为"普攻被改了"。
            if (def.Id == SkillConfig.SKILL_BASIC_SLASH)
            {
                VfxSlash.Play(origin, facing, def.Range, def.ArcDeg);
                return null;
            }
            if (def.Id == SkillConfig.SKILL_DODGE_ROLL)
            {
                // 翻滚是个位移技能，给它走残影形态而不是爆发形态。
                return PlayDodge(origin, facing, SkillConfig.DODGE_DISTANCE);
            }
            if (def.Shape == SkillShape.Circle)
            {
                return PlayBurst(origin, facing, def.Range);
            }
            // 其余弧形/扇形技能一律用莲花绽放。
            return PlayLotus(origin, facing, def.Range, def.ArcDeg);
        }

        // 简单的静态工厂：new 一个空 GameObject，摆到 origin，再挂本组件。
        // z 强制为 0，因为本游戏是 2D，所有战斗特效都贴在同一个平面上。
        private static VfxSkill NewInstance(string name, Vector3 origin)
        {
            GameObject go = new GameObject(name);
            go.transform.position = new Vector3(origin.x, origin.y, 0.0f);
            return go.AddComponent<VfxSkill>();
        }

        private void InitRing(Vector2 facing, float radius, Color tint)
        {
            _mode = VfxSkillMode.Ring;
            _duration = RingDuration;
            _age = 0.0f;
            _tint = tint;
            // 没传半径就退回配置里的默认爆发半径，保证"裸调用"也不会画成 0 大小。
            _radius = radius > 0.0f ? radius : SkillConfig.BURST_RANGE;
            _facingDeg = FacingToDegrees(facing);
            _origin = transform.position;

            // 光环由三层叠成：中心实心核 + 主环 + 延迟出现的回声环（Echo 复用同一张贴图）。
            Sprite ring = SpriteFactory.Circle(
                "vfx_ring", new Color(tint.r, tint.g, tint.b, 0.0f), Color.white, 5.0f);
            Sprite core = SpriteFactory.Circle(
                "vfx_core", Color.white, new Color(0.0f, 0.0f, 0.0f, 0.0f), 0.0f);

            _parts = new SpriteRenderer[3];
            _parts[0] = NewPart("Core", core, RingSortingOrder);
            _parts[1] = NewPart("Ring", ring, RingSortingOrder + 1);
            _parts[2] = NewPart("RingEcho", ring, RingSortingOrder + 2);

            LayoutRing(0.0f);
        }

        private void InitPetals(Vector2 facing, float radius, float arcDeg, Color tint)
        {
            _mode = VfxSkillMode.Petals;
            _duration = PetalDuration;
            _age = 0.0f;
            _tint = tint;
            // 与光环同理：调用方没给就用配置默认值。
            _radius = radius > 0.0f ? radius : SkillConfig.LOTUS_RANGE;
            _arcDeg = arcDeg > 0.0f ? arcDeg : SkillConfig.LOTUS_ARC_DEG;
            _facingDeg = FacingToDegrees(facing);
            _origin = transform.position;

            // 每片花瓣的张角：用总弧角均摊，再乘 0.9 留一点缝，避免花瓣糊在一起。
            // Mathf.Max 兜底：即使弧角极小也至少给 12°，形状才看得出来。
            float petalArc = Mathf.Max(12.0f, _arcDeg / PetalCount * 0.9f);
            Sprite petal = SpriteFactory.Crescent(
                "lotus_" + Mathf.RoundToInt(petalArc), tint, petalArc, 0.42f);

            _parts = new SpriteRenderer[PetalCount];
            for (int i = 0; i < PetalCount; i++)
            {
                _parts[i] = NewPart("Petal" + i, petal, PetalSortingOrder - i);
            }

            LayoutPetals(0.0f);
        }

        private void InitTrail(Vector2 dir, float distance, Color tint)
        {
            _mode = VfxSkillMode.Trail;
            _duration = TrailDuration;
            _age = 0.0f;
            _tint = tint;
            // 没传位移距离就用配置里的翻滚距离；方向为 0 时退化成朝右，避免归一化成 NaN。
            _distance = distance > 0.0f ? distance : SkillConfig.DODGE_DISTANCE;
            _dir = dir.sqrMagnitude > 0.0f ? dir.normalized : Vector2.right;
            _facingDeg = FacingToDegrees(_dir);
            _origin = transform.position;

            // 残影是几个半透明"幽灵"圆，叠在身后；排序号越大越靠前，所以 Segment 0 在最上层。
            Sprite ghost = SpriteFactory.Circle(
                "vfx_ghost", Color.white, new Color(0.0f, 0.0f, 0.0f, 0.0f), 0.0f);

            _parts = new SpriteRenderer[TrailSegments];
            for (int i = 0; i < TrailSegments; i++)
            {
                _parts[i] = NewPart("Ghost" + i, ghost, TrailSortingOrder - i);
            }

            LayoutTrail(0.0f);
        }

        // 建一个子物体并套上贴图，统一设置层级和着色（继承 _tint）。
        // 子物体用 SetParent(transform, false)：世界坐标不跟随父级缩放，方便我们手动算 localScale。
        private SpriteRenderer NewPart(string name, Sprite sprite, int sortingOrder)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = sortingOrder;
            sr.color = _tint;
            return sr;
        }

        private void Update()
        {
            // 用**表现层**帧间隔累加年龄，再把年龄换算成 0→1 的归一化进度 t。
            // duration 为 0 直接当放完（防御除零）。
            //
            // 【为什么不是 Time.deltaTime】
            // hitstop 期间技能特效必须与角色一起定格。同理于 VfxSlash：
            // 只冻人不冻特效，顿帧就穿帮了。
            _age += FeedbackClock.Delta;
            float t = _duration > 0.0f ? Mathf.Clamp01(_age / _duration) : 1.0f;

            // 按当前形态分发到对应的 Layout 方法，把"t → 具体画面"交给它们算。
            switch (_mode)
            {
                case VfxSkillMode.Petals:
                    LayoutPetals(t);
                    break;
                case VfxSkillMode.Trail:
                    LayoutTrail(t);
                    break;
                case VfxSkillMode.Ring:
                default:
                    LayoutRing(t);
                    break;
            }

            // 年龄到了就自毁——整个特效的生命周期就是这么简单，调用方完全不用管。
            if (_age >= _duration)
            {
                Destroy(gameObject);
            }
        }

        private void LayoutRing(float t)
        {
            if (_parts == null)
            {
                return;
            }

            // eased 是"二次缓出"曲线：开头快、收尾慢，比线性更有打击感。
            float eased = 1.0f - (1.0f - t) * (1.0f - t);
            // unit 把"半径（世界单位）"换算成 SpriteRenderer 的缩放值。
            float unit = CircleUnitScale();

            for (int i = 0; i < _parts.Length; i++)
            {
                SpriteRenderer sr = _parts[i];
                if (sr == null)
                {
                    continue;
                }

                // 第 2 片（Echo）整体延后 0.22 出现，形成"主环先到、回声随后追上"的层次。
                float lag = i == 2 ? 0.22f : 0.0f;
                // 把它自己的进度从全局 t 里"抠"出来，再单独缓出。
                float local = Mathf.Clamp01((t - lag) / Mathf.Max(0.01f, 1.0f - lag));
                float localEased = 1.0f - (1.0f - local) * (1.0f - local);

                float scale;
                float alpha;
                if (i == 0)
                {
                    // 中心核：从小到大轻微胀开，并在后期淡出（乘以 0.55 让它比环更柔）。
                    scale = unit * _radius * Mathf.Lerp(0.30f, 0.55f, eased);
                    alpha = _tint.a * (1.0f - eased) * 0.55f;
                }
                else
                {
                    // 主环/回声环：从小缩放线性展开到完整半径，同时透明度淡出到 0。
                    scale = unit * _radius * Mathf.Lerp(0.22f, 1.0f, localEased);
                    alpha = _tint.a * (1.0f - localEased);
                }

                sr.transform.localPosition = Vector3.zero;
                sr.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, _facingDeg);
                sr.transform.localScale = new Vector3(scale, scale, 1.0f);
                sr.color = new Color(_tint.r, _tint.g, _tint.b, Mathf.Max(0.0f, alpha));
            }
        }

        private void LayoutPetals(float t)
        {
            if (_parts == null)
            {
                return;
            }

            // 同样用二次缓出让花瓣"绽开"有弹性；fade 在前 45% 保持全亮，之后线性淡出。
            float eased = 1.0f - (1.0f - t) * (1.0f - t);
            float fade = t < 0.45f ? 1.0f : 1.0f - (t - 0.45f) / 0.55f;
            float unit = CrescentUnitScale();
            // 5 片花瓣沿总弧角均匀铺开：片间距 = 弧角 / (片数-1)，起点在中线偏左半弧处。
            float step = _parts.Length > 1 ? _arcDeg / (_parts.Length - 1) : 0.0f;
            float start = _facingDeg - _arcDeg * 0.5f;

            for (int i = 0; i < _parts.Length; i++)
            {
                SpriteRenderer sr = _parts[i];
                if (sr == null)
                {
                    continue;
                }

                float deg = start + step * i;
                float bloom = Mathf.Lerp(0.35f, 1.0f, eased);
                float scale = unit * _radius * bloom;

                sr.transform.localPosition = Vector3.zero;
                sr.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, deg);
                sr.transform.localScale = new Vector3(scale, scale, 1.0f);
                sr.color = new Color(_tint.r, _tint.g, _tint.b,
                    Mathf.Max(0.0f, _tint.a * fade));
            }
        }

        private void LayoutTrail(float t)
        {
            if (_parts == null)
            {
                return;
            }

            float eased = 1.0f - (1.0f - t) * (1.0f - t);
            float unit = CircleUnitScale();
            // 幽灵圆的大小参考玩家身体半径；取不到就兜底 16，避免画成 0。
            float bodyRadius = WorldBuilder.PlayerBodySize * 0.5f;
            if (bodyRadius <= 0.0f)
            {
                bodyRadius = 16.0f;
            }

            for (int i = 0; i < _parts.Length; i++)
            {
                SpriteRenderer sr = _parts[i];
                if (sr == null)
                {
                    continue;
                }

                // along：这片幽灵沿位移方向走过的距离（越靠后越短，像拖尾）。
                float along = _distance * eased * (i / (float)_parts.Length);
                // decay：越靠后的残影越透明、越小——头部最实，尾部渐隐。
                float decay = 1.0f - i / (float)_parts.Length;

                sr.transform.localPosition = new Vector3(_dir.x * along, _dir.y * along, 0.0f);
                sr.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, _facingDeg);
                float scale = unit * bodyRadius * Mathf.Lerp(0.55f, 1.0f, decay);
                sr.transform.localScale = new Vector3(scale, scale, 1.0f);
                sr.color = new Color(_tint.r, _tint.g, _tint.b,
                    Mathf.Max(0.0f, _tint.a * (1.0f - t) * decay));
            }
        }

        // 圆贴图是按"直径 = ShapePixels 像素"造的，所以 1 个单位半径对应 2/ShapePixels 的缩放。
        private static float CircleUnitScale()
        {
            return 2.0f / SpriteFactory.ShapePixels;
        }

        // 月牙贴图是按"张角宽度 = ShapePixels 像素"造的，缩放系数只有圆的一半（它是扇形不是整圆）。
        private static float CrescentUnitScale()
        {
            return 1.0f / SpriteFactory.ShapePixels;
        }

        // 把朝向向量转成角度（度）。朝向为 0 直接返回 0，避免 Atan2(0,0) 这种未定义情况。
        private static float FacingToDegrees(Vector2 facing)
        {
            if (facing.sqrMagnitude <= 0.0f)
            {
                return 0.0f;
            }
            return Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg;
        }
    }
}
