// -----------------------------------------------------------------------------
// 2.5D/EnemyNpcSpawner.cs —— 敌人 / NPC 确定性撒点（feature/2.5d，轮次 C）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过。
//
// 【职责】在竹林场景内确定性撒点：
//   - 竹林小怪：复用 boss/witch/elder/musician 缩放思路，默认 18 只；
//   - NPC 标记点：可对话/可交互，先占位（InteractableMarker），不战斗、不 harvest。
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
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Xianxia.Core; // ZoneSeed, PCG32

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

        [Tooltip("撒点区半边长（XY 正方形，与 BambooSceneContext.groveHalfExtent 同尺度）")]
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
        [Tooltip("纯标记 NPC 数量（不战斗、不 harvest）")]
        public int npcMarkerCount = 4;

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
    /// 敌人 / NPC 确定性撒点器（轮次 C）。挂在竹林场景内的独立对象上（或 BambooSceneContext 子树）。
    /// 生成 + 挂 CharacterView（Idle 占位）+ 注册深度排序集 + 暴露 harvestable 集给 BSC。
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

        private readonly List<Transform> _spawnedRoots = new List<Transform>();
        private readonly List<CharacterView> _views = new List<CharacterView>();
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
                    Debug.LogWarning("[2.5D][EnemyNpcSpawner] config 为空，跳过撒点。");
                }
                _spawned = true;
            }

            // 每帧推进敌人视图动画（与 BSC 同闸门：暂停语义只读 IsGameplayBlocked）。
            // CharacterView.Tick 内部只读 FeedbackClock.Delta，顿帧同步冻结。
            CombatBridge bridge = ResolveBridge();
            bool blocked = bridge != null && bridge.IsGameplayBlocked;
            if (!blocked)
            {
                for (int i = 0; i < _views.Count; i++)
                {
                    if (_views[i] != null)
                    {
                        _views[i].Tick();
                    }
                }
            }
        }

        // =====================================================================
        // 撒点
        // =====================================================================

        /// <summary>确定性撒点：生成小怪 + NPC 标记，挂视图并注册排序/harvest 集。</summary>
        public void SpawnAll()
        {
            ClearSpawned();

            BambooSceneContext ctx = ResolveContext();
            if (ctx != null)
            {
                _depthAxis = ctx.DepthAxis;
            }

            Transform parent = spawnParent != null ? spawnParent : transform;
            PCG32 rng = ZoneSeed.CreateRng(config.zoneSeedId, false, 0);

            // 目标数量：所有原型 count 之和 + NPC 标记数。
            int enemyTarget = 0;
            for (int i = 0; i < config.archetypes.Count; i++)
            {
                enemyTarget += Mathf.Max(0, config.archetypes[i].count);
            }
            int totalTarget = enemyTarget + Mathf.Max(0, config.npcMarkerCount);

            List<Vector2> pts = ScatterPositions(rng, totalTarget);
            if (pts.Count == 0)
            {
                Debug.LogWarning("[2.5D][EnemyNpcSpawner] 撒点未放下任何点（区域过小或 minDist 过大）。");
                return;
            }

            // 展开原型袋（确定性顺序），按点分配。
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

            int enemyIdx = 0;
            int npcIdx = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 worldXY = new Vector3(pts[i].x, pts[i].y, 0.0f);

                if (enemyIdx < bag.Count)
                {
                    SpawnEnemy(parent, worldXY, bag[enemyIdx], ctx);
                    enemyIdx++;
                }
                else
                {
                    SpawnNpc(parent, worldXY, npcIdx, ctx);
                    npcIdx++;
                }
            }

            Debug.Log(string.Format(
                "[2.5D][EnemyNpcSpawner] 已撒点：小怪 {0} / NPC {1}（目标 {2}），深度轴 = {3}。",
                enemyIdx, npcIdx, totalTarget, _depthAxis));
        }

        /// <summary>生成一只小怪：prefab 或 primitives + Ink 材质；挂 CharacterView；注册排序/harvest。</summary>
        private void SpawnEnemy(Transform parent, Vector3 worldXY, EnemyArchetypeEntry entry, BambooSceneContext ctx)
        {
            GameObject root = new GameObject(string.Format("Enemy_{0}_{1}", entry.kind, _spawnedRoots.Count));
            root.transform.SetParent(parent, false);
            root.transform.localPosition = worldXY;
            root.transform.localRotation = Quaternion.identity;

            float scale = (entry.scale > 0.0f ? entry.scale : DefaultScale(entry.kind)) * config.globalScaleRef;
            root.transform.localScale = new Vector3(scale, scale, scale);

            // 外观：prefab 或 primitives + Ink 材质。
            if (entry.prefab != null)
            {
                GameObject inst = Instantiate(entry.prefab);
                inst.name = "Visual";
                inst.transform.SetParent(root.transform, false);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one;
                // 套 Ink 材质（仅当 prefab 缺材质时回退，避免覆盖美术资产）。
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

            // 深度排序：注册进 BSC（由 BSC.ApplyDepthSort 统一排序）；无 BSC 时自管列表。
            if (config.depthSortEnabled)
            {
                if (ctx != null)
                {
                    ctx.RegisterSortable(root.transform);
                }
            }

            // harvest：默认开 + 原型可 harvest 才进命中集。
            bool harvestable = config.harvestableByDefault && entry.harvestable;
            if (harvestable && ctx != null)
            {
                ctx.RegisterHarvestTarget(root.transform);
            }

            _spawnedRoots.Add(root.transform);
        }

        /// <summary>生成一个 NPC 标记：Idle 占位 + InteractableMarker，不进 harvest 集。</summary>
        private void SpawnNpc(Transform parent, Vector3 worldXY, int index, BambooSceneContext ctx)
        {
            GameObject root = new GameObject(string.Format("Npc_{0}", index));
            root.transform.SetParent(parent, false);
            root.transform.localPosition = worldXY;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            // 可选外观：小 primitives 占位（纯标记，不入 harvest 也不战斗）。
            BuildInkPrimitive(root.transform, null, config);

            CharacterView cv = CharacterView.ResolveOn(root.transform);
            if (cv != null)
            {
                cv.PlayState(CharacterAnimState.Idle);
                _views.Add(cv);
            }

            InteractableMarker marker = root.AddComponent<InteractableMarker>();
            marker.markerId = string.Format("npc_{0}", index);
            marker.displayName = string.Format("NPC {0}", index);

            // NPC 仅标记、不 harvest，但仍参与深度排序以保证遮挡一致。
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
            Material trunkMat = (entry != null ? entry.inkMaterialOverride : null) ?? cfg.inkMaterialTrunk;
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

            Material headMat = cfg.inkMaterialLeaf ?? trunkMat;
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
            Material trunkMat = (entry != null ? entry.inkMaterialOverride : null) ?? cfg.inkMaterialTrunk;
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

        /// <summary>在方形域内带最小间距拒绝采样；attempts 上限防死循环。</summary>
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

            for (int i = 0; i < _ownedMaterials.Count; i++)
            {
                if (_ownedMaterials[i] != null)
                {
                    SafeDestroy(_ownedMaterials[i]);
                }
            }
            _ownedMaterials.Clear();
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
            context = Object.FindFirstObjectByType<BambooSceneContext>();
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
            _bridge = Object.FindFirstObjectByType<CombatBridge>();
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
