// -----------------------------------------------------------------------------
// 白衣女主 cutout 骨骼视图。
//
// 第一版不依赖黑衣旧图或不稳定的自动权重：将白衣母图的透明分层挂到明确的
// Transform 骨骼层级。每一层可单独替换为美术精修图，但运行时接口和动作不变。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    [DisallowMultipleComponent]
    public sealed class WhiteHeroineCutoutView : CharacterView
    {
        private const float PixelsPerUnit = 100.0f;

        private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
        private Transform _torso;
        private Transform _head;
        private Transform _armLeft;
        private Transform _armRight;
        private Transform _skirtLeft;
        private Transform _skirtRight;
        private float _time;
        private float _attackTime = -1.0f;

        protected override void Awake()
        {
            base.Awake();
            BuildRig();
            HasView = _renderers.Count > 0;
            Debug.Log("[WhiteHeroineCutout] 白衣分层骨骼已建立，层数=" + _renderers.Count + "。");
        }

        public override void PlayState(CharacterAnimState state)
        {
            _state = state;
            if (state == CharacterAnimState.Attack)
            {
                _attackTime = 0.0f;
            }
        }

        public override void SetFacing(Vector2 dir)
        {
            if (dir.sqrMagnitude < 0.0001f)
            {
                return;
            }

            _facing = dir.normalized;
            // 侧向移动时可见地翻转角色，但不把上下移动误判成翻身。
            if (Mathf.Abs(_facing.x) > 0.15f)
            {
                Vector3 currentScale = transform.localScale;
                float xScale = Mathf.Max(0.0001f, Mathf.Abs(currentScale.x));
                transform.localScale = new Vector3(
                    _facing.x < 0.0f ? -xScale : xScale,
                    currentScale.y,
                    currentScale.z);
            }
        }

        protected override void OnTick(float dt)
        {
            _time += dt;
            float breath = Mathf.Sin(_time * 2.3f);
            float walk = _state == CharacterAnimState.Walk ? Mathf.Sin(_time * 9.5f) : 0.0f;

            SetLocalRotation(_torso, breath * 1.1f);
            SetLocalRotation(_head, -breath * 1.6f + walk * 1.3f);
            SetLocalRotation(_armLeft, walk * 9.0f + breath * 1.2f);
            SetLocalRotation(_armRight, -walk * 9.0f - breath * 1.2f);
            SetLocalRotation(_skirtLeft, -walk * 5.0f + breath * 1.5f);
            SetLocalRotation(_skirtRight, walk * 5.0f - breath * 1.5f);

            if (_attackTime >= 0.0f)
            {
                _attackTime += dt;
                float slash = EvaluateSlash(_attackTime);
                SetLocalRotation(_torso, -slash * 6.0f);
                SetLocalRotation(_armRight, -slash * 66.0f);
                SetLocalRotation(_armLeft, slash * 16.0f);
                SetLocalRotation(_skirtLeft, slash * 8.0f);
                SetLocalRotation(_skirtRight, -slash * 8.0f);
                if (_attackTime >= 0.54f)
                {
                    _attackTime = -1.0f;
                }
            }
        }

        private void BuildRig()
        {
            if (transform.Find("WhiteHeroineCutoutRig") != null)
            {
                return;
            }

            Transform rig = NewBone("WhiteHeroineCutoutRig", transform, Vector2.zero);
            _torso = NewBone("torso", rig, new Vector2(0.0f, 2.0f));
            _head = NewBone("head_hair", _torso, new Vector2(0.0f, 2.55f));
            _armLeft = NewBone("arm_left", _torso, new Vector2(-1.72f, 1.78f));
            _armRight = NewBone("arm_right_sword", _torso, new Vector2(1.73f, 1.78f));
            _skirtLeft = NewBone("skirt_left", _torso, new Vector2(-0.05f, -0.12f));
            _skirtRight = NewBone("skirt_right", _torso, new Vector2(0.05f, -0.12f));

            AddLayer("Characters/CutoutRig/HeroineWhite_Torso", _torso, 0);
            AddLayer("Characters/CutoutRig/HeroineWhite_HeadHair", _head, 2);
            AddLayer("Characters/CutoutRig/HeroineWhite_ArmLeft", _armLeft, 3);
            AddLayer("Characters/CutoutRig/HeroineWhite_ArmRightSword", _armRight, 4);
            AddLayer("Characters/CutoutRig/HeroineWhite_SkirtLeft", _skirtLeft, 1);
            AddLayer("Characters/CutoutRig/HeroineWhite_SkirtRight", _skirtRight, 1);
        }

        private Transform NewBone(string boneName, Transform parent, Vector2 position)
        {
            GameObject bone = new GameObject(boneName);
            bone.transform.SetParent(parent, false);
            bone.transform.localPosition = position;
            return bone.transform;
        }

        private void AddLayer(string resourcePath, Transform bone, int sortingOffset)
        {
            Texture2D texture = Resources.Load<Texture2D>(resourcePath);
            if (texture == null)
            {
                Debug.LogWarning("[WhiteHeroineCutoutView] 缺少白衣分层贴图：" + resourcePath);
                return;
            }

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                PixelsPerUnit,
                0,
                SpriteMeshType.FullRect);
            sprite.name = texture.name + "_RuntimeSprite";

            GameObject layer = new GameObject(texture.name);
            layer.transform.SetParent(bone, false);
            // 每张切图保持原始 1024x1536 画布。把画布中心反向移回角色根，
            // 这样旋转父骨骼时，真正的旋转点就是肩/头/腰，而非整张图的中心。
            // bone.localPosition 只表示相对父骨的偏移；头发、手臂、裙摆又都在
            // torso 下。必须换算回角色根的静止位置，否则这些层会整体上漂一截。
            Vector3 restPositionFromRoot = transform.InverseTransformPoint(bone.position);
            layer.transform.localPosition = -restPositionFromRoot;
            SpriteRenderer renderer = layer.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = 20 + sortingOffset;
            _renderers.Add(renderer);
        }

        private static float EvaluateSlash(float time)
        {
            if (time < 0.15f)
            {
                return time / 0.15f;
            }
            if (time < 0.34f)
            {
                return 1.0f - (time - 0.15f) / 0.19f * 1.25f;
            }
            return -0.25f + (time - 0.34f) / 0.20f * 0.25f;
        }

        private static void SetLocalRotation(Transform bone, float z)
        {
            if (bone != null)
            {
                bone.localRotation = Quaternion.Euler(0.0f, 0.0f, z);
            }
        }
    }
}
