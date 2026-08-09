// -----------------------------------------------------------------------------
// PauseMenuHud.cs —— ESC 暂停面板（P0-5，asmdef: Xianxia.Unity.T2）
//
// 【它和 GameOverHud 的关系：互斥，不叠加】
// 终局面板(200) 一旦弹出，本面板就不该再出现 —— CombatBridge 的 ESC 分支用
// `if (!IsRunOver)` 把这条互斥写死在入口处。理由是两层面板同时在屏幕上时，
// "继续游戏"这个按钮语义会自相矛盾：本局都结束了，继续什么？
//
// 【为什么暂停不写 Time.timeScale = 0】
// 本工程的战斗推进走的是 CombatScheduler 的固定步长累加器，它吃的是
// CombatController.Update 传进去的 deltaTime。把 timeScale 清零确实也能停战斗，
// 但会连带停掉 UI 动画、协程和音频，还会让"暂停中仍需刷新的 HUD"变成死图。
// 所以暂停统一走 CombatBridge.SetMenuPaused → Scheduler.Paused 这一条闸门：
// 只冻结玩法，Unity 的帧循环照常跑，ESC 才有人接。
//
// 【sortingOrder = 250】
// 高于终局面板(200)与操作引导(220)，低于主菜单(300)。暂停时它必须是玩家能看到的
// 最上层，但主菜单一旦在场（玩法根本没开始），暂停面板就不该抢在它前面。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 暂停面板。由 CombatBridge 在 Start 中动态创建并 Build，默认隐藏，
    /// 按 ESC 由 CombatBridge 的 TogglePauseMenu 控制显隐。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PauseMenuHud : MonoBehaviour
    {
        /// <summary>点击"继续游戏"时回调。由 CombatBridge 绑定。</summary>
        public System.Action OnResumeClicked;

        /// <summary>点击"重新开始"时回调。由 CombatBridge 绑定。</summary>
        public System.Action OnRestartClicked;

        /// <summary>点击"返回主菜单"时回调。由 CombatBridge 绑定。</summary>
        public System.Action OnMainMenuClicked;

        /// <summary>点击"退出游戏"时回调。由 CombatBridge 绑定。</summary>
        public System.Action OnQuitClicked;

        private const float ButtonWidth = 340.0f;
        private const float ButtonHeight = 68.0f;

        /// <summary>面板当前是否显示。CombatBridge 的 Toggle 逻辑据此判断开还是关。</summary>
        public bool IsShown
        {
            get { return gameObject.activeSelf; }
        }

        /// <summary>
        /// 构建面板。AddComponent 之后必须由 CombatBridge 显式调一次。
        /// Canvas 套路与 GameOverHud.Build 一致，只是 sortingOrder 用 250。
        /// </summary>
        public void Build()
        {
            EnsureEventSystem();

            GameObject canvasGo = new GameObject("PauseMenuCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 250;   // 高于终局(200)/引导(220)，低于主菜单(300)

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // 全屏遮罩：既压暗战场，也吞掉穿透到世界的点击。
            NewOverlay(canvasGo.transform);

            Text title = NewText("PauseTitle", canvasGo.transform, 56,
                                 TextAnchor.MiddleCenter, Color.white);
            RectTransform titleRt = title.rectTransform;
            Anchor(titleRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            titleRt.anchoredPosition = new Vector2(0.0f, 250.0f);
            titleRt.sizeDelta = new Vector2(1200.0f, 100.0f);
            title.text = "已 暂 停";

            // 四个按钮等距排布。监听器读字段而非捕获实参 —— 回调是 Build 之后才绑的。
            Button resume = NewButton("ResumeButton", canvasGo.transform, "继 续 游 戏", 110.0f);
            resume.onClick.AddListener(() =>
            {
                if (OnResumeClicked != null)
                {
                    OnResumeClicked();
                }
            });

            Button restart = NewButton("RestartButton", canvasGo.transform, "重 新 开 始", 20.0f);
            restart.onClick.AddListener(() =>
            {
                if (OnRestartClicked != null)
                {
                    OnRestartClicked();
                }
            });

            Button mainMenu = NewButton("MainMenuButton", canvasGo.transform, "返 回 主 菜 单", -70.0f);
            mainMenu.onClick.AddListener(() =>
            {
                if (OnMainMenuClicked != null)
                {
                    OnMainMenuClicked();
                }
            });

            Button quit = NewButton("QuitButton", canvasGo.transform, "退 出 游 戏", -160.0f);
            quit.onClick.AddListener(() =>
            {
                if (OnQuitClicked != null)
                {
                    OnQuitClicked();
                }
            });

            gameObject.SetActive(false);
        }

        /// <summary>显示暂停面板。真正的冻结由 CombatBridge.SetMenuPaused 负责。</summary>
        public void Show()
        {
            gameObject.SetActive(true);
        }

        /// <summary>隐藏暂停面板。解冻同样由 CombatBridge 负责，本类不碰战斗状态。</summary>
        public void Hide()
        {
            gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------------
        // uGUI 小工具（与 MainMenuHud / GameOverHud 同款写法）
        // ---------------------------------------------------------------------

        /// <summary>保证场景里有 EventSystem，否则按钮点不动（说明见 MainMenuHud）。</summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }
            if (Object.FindObjectOfType<EventSystem>() != null)
            {
                return;
            }
            GameObject go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        private static Image NewOverlay(Transform parent)
        {
            GameObject go = new GameObject("Dim", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Image img = go.AddComponent<Image>();
            img.sprite = SpriteFactory.UiPixel();
            img.color = new Color(0.0f, 0.0f, 0.0f, 0.75f);
            img.raycastTarget = true;
            return img;
        }

        private static Button NewButton(string name, Transform parent, string label, float y)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            Anchor(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            rt.anchoredPosition = new Vector2(0.0f, y);
            rt.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);

            Image bg = go.AddComponent<Image>();
            bg.sprite = SpriteFactory.UiPixel();
            bg.color = Color.white;   // 白底 + ColorBlock 定色，理由见 MainMenuHud.NewButton
            bg.raycastTarget = true;

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            ColorBlock cb = btn.colors;
            cb.normalColor = new Color(0.10f, 0.12f, 0.11f, 0.92f);
            cb.highlightedColor = new Color(0.24f, 0.30f, 0.25f, 0.96f);
            cb.pressedColor = new Color(0.06f, 0.08f, 0.07f, 1.0f);
            cb.selectedColor = new Color(0.10f, 0.12f, 0.11f, 0.92f);
            cb.disabledColor = new Color(0.10f, 0.12f, 0.11f, 0.45f);
            cb.colorMultiplier = 1.0f;
            cb.fadeDuration = 0.08f;
            btn.colors = cb;

            Text t = NewText(name + "Label", go.transform, 28,
                             TextAnchor.MiddleCenter, new Color(0.93f, 0.95f, 0.92f, 1.0f));
            RectTransform tRt = t.rectTransform;
            tRt.anchorMin = Vector2.zero;
            tRt.anchorMax = Vector2.one;
            tRt.pivot = new Vector2(0.5f, 0.5f);
            tRt.offsetMin = Vector2.zero;
            tRt.offsetMax = Vector2.zero;
            t.text = label;

            return btn;
        }

        private static Text NewText(string name, Transform parent, int size, TextAnchor align, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = BuiltinFont();
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = false;
            return t;
        }

        private static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.pivot = pivot;
        }

        private static Font BuiltinFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null)
            {
                f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return f;
        }
    }
}
