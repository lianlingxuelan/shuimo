// -----------------------------------------------------------------------------
// Performance/FpsCounter.cs —— 轻量级 FPS / 帧时间计数器
//
// 【用途】在屏幕上实时显示 FPS 和 当前帧率限制状态，帮助判断卡顿来自
// VSync 锁定还是真实 CPU/GPU 瓶颈。
//
// 【用法】
//   · 菜单 Shuimo/Performance/Create FPS Counter 会在场景里创建一个。
//   · 或手动创建一个空对象，挂上本组件。
//   · 运行后 Game 视图左上角显示 FPS、FrameTime(ms)、VSyncCount、TargetFrameRate。
//
// 【红线】纯诊断工具，只读 Unity API，不写任何游戏逻辑/状态。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 屏幕 FPS 计数器。只在 Editor / Development Build 中绘制，Release 下自动静默。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FpsCounter : MonoBehaviour
    {
        [Header("显示")]
        [Tooltip("在 Game 视图左上角显示 FPS 信息。")]
        [SerializeField] private bool showGui = true;

        [Tooltip("刷新间隔（秒）。")]
        [SerializeField] private float updateInterval = 0.5f;

        [Tooltip("文字大小。")]
        [SerializeField] private int fontSize = 18;

        [Tooltip("文字颜色。")]
        [SerializeField] private Color textColor = new Color(0.9f, 0.9f, 0.2f, 1.0f);

        private float _accum;
        private int _frames;
        private float _timeLeft;
        private float _fps;
        private float _ms;

        private void Start()
        {
            _timeLeft = updateInterval;
        }

        private void Update()
        {
            float unscaledDt = Time.unscaledDeltaTime;
            _timeLeft -= unscaledDt;
            _accum += 1.0f / Mathf.Max(unscaledDt, 1e-5f);
            _frames++;

            if (_timeLeft <= 0.0f)
            {
                _fps = _accum / _frames;
                _ms = 1000.0f / Mathf.Max(_fps, 1e-5f);
                _timeLeft = updateInterval;
                _accum = 0.0f;
                _frames = 0;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showGui)
            {
                return;
            }

            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = fontSize;
            style.normal.textColor = textColor;

            string text = string.Format(
                "FPS: {0:F1}  ({1:F2} ms)\nVSync: {2}  Target: {3}",
                _fps, _ms, QualitySettings.vSyncCount, Application.targetFrameRate);

            GUI.Label(new Rect(10, 10, 240, 60), text, style);
        }
#endif

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Shuimo/Performance/Create FPS Counter", false, 1100)]
        private static void MenuCreate()
        {
            GameObject go = new GameObject("FpsCounter");
            go.AddComponent<FpsCounter>();
            UnityEditor.Selection.activeGameObject = go;
        }
#endif
    }
}
