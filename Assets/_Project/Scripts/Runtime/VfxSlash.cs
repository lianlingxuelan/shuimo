// -----------------------------------------------------------------------------
// VfxSlash.cs —— 弧形剑气占位特效（asmdef: Xianxia.Unity.T2，PRD Q11）
//
// 【明确不要什么：平涂扇形】
// 把判定范围直接涂成一块半透明扇形，是调试可视化，不是特效。它看起来像手电筒，
// 而且因为每一刀形状完全相同，第三刀之后玩家的眼睛就会自动忽略它——
// 打击感为零。
//
// 【要什么：一记挥砍】
// 武侠的剑气是**一条划过去的弧**，有三个必须同时成立的特征：
//   1. 形状是月牙：外缘锐、内缘钝、两端收尖（由 SpriteFactory.Crescent 画出）
//   2. 会动：月牙沿弧线从起手角扫到收势角，而不是原地淡出
//   3. 有拖尾：身后跟一串逐渐变淡变小的残影，速度感来自它
//
// 【生命周期与对象池】
// 每刀新建 GameObject 在 2.5 刀/秒下是可接受的（每秒 2.5 次分配），
// 但拖尾会把它乘以 TrailCount 倍。所以整套特效做成**一个** GameObject +
// 若干子 SpriteRenderer，一次分配、播完自毁；再多就该上池了，
// 而 T2 的量级还远没到那一步。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>一次挥砍的剑气特效。由 <see cref="Play"/> 创建，播完自毁。</summary>
    [DisallowMultipleComponent]
    public sealed class VfxSlash : MonoBehaviour
    {
        /// <summary>整个挥砍动作的时长（秒）。略短于攻击冷却，留一点"收势"空隙。</summary>
        public const float Duration = 0.22f;

        /// <summary>拖尾残影数量。</summary>
        public const int TrailCount = 4;

        /// <summary>残影之间的角度间隔（度）。</summary>
        public const float TrailStepDeg = 11.0f;

        /// <summary>剑气主色（青碧，呼应 zone_youhuang 的 accent #8fd97a）。</summary>
        public static readonly Color SlashTint = new Color(0.78f, 0.94f, 0.72f, 0.92f);

        /// <summary>渲染层级。要盖在敌人（5）与玩家（10）之上。</summary>
        public const int SortingOrder = 30;

        private SpriteRenderer[] _blades;
        private float _age;
        private float _startDeg;
        private float _sweepDeg;
        private float _radius;

        /// <summary>
        /// 播一次剑气。
        /// </summary>
        /// <param name="origin">挥砍原点（玩家位置）。</param>
        /// <param name="facing">朝向（归一化）。</param>
        /// <param name="radius">剑气半径，与判定半径一致。</param>
        /// <param name="arcDeg">判定张角（度）。月牙本体略窄于它，避免"看起来比判定大"。</param>
        public static VfxSlash Play(Vector3 origin, Vector2 facing, float radius, float arcDeg)
        {
            GameObject go = new GameObject("VfxSlash");
            go.transform.position = new Vector3(origin.x, origin.y, 0.0f);

            VfxSlash fx = go.AddComponent<VfxSlash>();
            fx.Init(facing, radius, arcDeg);
            return fx;
        }

        private void Init(Vector2 facing, float radius, float arcDeg)
        {
            _radius = radius > 0.0f ? radius : AttackController.AttackRadius;
            _age = 0.0f;

            float facingDeg = Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg;

            // 起手在朝向的一侧、收势在另一侧：挥砍是"扫过去"，不是"原地绽放"。
            // 扫过的总角度略小于判定张角 —— 特效比判定小一点点，玩家才不会
            // 产生"明明扫到了却没伤害"的错觉（反过来则会天天投诉）。
            _sweepDeg = arcDeg * 0.72f;
            _startDeg = facingDeg + _sweepDeg * 0.5f;

            // 月牙贴图本体张角取判定张角的 0.6：太宽会糊成一个环，太窄不像剑气。
            // 缓存 key 里必须带上张角——只用 "slash" 的话，将来加一种大范围横扫，
            // 它会静默复用第一把普攻的窄月牙，而且没有任何报错提示你。
            float bladeArc = arcDeg * 0.6f;
            Sprite blade = SpriteFactory.Crescent(
                "slash_" + Mathf.RoundToInt(bladeArc), SlashTint, bladeArc, 0.30f);

            _blades = new SpriteRenderer[TrailCount + 1];
            for (int i = 0; i < _blades.Length; i++)
            {
                GameObject seg = new GameObject(i == 0 ? "Blade" : "Trail" + i);
                seg.transform.SetParent(transform, false);

                SpriteRenderer sr = seg.AddComponent<SpriteRenderer>();
                sr.sprite = blade;
                sr.sortingOrder = SortingOrder - i;   // 主刃在最上，残影依次往下
                _blades[i] = sr;
            }

            Layout(0.0f);
        }

        private void Update()
        {
            // 吃表现层时钟而不是 Time.deltaTime：hitstop 期间剑气必须跟着一起定格。
            // 只冻角色不冻剑气的话，画面会出现"人停住了但刀光继续飞出去"，
            // 顿帧不但没有加强打击感，反而暴露了它是假的。
            _age += FeedbackClock.Delta;
            float t = Mathf.Clamp01(_age / Duration);

            Layout(t);

            if (_age >= Duration)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// 按进度摆放主刃与全部残影。
        /// </summary>
        /// <param name="t">0 = 起手，1 = 收势。</param>
        private void Layout(float t)
        {
            if (_blades == null)
            {
                return;
            }

            // 缓出：起手快、收势慢，这是挥砍的力学直觉。线性扫会像雨刷。
            float eased = 1.0f - (1.0f - t) * (1.0f - t);
            float headDeg = _startDeg - _sweepDeg * eased;

            // 整体淡出集中在后 45%：前半段保持满不透明，剑气才"实"。
            float fade = t < 0.55f ? 1.0f : 1.0f - (t - 0.55f) / 0.45f;

            for (int i = 0; i < _blades.Length; i++)
            {
                SpriteRenderer sr = _blades[i];
                if (sr == null)
                {
                    continue;
                }

                // 残影落在主刃身后（起手方向），越靠后越淡越小。
                float deg = headDeg + TrailStepDeg * i;
                float decay = 1.0f - i / (float)(TrailCount + 1);

                // Crescent 贴图是 128px、PPU=1 ⇒ 本体 128 单位宽，半径 64。
                // 缩放到目标半径：scale = radius / 64。
                float baseScale = _radius / (SpriteFactory.ShapePixels * 2.0f * 0.5f);
                float scale = baseScale * Mathf.Lerp(0.82f, 1.0f, decay);

                sr.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, deg);
                sr.transform.localScale = new Vector3(scale, scale, 1.0f);

                Color c = SlashTint;
                c.a = SlashTint.a * fade * decay * decay;   // 平方衰减，拖尾更干净
                sr.color = c;
            }
        }
    }
}
