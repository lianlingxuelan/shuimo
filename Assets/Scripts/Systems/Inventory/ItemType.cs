// -----------------------------------------------------------------------------
// ItemType.cs —— 物品大类（纯逻辑，Xianxia.Inventory）
//
// 数据驱动：物品类型只是个枚举标签，真正的内容（有哪些物品、各自数值）由
// ItemDefinition（ScriptableObject）在 Unity 侧定义，本层不持有任何具体内容。
// 禁止引用 UnityEngine。
// -----------------------------------------------------------------------------

namespace Xianxia.Inventory
{
    /// <summary>物品大类。决定它在背包里的归类与默认交互方式。</summary>
    public enum ItemType
    {
        /// <summary>材料：竹材、嫩笋、矿石等，可堆叠。</summary>
        Material = 0,

        /// <summary>消耗品：丹药、灵果，使用即减少。</summary>
        Consumable = 1,

        /// <summary>关键道具：任务 / 剧情相关，通常不堆叠。</summary>
        Key = 2,

        /// <summary>杂项：暂未分类。</summary>
        Misc = 3
    }
}
