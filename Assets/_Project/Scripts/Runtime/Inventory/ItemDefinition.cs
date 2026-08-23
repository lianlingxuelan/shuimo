// -----------------------------------------------------------------------------
// ItemDefinition.cs —— 物品静态定义（ScriptableObject，Unity 侧）
//
// 【为什么用 ScriptableObject 而不是写死在代码里】
// 跟战斗技能（SkillDef）同理：物品清单是「数据」，应该能在编辑器里增删改，
// 而不用改代码、不用重新编译。你以后想加「朱果」「玄铁」之类，
// 只要在 Project 窗口右键 → Shuimo → Item Definition，填名字 / 类型 / 堆叠上限即可。
//
// 【占位 Tint】本工程走「零美术资源」路线，没有图标贴图。Tint 是一格的底色，
// 让不同物品在格子里至少能用颜色区分；将来有图标了再换 Sprite 字段。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Inventory;

namespace Xianxia.Unity.T2
{
    /// <summary>物品静态定义（可在编辑器创建 .asset）。</summary>
    [CreateAssetMenu(fileName = "ItemDef", menuName = "Shuimo/Item Definition", order = 1)]
    public sealed class ItemDefinition : ScriptableObject
    {
        /// <summary>物品 id（唯一，背包按它归类堆叠）。</summary>
        public string ItemId = string.Empty;

        /// <summary>中文显示名。</summary>
        public string DisplayName = string.Empty;

        /// <summary>物品大类。</summary>
        public ItemType Type = ItemType.Material;

        /// <summary>单格最大堆叠数。</summary>
        public int MaxStack = 99;

        /// <summary>描述（图鉴 / tooltip 用）。</summary>
        [TextArea(2, 4)]
        public string Description = string.Empty;

        /// <summary>占位列底色（无图标时的视觉区分）。</summary>
        public Color Tint = new Color(0.80f, 0.80f, 0.80f, 1.0f);
    }
}
