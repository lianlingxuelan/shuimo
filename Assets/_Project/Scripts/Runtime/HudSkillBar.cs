// -----------------------------------------------------------------------------
// HudSkillBar.cs —— 屏幕底部的技能栏 UI：图标、冷却转圈、按键提示、消耗数字
//
// 【本文件是纯粹的"只读展示层"，请注意它一次都没有修改过战斗状态】
// 它每帧向 CombatBridge 查询"技能好了吗""冷却到百分之几"，然后刷新颜色和填充比例。
// 全程没有扣灵力、没有触发技能 —— UI 只反映状态，绝不制造状态。
// 一旦 UI 开始写状态（比如"点图标就放技能"直接调结算），
// 就会出现"UI 显示的和实际发生的不一致"这类最难查的 bug。
//
// 【执行序 210（一个较大的正数）的用意】
// UI 必须最后更新。前面的控制器(-90/-50)投意图、内核(0 附近)结算完毕，
// 数据才算尘埃落定；此时 UI 再来读，显示的就是本帧的最终结果。
// 若 UI 抢先执行，玩家看到的永远是上一帧的旧数据，血条会慢半拍。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>底部技能栏。由 Hud 在运行时用代码搭建（本工程不使用预制体，见 Build）。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(210)]
    public sealed class HudSkillBar : MonoBehaviour
    {
        /// <summary>技能格数量。下面几个 static 数组都必须保持这个长度。</summary>
        public const int SlotCount = 4;

        /// <summary>单格边长（像素）。</summary>
        public const float CellSize = 76.0f;

        /// <summary>格与格之间的间隙。</summary>
        public const float CellGap = 14.0f;

        /// <summary>技能栏距屏幕底部的距离。</summary>
        public const float BottomMargin = 34.0f;

        // 【下面这组 static readonly 数组是一种叫"平行数组"的组织方式】
        // 第 i 格的槽位是 Slots[i]、对应按键是 Actions[i]、颜色是 SlotTints[i]、文字是 SlotLabels[i]。
        // 它们靠"下标一致"来关联，所以四个数组的顺序必须严格对齐，改动时要四处同步。
        // 更严谨的做法是定义一个 SlotStyle 结构体、用一个数组存四个字段；
        // 这里选平行数组是因为条目少且都是编译期常量，改起来一目了然。
        // 但要清楚它的代价：漏改其中一个数组，编译器完全不会报错，只会在运行时错位。
        private static readonly IntentSlot[] Slots =
        {
            IntentSlot.Basic,
            IntentSlot.Skill1,
            IntentSlot.Skill2,
            IntentSlot.Dodge
        };

        private static readonly GameAction[] Actions =
        {
            GameAction.Attack,
            GameAction.Skill1,
            GameAction.Skill2,
            GameAction.Dodge
        };

        private static readonly Color[] SlotTints =
        {
            new Color(0.72f, 0.86f, 0.96f, 0.95f),
            new Color(0.52f, 0.72f, 1.00f, 0.95f),
            new Color(0.92f, 0.42f, 0.54f, 0.95f),
            new Color(0.86f, 0.90f, 0.78f, 0.95f)
        };

        private static readonly string[] SlotLabels = { "斩", "阵", "莲", "闪" };

        private readonly Image[] _icons = new Image[SlotCount];
        private readonly Image[] _masks = new Image[SlotCount];
        private readonly Image[] _frames = new Image[SlotCount];
        private readonly Text[] _keyTexts = new Text[SlotCount];
        private readonly Text[] _costTexts = new Text[SlotCount];

        private CombatBridge _bridge;
        private RectTransform _root;
        private string _scheme = string.Empty;
        private bool _built;

        public bool IsBuilt
        {
            get { return _built; }
        }

        public void Bind(CombatBridge bridge)
        {
            _bridge = bridge;
        }

        /// <summary>
        /// 用代码搭出整个技能栏。只会真正执行一次（由 _built 守卫）。
        ///
        /// 【为什么用代码建 UI，而不是在编辑器里拖预制体】
        /// 本工程刻意走"全代码搭场景"路线（见 WorldBuilder）。原因是：
        ///   · 预制体是二进制/YAML 资源，改动在 git 上几乎无法 review、极易冲突；
        ///   · 代码搭建的界面可以被自动化测试直接构造和断言，不依赖打开 Unity 编辑器；
        ///   · 布局参数（CellSize 等）集中成常量，调整时不用在面板里逐个拖拽。
        /// 代价是代码更长、所见非所得。这是一个明确的取舍，不是偷懒。
        /// </summary>
        public void Build(Transform canvasRoot)
        {
            // 幂等守卫：重复调用直接返回，否则会叠出好几层技能栏。
            if (_built || canvasRoot == null)
            {
                return;
            }

            float totalWidth = SlotCount * CellSize + (SlotCount - 1) * CellGap;

            _root = Hud.NewRect("SkillBar", canvasRoot);
            Hud.Anchor(_root, new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f));
            _root.anchoredPosition = new Vector2(0.0f, BottomMargin);
            _root.sizeDelta = new Vector2(totalWidth, CellSize);

            for (int i = 0; i < SlotCount; i++)
            {
                BuildCell(i, totalWidth);
            }

            _built = true;
            RefreshHints(true);
        }

        private void BuildCell(int index, float totalWidth)
        {
            float x = -totalWidth * 0.5f + index * (CellSize + CellGap);

            RectTransform cell = Hud.NewRect("Slot" + index, _root);
            Hud.Anchor(cell, new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f));
            cell.anchoredPosition = new Vector2(x + totalWidth * 0.5f, 0.0f);
            cell.sizeDelta = new Vector2(CellSize, CellSize);

            RectTransform bg = Hud.NewImageRect("Bg", cell, new Color(0.05f, 0.06f, 0.05f, 0.78f));
            Hud.Stretch(bg, 0.0f);

            RectTransform frame = Hud.NewImageRect("Frame", cell, new Color(0.62f, 0.68f, 0.60f, 0.55f));
            Hud.Stretch(frame, -2.0f);
            Image frameImg = frame.GetComponent<Image>();
            frameImg.type = Image.Type.Sliced;
            _frames[index] = frameImg;

            RectTransform icon = Hud.NewImageRect("Icon", cell, SlotTints[index]);
            Hud.Stretch(icon, 12.0f);
            _icons[index] = icon.GetComponent<Image>();

            Text label = Hud.NewText("Label", cell, 30, TextAnchor.MiddleCenter,
                                     new Color(0.10f, 0.11f, 0.10f, 0.92f));
            Hud.Stretch(label.rectTransform, 0.0f);
            label.text = SlotLabels[index];

            // 冷却遮罩：一块半透明黑片，用"径向填充"做成转圈效果。
            // 逐行解释这几个设置，它们共同决定了转圈的观感：
            //   Type.Filled        —— 允许按比例只画一部分（这是能做转圈的前提）
            //   Radial360          —— 填充方式为绕圆心一整圈（而非横向/纵向条状）
            //   fillOrigin = Top   —— 从 12 点钟方向开始，符合钟表直觉
            //   fillClockwise=false—— 逆时针。配合下面 fillAmount 从 1 递减到 0，
            //                         视觉上就是"黑片逆时针退去、技能逐渐亮起"。
            //                         若设成顺时针，观感会变成"黑片正在爬上来"，
            //                         玩家会误读成"技能正在变得不可用"，语义正好相反。
            //   fillAmount = 0     —— 初始无遮罩（技能可用）
            RectTransform mask = Hud.NewImageRect("CdMask", cell, new Color(0.02f, 0.02f, 0.03f, 0.68f));
            Hud.Stretch(mask, 2.0f);
            Image maskImg = mask.GetComponent<Image>();
            maskImg.type = Image.Type.Filled;
            maskImg.fillMethod = Image.FillMethod.Radial360;
            maskImg.fillOrigin = (int)Image.Origin360.Top;
            maskImg.fillClockwise = false;
            maskImg.fillAmount = 0.0f;
            _masks[index] = maskImg;

            Text keyText = Hud.NewText("Key", cell, 16, TextAnchor.LowerLeft,
                                       new Color(0.92f, 0.95f, 0.88f, 0.96f));
            Hud.Anchor(keyText.rectTransform, new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f));
            keyText.rectTransform.anchoredPosition = new Vector2(5.0f, 3.0f);
            keyText.rectTransform.sizeDelta = new Vector2(CellSize, 20.0f);
            _keyTexts[index] = keyText;

            Text costText = Hud.NewText("Cost", cell, 15, TextAnchor.UpperRight,
                                        new Color(0.66f, 0.84f, 0.98f, 0.96f));
            Hud.Anchor(costText.rectTransform, new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f));
            costText.rectTransform.anchoredPosition = new Vector2(-5.0f, -3.0f);
            costText.rectTransform.sizeDelta = new Vector2(CellSize, 20.0f);
            _costTexts[index] = costText;
        }

        private void Update()
        {
            if (!_built)
            {
                return;
            }
            RefreshHints(false);
            Refresh();
        }

        /// <summary>
        /// 刷新按键提示文字（键盘时显示 "J"，手柄时显示按钮名）。
        ///
        /// 【为什么要用 _scheme 缓存做"变了才刷"的判断】
        /// 给 UI Text 赋值 text 属性，即使内容完全相同，Unity 也会把这块 UI 标记为
        /// "需要重建网格"（dirty），触发一次布局与顶点重算。每帧对 4 个文本做这件事，
        /// 在低端机上是实打实的开销，且完全没有必要 —— 操作方案几分钟才可能变一次。
        /// 只在"真的变了"时才写入，是 UI 优化里回报最高的一条习惯。
        /// force 参数留给首次构建：那时 _scheme 是空串，需要强制写一次初始值。
        /// </summary>
        private void RefreshHints(bool force)
        {
            string scheme = InputBinder.Scheme;
            if (!force && scheme == _scheme)
            {
                return;
            }
            _scheme = scheme;

            for (int i = 0; i < SlotCount; i++)
            {
                if (_keyTexts[i] != null)
                {
                    _keyTexts[i].text = InputBinder.Hint(Actions[i]);
                }
            }
        }

        /// <summary>
        /// 每帧刷新四个技能格的冷却、可用态与消耗数字。
        /// </summary>
        private void Refresh()
        {
            CombatBridge bridge = ResolveBridge();
            // live = 战斗系统当前是否真的在跑。不 live 时下面统一按"冷却 0、不可用"显示，
            // 而不是直接 return —— 这样技能栏依然画得出来（只是灰着），
            // 便于在主菜单/基线模式下检查布局是否正常。
            bool live = bridge != null && bridge.IsReady && bridge.T3Enabled;

            for (int i = 0; i < SlotCount; i++)
            {
                IntentSlot slot = Slots[i];

                float cd = live ? bridge.SkillCdRatio(slot) : 0.0f;
                if (_masks[i] != null)
                {
                    // Clamp01 把值夹到 [0,1]。这是对上游数据的不信任式防御：
                    // 万一 SkillCdRatio 因为浮点误差返回 1.0000001，
                    // fillAmount 超范围会让 Unity 打警告并每帧刷屏。夹一下成本极低。
                    _masks[i].fillAmount = Mathf.Clamp01(cd);
                }

                bool ready = live && bridge.SkillReady(slot);
                if (_icons[i] != null)
                {
                    // 不可用时把 RGB 三个分量统一乘 0.45（压暗）并降低不透明度。
                    // 用"乘一个系数"而不是"换成一个固定的灰色"，
                    // 是为了保留每个技能各自的色相 —— 玩家仍能靠颜色分辨是哪个技能，
                    // 只是明显变暗了。这比全部变成同一种灰的可读性好得多。
                    Color tint = SlotTints[i];
                    _icons[i].color = ready
                        ? tint
                        : new Color(tint.r * 0.45f, tint.g * 0.45f, tint.b * 0.45f, 0.72f);
                }
                if (_frames[i] != null)
                {
                    _frames[i].color = ready
                        ? new Color(0.86f, 0.92f, 0.78f, 0.72f)
                        : new Color(0.40f, 0.44f, 0.40f, 0.45f);
                }

                if (_costTexts[i] != null)
                {
                    _costTexts[i].text = CostLabel(bridge, slot);
                }
            }
        }

        /// <summary>
        /// 取该技能格要显示的消耗数字。
        ///
        /// 【这里体现了灵力/体力"双资源池"的设计】
        /// 一个技能通常只花一种资源：法术类花灵力(Qi)，闪避这种位移动作花体力(Stamina)。
        /// 所以先查灵力、没有再查体力，两者都没有就不显示 —— 普攻通常是零消耗的。
        /// 为什么要分两个池？因为共用一个池会让玩家陷入"省着资源保命"的单一最优解，
        /// 技能永远不敢放。拆开后，放技能不会削弱逃生能力，两套操作各自独立地被鼓励。
        ///
        /// ToString("F0") = 保留 0 位小数。内部资源是 float（便于做百分比回复），
        /// 但 UI 上显示 "12.5 灵力" 既占地方又没意义，取整更清爽。
        /// </summary>
        private static string CostLabel(CombatBridge bridge, IntentSlot slot)
        {
            SkillDef def = bridge != null ? bridge.SkillOf(slot) : null;
            if (def == null)
            {
                return string.Empty;
            }
            if (def.QiCost > 0.0f)
            {
                return def.QiCost.ToString("F0");
            }
            if (def.StaminaCost > 0.0f)
            {
                return def.StaminaCost.ToString("F0");
            }
            return string.Empty;
        }

        private CombatBridge ResolveBridge()
        {
            if (_bridge != null)
            {
                return _bridge;
            }
#if UNITY_2023_1_OR_NEWER
            _bridge = Object.FindFirstObjectByType<CombatBridge>();
#else
            _bridge = Object.FindObjectOfType<CombatBridge>();
#endif
            return _bridge;
        }
    }
}
