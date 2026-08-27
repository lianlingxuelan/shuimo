// -----------------------------------------------------------------------------
// SceneLoader.cs —— 场景/区块加载与切换（asmdef: Xianxia.Unity.T2）
//
// 【它是什么】
// 蓝图 0.2 要求的 "SceneLoader"，并为第 6 周"无缝分区块加载"打底。本周先把
// 同步/异步两套入口 + 一个零美术的"加载中"遮罩做出来，将来切大世界时只需
// 在 LoadAsync 里加进度条/淡入淡出，不动调用方。
//
// 【为什么遮罩用动态 Canvas + Legacy Text】
// 与 GameOverHud / Hud 同款"零美术资源"套路：不落任何字体/贴图资产，
// 用 Unity 内置字体 + ScreenSpaceOverlay Canvas，sortingOrder 取 300 压在
// 结算面板(200)/血条(100)之上，保证读条时不被其它 UI 遮挡。
//
// 【为什么 Ensure GameManager 再切场景】
// GameManager 挂了 DontDestroyOnLoad，本应跨场景存活；但显式 Ensure 一遍
// 是双保险——万一某条加载路径在 GameManager 还没建时就触发，也不会让
// 管理器"消失一帧"。
//
// 【★ _loadingCanvas 是可写 static，必须有复位钩子】
// 与 EventManager._handlers 同源约束：关闭 Domain Reload 时 static 跨
// PlayMode 存活。若不清，第二次进 PlayMode 时 _loadingCanvas 仍指着上一局
// 已销毁的遮罩物体，HideLoading 的 `!= null` 在 Unity 重载下会把它当"活着"
// 去 Destroy 一个死对象（虽然 Unity 吞掉这种调用，但属于脏状态）。统一在
// SubsystemRegistration 置空最干净。场景切换本身也会销毁旧遮罩（它没挂
// DontDestroyOnLoad），所以运行时几乎碰不到残留，复位只是纪律兜底。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Xianxia.Unity.T2.Core
{
    /// <summary>场景加载门面。提供同步/异步切换 + 加载遮罩。</summary>
    public static class SceneLoader
    {
        private static GameObject _loadingCanvas;

        /// <summary>当前激活场景名。</summary>
        public static string CurrentSceneName => SceneManager.GetActiveScene().name;

        /// <summary>重载当前场景（重开一局 / 返回主菜单共用），带加载遮罩。</summary>
        public static void ReloadCurrent()
        {
            LoadAsync(SceneManager.GetActiveScene().name);
        }

        /// <summary>
        /// 异步加载场景。显示"加载中"遮罩，加载完成后自动隐藏。
        /// 默认 allowSceneActivation = true，行为与 LoadScene 等价但带过渡。
        /// </summary>
        public static AsyncOperation LoadAsync(string sceneName)
        {
            GameManager.Ensure();
            ShowLoading("加载中…");
            AsyncOperation op = SceneManager.LoadSceneAsync(sceneName);
            if (op != null)
            {
                op.completed += _ => HideLoading();
            }
            else
            {
                HideLoading();
            }
            return op;
        }

        /// <summary>同步加载（无遮罩，瞬间切换）。保留给不需要过渡的极少数路径。</summary>
        public static void Load(string sceneName)
        {
            GameManager.Ensure();
            SceneManager.LoadScene(sceneName);
        }

        private static void ShowLoading(string text)
        {
            HideLoading();
            _loadingCanvas = new GameObject("LoadingCanvas");

            GameObject canvasGo = new GameObject("LoadingCanvasRoot");
            canvasGo.transform.SetParent(_loadingCanvas.transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300;   // 高于 GameOver(200) / Hud(100)
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            GameObject go = new GameObject("LoadingText", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(canvasGo.transform, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(800.0f, 100.0f);

            Text t = go.AddComponent<Text>();
            // 与 GameOverHud.NewText 同款字体取法：先试内置 Legacy 字体，失败回退 Arial。
            // 不用 `??` —— 它对 UnityEngine.Object 的 null 语义有歧义，显式 == null 才走
            // Unity 重载、与工程既有写法保持一致。
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null)
            {
                f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            t.font = f;
            t.fontSize = 48;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = false;
            t.text = text;
        }

        private static void HideLoading()
        {
            if (_loadingCanvas != null)
            {
                Object.Destroy(_loadingCanvas);
                _loadingCanvas = null;
            }
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _loadingCanvas = null;
        }
    }
}
