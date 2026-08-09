// -----------------------------------------------------------------------------
// EnemySpawner.cs —— 敌人生成与落点校验（asmdef: Xianxia.Unity.T2）
//
// 【它不自己造怪】
// 数值全部由既有的 DifficultyBridge 经 CombatController.SpawnWave 生成——
// 那条路径已经被 T1 的 63 项自检和 NUnit 覆盖过，重写一遍等于把验收过的
// 精英/词缀/等级 jitter 抽取顺序再赌一次。本文件只做两件 Unity 侧的补充：
//   1. 落点校验（架构文档 U2）：把落在水/岩石上的怪挪到最近的可站立格
//   2. 占位外观：按 kind / 精英上色与缩放
//
// 【为什么落点校验放在生成之后而不是之中】
// SpawnWave 内部的圆环采样消耗随机流。若在采样时循环重试直到落点合法，
// 消耗的随机数个数就依赖地形，同一个种子换一版地形算法就长出不同的怪群。
// 先按固定次数抽完、再用**不消耗随机流**的确定性螺旋搜索修正，
// 随机流的形状就与地形彻底解耦了。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>敌人生成器。挂在 Combat 节点上，由 <see cref="CombatBridge"/> 调用。</summary>
    [DisallowMultipleComponent]
    public sealed class EnemySpawner : MonoBehaviour
    {
        /// <summary>血煞占位色（暗红）。</summary>
        public static readonly Color BloodColor = new Color(0.62f, 0.16f, 0.18f, 1.0f);

        /// <summary>巫蛊占位色（紫）。</summary>
        public static readonly Color WitchColor = new Color(0.55f, 0.32f, 0.72f, 1.0f);

        /// <summary>剑修占位色（青灰）。</summary>
        public static readonly Color SwordColor = new Color(0.60f, 0.68f, 0.72f, 1.0f);

        /// <summary>丹修占位色（土黄）。</summary>
        public static readonly Color AlchemyColor = new Color(0.76f, 0.62f, 0.30f, 1.0f);

        /// <summary>精英体型放大倍率。</summary>
        public const float EliteScale = 1.45f;

        /// <summary>精英描边宽度（贴图像素）。</summary>
        public const float EliteOutlinePx = 5.0f;

        private CombatController _ctrl;

        /// <summary>本波实际生成的敌人（按生成顺序），供 <see cref="DeterminismDump"/> 导出。</summary>
        public List<Combatant> LastWave { get; private set; }

        private void Awake()
        {
            _ctrl = GetComponent<CombatController>();
            LastWave = new List<Combatant>(8);
        }

        /// <summary>
        /// 生成首波敌人。数量取自 zones.json 的 <c>enemies.count</c>（zone_youhuang = 6）。
        /// </summary>
        /// <param name="center">波次中心，通常是玩家出生点。</param>
        public void SpawnInitialWave(Vector2 center)
        {
            if (_ctrl == null || _ctrl.Encounter == null)
            {
                Debug.LogError("[T2] EnemySpawner 找不到已初始化的 CombatController，本波取消。");
                return;
            }

            // 记录生成前的敌人集合，用来精确捞出"本波新增"的那些。
            HashSet<int> before = new HashSet<int>();
            List<Combatant> all = _ctrl.Encounter.Combatants;
            for (int i = 0; i < all.Count; i++)
            {
                before.Add(all[i].Id);
            }

            _ctrl.SpawnWave(center, WorldBuilder.SpawnRingMin, WorldBuilder.SpawnRingMax);

            LastWave.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                Combatant c = all[i];
                if (!c.IsEnemy || before.Contains(c.Id))
                {
                    continue;
                }
                LastWave.Add(c);
                RelocateIfBlocked(c);
                Dress(c);
            }

            int elites = 0;
            for (int i = 0; i < LastWave.Count; i++)
            {
                if (LastWave[i].IsElite)
                {
                    elites++;
                }
            }
            Debug.Log(string.Format("[T2] 首波生成 {0} 只敌人（其中精英 {1} 只），落点已按地形校验。",
                LastWave.Count, elites));
        }

        /// <summary>
        /// 落点校验（架构文档 U2）：判定依据是**生成后的 Tilemap 格子类型**，
        /// 不是 zones.json 的 operators —— P0 简化地形下后者压根没参与生成。
        /// </summary>
        private void RelocateIfBlocked(Combatant c)
        {
            Vector2 pos = new Vector2(c.Position.X, c.Position.Y);

            // 先把落点夹回地图内。圆环采样可能把怪甩到地图外，
            // 那里 KindAt 返回 Rock，直接螺旋搜索会从一个很远的地方往回找，很浪费。
            float half = WorldBuilder.TileUnit * 0.5f;
            pos.x = Mathf.Clamp(pos.x, half, WorldBuilder.WorldWidth - half);
            pos.y = Mathf.Clamp(pos.y, half, WorldBuilder.WorldHeight - half);

            if (!WorldBuilder.IsWalkableWorld(pos))
            {
                Vector2Int t = WorldBuilder.WorldToTile(pos);
                pos = WorldBuilder.FindSpawnableNear(t.x, t.y);
            }

            c.Position = new Vec2(pos.x, pos.y);
            if (c.AI != null)
            {
                // SpawnPos 是巡逻锚点。不同步更新会让怪一出生就往老落点（水里）跑。
                c.AI.SpawnPos = c.Position;
            }

            CombatView view = _ctrl.FindView(c.Id);
            if (view != null)
            {
                view.Bind(c);   // Bind 会把 Transform 对齐到内核位置
            }
        }

        /// <summary>按 kind / 精英给占位圆上色与缩放，然后激活视图。</summary>
        private void Dress(Combatant c)
        {
            CombatView view = _ctrl.FindView(c.Id);
            if (view == null)
            {
                return;
            }

            GameObject go = view.gameObject;
            SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                Color body = ColorOf(c.Kind);
                if (c.IsElite)
                {
                    // 精英：金色描边 + 放大。两个信号叠加，玩家扫一眼就能挑出优先目标。
                    sr.sprite = SpriteFactory.Circle("enemy_elite_" + c.Kind, body,
                                                     new Color(0.95f, 0.82f, 0.35f, 1.0f), EliteOutlinePx);
                    go.transform.localScale = Vector3.one * EliteScale;
                }
                else
                {
                    sr.sprite = SpriteFactory.Circle("enemy_" + c.Kind, body, new Color(0, 0, 0, 0), 0.0f);
                    go.transform.localScale = Vector3.one;
                }
            }

            // 模板是未激活的，副本也未激活；上完色才放出来，避免闪一帧白圆。
            if (!go.activeSelf)
            {
                go.SetActive(true);
            }
        }

        /// <summary>kind ⇒ 占位色。未知 kind 回落到巫蛊紫，与内核的 kind 回落口径一致。</summary>
        public static Color ColorOf(string kind)
        {
            if (kind == CombatConfig.KIND_BLOOD)
            {
                return BloodColor;
            }
            if (kind == CombatConfig.KIND_SWORD)
            {
                return SwordColor;
            }
            if (kind == CombatConfig.KIND_ALCHEMY)
            {
                return AlchemyColor;
            }
            return WitchColor;
        }
    }
}
