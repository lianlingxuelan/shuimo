// -----------------------------------------------------------------------------
// 2.5D/UnityBoneCharacterView.cs —— Unity 2D Animation 骨骼角色视图（feature/2.5d）
//
// 本类从 CharacterView.cs 拆出、独立成文件的原因：
//   Unity 多类 .cs 文件中，fileID 11500000 只指向「文件名匹配的主类」。此前本类与
//   抽象基类 CharacterView 同处一文件，HeroineBone.prefab 的 m_Script:{fileID:11500000}
//   会解析到抽象 CharacterView，报 “The script class can't be abstract!” 并被 Unity
//   剥离 → Inspector 报 Missing Script。拆为单类文件后，fileID 11500000 必然指向本类。
//
// 恒编译：本 asmdef 已硬引用 Unity.2D.Animation.Runtime（com.unity.2d.animation），
// 仅当运行时探测到 SpriteSkin 才实例化，未挂骨骼资源的角色自动回落 Sprite，无损。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using Xianxia.Combat.UnityBridge; // FeedbackClock

namespace Xianxia.Unity.T2
{
    // 2D 骨骼命名空间：本 asmdef 已硬引用 Unity.2D.Animation.Runtime，恒可用，故不包 #if。
    using UnityEngine.U2D.Animation;

// =============================================================================
// Unity 2D Animation 骨骼视图
//   原本整体包在 #if HAS_2D_BONE_PACKAGE 内，但该 asmdef 已硬引用
//   Unity.2D.Animation.Runtime（com.unity.2d.animation 随 com.unity.feature.2d 安装），
//   符号守卫既无法真正达成“零依赖”，又会在符号未被编译器实际吃进时让类消失、
//   导致 HeroineBone.prefab 报 Missing Script。故改为恒编译（与 Spine 可选依赖区分）。
//   仅当运行时探测到 SpriteSkin 才实例化，未挂骨骼资源的角色自动回落 Sprite，无损。
// =============================================================================
    /// <summary>
    /// Unity 2D Animation 骨骼角色视图（恒编译：本 asmdef 已硬引用 2D Animation 包）。
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
        private SpriteRenderer _spriteRenderer;
        private Sprite _sourceSprite;
        private Sprite _walkSprite;
        private Sprite _walkAlternateSprite;
        private Sprite _attackSprite;
        private Material _sourceMaterial;
        private Material _chromaKeyMaterial;
        private Vector3 _visualBaseScale = Vector3.one;
        private Quaternion _visualBaseRotation = Quaternion.identity;
        private float _poseClock;
        private float _oneShotRemain;

        // 当前骨骼资源还没有绘制 SpriteSkin 权重时，启用 SpriteSkin 会让原图消失。
        // 这组根节点姿态是安全的可见回退：保留原图，同时让站立、走路、挥砍和受击
        // 都有明确动作反馈；待权重完成后仍可无缝由 SpriteSkin 接管变形。
        private const float AttackPoseSeconds = 0.32f;
        private const float HitPoseSeconds = 0.22f;
        private const string ChromaKeyMaterialResourcePath = "Characters/HeroineChromaKey";

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
            _spriteRenderer = _skin != null ? _skin.GetComponent<SpriteRenderer>() : null;
            if (_sourceSprite == null && _spriteRenderer != null)
            {
                _sourceSprite = _spriteRenderer.sprite;
            }
            if (_sourceMaterial == null && _spriteRenderer != null)
            {
                _sourceMaterial = _spriteRenderer.sharedMaterial;
            }
            _visualBaseScale = transform.localScale;
            _visualBaseRotation = transform.localRotation;
            if (_skin != null)
            {
                // 不强制启用 SpriteSkin：若 prefab 中权重未绘制（Sprite Editor 里 weight 全 0），
                // 启用 SpriteSkin 会导致 SpriteRenderer 被骨骼系统接管后无法渲染，角色直接消失。
                // 保留 prefab 的 enabled 状态，让美术在 Sprite Editor 画完权重后再手动勾选。
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

        /// <summary>
        /// 设置当前角色可见的待机/行走/攻击立绘。SpriteSkin 权重尚未完成时，仍可提供
        /// 可读的跨步与挥剑画面；完成蒙皮后可以清空这些覆盖图，让骨骼动画自然接管。
        /// </summary>
        public void ConfigureSpriteOverrides(
            Sprite idleSprite,
            Sprite walkSprite,
            Sprite walkAlternateSprite,
            Sprite attackSprite)
        {
            _sourceSprite = idleSprite;
            _walkSprite = walkSprite;
            _walkAlternateSprite = walkAlternateSprite;
            _attackSprite = attackSprite;
            // Instantiate 会先触发 Awake/Bind，而 WorldBuilder 随后才把角色调整到
            // 适合当前相机的运行时尺寸。动作姿态必须以这个最终尺寸为基准，
            // 否则第一帧 OnTick 会把 11 倍角色缩回 prefab 里的旧尺寸。
            _visualBaseScale = transform.localScale;
            _visualBaseRotation = transform.localRotation;
            RestoreSourceSprite();
        }

        /// <summary>攻击状态且存在专用攻击图时，切换独立挥剑姿态。</summary>
        public static bool ShouldUseAttackSprite(CharacterAnimState state, bool hasAttackSprite)
        {
            return state == CharacterAnimState.Attack && hasAttackSprite;
        }

        /// <summary>
        /// Idle/Walk 会由场景每帧重复派发；相同循环状态不能反复从第 0 帧播放，
        /// 否则真正接入的骨骼动画会永远停在第一帧。一次性状态允许重复触发，
        /// 这样连续攻击或受击仍能从头播放反馈。
        /// </summary>
        public static bool ShouldRestartAnimator(CharacterAnimState current, CharacterAnimState requested)
        {
            return current != requested
                || (requested != CharacterAnimState.Idle && requested != CharacterAnimState.Walk);
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
            if ((state == CharacterAnimState.Idle || state == CharacterAnimState.Walk)
                && _oneShotRemain > 0.0f)
            {
                return;
            }
            bool shouldRestartAnimator = ShouldRestartAnimator(_state, state);
            _state = state;
            if (state == CharacterAnimState.Attack)
            {
                _oneShotRemain = AttackPoseSeconds;
            }
            else if (state == CharacterAnimState.Hit)
            {
                _oneShotRemain = HitPoseSeconds;
            }
            if (ShouldUseAttackSprite(state, _attackSprite != null))
            {
                SetVisibleSprite(_attackSprite);
            }
            else if (state == CharacterAnimState.Walk && _walkSprite != null)
            {
                SetVisibleSprite(ResolveWalkSprite());
            }
            else if (state != CharacterAnimState.Attack)
            {
                RestoreSourceSprite();
            }
            // 终局会立刻冻结 FeedbackClock，之后 Tick 不再推进。状态切换当下先应用
            // 一次姿态，确保 Death 等终局动作不会因为零 Delta 永远保持站立。
            ApplyVisibleFallbackPose();
            if (_animator == null)
            {
                return;
            }
            string clip;
            if (!_clipMap.TryGetValue(state, out clip) || string.IsNullOrEmpty(clip))
            {
                return;
            }
            if (!shouldRestartAnimator)
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
            if (_spriteRenderer != null)
            {
                // SpriteSkin 未启用时，根骨骼翻转不会作用于原 SpriteRenderer；
                // 同步翻转原图，保证左右移动与攻击方向可见。
                _spriteRenderer.flipX = dir.x < 0.0f;
            }
        }

        /// <inheritdoc />
        protected override void OnTick(float dt)
        {
            if (_animator != null)
            {
                // 手动推进 Mecanim（Manual 模式），精确用 FeedbackClock.Delta；
                // 顿帧期间 OnTick 不被调用 → Animator 不动 → 全场同步冻结。
                _animator.Update(dt);
            }

            _poseClock += dt;
            if (_state == CharacterAnimState.Walk && _walkSprite != null)
            {
                SetVisibleSprite(ResolveWalkSprite());
            }
            if (_oneShotRemain > 0.0f)
            {
                _oneShotRemain = Mathf.Max(0.0f, _oneShotRemain - dt);
                if (_oneShotRemain <= 0.0f && _state == CharacterAnimState.Attack)
                {
                    RestoreSourceSprite();
                }
            }
            ApplyVisibleFallbackPose();

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

        private void ApplyVisibleFallbackPose()
        {
            float bob = 0.0f;
            float tilt = 0.0f;
            float width = 1.0f;
            float height = 1.0f;

            switch (_state)
            {
                case CharacterAnimState.Idle:
                    bob = Mathf.Sin(_poseClock * 3.0f) * 0.018f;
                    height = 1.0f + bob;
                    width = 1.0f - bob * 0.45f;
                    break;
                case CharacterAnimState.Walk:
                    bob = Mathf.Sin(_poseClock * 11.0f) * 0.055f;
                    tilt = Mathf.Sin(_poseClock * 5.5f) * 4.5f;
                    height = 1.0f + bob;
                    width = 1.0f - bob * 0.5f;
                    break;
                case CharacterAnimState.Attack:
                    if (_attackSprite == null)
                    {
                        float attackT = 1.0f - _oneShotRemain / AttackPoseSeconds;
                        CutoutPose attackPose = FallbackCutoutPose.Evaluate(CharacterAnimState.Attack, attackT);
                        tilt = attackPose.TiltDeg;
                        width = attackPose.WidthScale;
                        height = attackPose.HeightScale;
                    }
                    break;
                case CharacterAnimState.Hit:
                    float hitT = 1.0f - _oneShotRemain / HitPoseSeconds;
                    tilt = Mathf.Sin(Mathf.Clamp01(hitT) * Mathf.PI * 3.0f) * 8.0f;
                    width = 0.90f;
                    height = 0.96f;
                    break;
                case CharacterAnimState.Death:
                    tilt = -76.0f;
                    height = 0.55f;
                    width = 1.20f;
                    break;
            }

            transform.localRotation = _visualBaseRotation * Quaternion.Euler(0.0f, 0.0f, tilt);
            transform.localScale = new Vector3(
                _visualBaseScale.x * width,
                _visualBaseScale.y * height,
                _visualBaseScale.z);
        }

        private void SetVisibleSprite(Sprite sprite)
        {
            if (_spriteRenderer != null && sprite != null)
            {
                if (_spriteRenderer.sprite != sprite)
                {
                    _spriteRenderer.sprite = sprite;
                }
                UseChromaKeyMaterial(
                    sprite == _walkSprite
                    || sprite == _walkAlternateSprite
                    || sprite == _attackSprite);
            }
        }

        private void RestoreSourceSprite()
        {
            SetVisibleSprite(_sourceSprite);
        }

        private Sprite ResolveWalkSprite()
        {
            if (_walkAlternateSprite == null)
            {
                return _walkSprite;
            }

            int frame = Mathf.FloorToInt(_poseClock * 5.0f);
            return (frame & 1) == 0 ? _walkSprite : _walkAlternateSprite;
        }

        private void UseChromaKeyMaterial(bool shouldUseChromaKey)
        {
            if (_spriteRenderer == null)
            {
                return;
            }

            if (!shouldUseChromaKey)
            {
                if (_spriteRenderer.sharedMaterial != _sourceMaterial)
                {
                    _spriteRenderer.sharedMaterial = _sourceMaterial;
                }
                return;
            }

            if (_chromaKeyMaterial == null)
            {
                Material template = Resources.Load<Material>(ChromaKeyMaterialResourcePath);
                if (template != null)
                {
                    _chromaKeyMaterial = new Material(template)
                    {
                        name = "Heroine Chroma Key (Runtime)"
                    };
                }
                else
                {
                    Shader shader = Shader.Find("Xianxia/Ink/ChromaKeySprite");
                    if (shader == null)
                    {
                        Debug.LogWarning("[UnityBoneCharacterView] 未找到 HeroineChromaKey 材质和 ChromaKeySprite Shader，动作帧保留默认材质。");
                        return;
                    }
                    _chromaKeyMaterial = new Material(shader)
                    {
                        name = "Heroine Chroma Key (Runtime Fallback)"
                    };
                }
            }

            if (_spriteRenderer.sharedMaterial != _chromaKeyMaterial)
            {
                _spriteRenderer.sharedMaterial = _chromaKeyMaterial;
            }
        }

        private void OnDestroy()
        {
            if (_chromaKeyMaterial == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(_chromaKeyMaterial);
            }
            else
            {
                DestroyImmediate(_chromaKeyMaterial);
            }
        }
    }
}
