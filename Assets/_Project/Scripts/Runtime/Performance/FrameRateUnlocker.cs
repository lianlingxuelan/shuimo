// -----------------------------------------------------------------------------
// Performance/FrameRateUnlocker.cs —— 一键解锁帧率上限，排除 VSync 干扰
//
// 【用途】Editor / Build 里常被 VSync 或 targetFrameRate 锁到显示器刷新率，
// 导致 Stats 看起来「只有 100 FPS」。本工具关闭所有帧率限制，让 CPU/GPU 全速跑，
// 便于判断真实性能瓶颈。
//
// 【用法】
//   · 菜单：Shuimo > Performance > Unlock Frame Rate（解锁）
//           Shuimo > Performance > Lock Frame Rate（恢复 VSync 默认）
//   · 代码：FrameRateUnlocker.Unlock() / Lock()
//   · 启动自动解锁：把 AutoUnlockAtStartup 勾选（默认不勾，避免用户不知情）。
//
// 【红线】纯表现层工具，不写任何战斗/时间/状态。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 帧率解锁器。挂在任意场景对象上即可；无对象时也可通过菜单或代码调用静态方法。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FrameRateUnlocker : MonoBehaviour
    {
        [Header("启动行为")]
        [Tooltip("场景加载后自动关闭 VSync 并解锁 targetFrameRate。默认不勾，避免影响不知情用户。")]
        [SerializeField] private bool autoUnlockAtStartup = false;

        [Header("诊断")]
        [Tooltip("解锁后在 Console 打印当前帧率限制状态。")]
        [SerializeField] private bool logOnStart = true;

        private void Start()
        {
            if (autoUnlockAtStartup)
            {
                Unlock();
            }
            else if (logOnStart)
            {
                LogCurrentState("启动");
            }
        }

        /// <summary>
        /// 关闭所有帧率限制：VSync = 0，targetFrameRate = -1，QualitySettings.vSyncCount = 0。
        /// </summary>
        public static void Unlock()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Debug.Log("[FrameRateUnlocker] 已解锁帧率：VSync=0, targetFrameRate=-1。");
        }

        /// <summary>
        /// 恢复 Unity 默认锁帧行为：VSync = Every V Blank（1），targetFrameRate 不做特殊设置。
        /// </summary>
        public static void Lock()
        {
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = -1;
            Debug.Log("[FrameRateUnlocker] 已恢复默认锁帧：VSync=Every V Blank。");
        }

        /// <summary>
        /// 仅打印当前状态，不修改。
        /// </summary>
        public static void LogCurrentState(string context)
        {
            Debug.Log(string.Format(
                "[FrameRateUnlocker] {0}：QualitySettings.vSyncCount={1}, Application.targetFrameRate={2}",
                context, QualitySettings.vSyncCount, Application.targetFrameRate));
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Shuimo/Performance/Unlock Frame Rate", false, 1000)]
        private static void MenuUnlock()
        {
            Unlock();
        }

        [UnityEditor.MenuItem("Shuimo/Performance/Lock Frame Rate", false, 1001)]
        private static void MenuLock()
        {
            Lock();
        }

        [UnityEditor.MenuItem("Shuimo/Performance/Log Frame Rate State", false, 1002)]
        private static void MenuLog()
        {
            LogCurrentState("菜单");
        }
#endif
    }
}
