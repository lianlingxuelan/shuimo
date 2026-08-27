// -----------------------------------------------------------------------------
// InventoryModel.cs —— 格子背包的数据模型（纯逻辑，不碰 UnityEngine）
//
// 【为什么是纯逻辑】背包的「加物品 / 减物品 / 交换格子」本质是数组操作，
// 与 Unity 的 GameObject、UI 毫无关系。把它放在 Xianxia.Inventory（noEngineReferences），
// 就能脱离引擎写 NUnit 单测（像 Xianxia.Combat 那样）。
//
// 【容量与堆叠】固定容量（格数）。每个物品有各自的最大堆叠数（来自 ItemDefinition），
// 加物品时先填已有同类、再填空槽，返回「没放下的数量」。
//
// 【事件】SlotChanged 在任一格变化后抛出（传入变化的格下标），UI 层订阅它做增量刷新，
// 而不是每帧全量重画。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Xianxia.Inventory
{
    /// <summary>格子背包的数据模型（纯逻辑）。</summary>
    public sealed class InventoryModel
    {
        private readonly ItemStack[] _slots;
        private readonly Dictionary<string, int> _maxStack = new Dictionary<string, int>();

        /// <summary>总格数。</summary>
        public int Capacity { get; }

        /// <summary>某格内容变化时抛出（参数为变化的格下标）。</summary>
        public event Action<int> SlotChanged;

        /// <summary>构造。</summary>
        /// <param name="capacity">格数（&gt;0）。</param>
        public InventoryModel(int capacity)
        {
            Capacity = capacity > 0 ? capacity : 1;
            _slots = new ItemStack[Capacity];
        }

        /// <summary>取某格内容（越界返回空堆叠）。</summary>
        public ItemStack Get(int index)
        {
            return index >= 0 && index < Capacity ? _slots[index] : default(ItemStack);
        }

        /// <summary>该格是否为空。</summary>
        public bool IsEmpty(int index)
        {
            return Get(index).IsEmpty;
        }

        /// <summary>注册某物品的最大堆叠数（来自 ItemDefinition.MaxStack）。</summary>
        public void RegisterMaxStack(string itemId, int max)
        {
            if (!string.IsNullOrEmpty(itemId) && max > 0)
            {
                _maxStack[itemId] = max;
            }
        }

        private int MaxOf(string itemId)
        {
            return _maxStack.TryGetValue(itemId, out int m) ? m : 99;
        }

        /// <summary>
        /// 加入 count 个 itemId 物品。先填已有同类堆叠，再填空槽。
        /// </summary>
        /// <returns>未能放入的剩余数量（0 表示全部放下）。</returns>
        public int Add(string itemId, int count)
        {
            if (string.IsNullOrEmpty(itemId) || count <= 0)
            {
                return count;
            }
            int max = MaxOf(itemId);
            for (int i = 0; i < Capacity && count > 0; i++)
            {
                if (!_slots[i].IsEmpty && _slots[i].ItemId == itemId)
                {
                    int space = max - _slots[i].Count;
                    if (space > 0)
                    {
                        int put = space < count ? space : count;
                        ItemStack s = _slots[i];
                        s.Count += put;
                        _slots[i] = s;
                        count -= put;
                        SlotChanged?.Invoke(i);
                    }
                }
            }
            for (int i = 0; i < Capacity && count > 0; i++)
            {
                if (_slots[i].IsEmpty)
                {
                    int put = max < count ? max : count;
                    _slots[i] = new ItemStack(itemId, put);
                    count -= put;
                    SlotChanged?.Invoke(i);
                }
            }
            return count;
        }

        /// <summary>从某格移除 count 个，返回实际移除数。</summary>
        public int RemoveAt(int index, int count)
        {
            if (index < 0 || index >= Capacity)
            {
                return 0;
            }
            ItemStack s = _slots[index];
            if (s.IsEmpty)
            {
                return 0;
            }
            int take = s.Count < count ? s.Count : count;
            s.Count -= take;
            _slots[index] = s.Count <= 0 ? default(ItemStack) : s;
            SlotChanged?.Invoke(index);
            return take;
        }

        /// <summary>交换两格内容（拖拽整理用）。</summary>
        public void Swap(int a, int b)
        {
            if (a < 0 || a >= Capacity || b < 0 || b >= Capacity || a == b)
            {
                return;
            }
            ItemStack t = _slots[a];
            _slots[a] = _slots[b];
            _slots[b] = t;
            SlotChanged?.Invoke(a);
            SlotChanged?.Invoke(b);
        }

        /// <summary>统计某物品总持有量。</summary>
        public int TotalOf(string itemId)
        {
            int n = 0;
            for (int i = 0; i < Capacity; i++)
            {
                if (_slots[i].ItemId == itemId)
                {
                    n += _slots[i].Count;
                }
            }
            return n;
        }
    }
}
