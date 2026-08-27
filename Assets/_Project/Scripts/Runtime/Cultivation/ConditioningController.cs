// -----------------------------------------------------------------------------
// ConditioningController.cs —— 调理状态与现有材料背包之间的 Unity 适配层
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 负责“用材料启用一种调理”。不结算技能、不写战斗帧；CombatBridge 以后只读取
    /// <see cref="ActiveKind"/> 来获取规则层已经定义好的修正值。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConditioningController : MonoBehaviour
    {
        private PlayerInventory _inventory;

        /// <summary>当前局正在生效的唯一调理；初始为未调理。</summary>
        public ConditioningKind ActiveKind { get; private set; } = ConditioningKind.None;

        /// <summary>成功切换时通知 HUD 刷新；载荷为新状态。</summary>
        public event Action<ConditioningKind> Activated;

        private void Awake()
        {
            _inventory = GetComponent<PlayerInventory>();
        }

        /// <summary>
        /// 尝试消耗一份配方并启用调理。材料不足时不扣任何材料，也不改变当前状态。
        /// </summary>
        public bool TryActivate(ConditioningKind kind)
        {
            if (kind == ConditioningKind.None)
            {
                ActiveKind = ConditioningKind.None;
                Activated?.Invoke(ActiveKind);
                return true;
            }

            PlayerInventory inventory = ResolveInventory();
            if (inventory == null)
            {
                return false;
            }

            ConditioningRecipe recipe = ConditioningRules.GetRecipe(kind);
            int wood = inventory.Count(PlayerInventory.BambooWood);
            int shoot = inventory.Count(PlayerInventory.BambooShoot);
            if (!ConditioningRules.CanCraft(wood, shoot, recipe))
            {
                return false;
            }

            // 同一帧、同一主线程的预检后连续扣除，不会与其他协程并发争用；两个 Consume
            // 失败均说明场景中存在越过背包 API 的外部改写，因此保守地不激活状态。
            if (!inventory.Consume(PlayerInventory.BambooWood, recipe.BambooWoodCost)
                || !inventory.Consume(PlayerInventory.BambooShoot, recipe.BambooShootCost))
            {
                return false;
            }

            ActiveKind = kind;
            Activated?.Invoke(ActiveKind);
            return true;
        }

        private PlayerInventory ResolveInventory()
        {
            if (_inventory == null)
            {
                _inventory = GetComponent<PlayerInventory>();
            }
            return _inventory;
        }
    }
}
