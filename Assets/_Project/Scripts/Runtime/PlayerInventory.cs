// -----------------------------------------------------------------------------
// PlayerInventory.cs —— 极简玩家材料背包（砍竹内容闭环）
//
// 【职责】记录玩家采集到的材料数量（竹材 / 嫩笋等）。纯数据，不碰战斗内核
// （红线：内核推进权唯一）。供 BambooSceneContext 掉落写入、InventoryHud 读取显示。
//
// 【为什么是单例】砍竹在 BambooSceneContext 里触发，HUD 在另一个对象里读取，
// 单例让任意位置都能直接访问，无需层层传引用。玩家重建时 Instance 重新绑定。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>极简玩家材料背包。第一个内容级背包，后续可扩展为多槽位/多类型。</summary>
    [DefaultExecutionOrder(0)]
    public sealed class PlayerInventory : MonoBehaviour
    {
        public static PlayerInventory Instance { get; private set; }

        /// <summary>竹材（砍断竹子掉落）。</summary>
        public const string BambooWood = "bamboo_wood";

        /// <summary>嫩笋（小概率额外掉落）。</summary>
        public const string BambooShoot = "bamboo_shoot";

        private readonly Dictionary<string, int> _materials = new Dictionary<string, int>();

        private void Awake()
        {
            // 抢占式单例：若已有旧实例（通常是上一局延迟销毁中的残留），
            // 本实例抢占为权威并销毁旧实例，避免重开时新玩家被旧单例判定「重复」而自杀。
            if (Instance != null && Instance != this)
            {
                if (Instance.gameObject != null)
                {
                    Destroy(Instance.gameObject);
                }
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>增加材料数量。</summary>
        public void AddMaterial(string id, int amount)
        {
            if (string.IsNullOrEmpty(id) || amount <= 0)
            {
                return;
            }
            if (_materials.ContainsKey(id))
            {
                _materials[id] += amount;
            }
            else
            {
                _materials[id] = amount;
            }
        }

        /// <summary>查询材料数量（无则 0）。</summary>
        public int Count(string id)
        {
            return _materials.TryGetValue(id, out int v) ? v : 0;
        }

        /// <summary>尝试消耗材料（足够则返回 true 并扣除，不足则返回 false 不动）。</summary>
        public bool Consume(string id, int amount)
        {
            if (!_materials.TryGetValue(id, out int v) || v < amount)
            {
                return false;
            }
            v -= amount;
            if (v <= 0)
            {
                _materials.Remove(id);
            }
            else
            {
                _materials[id] = v;
            }
            return true;
        }
    }
}
