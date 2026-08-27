// -----------------------------------------------------------------------------
// ItemStack.cs —— 一格的物品堆叠（纯逻辑）
//
// 背包的每一格存一个 ItemStack：同一种物品的若干数量。空格用 ItemId==null 表示。
// 这是「数据」，不含任何行为；行为在 InventoryModel 里。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Inventory
{
    /// <summary>一格的物品堆叠。ItemId 为空或 Count&lt;=0 表示空格。</summary>
    [Serializable]
    public struct ItemStack
    {
        /// <summary>物品 id（对应 ItemDefinition.ItemId）。</summary>
        public string ItemId;

        /// <summary>数量。</summary>
        public int Count;

        /// <summary>构造。</summary>
        public ItemStack(string itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        /// <summary>是否为空格。</summary>
        public bool IsEmpty
        {
            get { return string.IsNullOrEmpty(ItemId) || Count <= 0; }
        }
    }
}
