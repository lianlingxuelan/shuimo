using System;
using UnityEngine;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>View-only 5x4 bag grid. It maps existing inventory material ids to stable visual slots.</summary>
    public sealed class InkInventoryGrid
    {
        public const int Columns = 5;
        public const int Rows = 4;
        private const int Capacity = Columns * Rows;

        private readonly Text[] _labels = new Text[Capacity];
        private readonly Image[] _slots = new Image[Capacity];
        private readonly Action<string> _onSelected;

        public InkInventoryGrid(Transform parent, Action<string> onSelected)
        {
            _onSelected = onSelected;
            Build(parent);
        }

        public string SelectedItemId { get; private set; }

        public static int SlotFor(string itemId)
        {
            if (itemId == PlayerInventory.BambooWood) return 0;
            if (itemId == PlayerInventory.BambooShoot) return 1;
            return -1;
        }

        public void Refresh(PlayerInventory inventory)
        {
            int wood = inventory != null ? inventory.Count(PlayerInventory.BambooWood) : 0;
            int shoot = inventory != null ? inventory.Count(PlayerInventory.BambooShoot) : 0;
            SetSlot(0, "竹材", wood);
            SetSlot(1, "嫩笋", shoot);
            for (int i = 2; i < Capacity; i++)
            {
                _labels[i].text = string.Empty;
            }
        }

        private void Build(Transform parent)
        {
            RectTransform root = Hud.NewRect("InkInventoryGrid", parent);
            Hud.Anchor(root, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            root.anchoredPosition = new Vector2(40.0f, -135.0f);
            root.sizeDelta = new Vector2(388.0f, 320.0f);

            for (int index = 0; index < Capacity; index++)
            {
                int capturedIndex = index;
                int row = index / Columns;
                int column = index % Columns;
                Image slot = InkUiFactory.CreatePanel("InkInventorySlot" + index, root, new Vector2(68.0f, 68.0f));
                InkUiFactory.ApplyPanelStyle(slot, InkUiTheme.CreateFallbackSprite(), InkUiTheme.Paper);
                slot.rectTransform.anchoredPosition = new Vector2(column * 76.0f, -row * 78.0f);
                Outline hairline = slot.gameObject.AddComponent<Outline>();
                hairline.effectColor = new Color(InkUiTheme.Ink.r, InkUiTheme.Ink.g, InkUiTheme.Ink.b, 0.32f);
                hairline.effectDistance = new Vector2(1.0f, -1.0f);
                Button button = slot.gameObject.AddComponent<Button>();
                button.targetGraphic = slot;
                button.onClick.AddListener(() => SelectSlot(capturedIndex));

                Text label = InkUiFactory.CreateText("InkInventorySlotLabel" + index, slot.transform, 14, TextAnchor.MiddleCenter, InkUiTheme.Ink);
                Hud.Stretch(label.rectTransform, 7.0f);
                _labels[index] = label;
                _slots[index] = slot;
            }
        }

        private void SelectSlot(int index)
        {
            string itemId = index == 0 ? PlayerInventory.BambooWood
                : index == 1 ? PlayerInventory.BambooShoot : string.Empty;
            if (string.IsNullOrEmpty(itemId)) return;
            SelectedItemId = itemId;
            if (_onSelected != null)
            {
                _onSelected(itemId);
            }
        }

        private void SetSlot(int index, string label, int count)
        {
            bool decorated = InkUiDensityRules.ShouldShowDecorativeSlotFrame(count);
            InkUiFactory.ApplyPanelStyle(
                _slots[index],
                decorated ? InkUiTheme.LoadOptionalSprite("ink-grid-slot-v1") : InkUiTheme.CreateFallbackSprite(),
                decorated ? Color.white : InkUiTheme.Paper);
            Outline hairline = _slots[index].GetComponent<Outline>();
            if (hairline != null) hairline.enabled = !decorated;
            _labels[index].text = count > 0 ? label + "\n×" + count : label + "\n—";
        }
    }
}
