// -----------------------------------------------------------------------------
// 2.5D/CharacterView.cs —— 角色视图抽象层（feature/2.5d，轮次 C）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过。
//
// 【职责】竹林 2.5D 角色（玩家 / 敌人 / NPC）的动画视图抽象层：
//   - CharacterView（抽象基类）：统一 Tick 闸门（只读 FeedbackClock.Delta，不写 Frozen）、
//     PlayState / SetFacing / OnTick 抽象契约、ResolveOn 工厂。
//   - SpriteCharacterView（默认）：用 SpriteRenderer，零 Spine 依赖，立即可用。
//   - SpineCharacterView（可选）：仅当 HAS_SPINE_PACKAGE 定义时编译，引用 Spine 运行时。
//
// 【红线（与 BambooVfx 同口径）】
//   1. 动画推进唯一时钟 = FeedbackClock.Delta；Tick() 只读 FeedbackClock.Frozen 作闸门，
//      绝不写 FeedbackClock.Frozen，绝不碰 Time.timeScale。
//   2. 不引用 CombatScheduler / RunPhase / DamageResolver / RequestHitstop / KickHitstop。
//   3. 不改动任何内核类型。本类的 Xianxia.Unity.T2.CharacterView 与内核
//      Xianxia.Combat.UnityBridge.CombatView 是两个完全不同的类型/命名空间，互不引用。
//
// 【可选依赖守卫（决策见设计文档 §2.5，方案 A）】
//   SpineCharacterView 与 ResolveOn 内的 Spine 分支整体包在 #if HAS_SPINE_PACKAGE 内。
//   未定义符号时整类不编译、工厂只返回 Sprite → 工程零 Spine 依赖零报错。
//   导入 Spine 运行时并在 Player Settings 追加 HAS_SPINE_PACKAGE 后，含 SkeletonAnimation
//   的角色自动走 Spine 视图；即便定义符号，单个角色缺 SkeletonAnimation 仍回落 Sprite。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using Xianxia.Combat.UnityBridge; // FeedbackClock

namespace Xianxia.Unity.T2
{
// Spine 运行时命名空间引用必须放在命名空间体顶部（C# 要求 using 指令位于任何成员声明之前），
// 且整体包在 #if HAS_SPINE_PACKAGE 内：未定义符号时整段不编译，工程零 Spine 依赖零报错。
#if HAS_SPINE_PACKAGE
    using Spine.Unity;
#endif
#if HAS_2D_BONE_PACKAGE
    using UnityEngine.U2D.Animation;
#endif

    /// <summary>角色视图动画状态（战斗事件驱动）。</summary>
    public enum CharacterAnimState
    {
        /// <summary>静止待机。</summary>
        Idle,

        /// <summary>移动。</summary>
        Walk,

        /// <summary>挥砍（由 AttackController.SwingCount 增长沿驱动）。</summary>
        Attack,

        /// <summary>受击硬直（由 harvest 命中 / 受击事件驱动）。</summary>
        Hit,

        /// <summary>死亡（淡出 + 下沉，2.5D 占位）。</summary>
        Death,
    }

    /// <summary>
    /// 竹林 2.5D 角色视图抽象层（轮次 C）。
    /// 动画推进一律走 FeedbackClock.Delta；只读、绝不写 FeedbackClock.Frozen。
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class CharacterView : MonoBehaviour
    {
        /// <summary>供深度排序 / 挂载使用的锚点 Transform（通常是自身 transform）。</summary>
        public Transform Anchor { get; protected set; }

        /// <summary>是否存在可用视图（Sprite 或 Spine 任一）。</summary>
        public bool HasView { get; protected set; }

        /// <summary>当前动画状态（子类可读，用于 Sprite 委托 HeroineAnimator 时映射）。</summary>
        protected CharacterAnimState _state = CharacterAnimState.Idle;

        /// <summary>当前朝向（XY 单位向量，与 PlayerController.LastFacing 约定一致）。</summary>
        protected Vector2 _facing = Vector2.right;

        /// <summary>基类统一在 Awake 捕获锚点为自身 transform。</summary>
        protected virtual void Awake()
        {
            Anchor = transform;
        }

        /// <summary>
        /// 每帧推进动画。内部只读 FeedbackClock.Delta；
        /// Delta==0（顿帧/暂停/终局）时直接 return，动画随全场同步冻结。
        /// 不写 FeedbackClock.Frozen、不碰 Time.timeScale。
        /// </summary>
        public void Tick()
        {
            // 只读闸门：Frozen 为 true 时本帧与全场景表现层一起停。绝不对其赋值。
            if (FeedbackClock.Frozen)
            {
                return;
            }

            // 唯一时钟原语：表现层每秒推进量。
            float dt = FeedbackClock.Delta;
            if (dt > 0.0f)
            {
                OnTick(dt);
            }
        }

        /// <summary>切换动画状态（Idle/Walk/Attack/Hit/Death）。</summary>
        public abstract void PlayState(CharacterAnimState state);

        /// <summary>设置朝向（XY 单位向量），驱动精灵翻转 / 骨骼 flipX。</summary>
        public abstract void SetFacing(Vector2 dir);

        /// <summary>子类实现具体动画推进（dt 已为 FeedbackClock.Delta）。</summary>
        /// <param name="dt">本帧表现层应推进的秒数。</param>
        protected abstract void OnTick(float dt);

        /// <summary>
        /// 统一解析入口：在 root 上取或挂一个 CharacterView。
        /// 每个 2.5D 角色根节点有且只有一个 CharacterView 组件；玩家与敌人共用本方法解析。
        ///
        /// 守卫规则（设计 §2.5 方案 A）：
        ///   仅当 HAS_SPINE_PACKAGE 定义时，才探测 Spine.Unity.SkeletonAnimation（类型引用被
        ///   #if 隔离，缺包不编译）；否则直接挂 SpriteCharacterView。定义符号后仍做运行时探测：
        ///   单个角色若无 SkeletonAnimation 组件，回落 Sprite。
        /// </summary>
        /// <param name="root">角色根节点 Transform。</param>
        /// <returns>解析到的 CharacterView；root 为 null 时返回 null。</returns>
        public static CharacterView ResolveOn(Transform root)
        {
            if (root == null)
            {
                return null;
            }

#if HAS_SPINE_PACKAGE
            // 仅在符号存在时，才探测 Spine 组件（类型引用被 #if 隔离，缺包不编译）。
            Spine.Unity.SkeletonAnimation sk = root.GetComponent<Spine.Unity.SkeletonAnimation>();
            if (sk != null)
            {
                SpineCharacterView sv = root.GetComponent<SpineCharacterView>()
                    ?? root.gameObject.AddComponent<SpineCharacterView>();
                sv.Bind(sk);
                return sv;
            }
#endif

#if HAS_2D_BONE_PACKAGE
            // 探测 Unity 2D Animation 骨骼组件（类型引用被 #if 隔离，缺包不编译）。
            // 优先级低于 Spine（若两者都装，Spine 优先），高于默认 Sprite 回落。
            SpriteSkin skin = root.GetComponent<SpriteSkin>();
            if (skin != null)
            {
                UnityBoneCharacterView bv = root.GetComponent<UnityBoneCharacterView>()
                    ?? root.gameObject.AddComponent<UnityBoneCharacterView>();
                bv.Bind(skin);
                return bv;
            }
#endif

            // 默认：Sprite 视图（零依赖，立即可用）。
            SpriteCharacterView sp = root.GetComponent<SpriteCharacterView>()
                ?? root.gameObject.AddComponent<SpriteCharacterView>();
            return sp;
        }
    }

    /// <summary>
    /// 默认角色视图：用 SpriteRenderer 驱动 2D 精灵，零 Spine 依赖，立即可用。
    /// 可选委托既有 HeroineAnimator（只读复用，不改它）。表现一律走 FeedbackClock.Delta，
    /// 受击晃动 / tint 闪 / 死亡下沉均按 dt 衰减，顿帧期间与全场景同步冻结。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpriteCharacterView : CharacterView
    {
        [Header("受击表现（按 FeedbackClock.Delta 衰减，不碰 Frozen）")]
        [Tooltip("受击位移晃动幅度（局部单位）")]
        public float hitShakeAmplitude = 0.18f;

        [Tooltip("受击晃动时长（秒）")]
        public float hitShakeDuration = 0.22f;

        [Tooltip("受击 tint 闪时长（秒）")]
        public float hitTintFlash = 0.5f;

        [Header("攻击脉冲")]
        [Tooltip("攻击时精灵缩放脉冲峰值")]
        public float attackPulseScale = 1.18f;

        [Tooltip("攻击脉冲时长（秒）")]
        public float attackPulseDuration = 0.16f;

        [Header("死亡")]
        [Tooltip("死亡淡出 + 下沉时长（秒）")]
        public float deathDuration = 0.8f;

        [Tooltip("死亡下沉世界单位")]
        public float deathSink = 24.0f;

        private SpriteRenderer _sr;
        private HeroineAnimator _legacy;
        private Vector3 _srBaseScale = Vector3.one;
        private Vector3 _srBaseLocalPos = Vector3.zero;

        private float _hitShakeRemain;
        private float _hitTintRemain;
        private float _attackPulseRemain;
        private bool _dying;
        private float _deathRemain;
        private Color _baseTint = Color.white;

        /// <inheritdoc />
        protected override void Awake()
        {
            base.Awake();
            // Sprite 取自身或子节点的 SpriteRenderer；HeroineAnimator 只读复用。
            _sr = GetComponentInChildren<SpriteRenderer>();
            _legacy = GetComponent<HeroineAnimator>();
            HasView = _sr != null || _legacy != null;
            CaptureSpriteBase();
            _baseTint = _sr != null ? _sr.color : Color.white;
        }

        /// <inheritdoc />
        public override void PlayState(CharacterAnimState state)
        {
            _state = state;
            switch (state)
            {
                case CharacterAnimState.Attack:
                    _attackPulseRemain = attackPulseDuration;
                    break;
                case CharacterAnimState.Hit:
                    _hitShakeRemain = hitShakeDuration;
                    _hitTintRemain = hitTintFlash;
                    break;
                case CharacterAnimState.Death:
                    _dying = true;
                    _deathRemain = deathDuration;
                    break;
                case CharacterAnimState.Idle:
                case CharacterAnimState.Walk:
                default:
                    break;
            }
        }

        /// <inheritdoc />
        public override void SetFacing(Vector2 dir)
        {
            _facing = dir;
            if (_sr != null)
            {
                // 与 PlayerController.LastFacing 约定一致：dir.x < 0 朝左翻转。
                _sr.flipX = dir.x < 0.0f;
            }
        }

        /// <inheritdoc />
        protected override void OnTick(float dt)
        {
            // 动画表现：攻击脉冲 / 受击晃动 / tint 闪 / 死亡淡出，全部按 FeedbackClock.Delta 衰减。
            // 若场景里同时存在 HeroineAnimator（玩家），它自己负责精灵帧 playback，
            // 本视图只在它之上叠加变换级表现（缩放脉冲 / 位移晃动 / 下沉），
            // 互不抢 sprite，因此不调用 HeroineAnimator.SetState（避免与其状态机互打）。
            TickAttackPulse(dt);
            TickHitShake(dt);
            TickDeath(dt);
        }

        /// <summary>攻击缩放脉冲（dt 衰减回 1）。</summary>
        private void TickAttackPulse(float dt)
        {
            if (_attackPulseRemain <= 0.0f || _sr == null)
            {
                return;
            }

            _attackPulseRemain -= dt;
            float t = attackPulseDuration > 1e-4f
                ? Mathf.Clamp01(_attackPulseRemain / attackPulseDuration)
                : 0.0f;
            // 从峰值脉冲衰减回基线的 1.0（t 从 1→0）。
            float s = 1.0f + (attackPulseScale - 1.0f) * t;
            _sr.transform.localScale = _srBaseScale * s;
        }

        /// <summary>受击阻尼晃动 + tint 闪（dt 衰减）。</summary>
        private void TickHitShake(float dt)
        {
            if (_sr == null)
            {
                return;
            }

            if (_hitShakeRemain > 0.0f)
            {
                _hitShakeRemain -= dt;
                float t = hitShakeDuration > 1e-4f
                    ? Mathf.Clamp01(_hitShakeRemain / hitShakeDuration)
                    : 0.0f;
                float damp = t * t;
                float offset = Mathf.Sin((hitShakeDuration - _hitShakeRemain) * 40.0f) * hitShakeAmplitude * damp;
                _sr.transform.localPosition = _srBaseLocalPos + new Vector3(offset, Mathf.Abs(offset) * 0.5f, 0.0f);
            }
            else if (_sr.transform.localPosition != _srBaseLocalPos)
            {
                _sr.transform.localPosition = _srBaseLocalPos;
            }

            if (_hitTintRemain > 0.0f)
            {
                _hitTintRemain -= dt;
                float t = hitTintFlash > 1e-4f
                    ? Mathf.Clamp01(_hitTintRemain / hitTintFlash)
                    : 0.0f;
                // 从白闪衰减回基色。
                Color flash = Color.Lerp(_baseTint, Color.white, t);
                _sr.color = flash;
            }
            else if (_sr.color != _baseTint)
            {
                _sr.color = _baseTint;
            }
        }

        /// <summary>死亡淡出 + 轻微下沉（dt 衰减）。</summary>
        private void TickDeath(float dt)
        {
            if (!_dying || _sr == null)
            {
                return;
            }

            _deathRemain -= dt;
            float t = deathDuration > 1e-4f
                ? Mathf.Clamp01(_deathRemain / deathDuration)
                : 0.0f;
            Color c = _baseTint;
            c.a = t; // 淡出
            _sr.color = c;
            // 沿 -Y 轻微下沉（局部），不碰深度轴 z（深度排序独立管理）。
            Vector3 pos = _srBaseLocalPos;
            pos.y -= (1.0f - t) * deathSink;
            _sr.transform.localPosition = pos;

            if (_deathRemain <= 0.0f)
            {
                _dying = false;
            }
        }

        /// <summary>捕获 Sprite 变换的静止基准（缩放 / 局部位置），供脉冲与晃动回弹。</summary>
        private void CaptureSpriteBase()
        {
            if (_sr == null)
            {
                return;
            }
            _srBaseScale = _sr.transform.localScale;
            _srBaseLocalPos = _sr.transform.localPosition;
        }
    }

// =============================================================================
// Spine 可选依赖守卫：整个 SpineCharacterView 类被 #if HAS_SPINE_PACKAGE 包裹，
// 未定义符号时该类不编译 → 工程零 Spine 依赖零报错。using Spine.Unity 已移至命名空间体顶部（同 #if 内）。
// =============================================================================
#if HAS_SPINE_PACKAGE
    /// <summary>
    /// 可选 Spine 骨骼角色视图（仅当 HAS_SPINE_PACKAGE 定义时编译）。
    /// 引用 Spine.Unity.SkeletonAnimation，用 AnimationState.Update(FeedbackClock.Delta)
    /// 喂动画，使 Spine 动画与全场顿帧同步冻结。受击对骨骼做基于 dt 的抖动。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpineCharacterView : CharacterView
    {
        [Header("受击表现")]
        [Tooltip("受击骨骼抖动时长（秒）")]
        public float hitShakeDuration = 0.22f;

        [Tooltip("受击骨骼抖动幅度（局部单位）")]
        public float hitShakeAmplitude = 0.18f;

        [Header("状态→动画名映射")]
        [Tooltip("Idle 对应的 Spine 动画名")]
        public string clipIdle = "idle";

        [Tooltip("Walk 对应的 Spine 动画名")]
        public string clipWalk = "walk";

        [Tooltip("Attack 对应的 Spine 动画名")]
        public string clipAttack = "attack";

        [Tooltip("Hit 对应的 Spine 动画名")]
        public string clipHit = "hit";

        [Tooltip("Death 对应的 Spine 动画名")]
        public string clipDeath = "death";

        private SkeletonAnimation _skeleton;
        private readonly Dictionary<CharacterAnimState, string> _clipMap =
            new Dictionary<CharacterAnimState, string>();

        private float _hitShakeRemain;
        private Vector3 _skeletonBaseLocalPos = Vector3.zero;

        /// <summary>由 ResolveOn 在运行时探测到 SkeletonAnimation 后调用，绑定骨骼组件。</summary>
        /// <param name="skeleton">Spine 骨骼动画组件。</param>
        public void Bind(SkeletonAnimation skeleton)
        {
            _skeleton = skeleton;
            _clipMap[CharacterAnimState.Idle] = clipIdle;
            _clipMap[CharacterAnimState.Walk] = clipWalk;
            _clipMap[CharacterAnimState.Attack] = clipAttack;
            _clipMap[CharacterAnimState.Hit] = clipHit;
            _clipMap[CharacterAnimState.Death] = clipDeath;
            HasView = _skeleton != null;
            if (_skeleton != null)
            {
                _skeletonBaseLocalPos = _skeleton.transform.localPosition;
            }
        }

        /// <inheritdoc />
        protected override void Awake()
        {
            base.Awake();
            // 若未走 ResolveOn.Bind（例如场景里直接挂了组件），尝试自探。
            if (_skeleton == null)
            {
                _skeleton = GetComponent<SkeletonAnimation>();
                if (_skeleton != null)
                {
                    Bind(_skeleton);
                }
            }
        }

        /// <inheritdoc />
        public override void PlayState(CharacterAnimState state)
        {
            _state = state;
            if (_skeleton == null)
            {
                return;
            }

            string clip;
            if (!_clipMap.TryGetValue(state, out clip) || string.IsNullOrEmpty(clip))
            {
                return;
            }

            // Spine 动画状态切换（loop 仅在 Idle/Walk 时开启）。
            bool loop = state == CharacterAnimState.Idle || state == CharacterAnimState.Walk;
            _skeleton.AnimationState.SetAnimation(0, clip, loop);

            if (state == CharacterAnimState.Hit)
            {
                _hitShakeRemain = hitShakeDuration;
            }
        }

        /// <inheritdoc />
        public override void SetFacing(Vector2 dir)
        {
            _facing = dir;
            if (_skeleton != null && _skeleton.Skeleton != null)
            {
                // 与 Sprite 约定一致：dir.x < 0 朝左翻转。
                _skeleton.Skeleton.FlipX = dir.x < 0.0f;
            }
        }

        /// <inheritdoc />
        protected override void OnTick(float dt)
        {
            if (_skeleton == null || _skeleton.AnimationState == null)
            {
                return;
            }

            // 唯一时钟原语：用 FeedbackClock.Delta 推进 Spine 动画状态，
            // 顿帧期间 Delta==0 → 动画同步冻结（不写 Frozen、不碰 Time.timeScale）。
            _skeleton.AnimationState.Update(dt);

            // 受击骨骼抖动（按 dt 衰减）。
            if (_hitShakeRemain > 0.0f)
            {
                _hitShakeRemain -= dt;
                float t = hitShakeDuration > 1e-4f
                    ? Mathf.Clamp01(_hitShakeRemain / hitShakeDuration)
                    : 0.0f;
                float damp = t * t;
                float offset = Mathf.Sin((hitShakeDuration - _hitShakeRemain) * 40.0f) * hitShakeAmplitude * damp;
                _skeleton.transform.localPosition = _skeletonBaseLocalPos + new Vector3(offset, 0.0f, 0.0f);
            }
            else if (_skeleton.transform.localPosition != _skeletonBaseLocalPos)
            {
                _skeleton.transform.localPosition = _skeletonBaseLocalPos;
            }
        }
    }
#endif // HAS_SPINE_PACKAGE

// =============================================================================
// Unity 2D Animation 骨骼视图守卫（与 Spine 同口径）
//   整个 UnityBoneCharacterView 类 + ResolveOn 分支均包在 #if HAS_2D_BONE_PACKAGE
//   内。未定义符号时整类不编译 → 工程零 2D-Animation 依赖零报错。
//   启用：Player Settings > Scripting Define Symbols 追加 HAS_2D_BONE_PACKAGE，
//   并确保本 asmdef 引用了 UnityEngine.U2D.Animation 模块
//   （com.unity.2d.animation 已随 com.unity.feature.2d 安装，无需额外装包）。
// =============================================================================
#if HAS_2D_BONE_PACKAGE
    /// <summary>
    /// Unity 2D Animation 骨骼角色视图（仅当 HAS_2D_BONE_PACKAGE 定义时编译）。
    /// 探测 SpriteSkin（2D 骨骼绑定组件），用 Animator 手动 Update(FeedbackClock.Delta)
    /// 喂动画，与全场顿帧同步冻结（同 Spine 分支的推进闸门语义）。
    /// 受击对根骨骼做基于 dt 的抖动；朝向翻转翻 rootBone 的 localScale.x 符号。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnityBoneCharacterView : CharacterView
    {
        [Header("受击表现")]
        [Tooltip("受击骨骼抖动时长（秒）")]
        public float hitShakeDuration = 0.22f;

        [Tooltip("受击骨骼抖动幅度（局部单位）")]
        public float hitShakeAmplitude = 0.18f;

        [Header("状态→动画名映射")]
        [Tooltip("Idle 对应的 Animator 状态/Clip 名")]
        public string clipIdle = "idle";

        [Tooltip("Walk 对应的 Animator 状态/Clip 名")]
        public string clipWalk = "walk";

        [Tooltip("Attack 对应的 Animator 状态/Clip 名")]
        public string clipAttack = "attack";

        [Tooltip("Hit 对应的 Animator 状态/Clip 名")]
        public string clipHit = "hit";

        [Tooltip("Death 对应的 Animator 状态/Clip 名")]
        public string clipDeath = "death";

        private SpriteSkin _skin;
        private Animator _animator;
        private readonly Dictionary<CharacterAnimState, string> _clipMap =
            new Dictionary<CharacterAnimState, string>();

        private float _hitShakeRemain;
        private Vector3 _rootBaseLocalPos = Vector3.zero;
        private Transform _rootBone;
        private float _rootBaseScaleX = 1.0f;

        /// <summary>由 ResolveOn 在运行时探测到 SpriteSkin 后调用，绑定骨骼组件。</summary>
        /// <param name="skin">2D 骨骼绑定组件。</param>
        public void Bind(SpriteSkin skin)
        {
            _skin = skin;
            _animator = _skin != null ? _skin.GetComponent<Animator>() : null;
            _clipMap[CharacterAnimState.Idle] = clipIdle;
            _clipMap[CharacterAnimState.Walk] = clipWalk;
            _clipMap[CharacterAnimState.Attack] = clipAttack;
            _clipMap[CharacterAnimState.Hit] = clipHit;
            _clipMap[CharacterAnimState.Death] = clipDeath;
            HasView = _skin != null && _animator != null;
            if (_skin != null)
            {
                _skin.enabled = true;
                // 手动推进：避免 Animator 用 deltaTime 自动播放，改由 FeedbackClock.Delta 驱动，
                // 实现与 Spine 分支一致的顿帧同步（Frozen 时 OnTick 不调用 → Animator 不动）。
                if (_animator != null && _animator.playableGraph.IsValid())
                {
                    // Unity 2022.3 的 AnimatorUpdateMode 没有 Manual 枚举值，
                    // 改为把 Animator 的 PlayableGraph 设成 Manual 时间更新模式，
                    // 再由 OnTick 用 FeedbackClock.Delta 手动推进，顿帧期间自然冻结。
                    _animator.playableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                }
                // 记录根骨骼用于朝向翻转与受击抖动。
                _rootBone = _skin.rootBone;
                if (_rootBone != null)
                {
                    _rootBaseScaleX = _rootBone.localScale.x;
                    _rootBaseLocalPos = _rootBone.localPosition;
                }
                else
                {
                    _rootBaseLocalPos = _skin.transform.localPosition;
                }
            }
        }

        /// <inheritdoc />
        protected override void Awake()
        {
            base.Awake();
            // 若未走 ResolveOn.Bind（例如场景里直接挂了组件），尝试自探。
            if (_skin == null)
            {
                _skin = GetComponent<SpriteSkin>();
                if (_skin != null)
                {
                    Bind(_skin);
                }
            }
        }

        /// <inheritdoc />
        public override void PlayState(CharacterAnimState state)
        {
            _state = state;
            if (_animator == null)
            {
                return;
            }
            string clip;
            if (!_clipMap.TryGetValue(state, out clip) || string.IsNullOrEmpty(clip))
            {
                return;
            }
            // Mecanim 状态机内 idle/walk 设为循环、attack/hit/death 设为 OneShot。
            _animator.Play(clip, 0, 0.0f);
            if (state == CharacterAnimState.Hit)
            {
                _hitShakeRemain = hitShakeDuration;
            }
        }

        /// <inheritdoc />
        public override void SetFacing(Vector2 dir)
        {
            _facing = dir;
            // 与 Sprite/Spine 约定一致：dir.x < 0 朝左翻转。
            float sign = dir.x < 0.0f ? -1.0f : 1.0f;
            if (_rootBone != null)
            {
                Vector3 s = _rootBone.localScale;
                s.x = _rootBaseScaleX * sign;
                _rootBone.localScale = s;
            }
            else if (_skin != null)
            {
                SpriteRenderer sr = _skin.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    sr.flipX = dir.x < 0.0f;
                }
            }
        }

        /// <inheritdoc />
        protected override void OnTick(float dt)
        {
            if (_animator == null)
            {
                return;
            }
            // 手动推进 Mecanim（Manual 模式），精确用 FeedbackClock.Delta；
            // 顿帧期间 OnTick 不被调用 → Animator 不动 → 全场同步冻结。
            _animator.Update(dt);

            // 受击根骨骼抖动（按 dt 衰减）。
            if (_hitShakeRemain > 0.0f)
            {
                _hitShakeRemain -= dt;
                float t = hitShakeDuration > 1e-4f
                    ? Mathf.Clamp01(_hitShakeRemain / hitShakeDuration)
                    : 0.0f;
                float damp = t * t;
                float offset = Mathf.Sin((hitShakeDuration - _hitShakeRemain) * 40.0f) * hitShakeAmplitude * damp;
                if (_rootBone != null)
                {
                    Vector3 p = _rootBone.localPosition;
                    p.x = _rootBaseLocalPos.x + offset;
                    _rootBone.localPosition = p;
                }
                else if (_skin != null)
                {
                    Vector3 p = _skin.transform.localPosition;
                    p.x = _rootBaseLocalPos.x + offset;
                    _skin.transform.localPosition = p;
                }
            }
            else
            {
                if (_rootBone != null && _rootBone.localPosition != _rootBaseLocalPos)
                {
                    _rootBone.localPosition = _rootBaseLocalPos;
                }
                else if (_skin != null && _skin.transform.localPosition != _rootBaseLocalPos)
                {
                    _skin.transform.localPosition = _rootBaseLocalPos;
                }
            }
        }
    }
#endif // HAS_2D_BONE_PACKAGE
}
