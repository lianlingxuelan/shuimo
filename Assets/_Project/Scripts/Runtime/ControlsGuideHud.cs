// -----------------------------------------------------------------------------
// ControlsGuideHud.cs —— 操作引导（P0-6，asmdef: Xianxia.Unity.T2）
//
// 【为什么是"一屏说明 + 一行常驻"两件套，而不是只做一个】
// 用户实测反馈里最刺眼的一条是"进去了不知道能按什么"。一屏说明解决"第一次不知道"，
// 但它必须能被一键关掉（否则第二局起就是噪音）；关掉之后玩家仍会忘记技能键，
// 所以底部留一行 18px 的半透明小抄兜底。两者生命周期不同：
//   · 说明面板 = 一次性，任意键即关；
//   · 常驻小字 = 与本局同寿，Build 之后一直在。
// 因此常驻小字**不能**做成说明面板的子节点，否则关面板会把它一起带走。
// 实现上：本组件的 gameObject 始终 active，只切换 _panelRoot 这一个子节点。
//
// 【为什么关闭要排除 ESC】
// Input.anyKeyDown 是"这一帧有任何键按下"，ESC 当然也算。而 CombatBridge 的 ESC
// 分支在同一帧会打开暂停菜单 —— 于是一次 ESC 同时"关引导 + 开暂停"，玩家看到的是
// 引导莫名其妙消失了。所以这里显式把 ESC 从关闭条件里剔除，让 ESC 的语义唯一。
// 【现状补充】ESC 的处置权已完全归 CombatBridge：面板开着时它调 HidePanel 关引导、
// 不叠暂停菜单（CombatBridge.Update ESC 分支）。本组件保持不响应 ESC 即可。
//
// 【为什么还要一道"同帧不响应"闸门】
// 玩家是用鼠标点"开始游戏"或按 Space 触发 ShowPanel 的，而鼠标左键/Space 同样满足
// anyKeyDown。若两个组件的 Update 恰好是"先 CombatBridge 后本组件"的顺序（Unity 不
// 保证执行序），面板会在出现的同一帧被那次点击自己关掉，表现为"引导一闪而过"。
// 记下 ShowPanel 所在帧号并跳过该帧，是最小成本的去抖。
//
// 【sortingOrder = 220】高于 HUD(100) 与终局面板(200)，低于暂停(250)/主菜单(300)。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 操作引导。由 CombatBridge 在 Start 中动态创建并 Build；
    /// 说明面板默认隐藏，底部常驻小字 Build 后一直显示。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ControlsGuideHud : MonoBehaviour
    {
        /// <summary>面板被玩家关掉时回调。CombatBridge 用它决定何时解冻玩法。</summary>
        public System.Action OnPanelClosed;

        /// <summary>说明面板的根节点。只切它，不切本组件的 gameObject（常驻小字要留下）。</summary>
        private GameObject _panelRoot;

        /// <summary>ShowPanel 发生的帧号。用于跳过"打开面板那一帧"的按键，避免自关。</summary>
        private int _shownFrame = -1;

        /// <summary>说明面板正文。每行一条，等宽感靠空格对齐（Legacy Text 不支持制表位）。</summary>
        private const string BodyText =
            "  W A S D        移动\n"
            + "  鼠标左键 / J    普通攻击\n"
            + "  K / 鼠标右键    技能一\n"
            + "  L              技能二\n"
            + "  Shift / 空格    闪避（消耗体力）\n"
            + "  R              死亡后重新开始\n"
            + "  ESC            暂停 / 菜单\n"
            + "\n"
            + "        —— 按任意键继续 ——";

        /// <summary>底部常驻小抄。内容与上面的说明保持同源语义，改一处别忘改另一处。</summary>
        private const string StripText =
            "WASD 移动 · 左键/J 攻击 · K/右键 技能一 · L 技能二 · Shift 闪避 · ESC 菜单";

        /// <summary>说明面板当前是否显示。</summary>
        public bool PanelShown
        {
            get { return _panelRoot != null && _panelRoot.activeSelf; }
        }

        /// <summary>
        /// 构建引导。AddComponent 之后必须由 CombatBridge 显式调一次。
        /// Canvas 套路与 GameOverHud.Build 一致，只是 sortingOrder 用 220。
        /// </summary>
        public void Build()
        {
            GameObject canvasGo = new GameObject("ControlsGuideCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 220;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // ---------------- ① 一屏说明面板（可关） ----------------
            _panelRoot = new GameObject("GuidePanel", typeof(RectTransform));
            _panelRoot.layer = LayerMask.NameToLayer("UI");
            RectTransform panelRt = _panelRoot.GetComponent<RectTransform>();
            panelRt.SetParent(canvasGo.transform, false);
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            NewOverlay(panelRt);

            Text title = NewText("GuideTitle", panelRt, 36,
                                 TextAnchor.MiddleCenter, Color.white);
            RectTransform titleRt = title.rectTransform;
            Anchor(titleRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            titleRt.anchoredPosition = new Vector2(0.0f, 260.0f);
            titleRt.sizeDelta = new Vector2(1200.0f, 70.0f);
            title.text = "【 操 作 说 明 】";

            // 正文左对齐：按键表如果居中，键名和释义会像散沙一样对不齐。
            // 用 UpperLeft + 固定宽度块，再把整块水平居中放置。
            Text body = NewText("GuideBody", panelRt, 26,
                                TextAnchor.UpperLeft, new Color(0.90f, 0.92f, 0.90f, 0.98f));
            RectTransform bodyRt = body.rectTransform;
            Anchor(bodyRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1.0f));
            bodyRt.anchoredPosition = new Vector2(0.0f, 200.0f);
            bodyRt.sizeDelta = new Vector2(760.0f, 420.0f);
            body.lineSpacing = 1.35f;   // 行距拉开，八行按键表才不至于糊成一团
            body.text = BodyText;

            _panelRoot.SetActive(false);

            // ---------------- ② 底部常驻小抄（不可关） ----------------
            // 挂在 canvasGo 下、_panelRoot 之外，所以面板隐藏不影响它。
            Text strip = NewText("ControlsStrip", canvasGo.transform, 18,
                                 TextAnchor.LowerCenter, new Color(1.0f, 1.0f, 1.0f, 0.55f));
            RectTransform stripRt = strip.rectTransform;
            Anchor(stripRt, new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f));
            // 紧贴屏幕最底边，给技能栏让出净空：HudSkillBar.BottomMargin=34，
            // 本行占 y∈[4,28]，技能栏从 34 起，中间留 6px 间隙，不再压在格子下沿上。
            stripRt.anchoredPosition = new Vector2(0.0f, 4.0f);
            stripRt.sizeDelta = new Vector2(1400.0f, 24.0f);
            strip.text = StripText;
        }

        /// <summary>弹出一屏操作说明。</summary>
        public void ShowPanel()
        {
            if (_panelRoot == null)
            {
                return;
            }
            _shownFrame = Time.frameCount;   // 本帧的按键不算数，见文件头"同帧不响应"
            _panelRoot.SetActive(true);
        }

        /// <summary>
        /// 收起一屏操作说明。底部常驻小抄不受影响。
        ///
        /// 【为什么要"已隐藏就直接 return"】本方法有两个调用方：本组件 Update 的
        /// "任意键关闭"，以及 CombatBridge 的 ESC 分支。两者可能在相邻帧先后触发，
        /// 若不做闸门，OnPanelClosed 会被喊第二次 —— 而它绑的是 SetMenuPaused(false)，
        /// 重复解冻会把"玩家刚按 ESC 打开的暂停菜单"那次冻结反向踩掉。
        /// 因此回调只在**本次确实由显示态转为隐藏态**时触发。
        /// </summary>
        public void HidePanel()
        {
            if (_panelRoot == null)
            {
                return;
            }
            if (!_panelRoot.activeSelf)
            {
                return;   // 已经是隐藏态，不是一次真正的"关闭"，不重复回调
            }
            _panelRoot.SetActive(false);
            if (OnPanelClosed != null)
            {
                OnPanelClosed();
            }
        }

        /// <summary>面板显示期间，按任意键（ESC 除外）关闭。</summary>
        private void Update()
        {
            if (!PanelShown)
            {
                return;
            }
            if (Time.frameCount == _shownFrame)
            {
                return;
            }
            // 排除 ESC：它专属于"打开暂停菜单"，不能一键干两件事。
            if (Input.anyKeyDown && !Input.GetKeyDown(KeyCode.Escape))
            {
                HidePanel();
            }
        }

        // ---------------------------------------------------------------------
        // uGUI 小工具（与 GameOverHud / MainMenuHud 同款写法）
        // ---------------------------------------------------------------------

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
            // 引导面板没有按钮，但仍然拦射线：面板亮着时玩家点屏幕是想"关掉它"，
            // 不该让这一下点击穿透到下层界面上去误触。
            img.raycastTarget = true;
            return img;
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
