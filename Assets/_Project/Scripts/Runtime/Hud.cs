// -----------------------------------------------------------------------------
// Hud.cs —— 最小 HUD（asmdef: Xianxia.Unity.T2，PRD P0-10）
//
// 【三种分辨率不错位靠什么】
// 靠 CanvasScaler 的 ScaleWithScreenSize + 锚点，不靠手算像素。
//   · referenceResolution 1920×1080、matchWidthOrHeight = 0.5
//     ⇒ 1280×720 / 1920×1080 / 2560×1440 三档等比缩放，版式完全一致
//   · 每个元素锚点钉在自己所属的屏幕角（血条左上、调试面板右上）
//     ⇒ 非 16:9 的分辨率下也只是间距变化，不会互相压盖
// 用绝对坐标摆 UI 在 1920 下看着好好的，换 1280 就会飞出屏幕，这是最常见的翻车点。
//
// 【为什么用 Legacy Text 而不是 TextMeshPro】
// TMP 首次使用需要导入 TMP Essentials（一堆二进制资产），与 P0-11「零美术资源」
// 直接冲突。Legacy Text 用的是 Unity 内置字体，工程里一个文件都不落。
// HUD 是调试面板级别的需求，字形质量不构成瓶颈。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>切片 HUD：血条 + 敌数 + 区域名 + 调试面板。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
    public sealed class Hud : MonoBehaviour
    {
        /// <summary>设计分辨率。</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1920.0f, 1080.0f);

        /// <summary>血条尺寸（设计分辨率下的像素）。</summary>
        public static readonly Vector2 HpBarSize = new Vector2(420.0f, 26.0f);

        /// <summary>资源条（灵力 / 体力）尺寸。比血条矮一半，视觉上分出主次。</summary>
        public static readonly Vector2 ResourceBarSize = new Vector2(420.0f, 14.0f);

        /// <summary>fps 采样的平滑系数。越小越稳，越大越跟手。</summary>
        public const float FpsSmoothing = 0.08f;

        /// <summary>连击数达到多少才显示。低于它的连击是噪声，天天亮着反而没人看。</summary>
        public const int ComboVisibleMin = SkillConfig.COMBO_HUD_MIN;

        /// <summary>连击文字的淡出速度（每秒 alpha 衰减量）。</summary>
        public const float ComboFadeSpeed = 2.4f;

        /// <summary>经验条尺寸。与血条同宽，但更细 —— 它是次要信息，不该抢血条的视觉权重。</summary>
        public static readonly Vector2 ExpBarSize = new Vector2(420.0f, 10.0f);

        /// <summary>
        /// 单条升级提示的停留时长（秒）。
        /// 连升两级时两条提示**依次**播放，总时长翻倍 —— 玩家能数清自己升了几级。
        /// </summary>
        public const float LevelUpToastDuration = 1.35f;

        private CombatBridge _bridge;
        private AttackController _attack;
        private DeterminismDump _dump;
        private HudSkillBar _skillBar;
        private HudStatusIcons _statusIcons;

        /// <summary>P2-1 BOSS 血条。与本组件同体，由 <c>BuildBossBar</c> 装配。</summary>
        private HudBossBar _bossBar;

        /// <summary>BOSS 血条（供 <c>CombatBridge</c> 与集成测试取用）。未 Build 前为 null。</summary>
        public HudBossBar BossBar
        {
            get { return _bossBar; }
        }

        private Image _hpFill;
        private Image _cdFill;
        private Image _qiFill;
        private Image _stamFill;
        private Text _hpText;
        private Text _qiText;
        private Text _stamText;
        private Text _zoneText;
        private Text _enemyText;
        private Text _debugText;
        private Text _comboText;

        private RectTransform _resourcePanel;
        private float _comboAlpha;
        private int _lastCombo = -1;

        // --- P1-6 玩家成长 ---

        private Text _levelText;
        private Image _expFill;
        private Text _expText;
        private Text _levelUpText;

        /// <summary>
        /// 当前已订阅的成长状态机。用它做"认这个对象"的比对，而不是一个 bool 标志位。
        ///
        /// 【为什么不用 bool _bound】
        /// 场景重载后 CombatBridge 会造一个**新的** PlayerProgression，
        /// 但 Hud 若被复用，bool 还停在 true，于是永远订阅在旧对象上 ——
        /// 表现为"第二局升级了 HUD 毫无反应"。存引用比对就不会有这个问题。
        /// </summary>
        private PlayerProgression _boundProgression;

        /// <summary>
        /// 待播放的升级提示队列（存的是升到的等级）。
        ///
        /// 【为什么要排队而不是直接覆盖文本】
        /// 一次击杀可能连升两级，内核会**连着抛两次**事件（同一帧内）。
        /// 直接写文本的话，第二次会瞬间盖掉第一次，玩家只看到"升到 3 级"，
        /// 完全不知道自己刚刚连升了两级。排队依次播放才对得起那两次事件。
        /// </summary>
        private readonly List<int> _levelUpQueue = new List<int>(4);

        /// <summary>当前这条升级提示的剩余展示时间（秒）。</summary>
        private float _levelUpTimer;

        private float _fps = 60.0f;
        private readonly StringBuilder _sb = new StringBuilder(256);

        private void Start()
        {
            Build();
        }

        private void Update()
        {
            // 指数平滑：直接显示 1/deltaTime 会因为个别长帧疯狂跳数字，读不出趋势。
            if (Time.unscaledDeltaTime > 0.0f)
            {
                _fps = Mathf.Lerp(_fps, 1.0f / Time.unscaledDeltaTime, FpsSmoothing);
            }
            Refresh();
        }

        // ---------------------------------------------------------------------
        // 构建
        // ---------------------------------------------------------------------

        private void Build()
        {
            GameObject canvasGo = new GameObject("HudCanvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            // 0.5 = 宽高各占一半权重。取 0 会在 21:9 下让 UI 撑得过大，
            // 取 1 会在 4:3 下让 UI 缩得看不清；0.5 是三档 16:9 之外也不翻车的折中。
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            BuildHpBar(canvasGo.transform);
            BuildTopRight(canvasGo.transform);
            BuildCombo(canvasGo.transform);
            BuildLevelUpToast(canvasGo.transform);
            BuildT3Widgets(canvasGo.transform);
            BuildBossBar(canvasGo.transform);
        }

        /// <summary>
        /// 装配 P2-1 的 BOSS 血条。与 <see cref="BuildT3Widgets"/> 同款套路
        /// （GetComponent 兜底 → AddComponent → Build），理由见那个方法的注释。
        ///
        /// 放在最后建：血条在顶部中央、层级上应当压在既有控件之上，
        /// 而 uGUI 的绘制顺序就是 Hierarchy 顺序，后建即在上。
        /// </summary>
        /// <param name="canvasRoot">HUD Canvas 变换。</param>
        private void BuildBossBar(Transform canvasRoot)
        {
            _bossBar = GetComponent<HudBossBar>();
            if (_bossBar == null)
            {
                _bossBar = gameObject.AddComponent<HudBossBar>();
            }
            _bossBar.Build(canvasRoot);
        }

        /// <summary>
        /// 装配 T3 的两个子 HUD（技能栏 + 状态图标）。
        ///
        /// 【为什么它们是独立 MonoBehaviour 而不是 Hud 的私有方法】
        /// 两者各有自己的每帧刷新节奏与一大堆控件引用，塞进 Hud 会让这个文件
        /// 膨胀到 800 行以上。拆开之后，"技能栏坏了"与"血条坏了"是两个互不相干的
        /// 排查范围；而且 baselineMode 下可以整体不挂，连 Update 都不跑。
        ///
        /// 【为什么用 GetComponent 兜底而不是无脑 AddComponent】
        /// WorldBuilder 可能已经挂过了（代码装配路径）。重复 AddComponent 会得到
        /// 两个都在刷新的实例，控件叠在一起看起来像"字重了"，极难定位。
        /// </summary>
        private void BuildT3Widgets(Transform canvasRoot)
        {
            _skillBar = GetComponent<HudSkillBar>();
            if (_skillBar == null)
            {
                _skillBar = gameObject.AddComponent<HudSkillBar>();
            }
            _skillBar.Bind(ResolveBridge());
            _skillBar.Build(canvasRoot);

            _statusIcons = GetComponent<HudStatusIcons>();
            if (_statusIcons == null)
            {
                _statusIcons = gameObject.AddComponent<HudStatusIcons>();
            }
            _statusIcons.Bind(ResolveBridge());
            _statusIcons.Build(canvasRoot);
        }

        /// <summary>底部中央、技能栏正上方的连击数。</summary>
        private void BuildCombo(Transform parent)
        {
            _comboText = NewText("Combo", parent, 34, TextAnchor.LowerCenter,
                                 new Color(0.98f, 0.86f, 0.52f, 0.0f));
            Anchor(_comboText.rectTransform, new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f));
            _comboText.rectTransform.anchoredPosition = new Vector2(
                0.0f, HudSkillBar.BottomMargin + HudSkillBar.CellSize + 14.0f);
            _comboText.rectTransform.sizeDelta = new Vector2(420.0f, 44.0f);
            _comboText.text = string.Empty;
        }

        /// <summary>左上角：区域名 + 血条 + 冷却条。</summary>
        private void BuildHpBar(Transform parent)
        {
            RectTransform panel = NewRect("LeftTop", parent);
            Anchor(panel, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            panel.anchoredPosition = new Vector2(28.0f, -24.0f);
            panel.sizeDelta = new Vector2(HpBarSize.x, 150.0f);

            _zoneText = NewText("ZoneName", panel, 24, TextAnchor.UpperLeft,
                                new Color(0.90f, 0.94f, 0.86f, 0.96f));
            Anchor(_zoneText.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _zoneText.rectTransform.anchoredPosition = Vector2.zero;
            _zoneText.rectTransform.sizeDelta = new Vector2(HpBarSize.x, 30.0f);

            // --- 血条 ---
            RectTransform hpBg = NewImageRect("HpBg", panel, new Color(0.06f, 0.07f, 0.06f, 0.82f));
            Anchor(hpBg, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            hpBg.anchoredPosition = new Vector2(0.0f, -36.0f);
            hpBg.sizeDelta = HpBarSize;

            RectTransform hpFillRect = NewImageRect("HpFill", hpBg, new Color(0.70f, 0.22f, 0.24f, 0.95f));
            // 用 stretch 锚点 + fillAmount：条底和条身永远同宽同高，
            // 改血条尺寸只需要改 hpBg，不必同步改两处。
            Stretch(hpFillRect, 3.0f);
            _hpFill = hpFillRect.GetComponent<Image>();
            _hpFill.type = Image.Type.Filled;
            _hpFill.fillMethod = Image.FillMethod.Horizontal;
            _hpFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _hpFill.fillAmount = 1.0f;

            _hpText = NewText("HpText", hpBg, 18, TextAnchor.MiddleCenter, new Color(1.0f, 0.97f, 0.94f, 0.98f));
            Stretch(_hpText.rectTransform, 0.0f);

            // --- 灵力 / 体力（Q2 拍板：双池）---
            //
            // 【为什么两条都常驻，而不是"只在 T3 开启时才建"】
            // 建控件是一次性开销，隐藏只是一次 SetActive。若按开关决定建不建，
            // 运行中把 baselineMode 关掉就会出现"有数值没有条"。
            // 建好再按 T3Enabled 显隐，状态机只有一个方向，不会有半成品界面。
            float qiY = -36.0f - HpBarSize.y - 4.0f;
            float stamY = qiY - ResourceBarSize.y - 4.0f;

            _resourcePanel = NewRect("Resources", panel);
            Anchor(_resourcePanel, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _resourcePanel.anchoredPosition = Vector2.zero;
            _resourcePanel.sizeDelta = new Vector2(HpBarSize.x, ResourceBarSize.y * 2.0f + 8.0f);

            BuildResourceBar("Qi", _resourcePanel, qiY, new Color(0.38f, 0.68f, 0.96f, 0.95f),
                             out _qiFill, out _qiText);
            BuildResourceBar("Stamina", _resourcePanel, stamY, new Color(0.72f, 0.88f, 0.46f, 0.95f),
                             out _stamFill, out _stamText);

            // --- 攻击冷却条 ---
            RectTransform cdBg = NewImageRect("CdBg", panel, new Color(0.06f, 0.07f, 0.06f, 0.72f));
            Anchor(cdBg, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            cdBg.anchoredPosition = new Vector2(0.0f, stamY - ResourceBarSize.y - 6.0f);
            cdBg.sizeDelta = new Vector2(HpBarSize.x, 10.0f);

            RectTransform cdFillRect = NewImageRect("CdFill", cdBg, new Color(0.78f, 0.94f, 0.72f, 0.9f));
            Stretch(cdFillRect, 2.0f);
            _cdFill = cdFillRect.GetComponent<Image>();
            _cdFill.type = Image.Type.Filled;
            _cdFill.fillMethod = Image.FillMethod.Horizontal;
            _cdFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _cdFill.fillAmount = 1.0f;

            // --- P1-6 经验条 + 等级 ---
            //
            // 【为什么等级要单独一个文本，不并进 ZoneName 那一行】
            // ZoneName 那行末尾已经有一个 "Lv.N"，但那是**区域等级**（怪的强度），
            // 不是玩家等级。两个 Lv 挤在一行必然被误读成同一个东西。
            // 玩家等级贴在经验条上，语义自洽：条是经验，标签是等级。
            float expY = cdBg.anchoredPosition.y - 10.0f - 6.0f;

            RectTransform expBg = NewImageRect("ExpBg", panel, new Color(0.05f, 0.05f, 0.08f, 0.78f));
            Anchor(expBg, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            expBg.anchoredPosition = new Vector2(0.0f, expY);
            expBg.sizeDelta = ExpBarSize;

            RectTransform expFillRect = NewImageRect("ExpFill", expBg, new Color(0.86f, 0.76f, 0.38f, 0.95f));
            Stretch(expFillRect, 2.0f);
            _expFill = expFillRect.GetComponent<Image>();
            _expFill.type = Image.Type.Filled;
            _expFill.fillMethod = Image.FillMethod.Horizontal;
            _expFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _expFill.fillAmount = 0.0f;

            _levelText = NewText("PlayerLevel", panel, 18, TextAnchor.UpperLeft,
                                 new Color(0.96f, 0.90f, 0.62f, 0.96f));
            Anchor(_levelText.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _levelText.rectTransform.anchoredPosition = new Vector2(0.0f, expY - ExpBarSize.y - 2.0f);
            _levelText.rectTransform.sizeDelta = new Vector2(210.0f, 22.0f);

            _expText = NewText("ExpText", panel, 15, TextAnchor.UpperRight,
                               new Color(0.88f, 0.86f, 0.74f, 0.90f));
            Anchor(_expText.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _expText.rectTransform.anchoredPosition = new Vector2(ExpBarSize.x - 210.0f, expY - ExpBarSize.y - 2.0f);
            _expText.rectTransform.sizeDelta = new Vector2(210.0f, 22.0f);
        }

        /// <summary>
        /// 屏幕中部偏下的升级提示（R-14 的视觉反馈部分）。
        ///
        /// 【为什么和连击提示分开一个控件】
        /// 两者会同时出现（打死怪的那一刻既在连击中、又可能升级），
        /// 共用一个文本就会互相抢。分开之后各自淡出，互不干扰。
        /// </summary>
        private void BuildLevelUpToast(Transform parent)
        {
            _levelUpText = NewText("LevelUpToast", parent, 38, TextAnchor.LowerCenter,
                                   new Color(1.0f, 0.92f, 0.55f, 0.0f));
            Anchor(_levelUpText.rectTransform, new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f));
            _levelUpText.rectTransform.anchoredPosition = new Vector2(0.0f, 260.0f);
            _levelUpText.rectTransform.sizeDelta = new Vector2(560.0f, 52.0f);
            _levelUpText.text = string.Empty;
        }

        /// <summary>
        /// 造一条资源条（底 + 填充 + 居中文本）。灵力与体力唯一的差别只有颜色与文案，
        /// 抽成一个方法可以保证两条永远同尺寸、同锚点 —— 手抄两遍迟早会漂。
        /// </summary>
        /// <param name="name">节点名。</param>
        /// <param name="parent">父节点。</param>
        /// <param name="y">相对父节点顶部的 y 偏移（负值向下）。</param>
        /// <param name="tint">填充色。</param>
        /// <param name="fill">输出：填充 Image。</param>
        /// <param name="label">输出：文本。</param>
        private static void BuildResourceBar(string name, Transform parent, float y, Color tint,
                                             out Image fill, out Text label)
        {
            RectTransform bg = NewImageRect(name + "Bg", parent, new Color(0.05f, 0.06f, 0.06f, 0.78f));
            Anchor(bg, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            bg.anchoredPosition = new Vector2(0.0f, y);
            bg.sizeDelta = ResourceBarSize;

            RectTransform fillRect = NewImageRect(name + "Fill", bg, tint);
            Stretch(fillRect, 2.0f);
            fill = fillRect.GetComponent<Image>();
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 1.0f;

            label = NewText(name + "Text", bg, 13, TextAnchor.MiddleCenter,
                            new Color(0.96f, 0.98f, 0.96f, 0.92f));
            Stretch(label.rectTransform, 0.0f);
        }

        /// <summary>右上角：敌数 + 调试面板。</summary>
        private void BuildTopRight(Transform parent)
        {
            RectTransform panel = NewRect("RightTop", parent);
            Anchor(panel, new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f));
            panel.anchoredPosition = new Vector2(-28.0f, -24.0f);
            panel.sizeDelta = new Vector2(420.0f, 210.0f);

            _enemyText = NewText("EnemyCount", panel, 24, TextAnchor.UpperRight,
                                 new Color(0.95f, 0.86f, 0.62f, 0.96f));
            Anchor(_enemyText.rectTransform, new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f));
            _enemyText.rectTransform.anchoredPosition = Vector2.zero;
            _enemyText.rectTransform.sizeDelta = new Vector2(420.0f, 30.0f);

            RectTransform dbgBg = NewImageRect("DebugBg", panel, new Color(0.05f, 0.06f, 0.05f, 0.62f));
            Anchor(dbgBg, new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f));
            dbgBg.anchoredPosition = new Vector2(0.0f, -38.0f);
            dbgBg.sizeDelta = new Vector2(420.0f, 168.0f);

            _debugText = NewText("DebugText", dbgBg, 17, TextAnchor.UpperLeft,
                                 new Color(0.80f, 0.86f, 0.80f, 0.94f));
            Stretch(_debugText.rectTransform, 10.0f);
        }

        // ---------------------------------------------------------------------
        // 刷新
        // ---------------------------------------------------------------------

        private void Refresh()
        {
            CombatBridge bridge = ResolveBridge();

            // 每帧确认订阅仍挂在"当前这一局"的成长状态机上。
            // 放在 Refresh 而不是 Start：Hud.Start 可能早于 CombatBridge.Start 执行，
            // 那时 bridge.Progression 还是 null，一次性订阅会永远挂不上。
            BindProgression(bridge);
            RefreshProgression(bridge);
            RefreshLevelUpToast();

            if (_hpFill != null)
            {
                _hpFill.fillAmount = bridge != null ? bridge.PlayerHpRatio : 1.0f;
                // 血量越低越亮：低血预警不该只靠玩家自己盯数字。
                float r = _hpFill.fillAmount;
                _hpFill.color = Color.Lerp(new Color(0.92f, 0.30f, 0.22f, 0.98f),
                                           new Color(0.70f, 0.22f, 0.24f, 0.95f), r);
            }

            if (_hpText != null)
            {
                float hp = bridge != null ? bridge.PlayerHp : 0.0f;
                float max = bridge != null ? bridge.PlayerHpMaxNow : CombatBridge.PlayerHpMax;
                _hpText.text = string.Format("气血  {0:F0} / {1:F0}", Mathf.Max(0.0f, hp), max);
            }

            if (_zoneText != null)
            {
                string zoneName = bridge != null && bridge.Zone != null
                    ? bridge.Zone.DisplayName
                    : (WorldBuilder.Zone != null ? WorldBuilder.Zone.DisplayName : "未知区域");
                int lv = bridge != null && bridge.Zone != null ? bridge.Zone.BaseLevel : 0;
                _zoneText.text = string.Format("{0}   Lv.{1}", zoneName, lv);
            }

            if (_enemyText != null)
            {
                _enemyText.text = string.Format("残敌  {0}", bridge != null ? bridge.AliveEnemyCount : 0);
            }

            AttackController atk = ResolveAttack();
            if (_cdFill != null)
            {
                _cdFill.fillAmount = atk != null ? atk.CooldownRatio : 1.0f;
            }

            RefreshResources(bridge);
            RefreshCombo(bridge);

            if (_debugText != null)
            {
                _debugText.text = BuildDebugText(bridge, atk);
            }
        }

        /// <summary>刷新灵力 / 体力两条。T3 未装配时整组隐藏（P0-09 基线不显示 T3 元素）。</summary>
        // ---------------------------------------------------------------------
        // P1-6 玩家成长显示
        // ---------------------------------------------------------------------

        /// <summary>
        /// 把升级提示接到当前这一局的成长状态机上（幂等，每帧调用无副作用）。
        ///
        /// 【为什么要先退旧再订新】
        /// 场景重载后 CombatBridge 会造一个新的 PlayerProgression。
        /// 只 += 不 -= 的话，旧对象上的委托会一直吊着这个 Hud 不放，
        /// 是标准的事件泄漏；而且旧对象若被某处复用，还会触发重复提示。
        /// </summary>
        private void BindProgression(CombatBridge bridge)
        {
            PlayerProgression current = bridge != null ? bridge.Progression : null;
            if (ReferenceEquals(current, _boundProgression))
            {
                return;
            }

            if (_boundProgression != null)
            {
                _boundProgression.LeveledUp -= OnPlayerLeveledUp;
            }

            _boundProgression = current;

            if (_boundProgression != null)
            {
                _boundProgression.LeveledUp += OnPlayerLeveledUp;
            }

            // 换了一局，上一局没播完的提示必须丢弃，否则新局开局就弹"升到 7 级"。
            _levelUpQueue.Clear();
            _levelUpTimer = 0.0f;
            if (_levelUpText != null)
            {
                _levelUpText.text = string.Empty;
            }
        }

        /// <summary>
        /// 升级事件回调：**只入队，不直接改文本**。
        /// 连升两级时本方法会在同一帧被调用两次，两条提示依次播放（R-14）。
        /// </summary>
        private void OnPlayerLeveledUp(LevelUpInfo info)
        {
            _levelUpQueue.Add(info.NewLevel);
        }

        /// <summary>
        /// 刷新等级文本与经验条。
        ///
        /// 🚨【封顶时必须防除零】
        /// <c>ProgressionCurve.ExpToNext(10)</c> 返回 0（用 0 表示"没有下一级"）。
        /// 直接拿它做分母，fillAmount 会变成 NaN，Unity 的 Image 收到 NaN 后
        /// 整条控件会消失或渲染成花的 —— 而且不报任何错，极难定位。
        /// 所以满级走单独分支：条拉满、文字显示"已满级"。
        /// </summary>
        private void RefreshProgression(CombatBridge bridge)
        {
            PlayerProgression p = bridge != null ? bridge.Progression : null;

            if (_levelText != null)
            {
                int lv = p != null ? p.Level : 1;
                _levelText.text = string.Format("等级  Lv.{0}", lv);
            }

            // 尚未装配（Start 之前 / baselineMode 早期帧）时按 1 级空条显示，
            // 而不是把控件藏掉 —— 界面元素凭空出现/消失比"数字是 0"更让人困惑。
            if (p == null)
            {
                if (_expFill != null)
                {
                    _expFill.fillAmount = 0.0f;
                }
                if (_expText != null)
                {
                    _expText.text = string.Format("经验  0 / {0}", ProgressionCurve.ExpToNext(1));
                }
                return;
            }

            if (p.IsMaxLevel)
            {
                if (_expFill != null)
                {
                    _expFill.fillAmount = 1.0f;
                }
                if (_expText != null)
                {
                    _expText.text = "已满级";
                }
                return;
            }

            int need = p.ExpToNext;
            if (_expFill != null)
            {
                // need 在非满级分支里必然 > 0（曲线表 1~9 级都是正数），
                // 这里再判一次是纯防御：万一有人把表改坏了，也只是条不动，不会 NaN。
                _expFill.fillAmount = need > 0 ? Mathf.Clamp01((float)p.ExpInLevel / need) : 0.0f;
            }
            if (_expText != null)
            {
                _expText.text = string.Format("经验  {0} / {1}", p.ExpInLevel, need);
            }
        }

        /// <summary>
        /// 逐条播放升级提示：一条播完再播下一条，保证连升两级能被看见两次。
        /// </summary>
        private void RefreshLevelUpToast()
        {
            if (_levelUpText == null)
            {
                return;
            }

            if (_levelUpTimer > 0.0f)
            {
                _levelUpTimer -= Time.unscaledDeltaTime;

                // 用 unscaledDeltaTime：升级可能发生在终局暂停的前一刻，
                // 走 timeScale 的话提示会卡在屏幕上不消失。
                float t = Mathf.Clamp01(_levelUpTimer / LevelUpToastDuration);
                Color c = _levelUpText.color;
                // 后 40% 时间才开始淡出，前面保持全亮，读起来更从容。
                c.a = Mathf.Clamp01(t / 0.4f);
                _levelUpText.color = c;

                if (_levelUpTimer > 0.0f)
                {
                    return;
                }
            }

            if (_levelUpQueue.Count == 0)
            {
                if (_levelUpText.text.Length > 0)
                {
                    _levelUpText.text = string.Empty;
                }
                return;
            }

            int level = _levelUpQueue[0];
            _levelUpQueue.RemoveAt(0);

            _levelUpText.text = string.Format("突破！  Lv.{0}", level);
            _levelUpText.color = new Color(1.0f, 0.92f, 0.55f, 1.0f);
            _levelUpTimer = LevelUpToastDuration;
        }

        /// <summary>
        /// 退订成长事件。谁订阅谁负责退订 —— 与 CombatBridge.OnDestroy 同款约定。
        /// PlayerProgression.Reset() 刻意不清订阅者，所以这里不退就没人退。
        /// </summary>
        private void OnDestroy()
        {
            if (_boundProgression != null)
            {
                _boundProgression.LeveledUp -= OnPlayerLeveledUp;
                _boundProgression = null;
            }
        }

        private void RefreshResources(CombatBridge bridge)
        {
            bool live = bridge != null && bridge.IsReady && bridge.T3Enabled;

            if (_resourcePanel != null && _resourcePanel.gameObject.activeSelf != live)
            {
                _resourcePanel.gameObject.SetActive(live);
            }
            if (!live)
            {
                return;
            }

            if (_qiFill != null)
            {
                _qiFill.fillAmount = Mathf.Clamp01(bridge.QiRatio);
            }
            if (_qiText != null)
            {
                _qiText.text = string.Format("灵力  {0:F0} / {1:F0}", bridge.QiCurrent, bridge.QiMax);
            }
            if (_stamFill != null)
            {
                _stamFill.fillAmount = Mathf.Clamp01(bridge.StaminaRatio);
            }
            if (_stamText != null)
            {
                _stamText.text = string.Format("体力  {0:F0} / {1:F0}", bridge.StaminaCurrent, bridge.StaminaMax);
            }
        }

        /// <summary>
        /// 刷新连击数。断连不是"啪"地消失而是淡出：
        /// 瞬间消失会让玩家怀疑是不是自己看错了，淡出能明确传达"刚才那串断了"。
        /// </summary>
        private void RefreshCombo(CombatBridge bridge)
        {
            if (_comboText == null)
            {
                return;
            }

            int combo = bridge != null && bridge.IsReady && bridge.T3Enabled ? bridge.Combo : 0;

            if (combo >= ComboVisibleMin)
            {
                if (combo != _lastCombo)
                {
                    _comboText.text = string.Format("连击 {0}", combo);
                }
                _comboAlpha = 1.0f;
            }
            else
            {
                _comboAlpha -= ComboFadeSpeed * Time.unscaledDeltaTime;
                if (_comboAlpha < 0.0f)
                {
                    _comboAlpha = 0.0f;
                }
            }
            _lastCombo = combo;

            Color c = _comboText.color;
            if (!Mathf.Approximately(c.a, _comboAlpha))
            {
                _comboText.color = new Color(c.r, c.g, c.b, _comboAlpha);
            }
        }

        private string BuildDebugText(CombatBridge bridge, AttackController atk)
        {
            _sb.Length = 0;
            _sb.Append("seed     ").Append(WorldBuilder.Seed).Append('\n');
            _sb.Append("zone     ").Append(WorldBuilder.Zone != null ? WorldBuilder.Zone.ZoneId : "-")
               .Append("  v").Append(Bootstrap.ZoneVisits).Append('\n');
            _sb.Append("steps    ").Append(bridge != null ? bridge.StepCount : 0)
               .Append("   t=").Append((bridge != null ? bridge.ElapsedTime : 0.0f).ToString("F2")).Append("s\n");
            _sb.Append("fps      ").Append(_fps.ToString("F1")).Append('\n');
            _sb.Append("map      ").Append(WorldBuilder.Width).Append('x').Append(WorldBuilder.Height)
               .Append(" tile / ").Append(WorldBuilder.TileUnit).Append("u\n");
            _sb.Append("swings   ").Append(atk != null ? atk.SwingCount : 0)
               .Append("   last hits ").Append(atk != null ? atk.LastHitCount : 0).Append('\n');
            _sb.Append("atk raw  ").Append(AttackController.AttackRaw.ToString("F0"))
               .Append("   cd ").Append(AttackController.AttackCooldown.ToString("F2")).Append("s\n");

            if (bridge != null && bridge.T3Enabled)
            {
                _sb.Append("t3       qi ").Append(bridge.QiCurrent.ToString("F0"))
                   .Append('/').Append(bridge.QiMax.ToString("F0"))
                   .Append("  st ").Append(bridge.StaminaCurrent.ToString("F0"))
                   .Append('/').Append(bridge.StaminaMax.ToString("F0"))
                   .Append("  cb ").Append(bridge.Combo).Append('\n');
                _sb.Append("action   ").Append(bridge.PlayerAction)
                   .Append(bridge.PlayerIframeActive ? "  [iframe]" : string.Empty).Append('\n');
            }
            else
            {
                _sb.Append("t3       off (baseline)\n");
            }

            // ★P2-1 C3 防线：把 BOSS 出场编排的内部状态摊在开发期面板上。
            //   软锁最可怕的不是发生，是发生了没人知道 —— 有了这一行，
            //   "清完怪但 BOSS 不出来"的那几秒里，pending / idle 会当着开发者的面往上涨。
            if (bridge != null)
            {
                _sb.Append("boss     ").Append(bridge.BossFlowStateName)
                   .Append("  pending=").Append(bridge.IsBossPending ? 1 : 0)
                   .Append("  idle=").Append(bridge.BossPendingIdleSeconds.ToString("F1")).Append("s\n");
            }

            DeterminismDump dump = ResolveDump();
            if (dump != null && !string.IsNullOrEmpty(dump.LastDumpPath))
            {
                _sb.Append("dump     Logs/").Append(System.IO.Path.GetFileName(dump.LastDumpPath));
            }
            else
            {
                _sb.Append("dump     -");
            }
            return _sb.ToString();
        }

        // ---------------------------------------------------------------------
        // 引用解析（世界会被反复重建，缓存必须能自愈）
        // ---------------------------------------------------------------------

        private CombatBridge ResolveBridge()
        {
            if (_bridge == null)
            {
                _bridge = FindOne<CombatBridge>();
            }
            return _bridge;
        }

        private AttackController ResolveAttack()
        {
            if (_attack == null)
            {
                _attack = FindOne<AttackController>();
            }
            return _attack;
        }

        private DeterminismDump ResolveDump()
        {
            if (_dump == null)
            {
                _dump = FindOne<DeterminismDump>();
            }
            return _dump;
        }

        private static T FindOne<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<T>();
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        // ---------------------------------------------------------------------
        // uGUI 小工具
        //
        // 【为什么从 private 提到 public】
        // HudSkillBar / HudStatusIcons 需要造完全同款的控件。若各自抄一份，
        // "UI 层是哪一层""要不要 raycastTarget""字体怎么取"就有了三个答案，
        // 换 Unity 版本时会出现"血条正常、技能栏字全没了"这种局部翻车。
        // 提权是为了让这些约定只有一份实现。
        // ---------------------------------------------------------------------

        /// <summary>造一个空 RectTransform 节点（已设 UI 层与父节点）。</summary>
        public static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>造一个带纯色 Image 的节点（图源走 SpriteFactory，零美术资源）。</summary>
        public static RectTransform NewImageRect(string name, Transform parent, Color color)
        {
            RectTransform rt = NewRect(name, parent);
            Image img = rt.gameObject.AddComponent<Image>();
            img.sprite = SpriteFactory.UiPixel();
            img.color = color;
            img.raycastTarget = false;   // HUD 不接受点击，别挡住将来的可交互 UI
            return rt;
        }

        /// <summary>造一个 Legacy Text 节点（内置字体，不引入 TMP 资产）。</summary>
        public static Text NewText(string name, Transform parent, int size, TextAnchor align, Color color)
        {
            RectTransform rt = NewRect(name, parent);
            Text t = rt.gameObject.AddComponent<Text>();
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

        /// <summary>
        /// 取内置字体。Unity 2022.2 起把 Arial.ttf 更名为 LegacyRuntime.ttf，
        /// 两个名字都试一遍，免得跨版本打开工程时 HUD 变成一片空白。
        /// </summary>
        private static Font BuiltinFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null)
            {
                f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return f;
        }

        /// <summary>一次性设定锚点与轴心。</summary>
        public static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.pivot = pivot;
        }

        /// <summary>把 RectTransform 拉满父节点，四边留 <paramref name="padding"/> 的内边距。</summary>
        public static void Stretch(RectTransform rt, float padding)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(padding, padding);
            rt.offsetMax = new Vector2(-padding, -padding);
        }
    }
}
