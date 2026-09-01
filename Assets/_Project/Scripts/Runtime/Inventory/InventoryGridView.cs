// -----------------------------------------------------------------------------
// InventoryGridView.cs —— 格子背包的运行时 UI（多槽位网格，类似鬼谷八荒那种）
//
// 【纯展示，不持有数据】它只负责「把 InventoryModel 画出来」，
// 不决定加什么物品、能叠多少——那些在 InventoryModel / ItemDefinition 里。
// 订阅 model.SlotChanged 做增量刷新（哪一格变了刷哪一格），不每帧全量重画。
//
// 【为什么用 GridLayoutGroup】UGUI 的网格布局组件，设定列数 / 格子尺寸 / 间距后，
// 子物体自动排成网格。它等价于 CSS Grid（display:grid；grid-template-columns）。
// 前端同学可以把「加一个 slot = append 一个 div 进 grid 容器」直接对应过来。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Xianxia.Inventory;

namespace Xianxia.Unity.T2
{
    /// <summary>格子背包网格视图（读模型、画格子）。</summary>
    public sealed class InventoryGridView
    {
        private readonly InventoryModel _model;
        private readonly Dictionary<string, ItemDefinition> _defs;
        private readonly Image[] _slotImgs;
        private readonly Text[] _countTexts;
        private readonly int _capacity;

        private static readonly Color EmptyTint = new Color(0.06f, 0.07f, 0.06f, 0.70f);

        /// <summary>构建网格 UI。</summary>
        /// <param name="canvas">父 Canvas 变换。</param>
        /// <param name="model">背包数据模型。</param>
        /// <param name="defs">id → 物品定义 的查表（用于取名字 / 颜色）。</param>
        /// <param name="columns">网格列数。</param>
        public InventoryGridView(Transform canvas, InventoryModel model,
                                 IReadOnlyDictionary<string, ItemDefinition> defs, int columns)
        {
            _model = model;
            _defs = new Dictionary<string, ItemDefinition>(defs);
            _capacity = model.Capacity;

            RectTransform root = Hud.NewRect("InventoryGrid", canvas);
            Hud.Anchor(root, new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f));
            root.anchoredPosition = new Vector2(24.0f, 24.0f);

            GridLayoutGroup gl = root.gameObject.AddComponent<GridLayoutGroup>();
            gl.cellSize = new Vector2(64.0f, 64.0f);
            gl.spacing = new Vector2(8.0f, 8.0f);
            gl.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gl.constraintCount = columns;
            gl.childAlignment = TextAnchor.UpperLeft;

            _slotImgs = new Image[_capacity];
            _countTexts = new Text[_capacity];
            for (int i = 0; i < _capacity; i++)
            {
                RectTransform cell = Hud.NewImageRect("Slot" + i, root, EmptyTint);
                _slotImgs[i] = cell.GetComponent<Image>();

                Text cnt = Hud.NewText("Cnt" + i, cell, 16, TextAnchor.LowerRight,
                                       new Color(0.96f, 0.98f, 0.96f, 0.96f));
                Hud.Stretch(cnt.rectTransform, 2.0f);
                _countTexts[i] = cnt;
            }

            _model.SlotChanged += OnSlotChanged;
            RefreshAll();
        }

        private void OnSlotChanged(int index)
        {
            RefreshSlot(index);
        }

        private void RefreshAll()
        {
            for (int i = 0; i < _capacity; i++)
            {
                RefreshSlot(i);
            }
        }

        private void RefreshSlot(int i)
        {
            if (i < 0 || i >= _capacity)
            {
                return;
            }
            ItemStack s = _model.Get(i);
            if (_slotImgs[i] == null)
            {
                return;
            }
            if (s.IsEmpty)
            {
                _slotImgs[i].color = EmptyTint;
                if (_countTexts[i] != null)
                {
                    _countTexts[i].text = string.Empty;
                }
                return;
            }
            ItemDefinition def = _defs.TryGetValue(s.ItemId, out ItemDefinition d) ? d : null;
            _slotImgs[i].color = def != null ? def.Tint : new Color(0.70f, 0.70f, 0.70f, 1.0f);
            if (_countTexts[i] != null)
            {
                _countTexts[i].text = s.Count > 1 ? s.Count.ToString() : string.Empty;
            }
        }
    }
}
