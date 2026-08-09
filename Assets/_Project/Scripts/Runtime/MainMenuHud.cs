// -----------------------------------------------------------------------------
// MainMenuHud.cs —— 主菜单面板（P0-5，asmdef: Xianxia.Unity.T2）
//
// 【为什么主菜单不是一个独立场景】
// 本工程一贯约定"零美术资源、零场景文件"：UI 全部由代码动态创建 Canvas + Legacy Text。
// 再开一个 .unity 只为放四个字，会带来场景注册、构建索引、跨场景状态传递三笔额外成本，
// 而收益为零。所以主菜单只是**同一场景里的一层 Canvas**：开局把玩法冻结（CombatBridge
// 的 _menuPaused），玩家点"开始游戏"再解冻。世界其实早就建好了，只是被一层黑纱盖住。
//
// 【SkipOnNextLoad 为什么必须是 static】
// "重新开始"和按 R 都走 SceneManager.LoadScene，整个场景（连同本组件）会被销毁重建，
// 任何实例字段都活不过这一刀。而"重开之后不该再逼玩家点一次开始游戏"这个意图必须
// 传递到下一帧的新场景里去 —— static 字段挂在类型上、不随场景销毁，是这里唯一
// 不引入存档/单例的传递方式。用完即焚：CombatBridge.Start 读取后立刻复位为 false，
// 否则"返回主菜单"那一路会被上一次的残留旗标吃掉。
//
// 【为什么 sortingOrder = 300】
// 叠放次序：Hud(100) < ControlsGuide(220) < PauseMenu(250) < MainMenu(300)。
// 主菜单是"玩法尚未开始"的最外层，必须压住包括终局面板(200)在内的一切，
// 否则重开瞬间可能出现"主菜单被死亡面板盖住"的死锁观感。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 主菜单面板。由 CombatBridge 在 Start 中动态创建并 Build，
    /// 默认隐藏；是否显示由 CombatBridge 根据 <see cref="SkipOnNextLoad"/> 决定。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuHud : MonoBehaviour
    {
        /// <summary>
        /// 下次场景加载时跳过主菜单（按 R / 点"重新开始"时置 true）。
        /// static 才能跨 SceneManager.LoadScene 存活；CombatBridge.Start 读完即复位。
        /// </summary>
        public static bool SkipOnNextLoad;

        /// <summary>点击"开始游戏"（或按 Enter/Space）时回调。由 CombatBridge 绑定。</summary>
        public System.Action OnStartClicked;

        /// <summary>点击"退出游戏"时回调。由 CombatBridge 绑定。</summary>
        public System.Action OnQuitClicked;

        // 按钮版式常量。集中在这里，改一处即可整体对齐，避免魔数散落在 Build 里。
        private const float ButtonWidth = 360.0f;
        private const float ButtonHeight = 76.0f;

        /// <summary>
        /// 构建面板。AddComponent 之后必须由 CombatBridge 显式调一次 ——
        /// AddComponent 只挂脚本，不会自动建 Canvas/Text。
        /// 套路与 GameOverHud.Build 完全一致，只是 sortingOrder 用 300。
        /// </summary>
        public void Build()
        {
            EnsureEventSystem();

            GameObject canvasGo = new GameObject("MainMenuCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300;   // 最外层：压住 HUD(100) / 终局(200) / 暂停(250)

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            // 0.5 = 宽高各占一半权重，与 Hud / GameOverHud 同款，保证三档分辨率版式一致
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // --- 全屏半透明黑遮罩 ---
            // raycastTarget = true 是刻意的：它要把点击**吞掉**，否则玩家隔着主菜单
            // 点到下层世界（将来若有可交互物件）就成了穿透 bug。
            NewOverlay(canvasGo.transform);

            // --- 主标题 ---
            Text title = NewText("MainMenuTitle", canvasGo.transform, 64,
                                 TextAnchor.MiddleCenter, Color.white);
            RectTransform titleRt = title.rectTransform;
            Anchor(titleRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            titleRt.anchoredPosition = new Vector2(0.0f, 220.0f);
            titleRt.sizeDelta = new Vector2(1200.0f, 110.0f);
            title.text = "幽 篁 竹 海";

            // --- 副标题 ---
            Text sub = NewText("MainMenuSubtitle", canvasGo.transform, 24,
                               TextAnchor.MiddleCenter, new Color(0.72f, 0.74f, 0.72f, 0.95f));
            RectTransform subRt = sub.rectTransform;
            Anchor(subRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            subRt.anchoredPosition = new Vector2(0.0f, 140.0f);
            subRt.sizeDelta = new Vector2(1200.0f, 50.0f);
            sub.text = "水墨仙侠 · 单人试玩";

            // --- 按钮 ---
            // 监听器里读的是**字段**而不是构造时传进来的值：CombatBridge 是在 Build()
            // 之后才绑定回调的，若在这里捕获实参就会永远捕获到 null。
            Button start = NewButton("StartButton", canvasGo.transform, "开 始 游 戏", -10.0f);
            start.onClick.AddListener(() =>
            {
                if (OnStartClicked != null)
                {
                    OnStartClicked();
                }
            });

            Button quit = NewButton("QuitButton", canvasGo.transform, "退 出 游 戏", -120.0f);
            quit.onClick.AddListener(() =>
            {
                if (OnQuitClicked != null)
                {
                    OnQuitClicked();
                }
            });

            // 默认藏起来，由 CombatBridge 决定这一局要不要显示。
            gameObject.SetActive(false);
        }

        /// <summary>显示主菜单。</summary>
        public void Show()
        {
            gameObject.SetActive(true);
        }

        /// <summary>隐藏主菜单。</summary>
        public void Hide()
        {
            gameObject.SetActive(false);
        }

        /// <summary>
        /// 键盘兜底：面板显示时按 Enter / 小键盘 Enter / Space 等同于点"开始游戏"。
        ///
        /// 【为什么需要兜底】鼠标点击依赖 EventSystem + GraphicRaycaster 这条链路，
        /// 任何一环出问题（比如场景里被人塞了第二个 EventSystem 并禁用了输入模块）
        /// 玩家就会卡在主菜单里出不去 —— 一个键盘入口的成本极低，却能挡住死局。
        /// 注意 Unity 不会对未激活的 GameObject 调 Update，activeSelf 判断只是防御性冗余。
        /// </summary>
        private void Update()
        {
            if (!gameObject.activeSelf)
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.Return)
                || Input.GetKeyDown(KeyCode.KeypadEnter)
                || Input.GetKeyDown(KeyCode.Space))
            {
                if (OnStartClicked != null)
                {
                    OnStartClicked();
                }
            }
        }

        // ---------------------------------------------------------------------
        // uGUI 小工具（与 GameOverHud 同款写法，避免两套约定）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 保证场景里有 EventSystem，否则 Button 根本收不到点击。
        ///
        /// 【为什么由 UI 自己补】本工程的 UI 全是运行时代码创建的，场景文件里没有
        /// 任何 UI 节点，自然也没有 EventSystem。缺了它按钮是"看得见、点不动"，
        /// 且控制台一声不吭 —— 与 CombatBridge.EnsurePlayerT3Controllers 面对的是
        /// 同一类"静默失效"，处理手法也保持一致：有就跳过，没有就补上。
        /// </summary>
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

        /// <summary>造一张全屏半透明黑遮罩，并吞掉落在它上面的点击。</summary>
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
            img.sprite = SpriteFactory.UiPixel();   // 零美术资源：图源走程序化 1px 贴图
            img.color = new Color(0.0f, 0.0f, 0.0f, 0.75f);
            img.raycastTarget = true;
            return img;
        }

        /// <summary>造一个居中的文字按钮（底板 Image + 子节点 Text）。</summary>
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
            // 底板刻意留纯白：Button 的 ColorBlock 是**乘算在 targetGraphic 上的 tint**，
            // 白底相乘后 tint 值就等于最终显示色，四个状态一眼可读。
            // 若底板本身是深色，想做"悬停变亮"就得写大于 1 的颜色分量 —— LDR 下会被裁掉，
            // 结果是悬停毫无反馈。
            bg.color = Color.white;
            bg.raycastTarget = true;   // 按钮底板必须可拾取，否则只有文字挡不住射线

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

            Text t = NewText(name + "Label", go.transform, 30,
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
            t.raycastTarget = false;   // 文字不拦射线，点击一律由底板 Image 接
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
