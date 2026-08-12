// -----------------------------------------------------------------------------
// 2.5D/DepthSortUtility.cs —— 深度排序共享工具（feature/2.5d，轮次 C）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过。
//
// 【职责】把 BambooSceneContext.ApplyDepthSort 的「单物体按与玩家 Y 差推到深度轴」
// 内层逻辑抽成静态方法，供竹子 / 敌人 / NPC 统一复用，保证遮挡验收一致（红线 R3）。
//
// 【红线】
//   纯几何工具：不读 FeedbackClock、不碰任何战斗内核、不写 Frozen、
//   不引用 CombatScheduler / RunPhase / DamageResolver / RequestHitstop / KickHitstop、
//   不写 Time.timeScale。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 深度排序共享静态工具（轮次 C，从 <c>BambooSceneContext.ApplyDepthSort</c> 抽出）。
    /// 竹林、敌人、NPC 共用同一份「按与玩家 Y 差推到深度轴」逻辑，遮挡行为完全一致。
    /// </summary>
    public static class DepthSortUtility
    {
        /// <summary>
        /// 把一个物体的 z 坐标按「与玩家的 Y 差」投影到深度轴，实现「走到物体前后」的遮挡。
        /// 仅改 <paramref name="t"/> 的 z；XY 不动。
        ///
        /// 逻辑等价 <c>BambooSceneContext.ApplyDepthSort</c> 的内层循环：
        /// <code>
        ///   nearness = (playerY - t.y) * ySortToDepth;
        ///   t.z      = playerZ + depthAxis.z * nearness;
        /// </code>
        ///
        /// 玩家本体（取自身坐标调用）nearness 恒为 0 → z 不变，因此 PlayerController 一行不改。
        /// </summary>
        /// <param name="t">待排序的 Transform（竹子 / 敌人 / NPC 的根）。</param>
        /// <param name="playerY">玩家世界 Y。</param>
        /// <param name="playerZ">玩家世界 Z。</param>
        /// <param name="depthAxis">深度轴（指向相机，默认 (0,0,-1)）。只取 .z 分量。</param>
        /// <param name="ySortToDepth">Y→深度轴强度（与 BSC.ySortToDepth 同义）。</param>
        public static void Apply(
            Transform t,
            float playerY,
            float playerZ,
            Vector3 depthAxis,
            float ySortToDepth)
        {
            if (t == null)
            {
                return;
            }

            Vector3 p = t.position;
            // nearness > 0 表示物体在玩家「前方」（Y 更小 → 屏幕上更靠下 → 应更近相机），
            // depthAxis.z 默认 -1，于是「前方」物体 z 更小（更近相机 -Z 侧），遮挡女主。
            float nearness = (playerY - p.y) * ySortToDepth;
            p.z = playerZ + depthAxis.z * nearness;
            t.position = p;
        }
    }
}
