// -----------------------------------------------------------------------------
// PlayerWeaponRig.cs —— 把国风剑作为「独立世界物体」跟随玩家（feature/2.5d，商店武器接入）
//
// 【为什么不做成玩家骨骼的子物体】
//   玩家是 2D 骨骼精灵（HeroineBone，SpriteRenderer + 2D 骨骼，root 局部缩放 2.5）。
//   3D FBX 剑若挂成精灵子物体，会被 2.5 倍缩放放大、且与精灵的渲染排序/坐标轴不一致，
//   极易出现「剑巨大 / 穿模 / 挡在角色前」等盲调翻车（本环境无 Unity 无法肉眼校验）。
//   因此本组件把剑放在**场景根**（scale=1）下，每帧用玩家世界坐标 + 朝向算出剑的位置
//   与旋转，彻底绕开骨骼层级与缩放继承。位置/角度全部暴露为 Inspector 可调旋钮。
//
// 【红线】
//   1. 绝不修改 HeroineBone.prefab、绝不碰 SpriteRenderer / 2D 骨骼。
//   2. 资源缺失（未跑 Generate Prefabs）时自动禁用自身，画面与改前完全一致。
//   3. 只跟随，不写玩家的 transform.position（玩家移动权归 PlayerController 私有）。
//
// 【使用】
//   - 默认 weaponPrefab 留空 → 自动 Resources.Load("Weapons/TaomuSword")。
//   - 用户 Force Recompile 后若想换剑，在 Player 上的本组件指定其它剑 prefab 即可。
//   - 握持位置/角度若看着别扭，调 holdDistance / holdHeight / extraRotDeg 三个旋钮。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>把一把 3D 剑作为独立世界物体跟随玩家并朝向其 LastFacing。</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerWeaponRig : MonoBehaviour
    {
        [Header("武器（留空则自动用 Resources/Weapons/TaomuSword）")]
        [Tooltip("剑的 prefab。留空时运行时尝试 Resources.Load(\"Weapons/TaomuSword\")。")]
        [SerializeField] private GameObject weaponPrefab;

        [Header("握持位置（世界单位，相对玩家中心）")]
        [Tooltip("剑中心离玩家中心的水平距离。正值在身前，负值在身后。")]
        [SerializeField] private float holdDistance = -30.0f;
        [Tooltip("剑中心相对玩家中心的抬高量（正=高于中心）。")]
        [SerializeField] private float holdHeight = 15.0f;
        [Tooltip("深度偏移（正=更靠近相机，负=更远离相机）。建议负值让剑位于玩家身后。")]
        [SerializeField] private float holdDepth = -10.0f;

        [Header("朝向")]
        [Tooltip("额外旋转（度），负值让剑尖斜向下，形成背剑/拖剑姿态。")]
        [SerializeField] private float extraRotDeg = -135.0f;

        private GameObject _sword;
        private PlayerController _pc;

        private void Awake()
        {
            _pc = GetComponent<PlayerController>();

            if (weaponPrefab == null)
            {
                weaponPrefab = Resources.Load<GameObject>("Weapons/TaomuSword");
            }

            if (weaponPrefab == null)
            {
                // 资源还没生成（用户未跑 Shuimo/Store/Generate Prefabs）：
                // 安静禁用，绝不报错、绝不破坏现有画面。
                Debug.Log("[PlayerWeaponRig] 未找到剑 prefab，自动禁用（先跑 Shuimo/Store/Generate Prefabs）。");
                enabled = false;
                return;
            }

            // 挂在场景根（scale=1）下，避免继承玩家 2.5 倍局部缩放。
            Transform host = (transform.parent != null) ? transform.parent : null;
            _sword = Instantiate(weaponPrefab);
            _sword.name = "PlayerSword";
            if (host != null)
            {
                _sword.transform.SetParent(host, true);
            }
        }

        private void LateUpdate()
        {
            if (_sword == null)
            {
                return;
            }

            Vector2 facing = (_pc != null) ? _pc.LastFacing : Vector2.right;
            if (facing.sqrMagnitude < 1e-6f)
            {
                facing = Vector2.right;
            }
            float ang = Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg;

            Vector2 offset2 = Rotate(new Vector2(holdDistance, holdHeight), ang);
            Vector3 center = transform.position;
            _sword.transform.position = new Vector3(center.x + offset2.x, center.y + offset2.y, center.z + holdDepth);
            _sword.transform.rotation = Quaternion.Euler(0.0f, 0.0f, ang + extraRotDeg);
        }

        private static Vector2 Rotate(Vector2 v, float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            float c = Mathf.Cos(r);
            float s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        private void OnDestroy()
        {
            if (_sword != null)
            {
                Destroy(_sword);
                _sword = null;
            }
        }
    }
}
