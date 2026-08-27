// -----------------------------------------------------------------------------
// 2.5D/EnemyNpcSpawner.cs —— 敌人 / NPC 确定性撒点 + 多区域 + 巡逻（feature/2.5d，轮次 C + 地图升级选项A）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过。
//
// 【职责】在竹林场景内确定性撒点：
//   - 竹林小怪：复用 boss/witch/elder/musician 缩放思路，默认 18 只；
//   - NPC 标记点：可对话/可交互，先占位（InteractableMarker），不战斗、不 harvest；
//   - 多区域（选项A）：config.regions 非空时按区域分别撒点，每个区域独立种子、独立巡逻圈；
//     不配 regions 时退回「整体方形域」原行为（向后兼容）。
//   - 每个敌人在其所属区域圆心+半径内做巡逻（EnemyPatrol），驱动 Walk/Idle + 朝向。
// 复用 ZoneSeed.CreateRng → PCG32（与竹林撒点同源种子纪律，可复现）。
// 外观套 Ink 材质（Shader.Find 回退），深度排序复用 DepthSortUtility，
// 小怪 harvestable 集暴露给 BambooSceneContext.DetectHarvest 复用同一扇形（零内核改动）。
//
// 【红线】
//   1. 不写 Time.timeScale、不写 FeedbackClock.Frozen、不引用 CombatScheduler /
//      RunPhase / DamageResolver / RequestHitstop / KickHitstop。
//   2. 不改动任何内核类型；只读取 PlayerController / AttackController / CombatBridge /
//      BambooSceneContext 等既有引用。
//   3. 动画推进一律走 CharacterView（其内部只读 FeedbackClock.Delta）。
//   4. 巡逻推进由本组件在 !IsGameplayBlocked 帧、用 FeedbackClock.Delta 驱动；
//      本类与 EnemyPatrol 都不自行读 Time.deltaTime。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Xianxia.Core; // ZoneSeed, PCG32
using Xianxia.Combat.UnityBridge; // FeedbackClock

namespace Xianxia.Unity.T2
{
    /// <summary>敌人原型类别（复用 2D explore 的 4 类缩放思路）。</summary>
    public enum EnemyKind
    {
        /// <summary>精英大怪（缩放最大）。</summary>
        Boss,

        /// <summary>女巫。</summary>
        Witch,

        /// <summary>长者。</summary>
        Elder,

        /// <summary>乐师。</summary>
        Musician,

        /// <summary>商店导入角色候选（Char_Feng，3D 模型 + Feng.controller）。
        /// 仅作为「新增候选」，绝不替换/删除既有 boss/witch/elder/musician 占位。</summary>
        Feng,
    }

    /// <summary>单个敌人原型表项（ScriptableObject 内序列化）。</summary>
    [System.Serializable]
    public sealed class EnemyArchetypeEntry
    {
        /// <summary>原型类别。</summary>
        [Tooltip("原型类别")]
        public EnemyKind kind = EnemyKind.Boss;

        /// <summary>该原型数量。</summary>
        [Tooltip("该原型数量")]
        public int count = 1;

        /// <summary>相对缩放（&lt;=0 时按类别取默认：Boss1.6/Witch0.9/Elder1.1/Musician1.0）。</summary>
        [Tooltip("相对缩放（<=0 取类别默认）")]
        public float scale = 1.0f;

        /// <summary>可选模型 prefab（缺则 primitives + Ink 材质兜底）。</summary>
        [Tooltip("可选模型 prefab（缺则 primitives + Ink 材质）")]
        public GameObject prefab;

        /// <summary>该原型能否被扇形 harvest 命中。</summary>
        [Tooltip("该原型能否被扇形 harvest 命中")]
        public bool harvestable = true;

        /// <summary>该原型材质覆盖（缺则取 config 全局）。</summary>
        [Tooltip("该原型材质覆盖（缺则取 config 全局）")]
        public Material inkMaterialOverride;
    }

    /// <summary>撒点区域（选项A「多区域」的空间分区单元）。</summary>
    [System.Serializable]
    public sealed class SpawnRegion
    {
        /// <summary>区域 id（用于确定性种子派生）。</summary>
        [Tooltip("区域 id（用于确定性种子派生）")]
        public string id = "region";

        /// <summary>区域圆心（世界 XY）。</summary>
        [Tooltip("区域圆心（世界 XY）")]
        public Vector2 center;

        /// <summary>区域半径（世界单位，巡逻圈同此值）。</summary>
        [Tooltip("区域半径（世界单位，巡逻圈同此值）")]
        public float radius = 400.0f;

        /// <summary>该区域敌人数（原型类别复用 config.archetypes）。</summary>
        [Tooltip("该区域敌人数（原型类别复用 config.archetypes）")]
        public int enemyCount = 6;

        /// <summary>该区域 NPC 数。</summary>
        [Tooltip("该区域 NPC 数")]
        public int npcCount = 1;
    }

    /// <summary>敌人撒点配置载体（ScriptableObject，集中承载全部可配字段）。</summary>
    /// <remarks>
    /// 本运行时脚本归属于 Xianxia.Unity.T2（非 Editor 程序集），不能携带
    /// UnityEditor.CreateAssetMenu（否则非 Editor 平台编译失败）。请在编辑器内通过
    /// 其它方式创建该资产，或后续补一个 Editor 菜单脚本来实例化它。
    /// </remarks>
    public sealed class EnemyNpcSpawnConfig : ScriptableObject
    {
        [Header("种子 / 撒点区")]
        [Tooltip("确定性种子（走 ZoneSeed.CreateRng，与竹林同源纪律）")]
        public string zoneSeedId = "zone_bamboo_enemies_2_5d";

        [Tooltip("整体方形撒点区半边长（XY 正方形，与 BambooSceneContext.groveHalfExtent 同尺度）。仅在 regions 为空时生效。")]
        public float spawnAreaHalfExtent = 1400.0f;

        [Tooltip("同类/同物体最小间距（泊松拒绝采样）")]
        public float minDist = 160.0f;

        [Header("小怪原型表")]
        [Tooltip("Boss/Witch/Elder/Musician 各 1，count 分别为 2/5/6/5 ≈ 18")]
        public List<EnemyArchetypeEntry> archetypes = new List<EnemyArchetypeEntry>
        {
            new EnemyArchetypeEntry { kind = EnemyKind.Boss, count = 2, scale = 1.6f },
            new EnemyArchetypeEntry { kind = EnemyKind.Witch, count = 5, scale = 0.9f },
            new EnemyArchetypeEntry { kind = EnemyKind.Elder, count = 6, scale = 1.1f },
            new EnemyArchetypeEntry { kind = EnemyKind.Musician, count = 5, scale = 1.0f },
        };

        [Header("NPC 标记")]
        [Tooltip("无 regions 时的整体 NPC 数量（不战斗、不 harvest）")]
        public int npcMarkerCount = 4;

        [Header("多区域（选项A）")]
        [Tooltip("空间分区：非空时按区域分别撒点（各自独立种子+巡逻圈）。空则退回整体方形域。")]
        public List<SpawnRegion> regions = new List<SpawnRegion>();

        [Header("外观 / 排序 / 交互")]
        [Tooltip("整体缩放基准")]
        public float globalScaleRef = 1.0f;

        [Tooltip("小怪躯干材质（缺则运行时按 Xianxia/Ink/BambooTrunk 建）")]
        public Material inkMaterialTrunk;

        [Tooltip("小怪叶片/装饰材质（缺则运行时按 Xianxia/Ink/BambooLeaf 建）")]
        public Material inkMaterialLeaf;

        [Tooltip("是否参与深度排序")]
        public bool depthSortEnabled = true;

        [Tooltip("Y→深度轴强度（与 BSC 同义）")]
        public float ySortToDepth = 0.5f;

        [Tooltip("小怪默认可被扇形 harvest 命中")]
        public bool harvestableByDefault = true;
    }

    /// <summary>占位 NPC 交互标记（先无实质内容，留待后续填充对话/交互数据）。</summary>
    [DisallowMultipleComponent]
    public sealed class InteractableMarker : MonoBehaviour
    {
        [Tooltip("标记 id（后续对话/交互系统使用）")]
        public string markerId;

        [Tooltip("展示名")]
        public string displayName;
    }

    /// <summary>
    /// 敌人 / NPC 确定性撒点器 + 多区域 + 巡逻（地图升级 选项A）。
    /// 生成 + 挂 CharacterView（Idle 占位）+ 注册深度排序集 + 暴露 harvestable 集给 BSC；
    /// 每个敌人挂 EnemyPatrol（区域圆心/半径/种子），由本组件每帧在 !blocked 时驱动 Tick。
    /// NPC 仅生成 + Idle + InteractableMarker，不进 harvest 集。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyNpcSpawner : MonoBehaviour
    {
        [Header("配置 / 上下文")]
        [Tooltip("撒点配置（ScriptableObject）。留空则不撒点。")]
        public EnemyNpcSpawnConfig config;

        [Tooltip("竹林场景上下文（提供 DepthAxis 与 harvest 命中集；留空则运行时惰性解析）")]
        public BambooSceneContext context;

        [Tooltip("生成根的父节点（留空则用本对象自身）")]
        public Transform spawnParent;

        [Header("商店角色候选（Char_Feng，纯新增，不碰既有 4 类占位）")]
        [Tooltip("是否额外生成 Char_Feng 角色候选（从 Resources 加载 FengEnemy prefab）。")]
        public bool spawnFengStoreEnemy = true;

        [Tooltip("Char_Feng 候选数量。")]
        public int fengStoreEnemyCount = 2;

        [Tooltip("Char_Feng prefab 的 Resources 路径（由 Shuimo/Store/Generate Prefabs 生成）。")]
        public string fengStoreEnemyPath = "Enemies/FengEnemy";

        private readonly List<Transform> _spawnedRoots = new List<Transform>();
        private readonly List<CharacterView> _views = new List<CharacterView>();
        private readonly List<EnemyPatrol> _patrols = new List<EnemyPatrol>();
        private readonly List<Material> _ownedMaterials = new List<Material>();

        private CombatBridge _bridge;
        private bool _spawned;
        private Vector3 _depthAxis = new Vector3(0.0f, 0.0f, -1.0f);

        // =====================================================================
        // 生命周期
        // =====================================================================

        private void OnEnable()
        {
            if (_spawned)
            {
                return;
            }
            // 延迟到 Update 首帧真正撒点：先确保 BambooSceneContext 已就绪（注册入口可用）。
        }

        private void OnDisable()
        {
            ClearSpawned();
        }

        private void OnDestroy()
        {
            ClearSpawned();
        }

        private void Update()
        {
            if (!_spawned)
            {
                BambooSceneContext ctx = ResolveContext();
                if (ctx != null)
                {
                    _depthAxis = ctx.DepthAxis;
                }
                if (config != null)
                {
                    SpawnAll();
                }
                else
                {
                    Debug.LogWarning("[2.5D][EnemyNpcSpawner] config 为空，跳过常规撒点。");
                }

                // 商店角色候选：独立路径，不依赖 config（即使没配 config 也能生成 Feng）。
                SpawnStoreFengEnemies(ctx);

                _spawned = true;
            }

            // 每帧推进敌人视图动画 + 巡逻（与 BSC 同闸门：暂停语义只读 IsGameplayBlocked）。
            // 两者时钟均为 FeedbackClock.Delta，顿帧同步冻结。
            CombatBridge bridge = ResolveBridge();
            bool blocked = bridge != null && bridge.IsGameplayBlocked;
            if (!blocked)
            {
                float dt = FeedbackClock.Delta;
                if (dt > 0.0f)
                {
                    for (int i = 0; i < _patrols.Count; i++)
                    {
                        EnemyPatrol p = _patrols[i];
                        if (p != null)
                        {
                            // 兜底注入：首帧 CombatBridge 未就绪时后续帧补上，敌人即可真正掉血；
                            // NPC 的 damageEnabled=false，此分支永不成立，保持零伤害。
                            if (bridge != null && p.damageEnabled && p.damageRequester == null)
                            {
                                p.damageRequester = bridge;
                            }
                            p.Tick(dt);
                        }
                    }
                    for (int i = 0; i < _views.Count; i++)
                    {
                        if (_views[i] != null)
                        {
                            _views[i].Tick();
                        }
                    }
                }
            }
        }

        // =====================================================================
        // 撒点（多区域 / 整体方形 两条路径）
        // =====================================================================

        /// <summary>确定性撒点：多区域或整体方形，生成小怪 + NPC 标记，挂视图/巡逻并注册排序/harvest。</summary>
        public void SpawnAll()
        {
            ClearSpawned();

            BambooSceneContext ctx = ResolveContext();
            if (ctx != null)
            {
                _depthAxis = ctx.DepthAxis;
            }

            Transform parent = spawnParent != null ? spawnParent : transform;

            if (config.regions != null && config.regions.Count > 0)
            {
                SpawnByRegions(parent, ctx);
            }
            else
            {
                SpawnSquare(parent, ctx);
            }
        }

        /// <summary>选项A 多区域路径：每个 region 独立种子、独立圆内撒点、独立巡逻圈。</summary>
        private void SpawnByRegions(Transform parent, BambooSceneContext ctx)
        {
            int enemyIdx = 0;
            int npcIdx = 0;

            for (int r = 0; r < config.regions.Count; r++)
            {
                SpawnRegion reg = config.regions[r];
                PCG32 rng = ZoneSeed.CreateRng(config.zoneSeedId + "_r" + r, false, 0);

                // 该区域敌人原型袋（确定性顺序展开，按敌人序号循环取用）。
                List<EnemyArchetypeEntry> bag = new List<EnemyArchetypeEntry>();
                for (int i = 0; i < config.archetypes.Count; i++)
                {
                    EnemyArchetypeEntry a = config.archetypes[i];
                    int c = Mathf.Max(0, a.count);
                    for (int k = 0; k < c; k++)
                    {
                        bag.Add(a);
                    }
                }

                int regEnemy = Mathf.Max(0, reg.enemyCount);
                List<Vector2> ePts = ScatterCircle(rng, reg.center, reg.radius, regEnemy);
                for (int i = 0; i < ePts.Count; i++)
                {
                    EnemyArchetypeEntry entry = bag.Count > 0 ? bag[enemyIdx % bag.Count] : null;
                    uint pseed = HashSeed(config.zoneSeedId + "_" + reg.id + "_e" + i);
                    SpawnEnemy(parent, ePts[i], entry, ctx, reg.center, reg.radius, pseed);
                    enemyIdx++;
                }

                int regNpc = Mathf.Max(0, reg.npcCount);
                List<Vector2> nPts = ScatterCircle(rng, reg.center, reg.radius, regNpc);
                for (int i = 0; i < nPts.Count; i++)
                {
                    uint pseed = HashSeed(config.zoneSeedId + "_" + reg.id + "_n" + i);
                    SpawnNpc(parent, nPts[i], npcIdx, ctx, reg.center, reg.radius, pseed);
                    npcIdx++;
                }
            }

            Debug.Log(string.Format(
                "[2.5D][EnemyNpcSpawner] 多区域撒点完成：区域 {0}，小怪 {1} / NPC {2}，深度轴 = {3}。",
                config.regions.Count, enemyIdx, npcIdx, _depthAxis));
        }

        /// <summary>向后兼容路径：整体方形域内撒点（regions 为空时）。</summary>
        private void SpawnSquare(Transform parent, BambooSceneContext ctx)
        {
            int enemyTarget = 0;
            for (int i = 0; i < config.archetypes.Count; i++)
            {
                enemyTarget += Mathf.Max(0, config.archetypes[i].count);
            }
            int totalTarget = enemyTarget + Mathf.Max(0, config.npcMarkerCount);

            PCG32 rng = ZoneSeed.CreateRng(config.zoneSeedId, false, 0);
            List<Vector2> pts = ScatterPositions(rng, totalTarget);
            if (pts.Count == 0)
            {
                Debug.LogWarning("[2.5D][EnemyNpcSpawner] 撒点未放下任何点（区域过小或 minDist 过大）。");
                return;
            }

            List<EnemyArchetypeEntry> bag = new List<EnemyArchetypeEntry>();
            for (int i = 0; i < config.archetypes.Count; i++)
            {
                EnemyArchetypeEntry a = config.archetypes[i];
                int c = Mathf.Max(0, a.count);
                for (int k = 0; k < c; k++)
                {
                    bag.Add(a);
                }
            }

            // 整体域的巡逻圈：以原点为圆心、半径取方形半边长（含默认全局缩放前的世界尺度）。
            Vector2 patrolCenter = Vector2.zero;
            float patrolRadius = config.spawnAreaHalfExtent;

            int enemyIdx = 0;
            int npcIdx = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (enemyIdx < bag.Count)
                {
                    uint pseed = HashSeed(config.zoneSeedId + "_e" + enemyIdx);
                    SpawnEnemy(parent, pts[i], bag[enemyIdx], ctx, patrolCenter, patrolRadius, pseed);
                    enemyIdx++;
                }
                else
                {
                    uint pseed = HashSeed(config.zoneSeedId + "_n" + npcIdx);
                    SpawnNpc(parent, pts[i], npcIdx, ctx, patrolCenter, patrolRadius, pseed);
                    npcIdx++;
                }
            }

            Debug.Log(string.Format(
                "[2.5D][EnemyNpcSpawner] 整体方形撒点完成：小怪 {0} / NPC {1}（目标 {2}），深度轴 = {3}。",
                enemyIdx, npcIdx, totalTarget, _depthAxis));
        }

        /// <summary>生成一只小怪：prefab（entry.prefab 或 prefabOverride）或 primitives + Ink 材质；
        /// 挂 CharacterView + 巡逻；注册排序/harvest。config 可为 null（商店候选路径）。</summary>
        private void SpawnEnemy(Transform parent, Vector2 worldXY, EnemyArchetypeEntry entry, BambooSceneContext ctx,
            Vector2 patrolCenter, float patrolRadius, uint patrolSeed,
            GameObject prefabOverride = null, bool? harvestableOverride = null)
        {
            GameObject root = new GameObject(string.Format("Enemy_{0}_{1}", entry != null ? entry.kind.ToString() : "X", _spawnedRoots.Count));
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(worldXY.x, worldXY.y, 0.0f);
            root.transform.localRotation = Quaternion.identity;

            float gscale = (config != null) ? config.globalScaleRef : 1.0f;
            float scale = (entry != null && entry.scale > 0.0f ? entry.scale : DefaultScale(entry != null ? entry.kind : EnemyKind.Musician)) * gscale;
            root.transform.localScale = new Vector3(scale, scale, scale);

            // 外观：prefab（override 优先）或 primitives + Ink 材质。
            GameObject prefabToUse = prefabOverride != null ? prefabOverride : (entry != null ? entry.prefab : null);
            if (prefabToUse != null)
            {
                GameObject inst = Instantiate(prefabToUse);
                inst.name = "Visual";
                inst.transform.SetParent(root.transform, false);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one;
                ApplyInkIfMissing(inst, entry, config);
            }
            else
            {
                BuildInkPrimitive(root.transform, entry, config);
            }

            // 挂视图（统一挂载约定：每个角色根节点有且只有一个 CharacterView）。
            CharacterView cv = CharacterView.ResolveOn(root.transform);
            if (cv != null)
            {
                cv.PlayState(CharacterAnimState.Idle);
                _views.Add(cv);
            }

            // 挂巡逻（选项A）：在所属区域内随机游走。
            EnemyPatrol patrol = root.AddComponent<EnemyPatrol>();
            patrol.Configure(patrolCenter, patrolRadius, patrolSeed);
            // 注入伤害出口：让敌人贴身攻击时经 CombatBridge 真正掉玩家血（闭环）。
            // 首帧 CombatBridge 未就绪时置 null，由 Update 循环兜底补注入。
            patrol.damageRequester = ResolveBridge();
            _patrols.Add(patrol);

            // 深度排序：注册进 BSC（由 BSC.ApplyDepthSort 统一排序）；无 BSC 时自管列表。
            bool depthSort = (config != null && config.depthSortEnabled) || (ctx != null);
            if (depthSort && ctx != null)
            {
                ctx.RegisterSortable(root.transform);
            }

            // harvest：override 优先；否则默认开 + 原型可 harvest 才进命中集。
            bool harvestable = harvestableOverride.HasValue
                ? harvestableOverride.Value
                : (config != null && config.harvestableByDefault && (entry == null || entry.harvestable));
            if (harvestable && ctx != null)
            {
                ctx.RegisterHarvestTarget(root.transform);
            }

            _spawnedRoots.Add(root.transform);
        }

        /// <summary>商店角色候选（Char_Feng）：独立生成路径，不依赖 config 是否存在。
        /// prefab 由菜单 Shuimo/Store/Generate Prefabs 生成到 Resources/Enemies/FengEnemy。</summary>
        private void SpawnStoreFengEnemies(BambooSceneContext ctx)
        {
            if (!spawnFengStoreEnemy || fengStoreEnemyCount <= 0)
            {
                return;
            }
            GameObject prefab = Resources.Load<GameObject>(fengStoreEnemyPath);
            if (prefab == null)
            {
                Debug.LogWarning(string.Format(
                    "[2.5D][EnemyNpcSpawner] 未找到 FengEnemy prefab（Resources/{0}）。先跑菜单 Shuimo/Store/Generate Prefabs。",
                    fengStoreEnemyPath));
                return;
            }

            Transform parent = spawnParent != null ? spawnParent : transform;
            float area = (config != null) ? config.spawnAreaHalfExtent : 1400.0f;

            for (int i = 0; i < fengStoreEnemyCount; i++)
            {
                Vector2 p = new Vector2(
                    Random.Range(-area, area),
                    Random.Range(-area, area));
                uint seed = HashSeed("store_feng_" + i);
                EnemyArchetypeEntry entry = new EnemyArchetypeEntry
                {
                    kind = EnemyKind.Feng,
                    scale = 1.0f,
                    harvestable = false,
                };
                SpawnEnemy(parent, p, entry, ctx, Vector2.zero, area, seed, prefab, false);
            }

            Debug.Log(string.Format(
                "[2.5D][EnemyNpcSpawner] 商店角色候选 Feng 生成 {0} 只（Resources/{1}）。既有 boss/witch/elder/musician 占位不受影响。",
                fengStoreEnemyCount, fengStoreEnemyPath));
        }

        /// <summary>生成一个 NPC 标记：Idle 占位 + InteractableMarker，不进 harvest 集，但参与巡逻（轻量游走）。</summary>
        private void SpawnNpc(Transform parent, Vector2 worldXY, int index, BambooSceneContext ctx,
            Vector2 patrolCenter, float patrolRadius, uint patrolSeed)
        {
            GameObject root = new GameObject(string.Format("Npc_{0}", index));
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(worldXY.x, worldXY.y, 0.0f);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            BuildInkPrimitive(root.transform, null, config);

            CharacterView cv = CharacterView.ResolveOn(root.transform);
            if (cv != null)
            {
                cv.PlayState(CharacterAnimState.Idle);
                _views.Add(cv);
            }

            // NPC 也做极慢巡逻，让场景更有生气（不战斗、不 harvest）。
            EnemyPatrol patrol = root.AddComponent<EnemyPatrol>();
            patrol.moveSpeed = 28.0f; // NPC 比敌人慢
            patrol.damageEnabled = false; // NPC 永不造成玩家伤害
            patrol.Configure(patrolCenter, patrolRadius, patrolSeed);
            _patrols.Add(patrol);

            InteractableMarker marker = root.AddComponent<InteractableMarker>();
            marker.markerId = string.Format("npc_{0}", index);
            marker.displayName = string.Format("NPC {0}", index);

            if (config.depthSortEnabled && ctx != null)
            {
                ctx.RegisterSortable(root.transform);
            }

            _spawnedRoots.Add(root.transform);
        }

        // =====================================================================
        // 几何 / 材质（运行时复刻 BambooInkImporter 同款参数，不改 Importer 一行）
        // =====================================================================

        /// <summary>路线 B 兜底：Cylinder 躯干 + Sphere 头，套 Ink 材质（Shader.Find 回退）。</summary>
        private void BuildInkPrimitive(Transform parent, EnemyArchetypeEntry entry, EnemyNpcSpawnConfig cfg)
        {
            Material trunkMat = (entry != null ? entry.inkMaterialOverride : null) ?? (cfg != null ? cfg.inkMaterialTrunk : null);
            if (trunkMat == null)
            {
                trunkMat = EnsureRuntimeInk(false);
            }

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.name = "Body";
            body.transform.SetParent(parent, false);
            body.transform.localRotation = Quaternion.FromToRotation(Vector3.up, _depthAxis);
            body.transform.localScale = new Vector3(40.0f, 80.0f, 40.0f);
            body.transform.localPosition = _depthAxis * 80.0f;

            MeshRenderer bodyMr = body.GetComponent<MeshRenderer>();
            if (bodyMr != null && trunkMat != null)
            {
                bodyMr.sharedMaterial = trunkMat;
            }
            Collider bodyCol = body.GetComponent<Collider>();
            if (bodyCol != null)
            {
                bodyCol.isTrigger = true; // 只作命中几何，不参与物理
            }

            Material headMat = (cfg != null ? cfg.inkMaterialLeaf : null) ?? trunkMat;
            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(parent, false);
            head.transform.localScale = new Vector3(48.0f, 48.0f, 48.0f);
            head.transform.localPosition = _depthAxis * 160.0f;
            MeshRenderer headMr = head.GetComponent<MeshRenderer>();
            if (headMr != null && headMat != null)
            {
                headMr.sharedMaterial = headMat;
            }
            Collider headCol = head.GetComponent<Collider>();
            if (headCol != null)
            {
                headCol.isTrigger = true;
            }
        }

        /// <summary>prefab 分支：仅在子节点 MeshRenderer 缺材质时补 Ink 材质（不覆盖美术资产）。</summary>
        private static void ApplyInkIfMissing(GameObject inst, EnemyArchetypeEntry entry, EnemyNpcSpawnConfig cfg)
        {
            Material trunkMat = (entry != null ? entry.inkMaterialOverride : null) ?? (cfg != null ? cfg.inkMaterialTrunk : null);
            if (trunkMat == null)
            {
                return;
            }
            MeshRenderer[] mrs = inst.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                if (mrs[i].sharedMaterial == null)
                {
                    mrs[i].sharedMaterial = trunkMat;
                }
            }
        }

        /// <summary>运行时按 Shader 名建 Ink 材质；找不到则退回 Built-in 兜底。登记以便清理。</summary>
        private Material EnsureRuntimeInk(bool leaf)
        {
            string shaderName = leaf ? "Xianxia/Ink/BambooLeaf" : "Xianxia/Ink/BambooTrunk";
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning(string.Format(
                    "[2.5D][EnemyNpcSpawner] 找不到 Shader \"{0}\"，退回 Built-in 兜底。打包前请加进 Always Included Shaders。",
                    shaderName));
                shader = Shader.Find("Legacy Shaders/Diffuse");
            }
            if (shader == null)
            {
                shader = Shader.Find("Diffuse");
            }
            if (shader == null)
            {
                return null;
            }

            Material mat = new Material(shader);
            mat.name = leaf ? "MAT_Ink_Leaf_Spawner_Runtime" : "MAT_Ink_Trunk_Spawner_Runtime";
            mat.hideFlags = HideFlags.DontSave;
            _ownedMaterials.Add(mat);
            return mat;
        }

        // =====================================================================
        // 撒点算法（确定性泊松式拒绝采样，与 BambooSceneContext.ScatterPositions 同源纪律）
        // =====================================================================

        /// <summary>方形域内带最小间距拒绝采样；attempts 上限防死循环。</summary>
        private List<Vector2> ScatterPositions(PCG32 rng, int targetCount)
        {
            List<Vector2> result = new List<Vector2>();
            if (targetCount <= 0)
            {
                return result;
            }

            float half = Mathf.Max(1.0f, config.spawnAreaHalfExtent);
            float minDistSqr = config.minDist * config.minDist;
            int maxAttempts = targetCount * 60;

            for (int attempt = 0; attempt < maxAttempts && result.Count < targetCount; attempt++)
            {
                float x = rng.NextRange(-half, half);
                float y = rng.NextRange(-half, half);
                Vector2 candidate = new Vector2(x, y);

                bool ok = true;
                for (int i = 0; i < result.Count; i++)
                {
                    if ((result[i] - candidate).sqrMagnitude < minDistSqr)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    result.Add(candidate);
                }
            }

            if (result.Count < targetCount)
            {
                Debug.LogWarning(string.Format(
                    "[2.5D][EnemyNpcSpawner] 撒点只放下 {0}/{1}（minDist={2}, half={3} 过密）。",
                    result.Count, targetCount, config.minDist, half));
            }
            return result;
        }

        /// <summary>圆形域内带最小间距拒绝采样（选项A 多区域用）。</summary>
        private List<Vector2> ScatterCircle(PCG32 rng, Vector2 center, float radius, int targetCount)
        {
            List<Vector2> result = new List<Vector2>();
            if (targetCount <= 0)
            {
                return result;
            }

            float r = Mathf.Max(1.0f, radius);
            float minDistSqr = config.minDist * config.minDist;
            int maxAttempts = targetCount * 60;

            for (int attempt = 0; attempt < maxAttempts && result.Count < targetCount; attempt++)
            {
                float ang = rng.NextRange(0.0f, Mathf.PI * 2.0f);
                float rad = Mathf.Sqrt(rng.NextRange(0.0f, 1.0f)) * r; // 圆内均匀
                Vector2 candidate = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rad;

                bool ok = true;
                for (int i = 0; i < result.Count; i++)
                {
                    if ((result[i] - candidate).sqrMagnitude < minDistSqr)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    result.Add(candidate);
                }
            }

            if (result.Count < targetCount)
            {
                Debug.LogWarning(string.Format(
                    "[2.5D][EnemyNpcSpawner] 区域 {0} 圆内撒点只放下 {1}/{2}（minDist={3}, r={4} 过密）。",
                    center, result.Count, targetCount, config.minDist, r));
            }
            return result;
        }

        /// <summary>把字符串派生为稳定的 32 位种子（FNV-1a 变体）。</summary>
        private static uint HashSeed(string s)
        {
            uint h = 0x811C9DC5u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= (byte)s[i];
                h *= 0x01000193u;
            }
            return h;
        }

        // =====================================================================
        // 清理 / 惰性解析
        // =====================================================================

        /// <summary>销毁全部生成对象、解除 BSC 注册、释放自建材质。</summary>
        private void ClearSpawned()
        {
            BambooSceneContext ctx = context;
            if (ctx == null)
            {
                ctx = ResolveContext();
            }

            for (int i = 0; i < _spawnedRoots.Count; i++)
            {
                Transform t = _spawnedRoots[i];
                if (ctx != null)
                {
                    ctx.UnregisterSortable(t);
                    ctx.UnregisterHarvestTarget(t);
                }
                if (t != null && t.gameObject != null)
                {
                    SafeDestroy(t.gameObject);
                }
            }
            _spawnedRoots.Clear();
            _views.Clear();
            _patrols.Clear();

            for (int i = 0; i < _ownedMaterials.Count; i++)
            {
                if (_ownedMaterials[i] != null)
                {
                    SafeDestroy(_ownedMaterials[i]);
                }
            }
            _ownedMaterials.Clear();

            // 复位闸门：ClearSpawned 后若 spawner 被重新激活（OnDisable→OnEnable、
            // 或后续波次/分区重刷），Update 才能再次进入撒点分支，否则敌人会永久消失。
            _spawned = false;
        }

        /// <summary>各类别默认缩放（复用 2D explore 的 4 类缩放思路）。</summary>
        private static float DefaultScale(EnemyKind kind)
        {
            switch (kind)
            {
                case EnemyKind.Boss:
                    return 1.6f;
                case EnemyKind.Witch:
                    return 0.9f;
                case EnemyKind.Elder:
                    return 1.1f;
                case EnemyKind.Musician:
                default:
                    return 1.0f;
            }
        }

        /// <summary>惰性解析 BambooSceneContext（Inspector 未拖引用时）。</summary>
        private BambooSceneContext ResolveContext()
        {
            if (context != null)
            {
                return context;
            }
#if UNITY_2023_1_OR_NEWER
            context = Object.FindFirstObjectOfType<BambooSceneContext>();
#else
            context = Object.FindObjectOfType<BambooSceneContext>();
#endif
            return context;
        }

        /// <summary>惰性解析 CombatBridge（只读 IsGameplayBlocked）。</summary>
        private CombatBridge ResolveBridge()
        {
            if (_bridge != null)
            {
                return _bridge;
            }
#if UNITY_2023_1_OR_NEWER
            _bridge = Object.FindFirstObjectOfType<CombatBridge>();
#else
            _bridge = Object.FindObjectOfType<CombatBridge>();
#endif
            return _bridge;
        }

        /// <summary>编辑器/运行时通用销毁（不使用任何战斗内核 API）。</summary>
        private static void SafeDestroy(Object o)
        {
            if (o == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Destroy(o);
            }
            else
            {
                DestroyImmediate(o);
            }
        }
    }
}
