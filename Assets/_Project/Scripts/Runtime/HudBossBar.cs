// -----------------------------------------------------------------------------
// HudBossBar.cs —— BOSS 血条（asmdef: Xianxia.Unity.T2）
//
// 【归属】作为 Hud 的**兄弟 MonoBehaviour** 挂在同一个 GameObject 上，
// 由 Hud.Build() 在 BuildT3Widgets 之后调 BuildBossBar 装配。
// 理由与 HudSkillBar / HudStatusIcons 完全一致（见 Hud.cs:162-169）：
//   ① 它有自己的每帧刷新节奏与一堆控件引用，塞进 Hud 只会让那个文件继续膨胀；
//   ② 用 GetComponent 兜底再 AddComponent，防止代码装配路径重复挂两份
//      —— 两份都在刷新，控件叠在一起看起来像"字重了"，极难定位。
//
// 【布局：距顶 72，不是 40】
// PRD 给的是「距顶 40」。实测不能用：HudStatusIcons 的「目标状态行」占据
// 距顶 28~62、x∈[−147,+147]（HudStatusIcons.cs:105-106）。血条放 40 的话
// 纵向 [40,68] 与它重叠 22px，横向 [−360,+360] 还完全包住它 ——
// 打 BOSS 时目标 DEBUFF 图标被血条盖住，而打 BOSS 恰恰是最需要看 DEBUFF 的时候。
// 让新组件下移到 72，既有 HUD 零布局改动、既有测试零风险，视觉代价接近零。
//
// 【★测试陷阱】Build() 末尾 _root.SetActive(false)（没打 BOSS 时不该有空血条）。
// GetComponentInChildren<T>() 默认**跳过未激活对象**，单测里取组件必须写
//     hudGo.GetComponentInChildren<HudBossBar>(true);   // ← true 不能漏
// Transform.Find 对未激活对象有效，可以直接找路径 "HudCanvas/BossBar/BarFill"。
// 这与 P0_5_MenuHudTests.cs:20 记录的 MainMenuHud/PauseMenuHud 是同款坑。
//
// 【阶段阈值不在这里定义】0.65 / 0.30 的真源是 CombatConfig.BOSS_PHASE_P2/P3_THRESHOLD，
// 与 BossController 判定阶段用的是同一对常量。刻度线直接引用它们 ——
// 在这里再抄一份，哪天调平衡改了阈值，血条刻度就会和实际变身点对不上。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 屏幕顶部中央的 BOSS 血条。默认隐藏，由 <c>CombatBridge</c> 在 BOSS 出场时
    /// 调 <see cref="Show"/> 放出来，BOSS 死亡或终局时 <see cref="Hide"/> 收起。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudBossBar : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // 配色（三阶段各一档，玩家扫一眼就知道打到哪了）
        // ---------------------------------------------------------------------

        /// <summary>血槽底色（深墨）。</summary>
        public static readonly Color SlotColor = new Color(0.07f, 0.07f, 0.09f, 0.86f);

        /// <summary>血槽描边色。</summary>
        public static readonly Color SlotEdgeColor = new Color(0.82f, 0.78f, 0.68f, 0.55f);

        /// <summary>P1 血色（正红）。</summary>
        public static readonly Color Phase1Color = new Color(0.78f, 0.20f, 0.22f, 0.95f);

        /// <summary>P2 血色（橙，提示"它变强了"）。</summary>
        public static readonly Color Phase2Color = new Color(0.92f, 0.52f, 0.18f, 0.95f);

        /// <summary>P3 血色（灼白红，狂暴期）。</summary>
        public static readonly Color Phase3Color = new Color(0.98f, 0.30f, 0.24f, 1.0f);

        /// <summary>名字行文字色。</summary>
        public static readonly Color NameColor = new Color(0.94f, 0.90f, 0.82f, 0.98f);

        /// <summary>阶段刻度线颜色。</summary>
        public static readonly Color TickColor = new Color(0.05f, 0.05f, 0.06f, 0.85f);

        // ---------------------------------------------------------------------
        // 控件引用
        // ---------------------------------------------------------------------

        private RectTransform _root;
        private Text _nameText;
        private RectTransform _fill;
        private Image _fillImage;

        private Combatant _boss;
        private bool _built;
        private BossPhase _phase = BossPhase.P1;

        // 上一帧写进去的血量比例。用来避免每帧无谓地改 sizeDelta（uGUI 会触发重排）。
        private float _lastRatio = -1.0f;

        /// <summary>是否已装配完控件。</summary>
        public bool IsBuilt
        {
            get { return _built; }
        }

        /// <summary>当前是否显示中。</summary>
        public bool IsShown
        {
            get { return _built && _root != null && _root.gameObject.activeSelf; }
        }

        /// <summary>当前绑定的 BOSS（未显示时为 null）。</summary>
        public Combatant Boss
        {
            get { return _boss; }
        }

        /// <summary>当前显示的阶段。</summary>
        public BossPhase Phase
        {
            get { return _phase; }
        }

        // ---------------------------------------------------------------------
        // 构建
        // ---------------------------------------------------------------------

        /// <summary>
        /// 在指定 Canvas 下搭出血条控件。**幂等**：重复调用直接返回，不会建出第二份。
        /// </summary>
        /// <param name="canvasRoot">HUD 的 Canvas 变换（<c>Hud.Build</c> 里的 canvasGo.transform）。</param>
        public void Build(Transform canvasRoot)
        {
            // ① 幂等守卫。与 HudSkillBar.cs:102 / HudStatusIcons.cs:97 同款。
            if (_built || canvasRoot == null)
            {
                return;
            }

            // ② 根节点：顶部中央，pivot 取左上系的 (0.5,1) —— 子控件的 y 都用负值往下排。
            _root = Hud.NewRect("BossBar", canvasRoot);
            Hud.Anchor(_root, new Vector2(0.5f, 1.0f), new Vector2(0.5f, 1.0f), new Vector2(0.5f, 1.0f));
            _root.anchoredPosition = new Vector2(0.0f, -BossFlowConfig.BarTopMargin);
            _root.sizeDelta = new Vector2(BossFlowConfig.BarWidth, BossFlowConfig.BarRootHeight);

            // ③ 名字行。横跨整根，居中。
            _nameText = Hud.NewText("BossName", _root, BossFlowConfig.BarNameFontSize,
                                    TextAnchor.UpperCenter, NameColor);
            Hud.Anchor(_nameText.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f),
                       new Vector2(0.0f, 1.0f));
            _nameText.rectTransform.anchoredPosition = Vector2.zero;
            _nameText.rectTransform.sizeDelta = new Vector2(BossFlowConfig.BarWidth,
                                                            BossFlowConfig.BarNameHeight);
            _nameText.text = string.Empty;

            Vector2 slotPos = new Vector2(0.0f, -BossFlowConfig.BarSlotOffsetY);
            Vector2 slotSize = new Vector2(BossFlowConfig.BarWidth, BossFlowConfig.BarHeight);

            // ④ 槽底（深墨）。
            RectTransform bg = Hud.NewImageRect("BarBg", _root, SlotColor);
            Hud.Anchor(bg, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            bg.anchoredPosition = slotPos;
            bg.sizeDelta = slotSize;

            // ⑤ 血条。
            //    ★pivot.x 必须为 0：sizeDelta.x = BarWidth * ratio 是"改宽度"，
            //      pivot 在中心的话宽度会从两头一起缩，看起来像"血从两边往中间消失"，
            //      而不是常识中的"从右往左掉"。这个错误在静态截图里几乎看不出来。
            _fill = Hud.NewImageRect("BarFill", _root, Phase1Color);
            Hud.Anchor(_fill, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _fill.anchoredPosition = slotPos;
            _fill.sizeDelta = slotSize;
            _fillImage = _fill.GetComponent<Image>();

            // ⑥ 两条阶段刻度线。x 直接由内核阈值算出，与 BossController 同源。
            BuildTick("TickP2", CombatConfig.BOSS_PHASE_P2_THRESHOLD, slotPos.y);
            BuildTick("TickP3", CombatConfig.BOSS_PHASE_P3_THRESHOLD, slotPos.y);

            // ⑦ 槽边框（细描边）。放在刻度线之后建，保证它压在最上层，
            //    否则刻度线会盖住边框，边角看起来像缺了一块。
            BuildSlotEdge(slotPos, slotSize);

            // ⑧ 默认隐藏：没打 BOSS 时不该有一根空血条挂在屏幕顶上。
            _root.gameObject.SetActive(false);

            // ⑨ 最后置位。放在最后是为了让"Build 中途抛异常"不会留下一个
            //    _built == true 但控件不全的半成品。
            _built = true;
        }

        /// <summary>造一条阶段刻度线。</summary>
        /// <param name="name">节点名。</param>
        /// <param name="ratio">阈值比例（0~1），直接取自内核常量。</param>
        /// <param name="slotY">血槽的 y 偏移。</param>
        private void BuildTick(string name, float ratio, float slotY)
        {
            RectTransform tick = Hud.NewImageRect(name, _root, TickColor);
            // pivot.x = 0.5：让刻度线**跨坐**在阈值点上，而不是整条压在阈值右侧。
            Hud.Anchor(tick, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.5f, 1.0f));
            tick.anchoredPosition = new Vector2(BossFlowConfig.BarWidth * ratio, slotY);
            tick.sizeDelta = new Vector2(BossFlowConfig.TickWidth, BossFlowConfig.BarHeight);
        }

        /// <summary>
        /// 造血槽的 1px 描边。用四条细矩形拼，而不是给 Image 换九宫格精灵 ——
        /// 本工程零美术资源，SpriteFactory 只出纯色像素与圆。
        /// </summary>
        /// <param name="slotPos">血槽左上角位置。</param>
        /// <param name="slotSize">血槽尺寸。</param>
        private void BuildSlotEdge(Vector2 slotPos, Vector2 slotSize)
        {
            const float thickness = 1.0f;

            // 上、下、左、右四条。名字带 Edge 前缀，方便测试里整体排除。
            RectTransform top = Hud.NewImageRect("BarEdgeTop", _root, SlotEdgeColor);
            Hud.Anchor(top, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            top.anchoredPosition = slotPos;
            top.sizeDelta = new Vector2(slotSize.x, thickness);

            RectTransform bottom = Hud.NewImageRect("BarEdgeBottom", _root, SlotEdgeColor);
            Hud.Anchor(bottom, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            bottom.anchoredPosition = new Vector2(slotPos.x, slotPos.y - slotSize.y + thickness);
            bottom.sizeDelta = new Vector2(slotSize.x, thickness);

            RectTransform left = Hud.NewImageRect("BarEdgeLeft", _root, SlotEdgeColor);
            Hud.Anchor(left, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            left.anchoredPosition = slotPos;
            left.sizeDelta = new Vector2(thickness, slotSize.y);

            RectTransform right = Hud.NewImageRect("BarEdgeRight", _root, SlotEdgeColor);
            Hud.Anchor(right, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            right.anchoredPosition = new Vector2(slotPos.x + slotSize.x - thickness, slotPos.y);
            right.sizeDelta = new Vector2(thickness, slotSize.y);
        }

        // ---------------------------------------------------------------------
        // 显示 / 隐藏 / 刷新
        // ---------------------------------------------------------------------

        /// <summary>
        /// 绑定 BOSS 并显示血条。<paramref name="boss"/> 为 null 或未 Build 时是空操作。
        /// </summary>
        /// <param name="boss">已入列的 BOSS 实体。</param>
        public void Show(Combatant boss)
        {
            if (!_built || boss == null)
            {
                return;
            }

            _boss = boss;
            _phase = BossPhase.P1;
            _lastRatio = -1.0f;

            string display = string.IsNullOrEmpty(boss.DisplayName) ? boss.Kind : boss.DisplayName;
            _nameText.text = string.Format("{0}　Lv.{1}", display, boss.Level);

            ApplyPhaseColor();
            Refresh();

            _root.gameObject.SetActive(true);
        }

        /// <summary>
        /// 切阶段（染色）。由 <c>CombatBridge</c> 订阅内核的
        /// <c>CombatEventsUnity.BossPhaseChanged</c> 后转发过来。
        ///
        /// 内核的 <c>BossController.CheckPhaseTransition</c> 保证**只降不升**，
        /// 且 P1 直接跌破 30% 的连跳只抛一次 P3 —— 所以这里不需要自己去重。
        /// </summary>
        /// <param name="phase">新阶段。</param>
        public void SetPhase(BossPhase phase)
        {
            _phase = phase;
            if (!_built)
            {
                return;
            }
            ApplyPhaseColor();
        }

        /// <summary>隐藏血条并解绑。可重复调用。</summary>
        public void Hide()
        {
            _boss = null;
            _lastRatio = -1.0f;
            if (_built && _root != null)
            {
                _root.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            // 非战斗期直接空转出去，别为一根隐藏的血条每帧算比例。
            if (!IsShown || _boss == null)
            {
                return;
            }

            // BOSS 死了自动收起。**不依赖事件**：万一哪天订阅漏了或事件顺序变了，
            // 至少不会出现"BOSS 早没了，血条还空着挂在屏幕顶上"这种穿帮。
            if (!_boss.IsAlive)
            {
                Hide();
                return;
            }

            Refresh();
        }

        /// <summary>按当前血量比例刷新血条宽度。</summary>
        private void Refresh()
        {
            if (_boss == null || _fill == null)
            {
                return;
            }

            float max = _boss.HpMax;
            // HpMax 为 0 会算出 NaN，NaN 写进 sizeDelta 会让整个 Canvas 静默不渲染 ——
            // 这类"UI 全没了但没有任何报错"的故障排查成本极高，这里直接夹死。
            float ratio = max > 0.0f ? Mathf.Clamp01(_boss.Hp / max) : 0.0f;

            // 变化小于半个像素就不写。uGUI 改 sizeDelta 会标脏并触发重排，
            // 每帧无谓地写一次在低端机上是实打实的开销。
            if (_lastRatio >= 0.0f && Mathf.Abs(ratio - _lastRatio) * BossFlowConfig.BarWidth < 0.5f)
            {
                return;
            }
            _lastRatio = ratio;

            _fill.sizeDelta = new Vector2(BossFlowConfig.BarWidth * ratio, BossFlowConfig.BarHeight);
        }

        /// <summary>把当前阶段的配色写进血条。</summary>
        private void ApplyPhaseColor()
        {
            if (_fillImage == null)
            {
                return;
            }

            if (_phase == BossPhase.P3)
            {
                _fillImage.color = Phase3Color;
            }
            else if (_phase == BossPhase.P2)
            {
                _fillImage.color = Phase2Color;
            }
            else
            {
                _fillImage.color = Phase1Color;
            }
        }
    }
}
