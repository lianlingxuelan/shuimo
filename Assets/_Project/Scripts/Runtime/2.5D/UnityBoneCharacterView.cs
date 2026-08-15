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
}
