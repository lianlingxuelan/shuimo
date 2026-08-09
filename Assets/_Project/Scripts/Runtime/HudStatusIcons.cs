// -----------------------------------------------------------------------------
// HudStatusIcons.cs —— 状态图标栏：显示我方与目标身上的 buff/debuff
//
// 【本文件最值得学的是"对象池"思想，见 BuildRow 与 FillRow】
// 状态是频繁增减的（中毒 3 秒后消失、又被点燃）。最直觉的写法是
// "有新状态就 Instantiate 一个图标，状态消失就 Destroy 它"。
// 但这在 Unity 里是性能陷阱：创建/销毁 GameObject 都要走引擎的对象管理，
// 还会产生垃圾内存，战斗激烈时每秒几十次增删足以造成肉眼可见的卡顿。
//
// 本文件的做法是：开场就把 6 个图标全部建好、然后隐藏起来；
// 之后只用 SetActive(true/false) 来"显示/隐藏"，永远不再创建或销毁。
// 这就是对象池：用固定的内存换取零运行时分配。
// 代价是同时最多只能显示 6 个状态（MaxIconsPerRow），超出的不显示 ——
// 这是刻意的取舍，屏幕上塞 20 个小图标本来也没人看得清。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>状态图标 HUD。分"我方"和"目标"两行显示。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(210)]
    public sealed class HudStatusIcons : MonoBehaviour
    {
        /// <summary>每行最多显示几个图标（同时也是对象池的容量）。</summary>
        public const int MaxIconsPerRow = 6;

        /// <summary>单个图标边长。</summary>
        public const float IconSize = 34.0f;

        /// <summary>图标间隙。</summary>
        public const float IconGap = 6.0f;

        /// <summary>
        /// 一个图标由哪几个 UI 元件构成。
        /// 把这四个引用打包成一个小类，是为了避免写成四个平行数组
        /// （_bodies[]、_remains[]、_stacks[]……）—— 那样极易下标错位。
        /// 这里条目多且是运行时创建的，用类打包比平行数组安全。
        /// </summary>
        private sealed class IconWidget
        {
            public RectTransform Root;
            public Image Body;      // 底色块，颜色代表状态类型
            public Image Remain;    // 剩余时间遮罩，从上往下退去
            public Text Stacks;     // 层数文字
        }

        /// <summary>一整行图标（含标题）。</summary>
        private sealed class Row
        {
            public RectTransform Root;
            public Text Title;
            public readonly IconWidget[] Icons = new IconWidget[MaxIconsPerRow];
            public int VisibleCount;
        }

        private readonly Row _playerRow = new Row();
        private readonly Row _targetRow = new Row();

        // 【为什么要一个成员级的 _buffer，而不是每次查询都 new 一个 List】
        // Update 每帧调用两次状态查询。若每次都 new List<ActiveStatus>()，
        // 一秒就产生 120 个短命对象丢给 GC。复用同一个 List、每次用前 Clear()，
        // 分配次数降为 0（容量够时连内部数组都不会重新分配）。
        // 初始容量给 8：比常见状态数略大，避免运行中扩容。
        // 这个"复用缓冲区"模式在每帧执行的代码里非常常见，值得形成肌肉记忆。
        private readonly List<ActiveStatus> _buffer = new List<ActiveStatus>(8);

        private CombatBridge _bridge;
        private bool _built;

        public bool IsBuilt
        {
            get { return _built; }
        }

        public int PlayerIconCount
        {
            get { return _playerRow.VisibleCount; }
        }

        public int TargetIconCount
        {
            get { return _targetRow.VisibleCount; }
        }

        public void Bind(CombatBridge bridge)
        {
            _bridge = bridge;
        }

        public void Build(Transform canvasRoot)
        {
            if (_built || canvasRoot == null)
            {
                return;
            }

            BuildRow(_playerRow, "PlayerStatus", canvasRoot, "我方",
                     new Vector2(0.0f, 1.0f), new Vector2(28.0f, -150.0f), TextAnchor.MiddleLeft, 1.0f);

            BuildRow(_targetRow, "TargetStatus", canvasRoot, "目标",
                     new Vector2(0.5f, 1.0f), new Vector2(0.0f, -28.0f), TextAnchor.MiddleRight, 0.0f);

            _built = true;
        }

        private static void BuildRow(Row row, string name, Transform parent, string title,
                                     Vector2 anchor, Vector2 offset, TextAnchor titleAlign, float growRight)
        {
            float width = MaxIconsPerRow * IconSize + (MaxIconsPerRow - 1) * IconGap + 60.0f;

            row.Root = Hud.NewRect(name, parent);
            Hud.Anchor(row.Root, anchor, anchor, new Vector2(anchor.x, 1.0f));
            row.Root.anchoredPosition = offset;
            row.Root.sizeDelta = new Vector2(width, IconSize);

            row.Title = Hud.NewText("Title", row.Root, 16, titleAlign,
                                    new Color(0.82f, 0.86f, 0.80f, 0.88f));
            Hud.Anchor(row.Title.rectTransform, new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f));
            row.Title.rectTransform.anchoredPosition = new Vector2(0.0f, 0.0f);
            row.Title.rectTransform.sizeDelta = new Vector2(52.0f, IconSize);
            row.Title.text = title;

            for (int i = 0; i < MaxIconsPerRow; i++)
            {
                IconWidget w = new IconWidget();

                w.Root = Hud.NewRect("Icon" + i, row.Root);
                Hud.Anchor(w.Root, new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f));
                w.Root.anchoredPosition = new Vector2(56.0f + i * (IconSize + IconGap), 0.0f);
                w.Root.sizeDelta = new Vector2(IconSize, IconSize);

                RectTransform body = Hud.NewImageRect("Body", w.Root, new Color(0.7f, 0.7f, 0.7f, 0.9f));
                Hud.Stretch(body, 0.0f);
                w.Body = body.GetComponent<Image>();

                RectTransform remain = Hud.NewImageRect("Remain", w.Root, new Color(0.03f, 0.03f, 0.04f, 0.60f));
                Hud.Stretch(remain, 2.0f);
                w.Remain = remain.GetComponent<Image>();
                w.Remain.type = Image.Type.Filled;
                w.Remain.fillMethod = Image.FillMethod.Vertical;
                w.Remain.fillOrigin = (int)Image.OriginVertical.Top;
                w.Remain.fillAmount = 0.0f;

                w.Stacks = Hud.NewText("Stacks", w.Root, 15, TextAnchor.LowerRight,
                                       new Color(1.0f, 0.98f, 0.94f, 0.98f));
                Hud.Anchor(w.Stacks.rectTransform, new Vector2(1.0f, 0.0f), new Vector2(1.0f, 0.0f), new Vector2(1.0f, 0.0f));
                w.Stacks.rectTransform.anchoredPosition = new Vector2(-2.0f, 1.0f);
                w.Stacks.rectTransform.sizeDelta = new Vector2(IconSize, 18.0f);

                // 建好即隐藏 —— 这就是对象池的"入池"动作。
                // 开局时玩家身上没有任何状态，6 个图标全部躺在池里待命。
                w.Root.gameObject.SetActive(false);
                row.Icons[i] = w;
            }

            // growRight 为 0 时行内容整体左移半宽，使其相对锚点居中展开。
            if (growRight <= 0.0f)
            {
                row.Root.pivot = new Vector2(0.5f, 1.0f);
            }
        }

        private void Update()
        {
            if (!_built)
            {
                return;
            }

            CombatBridge bridge = ResolveBridge();
            bool live = bridge != null && bridge.IsReady && bridge.T3Enabled;

            if (!live)
            {
                HideRow(_playerRow);
                HideRow(_targetRow);
                return;
            }

            // 注意这里的调用风格：把 _buffer 传进去让对方填充，而不是让对方 return 一个新 List。
            // 这叫"调用方提供缓冲区"，是避免每帧堆分配的标准手法（.NET 里 TryFormat、
            // Unity 里 GetComponents(List<T>) 都是这个模式）。用之前必须 Clear()，否则会越积越多。
            _buffer.Clear();
            bridge.PlayerStatuses(_buffer);
            FillRow(_playerRow, _buffer);

            _buffer.Clear();
            Combatant target = bridge.NearestEnemy();
            if (target != null)
            {
                bridge.StatusesOf(target, _buffer);
            }
            // 即使 target 为 null 也照常调用 FillRow —— 此时 _buffer 是空的，
            // FillRow 会把整行图标都隐藏掉。这比在外面加 if/else 分支更简洁，
            // 且天然处理了"目标刚死掉"时残留图标的清理。
            FillRow(_targetRow, _buffer);
        }

        /// <summary>
        /// 把一批状态填进一行图标里（对象池的"取用"动作）。
        /// </summary>
        private static void FillRow(Row row, List<ActiveStatus> statuses)
        {
            // 取二者较小值：状态数可能超过池容量，超出部分直接不显示。
            int n = statuses.Count < MaxIconsPerRow ? statuses.Count : MaxIconsPerRow;
            row.VisibleCount = n;

            for (int i = 0; i < MaxIconsPerRow; i++)
            {
                IconWidget w = row.Icons[i];
                if (w == null || w.Root == null)
                {
                    continue;
                }

                // 前 n 个显示，其余隐藏。遍历整个池（而不是只遍历前 n 个）
                // 才能把上一帧多出来的图标关掉。
                bool active = i < n;
                // 先比较再赋值：SetActive 即使传入相同的值也会走一遍引擎调用并触发
                // OnEnable/OnDisable 相关流程，不是免费的。加一句判断即可省掉绝大多数无谓调用。
                // 这和 HudSkillBar.RefreshHints 里"变了才写"是同一条优化原则。
                if (w.Root.gameObject.activeSelf != active)
                {
                    w.Root.gameObject.SetActive(active);
                }
                if (!active)
                {
                    continue;
                }

                ActiveStatus s = statuses[i];
                Color tint = VfxStatus.TintFor(s.Def);
                // 复用 VfxStatus 的配色函数，保证"图标颜色"和"角色身上的特效颜色"一致 ——
                // 玩家看到角色泛紫光，就能立刻在图标栏里找到那个紫色图标。
                // 颜色定义只有一处（VfxStatus.TintFor），改一次两边同时生效，不会走样。
                w.Body.color = new Color(tint.r, tint.g, tint.b, 0.92f);

                // 用 1 - 剩余比例 作为遮罩填充量：
                // 状态刚上身时 RemainRatio=1 → 填充 0 → 完全不遮挡（图标最亮）；
                // 快结束时 RemainRatio→0 → 填充→1 → 图标被逐渐盖住。
                // 这个"取反"很容易写反，写反的表现是"图标一开始全黑、快消失时才变亮"。
                w.Remain.fillAmount = 1.0f - Mathf.Clamp01(s.RemainRatio);

                // 只有多层时才显示数字。1 层就写个"1"是视觉噪音，没有信息量。
                w.Stacks.text = s.Stacks > 1 ? s.Stacks.ToString() : string.Empty;
            }

            if (row.Title != null)
            {
                bool showTitle = n > 0;
                if (row.Title.gameObject.activeSelf != showTitle)
                {
                    row.Title.gameObject.SetActive(showTitle);
                }
            }
        }

        private static void HideRow(Row row)
        {
            row.VisibleCount = 0;
            for (int i = 0; i < MaxIconsPerRow; i++)
            {
                IconWidget w = row.Icons[i];
                if (w != null && w.Root != null && w.Root.gameObject.activeSelf)
                {
                    w.Root.gameObject.SetActive(false);
                }
            }
            if (row.Title != null && row.Title.gameObject.activeSelf)
            {
                row.Title.gameObject.SetActive(false);
            }
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
