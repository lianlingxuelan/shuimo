// -----------------------------------------------------------------------------
// 2.5D/BambooSceneContext.cs —— 竹林 2.5D 场景上下文（feature/2.5d，轮次 B）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过，并按 docs/bamboo-2_5d-roundB-design.md §1.1/§1.3 做
// PlayMode 验收（竹林生成、遮挡、砍竹特效、顿帧同步）。
//
// 【职责】对标 WorldBuilder.BuildScene 的「世界根」思路，但产出 3D 竹子而非 2D
// Tilemap：程序化生成 20–30 根竹子 + 地面 + 雾层；把女主 Transform 接给
// IsometricCameraRig.BindTarget；每帧轮询攻击扇形命中竹子并触发 BambooVfx.OnHit。
//
// 【红线（对应 docs/bamboo-2_5d-roundB-design.md §4）】
//   1. 本根**不挂** ShuimoGenerated 标记 —— 否则会被 WorldBuilder.DestroyGeneratedRoots()
//      在「菜单 Clean / 按 R 重开」时误删。竹林与 2D 世界生成互不干扰。
//   2. **不写** Time.timeScale / Scheduler / RunPhase / CombatScheduler。本文件
//      连引用都没有；暂停语义只读 CombatBridge.IsGameplayBlocked（与 HeroineAnimator 同闸门）。
//   3. **不调** DamageResolver、不进战斗内核。竹子不是 Combatant，不占 AliveEnemyCount。
//   4. 砍竹检测走「轮询 AttackController.SwingCount 增长沿 + 自管扇形」，对
//      AttackController / PlayerController / CombatBridge **零改动**（同 HeroineAnimator 的非侵入模式）。
//
// 【已拍板决策（用户 2026-08-11）】
//   D4 渲染管线：Built-in 兼容 —— 复用现有 Xianxia/Ink/* Shader（surface surf Lambert，
//                Built-in RP），不切 URP。
//   D5 场景：叠加 SampleScene —— 本组件作为 SampleScene 内的独立子树，不新建场景文件。
//   D6 顿帧：竹子只冻自身动画（BambooVfx 用 FeedbackClock.Delta 推进），
//            不扩 HitFeedbackDirector、不直写 FeedbackClock.Frozen。
//
// 【BambooInkImporter 复用结论（实测，非假设）】
// Assets/_Project/Scripts/Editor/BambooInkImporter.cs 是 **Editor-only** 工具
// （using UnityEditor / namespace Shuimo.EditorTools / asmdef Xianxia.Unity.T2.Editor），
// 且它**只套材质、不生成几何** —— 它把 Xianxia/Ink/* Shader 套到已导入的
// bamboo_ink.fbx 的 MeshRenderer 上。运行时程序集 Xianxia.Unity.T2 在依赖方向上
// 够不着 Editor 程序集，因此**无法在本类里调用它生成竹子**。
// 采取的等效方案（两条路，均不改 Importer 一行）：
//   路线 A（推荐，美术保真）：用户在 Unity 里选中 Assets/_Project/Art/Bamboo/bamboo_ink.fbx，
//        点菜单 Shuimo/2.5D/Apply Ink Bamboo Materials 套好水墨材质，再把该 FBX
//        拖到本组件的 bambooModelPrefab 字段 → Load() 直接 Instantiate 真模型。
//   路线 B（兜底，零资产依赖）：bambooModelPrefab 为空时退回 Unity primitives
//        （Cylinder 竹竿 + Quad 面片竹叶），并在运行时用 Shader.Find 找同一批
//        Xianxia/Ink/* Shader、按 BambooInkImporter 里**同款默认参数**建材质，
//        使 primitives 也是水墨观感。Shader 找不到时再退 Built-in 内置 Shader 兜底。
//   注意：Shader.Find 在打包后只对「Always Included Shaders / Resources 内被引用」
//        的 Shader 有效。发布前请把 Xianxia/Ink/* 三个 Shader 加进
//        Project Settings > Graphics > Always Included Shaders。
//
// 【坐标约定的一处必要修正（与设计文档 §6.2 的偏差，已核实）】
// 设计文档写「+Z = 深度/竹高方向（朝向相机）」，但工程实测
// WorldBuilder.SetupCamera() 把相机放在 z = -100（WorldBuilder.cs:662，标准 Unity 2D
// 布局：相机在 -Z 侧朝 +Z 看）。若竹子沿 +Z 生长，竹子会长到相机背面、
// **永远不可能遮挡女主**，§2.4 的遮挡验收项直接失效。
// 因此本类把「深度轴」定义为**朝向相机的方向**（默认 -Z），并默认从实际相机
// 位置自动推导符号（autoDepthAxisFromCamera），把这处朝向错误消灭在运行时。
// 玩法平面仍是 XY（z≈0），PlayerController / AttackController 一行不改。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using Xianxia.Combat;
using Xianxia.Core;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 竹林 2.5D 场景上下文（feature/2.5d，仅此文件 + BambooVfx.cs 属轮次 B）。
    ///
    /// 生命周期：<see cref="Load"/> 生成竹林子树，<see cref="BindPlayer"/> 绑定相机跟随，
    /// <see cref="Unload"/> 只销毁自己生成的子树。全程不触碰 WorldBuilder 的根、
    /// 不改任何战斗静态状态。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BambooSceneContext : MonoBehaviour
    {
        // =====================================================================
        // 常量
        // =====================================================================

        /// <summary>竹子数量下限（设计 §2.1：20–30 根）。</summary>
        public const int MinBambooCount = 20;

        /// <summary>竹子数量上限（设计 §2.1：20–30 根）。</summary>
        public const int MaxBambooCount = 30;

        /// <summary>竹林根节点名。刻意不带 "Shuimo" 前缀，避免被误认为生成物。</summary>
        public const string GroveRootName = "BambooGrove_Root";

        /// <summary>惰性解析外部引用的重试间隔（秒），避免每帧 FindObjectOfType。</summary>
        private const float ResolveRetryInterval = 0.5f;

        // =====================================================================
        // 生成参数（Inspector 可配）
        // =====================================================================

        [Header("竹林布局（世界单位 = px，PPU=1，与 2D 同尺度，见设计 §6.1）")]
        [Tooltip("竹子数量，运行时会被夹到 [10,30]；2.5D 俯视再砍 50% 到 13")]
        public int bambooCount = 13;

        [Tooltip("竹林可行走区半边长（XY 平面正方形）。调小 = 竹林更聚拢在玩家身边")]
        public float groveHalfExtent = 700.0f;

        [Tooltip("竹子之间的最小间距，泊松式撒点用。半径变小后可适度加密")]
        public float bambooMinDist = 85.0f;

        [Header("世界铺满（把竹林撒满整张地图，不只是玩家身边，构成完整竹林世界）")]
        [Tooltip("开启后，在整张地图范围再撒一层静态竹林；2.5D 俯视下这层在屏幕外也吃 draw call，默认关闭")]
        public bool worldFill = false;

        [Tooltip("世界层竹林数量上限（性能与观感平衡，建议 60–220；2.5D 俯视满图再砍 50% 到 22）")]
        public int worldBambooCount = 22;

        [Tooltip("世界层竹林最小间距（世界单位），过密会卡脚、过疏显得空")]
        public float worldMinDist = 240.0f;

        [Tooltip("竹竿半径（同尺度）。2.5D 俯视下 9 左右显细长，避免粗黑柱子感")]
        public float trunkRadius = 9.0f;

        [Tooltip("竹子高度下限")]
        public float heightMin = 360.0f;

        [Tooltip("竹子高度上限")]
        public float heightMax = 520.0f;

        [Tooltip("每根竹子的竹叶面片数下限（竹叶在梢部成簇，数量适中即可，半透明叠加是 overdraw 主因）")]
        public int leavesMin = 4;

        [Tooltip("每根竹子的竹叶面片数上限（竹叶在梢部成簇，数量适中即可，半透明叠加是 overdraw 主因）")]
        public int leavesMax = 6;

        [Tooltip("竹子自然倾斜角上限（度）。真实竹林不是垂直于地面，每根会随机向某个方向歪斜，避免像黑色柱子")]
        [Range(0.0f, 60.0f)]
        public float bambooLeanAngle = 16.0f;

        [Tooltip("确定性布局用的区域种子 id（走 ZoneSeed.CreateRng，复刻 WorldBuilder 的随机纪律）")]
        public string zoneSeedId = "zone_bamboo_2_5d";

        // =====================================================================
        // 砍竹判定（复用 AttackController 的扇形常量，见设计 §3.4）
        // =====================================================================

        [Header("砍竹判定（复用 AttackController 常量，不改其一行）")]
        [Tooltip("命中半径。默认直接复用 AttackController.AttackRadius（70）")]
        public float harvestRadius = AttackController.AttackRadius;

        [Tooltip("命中扇形角度。默认直接复用 AttackController.AttackArcDeg（90）")]
        public float harvestArcDeg = AttackController.AttackArcDeg;

        [Header("砍竹掉落（内容闭环）")]
        [Tooltip("每根竹子砍断掉落的竹材数量")]
        public int bambooWoodPerBreak = 2;

        [Tooltip("额外掉落嫩笋的概率（0-100）")]
        [Range(0.0f, 100.0f)]
        public float bambooShootChance = 20.0f;

        [Tooltip("嫩笋掉落数量")]
        public int bambooShootPerBreak = 1;

        // =====================================================================
        // 相机取景（见设计 §2.4 / §6.3）
        // =====================================================================

    [Header("相机取景（覆写 IsometricCameraRig 的占位默认值）")]
    [Tooltip("正交视野尺寸。越小 = 相机越近、人物越大。240 在 2.5D 下人物占比明显更大")]
    public float orthographicSize = 240.0f;

        [Tooltip("相机俯仰角（度）。0 = 正视 XY 板（纯 2D 观感），越大越斜、竹子越立体")]
        [Range(0.0f, 70.0f)]
        public float cameraTiltDeg = 34.0f;

        [Tooltip("相机沿深度轴到玩法平面的距离（正交下只影响裁剪，不影响取景大小）")]
        public float cameraDistance = 900.0f;

        [Tooltip("是否由本组件覆写相机取景参数（关掉则完全沿用 IsometricCameraRig 自身设置）")]
        public bool overrideCameraFraming = true;

        // =====================================================================
        // 深度轴（本类对设计 §6.2 的必要修正，见文件头注释）
        // =====================================================================

        [Header("深度轴（竹子生长方向 = 朝向相机，默认 -Z）")]
        [Tooltip("勾选后从实际相机位置自动推导深度轴符号，避免相机在 -Z 时竹子长到背面")]
        public bool autoDepthAxisFromCamera = true;

        [Tooltip("手动深度轴符号：-1 = 竹子沿 -Z 生长（工程 2D 相机在 z=-100，默认）；+1 = 沿 +Z")]
        public float depthSign = -1.0f;

        [Tooltip("Y 轴伪深度排序强度：按与玩家的 Y 差把竹子投影到深度轴，实现「走到竹子前后」的遮挡")]
        public float ySortToDepth = 0.5f;

        [Tooltip("是否每帧做 Y 轴伪深度排序（关掉后竹子恒在玩家前方）")]
        public bool depthSortEnabled = true;

        // =====================================================================
        // 外部引用（Inspector 拖引用，留空则运行时惰性解析）
        // =====================================================================

        [Header("外部引用（留空则运行时惰性解析）")]
        [Tooltip("等距相机。留空则自动在场景里找 IsometricCameraRig")]
        public IsometricCameraRig cameraRig;

        [Tooltip("水墨竹子模型（路线 A）：把套好材质的 bamboo_ink.fbx 拖进来；留空则退回 primitives")]
        public GameObject bambooModelPrefab;

        [Tooltip("性能保险开关（默认开）：用程序化 primitives 竹子（已验证 GPU Instancing，Batches≈1）。取消勾选才会用你拖进来的 Bamboo Model Prefab；取消后模型材质也会被强制开 Instancing，正常情况下同样不卡。若用模型仍卡，把这个开关重新勾上即可。")]
        public bool forcePrimitiveBamboo = true;

        [Tooltip("竹竿材质。留空则运行时按 BambooInkImporter 同款参数建 Xianxia/Ink/BambooTrunk 材质")]
        public Material trunkMaterial;

        [Tooltip("竹叶材质。留空则运行时按 BambooInkImporter 同款参数建 Xianxia/Ink/BambooLeaf 材质")]
        public Material leafMaterial;

        [Tooltip("地面材质。留空则运行时按 BambooInkImporter 同款参数建 Xianxia/Ink/InkGround 材质")]
        public Material groundMaterial;

        [Tooltip("砍竹粒子 prefab（BambooHitFx）。留空则 BambooVfx 走 Resources 兜底 / 运行时构造")]
        public GameObject inkLeafPrefab;

        // =====================================================================
        // 雾（Built-in RP 的 RenderSettings.fog，D4 已拍板 Built-in）
        // =====================================================================

        [Header("雾层（Built-in RP RenderSettings，Unload 时原样还原）")]
        [Tooltip("是否由本组件接管场景雾")]
        public bool enableFog = true;

        [Tooltip("雾色（宣纸白，与画布同色，远处淡出成水墨留白）")]
        public Color fogColor = new Color(0.95f, 0.94f, 0.89f, 1.0f);

        [Tooltip("线性雾起点距离。相机距玩家约 900，起点设 600 让近处竹林保持清晰。")]
        public float fogStart = 600.0f;

        [Tooltip("线性雾终点距离。远山约 1600-2300，终点 2600 让远山成淡墨剪影。")]
        public float fogEnd = 2600.0f;

        // =====================================================================
        // 运行时状态
        // =====================================================================

        /// <summary>竹林是否已生成。</summary>
        public bool IsLoaded
        {
            get { return _loaded; }
        }

        /// <summary>当前存活（未断裂）的竹子数量，供验收面板 / 调试读数。</summary>
        public int StandingBambooCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _bamboos.Count; i++)
                {
                    BambooVfx v = _bamboos[i];
                    if (v != null && !v.IsBroken)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>累计砍中竹子的次数（单次挥砍命中 N 根算 N 次），验收用。</summary>
        public int TotalHarvestHits
        {
            get { return _totalHarvestHits; }
        }

        /// <summary>实际生成的竹子总数。</summary>
        public int BambooCountActual
        {
            get { return _bamboos.Count; }
        }

        /// <summary>
        /// 深度轴（指向相机，默认 (0,0,-1)）。供 EnemyNpcSpawner 复用同一套深度排序，
        /// 保证遮挡验收一致（红线 R3）。从现有 <see cref="_depthAxis"/> 取值。
        /// </summary>
        public Vector3 DepthAxis
        {
            get { return _depthAxis; }
        }

        /// <summary>注册一个待深度排序的物体（EnemyNpcSpawner 调用）。</summary>
        /// <param name="t">物体根 Transform。</param>
        public void RegisterSortable(Transform t)
        {
            if (t != null && !_registeredSortables.Contains(t))
            {
                _registeredSortables.Add(t);
            }
        }

        /// <summary>注销一个待深度排序的物体。</summary>
        /// <param name="t">物体根 Transform。</param>
        public void UnregisterSortable(Transform t)
        {
            if (t != null)
            {
                _registeredSortables.Remove(t);
            }
        }

        /// <summary>注册一个可被扇形 harvest 命中的目标（EnemyNpcSpawner 调用）。</summary>
        /// <param name="t">目标根 Transform（其上的 CharacterView 受击时播 Hit）。</param>
        public void RegisterHarvestTarget(Transform t)
        {
            if (t != null && !_harvestTargets.Contains(t))
            {
                _harvestTargets.Add(t);
            }
        }

        /// <summary>注销一个 harvest 目标。</summary>
        /// <param name="t">目标根 Transform。</param>
        public void UnregisterHarvestTarget(Transform t)
        {
            if (t != null)
            {
                _harvestTargets.Remove(t);
            }
        }

        /// <summary>
        /// 竹子被砍断的回调（订阅 <see cref="BambooVfx.OnBroken"/>）。
        /// 这是砍竹内容闭环的「掉落」环节：给玩家背包加竹材 / 概率嫩笋。
        /// 竹子的重生由 BambooVfx 自身管理，本方法只处理掉落数据。
        /// </summary>
        /// <param name="v">被砍断的竹子（其对象会原地重生，本方法无需处理其生命周期）。</param>
        private void HandleBambooBroken(BambooVfx v)
        {
            if (PlayerInventory.Instance == null)
            {
                return;
            }
            PlayerInventory.Instance.AddMaterial(PlayerInventory.BambooWood, bambooWoodPerBreak);
            if (UnityEngine.Random.value * 100.0f < bambooShootChance)
            {
                PlayerInventory.Instance.AddMaterial(PlayerInventory.BambooShoot, bambooShootPerBreak);
            }
        }

        private readonly List<BambooVfx> _bamboos = new List<BambooVfx>();
        private readonly List<Vector2> _bambooPlan = new List<Vector2>();
        private readonly List<Material> _ownedMaterials = new List<Material>();

        private Transform _groveRoot;
        private Transform _worldRoot;
        private bool _loaded;

        private Transform _player;
        private PlayerController _playerController;
        private AttackController _attack;
        private CharacterView _playerView;
        private int _lastSwingCount;
        private int _totalHarvestHits;

        // 轮次 C：EnemyNpcSpawner 注册进来的「待深度排序物体」与「可被扇形 harvest 命中的目标」。
        private readonly List<Transform> _registeredSortables = new List<Transform>();
        private readonly List<Transform> _harvestTargets = new List<Transform>();

        private CombatBridge _bridge;
        private CombatEventsT3Unity _eventsT3;
        private float _bridgeRetry;
        private float _playerRetry;

        private Vector3 _depthAxis = new Vector3(0.0f, 0.0f, -1.0f);

        // WorldBuilder 生成的占位 Tilemap（纯色宣纸底），水墨地面启用后需把它隐藏，
        // 否则它会盖在 InkGroundRich Plane 上面，看起来仍是纯色地面。
        private TilemapRenderer _worldTilemapRenderer;

        // 雾的原始设置，Unload 时原样还原（非侵入）
        private bool _fogSaved;
        private bool _prevFogEnabled;
        private Color _prevFogColor;
        private FogMode _prevFogMode;
        private float _prevFogStart;
        private float _prevFogEnd;

        // =====================================================================
        // Unity 生命周期
        // =====================================================================

        private void Awake()
        {
            ResolveCameraRig();
            UpgradeLegacyDefaults();
        }

        /// <summary>
        /// 自动把旧版高密度默认值升级到当前性能默认值。
        /// 场景组件序列化值不会随脚本默认值刷新，故在运行时做一次一次性修正。
        /// </summary>
        private void UpgradeLegacyDefaults()
        {
            // 旧版默认：身边 28 根 + 世界铺满 180 根 + 5 片叶 = 近 15000 Batches。
            // 也处理部分升级场景（如 bambooCount/worldBambooCount 已改但 leavesMax 仍为旧值 5）。
            // 2026-08-17：再加「半径 >=20」判定，旧场景里的粗黑大竹子自动变细长。
            bool isLegacy = (bambooCount == 28 && worldBambooCount == 180 && worldFill && leavesMax >= 5)
                || (bambooCount == 13 && worldBambooCount == 22 && !worldFill && leavesMax >= 5)
                || (trunkRadius >= 20.0f);
            if (!isLegacy)
            {
                return;
            }

            bambooCount = 13;
            worldBambooCount = 22;
            worldFill = false;
            leavesMin = 4;
            leavesMax = 6;
            trunkRadius = 9.0f;
            heightMin = 360.0f;
            heightMax = 520.0f;
            bambooMinDist = 85.0f;
            bambooLeanAngle = 16.0f;
            Debug.Log("[2.5D][BambooSceneContext] 检测到旧版默认值，已自动升级：bambooCount=13, worldBambooCount=22, worldFill=false, leaves=4-6, trunkRadius=9, height=360-520, bambooMinDist=85, bambooLeanAngle=16。");
        }

        private void OnEnable()
        {
            // 仅运行时（PlayMode）自动生成竹林；编辑态不生成，避免把运行时生成的子物体
            // 序列化进场景文件、重载后 OnEnable 再生成一次导致「双份竹林」。
            // 想预览就直接进 PlayMode；菜单放置也不在编辑态强制生成。
            if (Application.isPlaying && !_loaded)
            {
                Load();
            }
        }

        private void OnDestroy()
        {
            // 场景重载 / 对象销毁时兜底清理（含还原雾设置）。
            Unload();
        }

        // =====================================================================
        // 公开接口
        // =====================================================================

        /// <summary>
        /// 程序化生成竹林 + 地面 + 雾层，并配置相机取景。
        ///
        /// 对标 WorldBuilder.BuildScene 的「世界根」思路，但本根**不挂**
        /// ShuimoGenerated 标记 —— 因此 DestroyGeneratedRoots()（菜单 Clean / 按 R 重开）
        /// 不会误删竹林。自幂等：重复调用会先 Unload 再重建。
        /// </summary>
        public void Load()
        {
            if (_loaded)
            {
                Unload();
            }

            // 幂等保护：编辑态预览后子物体可能被序列化进场景、域重载导致 _loaded 复位，
            // 进入 Play 时再次 Load 会产生「双份竹林」。这里先把已存在的根清掉。
            Transform existing = transform.Find(GroveRootName);
            if (existing != null)
            {
                SafeDestroy(existing.gameObject);
            }

            ResolveDepthAxis();
            EnsureMaterials();

            _groveRoot = new GameObject(GroveRootName).transform;
            _groveRoot.SetParent(transform, false);
            _groveRoot.localPosition = Vector3.zero;
            _groveRoot.localRotation = Quaternion.identity;
            _groveRoot.localScale = Vector3.one;
            // 注意：这里**故意不调** ShuimoGenerated.Mark(...)，见文件头红线 1。

            // 把竹林根对准玩家出生点附近，确保进入 PlayMode 后相机立刻被竹林包围，
            // 而不是漂到远离玩家的世界原点（否则玩家看不到竹、误以为没放进来）。
            // 玩家晚于本组件生成也没关系：BindPlayer 里会再对准一次。
            _groveRoot.localPosition = (Vector3)ResolveGroveCenter();

            BuildGround();
            TakeOverWorldTilemap();   // 关闭 WorldBuilder 的纯色 Tilemap，避免盖住水墨地面
            ApplyFog();

            int target = Mathf.Clamp(bambooCount, MinBambooCount, MaxBambooCount);
            PCG32 rng = ZoneSeed.CreateRng(zoneSeedId, false, 0);
            ScatterPositions(rng, target);

            for (int i = 0; i < _bambooPlan.Count; i++)
            {
                float height = rng.NextRange(heightMin, heightMax);
                BambooVfx vfx = BuildOneBamboo(i, _bambooPlan[i], height, rng);
                if (vfx != null)
                {
                    _bamboos.Add(vfx);
                }
            }

            // 世界铺满：在整张地图再撒一层静态竹林，构成完整竹林世界（不随玩家移动）。
            if (worldFill)
            {
                BuildWorldField(rng);
            }

            _loaded = true;
            ApplyCameraFraming();

            int renderers = CountMeshRenderersIn(_groveRoot);
            if (_worldRoot != null)
            {
                renderers += CountMeshRenderersIn(_worldRoot);
            }

            Debug.Log(string.Format(
                "[2.5D] 竹林已生成：{0} 根（目标 {1}），几何来源 = {2}，深度轴 = {3}，MeshRenderer 总数 = {4}。根节点未挂世界生成清理标记。",
                _bamboos.Count, target,
                (bambooModelPrefab != null && !forcePrimitiveBamboo) ? "bamboo_ink 模型" : "primitives 兜底",
                _depthAxis, renderers));
        }

        private static int CountMeshRenderersIn(Transform root)
        {
            if (root == null)
            {
                return 0;
            }
            MeshRenderer[] mrs = root.GetComponentsInChildren<MeshRenderer>(true);
            return mrs != null ? mrs.Length : 0;
        }

        /// <summary>
        /// 竹林中心：让玩家一进 PlayMode 就身处竹林之中。
        /// 优先级：玩家出生点 → WorldBuilder 已生成世界时取其中心 → 世界原点。
        /// 这样无论 BuildScene 与本组件 OnEnable 的先后，竹林都不会漂到玩家视野外。
        /// </summary>
        private Vector2 ResolveGroveCenter()
        {
#if UNITY_2023_1_OR_NEWER
            PlayerController pc = Object.FindFirstObjectByType<PlayerController>();
#else
            PlayerController pc = Object.FindObjectOfType<PlayerController>();
#endif
            if (pc != null)
            {
                return new Vector2(pc.transform.position.x, pc.transform.position.y);
            }
            if (WorldBuilder.Grid != null && WorldBuilder.Width > 0 && WorldBuilder.Height > 0)
            {
                return new Vector2(WorldBuilder.Width * WorldBuilder.TileUnit * 0.5f,
                                   WorldBuilder.Height * WorldBuilder.TileUnit * 0.5f);
            }
            return Vector2.zero;
        }

        /// <summary>
        /// 销毁竹林根（仅自身生成的子节点），还原雾设置、释放自建材质。
        /// 不触碰 WorldBuilder 的根、不改任何战斗静态状态。
        /// </summary>
        public void Unload()
        {
            _bamboos.Clear();
            _bambooPlan.Clear();
            _registeredSortables.Clear();
            _harvestTargets.Clear();

            UnsubscribeT3();

            if (_groveRoot != null)
            {
                SafeDestroy(_groveRoot.gameObject);
                _groveRoot = null;
            }

            if (_worldRoot != null)
            {
                SafeDestroy(_worldRoot.gameObject);
                _worldRoot = null;
            }

            RestoreWorldTilemap();
            RestoreFog();

            for (int i = 0; i < _ownedMaterials.Count; i++)
            {
                if (_ownedMaterials[i] != null)
                {
                    SafeDestroy(_ownedMaterials[i]);
                }
            }
            _ownedMaterials.Clear();

            _loaded = false;
        }

        /// <summary>
        /// 缓存玩家 Transform，调用 <c>cameraRig.BindTarget(player)</c> 并重配相机取景。
        ///
        /// 玩家由 WorldBuilder.BuildPlayer 生成，本类**不修改 WorldBuilder**，
        /// 只惰性解析引用、只读其组件。
        /// </summary>
        /// <param name="player">女主 Transform；传 null 表示解绑。</param>
        public void BindPlayer(Transform player)
        {
            _player = player;

            if (player != null)
            {
                _playerController = player.GetComponent<PlayerController>();
                _attack = player.GetComponent<AttackController>();
                // 轮次 C：识别并解析角色视图（统一挂载约定：每个角色根节点有且只有一个 CharacterView）。
                _playerView = CharacterView.ResolveOn(player);
                // 绑定瞬间同步一次挥砍计数，避免把绑定前累积的挥砍当成新边沿一次性补刀。
                _lastSwingCount = _attack != null ? _attack.SwingCount : 0;
            }
            else
            {
                _playerController = null;
                _attack = null;
                _playerView = null;
                _lastSwingCount = 0;
            }

            ResolveCameraRig();
            if (cameraRig != null)
            {
                cameraRig.BindTarget(player);
            }

            // 玩家首次绑定时把竹林根对准玩家出生点：Load 时的 best-effort 可能早于玩家生成，
            // 这里补一次，保证竹林始终在玩家视野内（只钉在出生点，不随玩家走动而跟随）。
            if (_groveRoot != null && _player != null)
            {
                _groveRoot.localPosition = new Vector3(_player.position.x, _player.position.y, 0.0f);
            }

            ApplyCameraFraming();
        }

        // =====================================================================
        // 每帧：轮询挥砍边沿 + 伪深度排序
        // =====================================================================

        private void Update()
        {
            if (!_loaded)
            {
                return;
            }

            EnsurePlayerBound();

            // 暂停语义：只读 CombatBridge.IsGameplayBlocked（= IsRunOver || _menuPaused）。
            // 绝不读写 CombatScheduler.Paused、绝不碰 Time.timeScale。
            CombatBridge bridge = ResolveBridge();
            bool blocked = bridge != null && bridge.IsGameplayBlocked;

            // 监听 T3 技能事件：T3 开启后 AttackController.SwingCount 不再增长，
            // 竹子检测必须同步订阅内核技能施放事件，否则普攻砍不到竹子。
            ResolveT3Subscription(bridge);

            if (_attack != null)
            {
                int swings = _attack.SwingCount;
                bool attackEdge = swings > _lastSwingCount;
                // 无论是否冻结都同步计数：否则解冻瞬间会把暂停期间累积的挥砍一次性补砍出来。
                _lastSwingCount = swings;

            if (attackEdge && !blocked)
            {
                Vector2 facing = _playerController != null ? _playerController.LastFacing : Vector2.right;
                // 选项A·竹剑切割特效：挥砍边沿播放月牙剑气（吃 FeedbackClock.Delta，顿帧同步冻结）。
                VfxSlash.Play(_player.position, facing, harvestRadius, harvestArcDeg);
                DetectHarvest(facing);
                // 轮次 C：挥砍边沿驱动玩家视图攻击状态。
                if (_playerView != null)
                {
                    _playerView.PlayState(CharacterAnimState.Attack);
                }
            }
            }

            if (!blocked)
            {
                // 轮次 C：每帧维护玩家朝向 + 推进动画（CharacterView.Tick 内部只读 FeedbackClock.Delta，
                // 顿帧同步冻结；不写 FeedbackClock.Frozen、不碰 Time.timeScale）。
                if (_playerView != null && _playerController != null)
                {
                    _playerView.SetFacing(_playerController.LastFacing);
                    _playerView.Tick();
                }
                ApplyDepthSort();
            }
        }

        /// <summary>
        /// 90° 扇形命中检测（玩法平面 XY），命中竹子调 <see cref="BambooVfx.OnHit"/>。
        ///
        /// 几何思路复用 AttackController.ResolveHits（半径 + 半角余弦点乘），
        /// 但**完全自管**：不调 DamageResolver、不发内核事件、不把竹子登记为 Combatant。
        /// </summary>
        /// <param name="facing">玩家朝向（XY，未必归一）。</param>
        private void DetectHarvest(Vector2 facing)
        {
            if (_player == null)
            {
                return;
            }

            if (facing.sqrMagnitude < 1e-6f)
            {
                facing = Vector2.right;
            }
            facing = facing.normalized;

            Vector2 origin = new Vector2(_player.position.x, _player.position.y);
            float damage = EffectiveRaw();
            float halfArcCos = Mathf.Cos(harvestArcDeg * 0.5f * Mathf.Deg2Rad);
            float reach = harvestRadius + trunkRadius;
            float reachSqr = reach * reach;

            for (int i = 0; i < _bamboos.Count; i++)
            {
                BambooVfx vfx = _bamboos[i];
                if (vfx == null || vfx.IsBroken)
                {
                    continue;
                }

                Vector3 wp = vfx.transform.position;
                Vector2 delta = new Vector2(wp.x - origin.x, wp.y - origin.y);
                float distSqr = delta.sqrMagnitude;
                if (distSqr > reachSqr)
                {
                    continue;
                }

                Vector2 dir = distSqr > 1e-6f ? delta / Mathf.Sqrt(distSqr) : facing;
                if (distSqr > 1e-6f && Vector2.Dot(dir, facing) < halfArcCos)
                {
                    continue;
                }

                // 命中点取竹竿朝玩家一侧的表面，粒子从「刀口」而不是竹心冒出来。
                Vector2 hitPoint = new Vector2(wp.x, wp.y) - dir * trunkRadius;
                vfx.OnHit(damage, hitPoint, dir);
                _totalHarvestHits++;
            }

            // 轮次 C：把 EnemyNpcSpawner 暴露的 harvestable 集并入同一扇形判定（零内核改动，
            // 仅扩展 2.5D 内命中集）。命中即对目标上的 CharacterView 播 Hit 受击表现。
            for (int i = 0; i < _harvestTargets.Count; i++)
            {
                Transform ht = _harvestTargets[i];
                if (ht == null)
                {
                    continue;
                }

                CharacterView cv = ht.GetComponent<CharacterView>();
                if (cv == null)
                {
                    continue;
                }

                Vector3 wp2 = ht.position;
                Vector2 delta2 = new Vector2(wp2.x - origin.x, wp2.y - origin.y);
                float distSqr2 = delta2.sqrMagnitude;
                if (distSqr2 > reachSqr)
                {
                    continue;
                }

                Vector2 dir2 = distSqr2 > 1e-6f ? delta2 / Mathf.Sqrt(distSqr2) : facing;
                if (distSqr2 > 1e-6f && Vector2.Dot(dir2, facing) < halfArcCos)
                {
                    continue;
                }

                cv.PlayState(CharacterAnimState.Hit);
                _totalHarvestHits++;
            }
        }

        /// <summary>
        /// 有效伤害 = AttackController.AttackRaw(12) + CombatBridge.PlayerAtkBonus。
        /// 与 AttackController.ResolveHits 的 effectiveRaw 同公式 —— 竹子断裂节奏
        /// 因此随玩家等级成长，这就是「复用伤害管线」的落地方式（只读，不进内核）。
        /// </summary>
        private float EffectiveRaw()
        {
            CombatBridge bridge = ResolveBridge();
            float bonus = bridge != null ? bridge.PlayerAtkBonus : 0.0f;
            return AttackController.AttackRaw + bonus;
        }

        /// <summary>
        /// Y 轴伪深度排序：把每根竹子按「与玩家的 Y 差」推到深度轴上，
        /// 使 Y 小于玩家的竹子更靠近相机（遮住女主），Y 大于玩家的退到女主身后。
        ///
        /// 玩家自身**不动**（z 恒为其原值），因此 PlayerController 一行不改。
        /// 最终遮挡由标准 3D 深度缓冲解决（竹子 Opaque 写深度，女主 Sprite 做深度测试）。
        /// </summary>
        // 深度排序守卫：玩家不动时跳过整轮重排，避免每帧对全部竹子写 transform.z。
        private float _lastSortPlayerY = float.NaN;
        private float _lastSortPlayerZ = float.NaN;

        private void ApplyDepthSort()
        {
            if (!depthSortEnabled || _player == null)
            {
                return;
            }

            float playerY = _player.position.y;
            float playerZ = _player.position.z;

            // 玩家未移动（含首帧 NaN 兜底：首帧一定跑一次）则跳过本轮，省掉几百次 transform 写入。
            if (!float.IsNaN(_lastSortPlayerY) && !float.IsNaN(_lastSortPlayerZ)
                && Mathf.Abs(playerY - _lastSortPlayerY) < 0.01f
                && Mathf.Abs(playerZ - _lastSortPlayerZ) < 0.01f)
            {
                return;
            }
            _lastSortPlayerY = playerY;
            _lastSortPlayerZ = playerZ;

            // 竹子：逻辑等价原实现，统一改调 DepthSortUtility.Apply（轮次 C 抽出共享）。
            for (int i = 0; i < _bamboos.Count; i++)
            {
                BambooVfx vfx = _bamboos[i];
                if (vfx == null)
                {
                    continue;
                }

                DepthSortUtility.Apply(vfx.transform, playerY, playerZ, _depthAxis, ySortToDepth);
            }

            // 轮次 C：对 EnemyNpcSpawner 注册进来的角色（敌人 / NPC）做同一套深度排序。
            for (int i = 0; i < _registeredSortables.Count; i++)
            {
                DepthSortUtility.Apply(_registeredSortables[i], playerY, playerZ, _depthAxis, ySortToDepth);
            }
        }

        // =====================================================================
        // 生成：撒点 / 单根竹子 / 地面 / 雾
        // =====================================================================

        /// <summary>确定性泊松式撒点：矩形域内带最小间距的拒绝采样。</summary>
        private void ScatterPositions(PCG32 rng, int targetCount)
        {
            _bambooPlan.Clear();

            float half = Mathf.Max(1.0f, groveHalfExtent);
            float minDistSqr = bambooMinDist * bambooMinDist;
            // 上限防死循环：密度过高时自然收敛到少于 targetCount，不卡帧。
            int maxAttempts = targetCount * 60;

            for (int attempt = 0; attempt < maxAttempts && _bambooPlan.Count < targetCount; attempt++)
            {
                float x = rng.NextRange(-half, half);
                float y = rng.NextRange(-half, half);
                Vector2 candidate = new Vector2(x, y);

                bool ok = true;
                for (int i = 0; i < _bambooPlan.Count; i++)
                {
                    if ((_bambooPlan[i] - candidate).sqrMagnitude < minDistSqr)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    _bambooPlan.Add(candidate);
                }
            }

            if (_bambooPlan.Count < MinBambooCount)
            {
                Debug.LogWarning(string.Format(
                    "[2.5D] 撒点只放下 {0} 根竹子（目标 {1}）。请调小 bambooMinDist({2}) 或调大 groveHalfExtent({3})。",
                    _bambooPlan.Count, targetCount, bambooMinDist, groveHalfExtent));
            }
        }

        /// <summary>
        /// 世界铺满：在整张地图（默认 WorldBuilder 已生成的 120×80×32 世界，或退化到玩家周边大区域）
        /// 用「网格 + 抖动」撒一层静态竹林，挂在 _worldRoot（世界原点、恒等变换）下，故局部坐标即世界坐标。
        /// 这部分竹子**不随玩家移动**，用于让整张地图看起来都是竹林，而非只有玩家身边一小丛。
        /// 与玩家身边的小丛（_groveRoot）共用 BuildOneBamboo / BambooVfx / 深度排序 / 收割命中逻辑。
        /// </summary>
        private void BuildWorldField(PCG32 rng)
        {
            _worldRoot = new GameObject("WorldBambooField").transform;
            _worldRoot.SetParent(transform, false);
            _worldRoot.localPosition = Vector3.zero;
            _worldRoot.localRotation = Quaternion.identity;
            _worldRoot.localScale = Vector3.one;

            // 世界范围：优先用 WorldBuilder 已生成的世界尺寸；否则退化到「以竹林中心为原点的大方块」。
            float worldW, worldH;
            if (WorldBuilder.Width > 0 && WorldBuilder.Height > 0)
            {
                worldW = WorldBuilder.WorldWidth;
                worldH = WorldBuilder.WorldHeight;
            }
            else
            {
                Vector2 c = ResolveGroveCenter();
                worldW = c.x * 2.0f + 3800.0f;
                worldH = c.y * 2.0f + 2600.0f;
            }

            float margin = worldMinDist * 0.5f;
            float usableW = Mathf.Max(1.0f, worldW - margin * 2.0f);
            float usableH = Mathf.Max(1.0f, worldH - margin * 2.0f);
            int cols = Mathf.Max(1, Mathf.FloorToInt(usableW / worldMinDist));
            int rows = Mathf.Max(1, Mathf.FloorToInt(usableH / worldMinDist));
            int cap = Mathf.Max(0, worldBambooCount);
            int idx = 0;
            int placed = 0;

            for (int r = 0; r < rows && placed < cap; r++)
            {
                for (int c = 0; c < cols && placed < cap; c++)
                {
                    float jx = rng.NextRange(-worldMinDist * 0.4f, worldMinDist * 0.4f);
                    float jy = rng.NextRange(-worldMinDist * 0.4f, worldMinDist * 0.4f);
                    float x = margin + (c + 0.5f) * worldMinDist + jx;
                    float y = margin + (r + 0.5f) * worldMinDist + jy;
                    float h = rng.NextRange(heightMin, heightMax);
                    BambooVfx v = BuildOneBamboo(idx++, new Vector2(x, y), h, rng, _worldRoot);
                    if (v != null)
                    {
                        _bamboos.Add(v);
                        placed++;
                    }
                }
            }

            Debug.Log(string.Format(
                "[2.5D] 世界铺满：额外撒下 {0} 根静态竹林（世界范围 {1:0}x{2:0}，网格 {3}x{4}）。",
                placed, worldW, worldH, cols, rows));
        }

        /// <summary>
        /// 生成一根竹子：父节点挂 BambooVfx，子节点是竹竿 + 竹叶。
        /// 若 <see cref="bambooModelPrefab"/> 已配置则用真模型（路线 A），否则 primitives（路线 B）。
        /// </summary>
        private BambooVfx BuildOneBamboo(int index, Vector2 planXY, float height, PCG32 rng, Transform parent = null)
        {
            GameObject root = new GameObject(string.Format("Bamboo_{0:D2}", index));
            root.transform.SetParent(parent != null ? parent : _groveRoot, false);
            root.transform.localPosition = new Vector3(planXY.x, planXY.y, 0.0f);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            // 每根竹子有独立的生长方向：沿世界 +Y（屏幕「上」）向上，再随机向某个水平
            // 方位歪斜一点，模拟自然竹林，避免像笔直黑柱。
            Vector3 growDir = SampleGrowthDirection(rng);

            Transform trunk = null;
            if (bambooModelPrefab != null && !forcePrimitiveBamboo)
            {
                trunk = BuildTrunkFromModel(root.transform, height, growDir);
            }

            if (trunk == null)
            {
                trunk = BuildTrunkFromPrimitive(root.transform, height, rng, growDir);
                BuildLeaves(trunk, height, rng, growDir);
            }

            AddSoftCollider(root, height, growDir);

            BambooVfx vfx = root.AddComponent<BambooVfx>();
            vfx.Configure(trunk, growDir, height, trunkRadius, inkLeafPrefab, leafMaterial);
            vfx.OnBroken += HandleBambooBroken;
            return vfx;
        }

        /// <summary>
        /// 采样单根竹子的生长方向：真实竹子从地面沿世界 +Y（本工程玩法平面是 XY，
        /// +Y 即屏幕「上」，与女主站立方向一致）向上生长，而不是沿深度轴戳向相机。
        /// 每根再随机向某个水平方向歪斜一点，模拟自然竹林，避免像笔直黑柱。
        /// 倾斜上限由 bambooLeanAngle 控制，0 = 全部笔直朝天。
        /// </summary>
        private Vector3 SampleGrowthDirection(PCG32 rng)
        {
            float maxRad = Mathf.Clamp(bambooLeanAngle, 0.0f, 60.0f) * Mathf.Deg2Rad;
            if (maxRad < 0.001f)
            {
                return Vector3.up;
            }

            // 随机水平歪斜方向（XZ 平面内，垂直于「上」），决定竹子往哪个方位倾。
            float theta = rng.NextRange(0.0f, Mathf.PI * 2.0f);
            Vector3 leanDir = new Vector3(Mathf.Cos(theta), 0.0f, Mathf.Sin(theta));

            // 绕「上 × 歪斜方向」这根水平轴旋转，把 +Y 倾到 leanDir 一侧。
            float leanRad = rng.NextRange(0.0f, maxRad);
            Vector3 axis = Vector3.Cross(Vector3.up, leanDir);
            if (axis.sqrMagnitude < 1e-6f)
            {
                axis = Vector3.right;
            }
            return Quaternion.AngleAxis(leanRad * Mathf.Rad2Deg, axis.normalized) * Vector3.up;
        }

        /// <summary>路线 A：实例化套好水墨材质的 bamboo_ink 模型，并缩放到目标高度。</summary>
        private Transform BuildTrunkFromModel(Transform parent, float height, Vector3 growDir)
        {
            GameObject inst = Instantiate(bambooModelPrefab);
            inst.name = "Trunk_Model";
            inst.transform.SetParent(parent, false);

            // 模型的「上」是它自己的 +Y，先转到本根竹子的实际生长方向。
            inst.transform.localRotation = Quaternion.FromToRotation(Vector3.up, growDir);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localScale = Vector3.one;

            float modelHeight = MeasureLocalHeight(inst, growDir);
            if (modelHeight > 1e-4f)
            {
                float scale = height / modelHeight;
                inst.transform.localScale = new Vector3(scale, scale, scale);
            }

            // FBX 自带的碰撞体一律转 Trigger，避免它参与物理把女主顶飞。
            Collider[] cols = inst.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                cols[i].isTrigger = true;
            }

            // 强制模型自带材质也开 Instancing：否则 bamboo_ink 多子网格/多材质时
            // 每根竹子会复制出几十个独立 MeshRenderer，draw call 直接爆（曾现 4402 Batches）。
            // 这一步让「用模型」和「用 primitives」一样能塌成个位数 draw call。
            MeshRenderer[] mrs = inst.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                if (mrs[i].sharedMaterial != null && !mrs[i].sharedMaterial.enableInstancing)
                {
                    mrs[i].sharedMaterial.enableInstancing = true;
                }
            }

            return inst.transform;
        }

        /// <summary>路线 B 兜底：Cylinder 竹竿，沿 growDir 生长。</summary>
        private Transform BuildTrunkFromPrimitive(Transform parent, float height, PCG32 rng, Vector3 growDir)
        {
            GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(parent, false);

            // Unity Cylinder：局部 +Y 生长，原始高 2 单位、半径 0.5 单位（scale=1 时）。
            trunk.transform.localRotation = Quaternion.FromToRotation(Vector3.up, growDir);
            trunk.transform.localScale = new Vector3(trunkRadius * 2.0f, height * 0.5f, trunkRadius * 2.0f);
            // 竹根落在玩法平面，竹心沿生长方向推到半高处。
            trunk.transform.localPosition = growDir * (height * 0.5f);

            // primitive 自带的 CapsuleCollider 转 Trigger：只作命中几何，不参与物理。
            Collider selfCol = trunk.GetComponent<Collider>();
            if (selfCol != null)
            {
                selfCol.isTrigger = true;
            }

            MeshRenderer mr = trunk.GetComponent<MeshRenderer>();
            if (mr != null && trunkMaterial != null)
            {
                mr.sharedMaterial = trunkMaterial;
            }

            // 竹节：沿竿身等距放几圈略粗的短环，水墨竹的辨识度主要来自竹节。
            // 半径变细后竹节间距也略收，避免节环过疏。
            int nodes = Mathf.Clamp(Mathf.RoundToInt(height / 55.0f), 2, 6);
            for (int i = 1; i <= nodes; i++)
            {
                float t = (float)i / (nodes + 1);
                GameObject node = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                node.name = string.Format("Node_{0}", i);
                node.transform.SetParent(parent, false);
                node.transform.localRotation = trunk.transform.localRotation;
                node.transform.localPosition = growDir * (height * t);
                node.transform.localScale = new Vector3(
                    trunkRadius * 2.3f, height * 0.012f, trunkRadius * 2.3f);

                Collider nodeCol = node.GetComponent<Collider>();
                if (nodeCol != null)
                {
                    SafeDestroy(nodeCol);
                }

                MeshRenderer nodeMr = node.GetComponent<MeshRenderer>();
                if (nodeMr != null && trunkMaterial != null)
                {
                    nodeMr.sharedMaterial = trunkMaterial;
                }
            }

            return trunk.transform;
        }

        /// <summary>路线 B 兜底：3–5 片 Quad 竹叶，挂在竹竿上部，随机朝向。</summary>
        private void BuildLeaves(Transform trunk, float height, PCG32 rng, Vector3 growDir)
        {
            Transform parent = trunk.parent != null ? trunk.parent : trunk;
            int count = rng.NextRangeInt(Mathf.Max(1, leavesMin), Mathf.Max(leavesMin, leavesMax));

            for (int i = 0; i < count; i++)
            {
                GameObject leaf = GameObject.CreatePrimitive(PrimitiveType.Quad);
                leaf.name = string.Format("Leaf_{0}", i);
                leaf.transform.SetParent(parent, false);

                // 只挂在上部 60%–98% 区间，下部留干净竹竿。
                float t = rng.NextRange(0.60f, 0.98f);
                float yaw = rng.NextRange(0.0f, 360.0f);
                float pitch = rng.NextRange(-35.0f, 25.0f);
                // 半径变细后，叶片相对竿身略放大，避免竹梢太秃；整体略收窄更显竹叶细长。
                float len = rng.NextRange(trunkRadius * 3.0f, trunkRadius * 6.0f);
                float wide = rng.NextRange(trunkRadius * 0.7f, trunkRadius * 1.3f);

                // 先摆到本根竹子生长方向对齐的朝向，再叠加随机偏转，让叶片自然散开。
                Quaternion basis = Quaternion.FromToRotation(Vector3.up, growDir);
                leaf.transform.localRotation = basis * Quaternion.Euler(pitch, yaw, 0.0f);
                leaf.transform.localPosition = growDir * (height * t)
                    + leaf.transform.localRotation * new Vector3(len * 0.5f, 0.0f, 0.0f);
                leaf.transform.localScale = new Vector3(len, wide, 1.0f);

                Collider leafCol = leaf.GetComponent<Collider>();
                if (leafCol != null)
                {
                    SafeDestroy(leafCol);
                }

                MeshRenderer mr = leaf.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    if (leafMaterial != null)
                    {
                        mr.sharedMaterial = leafMaterial;
                    }
                    // 叶片是半透明面片，不投影（与 BambooInkImporter 的处理一致）。
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
        }

        /// <summary>
        /// 竹子软碰撞体（设计 §2.3 / D3：单根圆形软碰撞）。
        /// 一律 isTrigger —— 只作命中与软碰撞几何，不参与物理，
        /// 不会把走 Transform 移动的女主顶飞。
        /// </summary>
        private void AddSoftCollider(GameObject root, float height, Vector3 growDir)
        {
            CapsuleCollider cap = root.AddComponent<CapsuleCollider>();
            cap.isTrigger = true;
            cap.radius = trunkRadius;
            cap.height = height;
            // 竹竿沿世界 +Y（屏幕「上」）生长，胶囊碰撞体轴对齐 +Y（1）。
            cap.direction = 1; // 1 = Y 轴
            cap.center = growDir * (height * 0.5f);
        }

        /// <summary>地面：大 Plane，法线朝相机侧，置于玩法平面之后。</summary>
        private void BuildGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(_groveRoot, false);

            // Plane 原始 10×10 单位、法线 +Y；转到深度轴后法线朝相机。
            ground.transform.localRotation = Quaternion.FromToRotation(Vector3.up, _depthAxis);
            // 略微退到玩法平面之后，避免与 z=0 的女主精灵 z-fighting。
            ground.transform.localPosition = -_depthAxis * 2.0f;

            // 地面大小覆盖整个 WorldBuilder 世界（优先），避免竹林区域外露出相机底色。
            // Plane 默认 10×10 单位，scale = 目标尺寸 / 10。取正方形以简化旋转后的覆盖。
            float worldSize = groveHalfExtent * 2.0f;
            if (WorldBuilder.Grid != null && WorldBuilder.Width > 0 && WorldBuilder.Height > 0)
            {
                worldSize = Mathf.Max(WorldBuilder.Width * WorldBuilder.TileUnit,
                                      WorldBuilder.Height * WorldBuilder.TileUnit);
            }
            float s = Mathf.Max(1.0f, worldSize) / 10.0f;
            ground.transform.localScale = new Vector3(s, 1.0f, s);

            // 地面碰撞体没有用处，删掉以免干扰任何射线/物理查询。
            Collider col = ground.GetComponent<Collider>();
            if (col != null)
            {
                SafeDestroy(col);
            }

            MeshRenderer mr = ground.GetComponent<MeshRenderer>();
            if (mr != null && groundMaterial != null)
            {
                mr.sharedMaterial = groundMaterial;
            }
        }

        /// <summary>
        /// 关闭 WorldBuilder 生成的纯色 Terrain/Tilemap，避免它盖在 InkGroundRich 之上。
        /// 
        /// 为什么必须这样做：
        ///   - WorldBuilder 把 TilemapRenderer.sortingOrder 设为 -100，默认在最底；
        ///   - 但 Tilemap 的局部 Z 为 0，而 InkGroundRich Plane 被推到 -_depthAxis*2（z≈+2），
        ///     在正交相机下 z 越小越靠近相机，于是 Tilemap 反而挡在水墨地面之前；
        ///   - 两者都是宣纸白色，用户看到的是「纯色地面」。
        /// 
        /// 这里只关渲染器、不删对象，WorldBuilder.Grid 数据完整保留，玩法逻辑不受影响。
        /// Unload 时恢复，保证按 R 重开 / 编辑器清理后状态干净。
        /// </summary>
        private void TakeOverWorldTilemap()
        {
            RestoreWorldTilemap();

            GameObject worldRoot = GameObject.Find("Shumo_T2World");
            if (worldRoot == null) return;

            Transform terrain = worldRoot.transform.Find("Terrain");
            if (terrain == null) return;

            Transform tilemap = terrain.Find("Tilemap");
            if (tilemap == null) return;

            _worldTilemapRenderer = tilemap.GetComponent<TilemapRenderer>();
            if (_worldTilemapRenderer != null)
            {
                _worldTilemapRenderer.enabled = false;
                Debug.Log("[2.5D] 已隐藏 WorldBuilder 的纯色 Tilemap，改由 InkGroundRich 渲染水墨地面。");
            }
        }

        private void RestoreWorldTilemap()
        {
            if (_worldTilemapRenderer != null)
            {
                _worldTilemapRenderer.enabled = true;
                _worldTilemapRenderer = null;
            }
        }

        /// <summary>接管场景雾（Built-in RP），先把原设置存下来供 Unload 还原。</summary>
        private void ApplyFog()
        {
            if (!enableFog || _fogSaved)
            {
                return;
            }

            _prevFogEnabled = RenderSettings.fog;
            _prevFogColor = RenderSettings.fogColor;
            _prevFogMode = RenderSettings.fogMode;
            _prevFogStart = RenderSettings.fogStartDistance;
            _prevFogEnd = RenderSettings.fogEndDistance;
            _fogSaved = true;

            RenderSettings.fog = true;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
        }

        /// <summary>还原 Load 之前的雾设置。非侵入原则：我们改过的都要还回去。</summary>
        private void RestoreFog()
        {
            if (!_fogSaved)
            {
                return;
            }

            RenderSettings.fog = _prevFogEnabled;
            RenderSettings.fogColor = _prevFogColor;
            RenderSettings.fogMode = _prevFogMode;
            RenderSettings.fogStartDistance = _prevFogStart;
            RenderSettings.fogEndDistance = _prevFogEnd;
            _fogSaved = false;
        }

        // =====================================================================
        // 材质：运行时复刻 BambooInkImporter 的默认参数（不改 Importer 一行）
        // =====================================================================

        /// <summary>
        /// 三个材质字段为空时，运行时按 BambooInkImporter 的**同款默认参数**创建。
        /// D4 已拍板 Built-in 兼容，Xianxia/Ink/* 就是 Built-in surface shader，直接可用。
        /// </summary>
        private void EnsureMaterials()
        {
            if (trunkMaterial == null)
            {
                trunkMaterial = CreateRuntimeMaterial("Xianxia/Ink/BambooTrunk", "MAT_Ink_Trunk_Runtime");
                if (trunkMaterial != null)
                {
                    SetColorIfHas(trunkMaterial, "_InkBottom", new Color(0.12f, 0.18f, 0.10f, 1.0f));
                    SetColorIfHas(trunkMaterial, "_InkMid", new Color(0.20f, 0.29f, 0.15f, 1.0f));
                    SetColorIfHas(trunkMaterial, "_InkTop", new Color(0.30f, 0.39f, 0.21f, 1.0f));
                    SetFloatIfHas(trunkMaterial, "_GradStart", 0.0f);
                    // 竹高按本场景尺度（360–520 单位），渐变终点跟着放大才不会一片死墨。
                    SetFloatIfHas(trunkMaterial, "_GradEnd", heightMax);
                    SetFloatIfHas(trunkMaterial, "_NoiseScale", 12.0f);
                    SetFloatIfHas(trunkMaterial, "_NoiseStrength", 0.18f);
                    SetFloatIfHas(trunkMaterial, "_StrokeScale", 8.0f);
                    SetFloatIfHas(trunkMaterial, "_StrokeStrength", 0.12f);
                    SetFloatIfHas(trunkMaterial, "_EdgeInk", 0.25f);
                    SetFloatIfHas(trunkMaterial, "_Roughness", 0.6f);
                    SetFloatIfHas(trunkMaterial, "_NodeSpacing", 2.2f);
                    SetFloatIfHas(trunkMaterial, "_NodeWidth", 0.12f);
                    SetFloatIfHas(trunkMaterial, "_NodeInk", 0.38f);
                    SetColorIfHas(trunkMaterial, "_Color", new Color(0.22f, 0.31f, 0.15f, 1.0f));
                }
            }

            if (leafMaterial == null)
            {
                leafMaterial = CreateRuntimeMaterial("Xianxia/Ink/BambooLeaf", "MAT_Ink_Leaf_Runtime");
                if (leafMaterial != null)
                {
                    SetColorIfHas(leafMaterial, "_LeafColor", new Color(0.15f, 0.22f, 0.13f, 1.0f));
                    SetFloatIfHas(leafMaterial, "_Alpha", 0.92f);
                    SetFloatIfHas(leafMaterial, "_EdgeFade", 0.55f);
                    SetFloatIfHas(leafMaterial, "_NoiseScale", 16.0f);
                    SetFloatIfHas(leafMaterial, "_NoiseStrength", 0.25f);
                    SetFloatIfHas(leafMaterial, "_TipInk", 0.4f);
                    SetColorIfHas(leafMaterial, "_Color", new Color(0.15f, 0.22f, 0.13f, 1.0f));
                }
            }

            if (groundMaterial == null)
            {
                // 地面改用增强版水墨地表，与竹林同款重墨风格。
                groundMaterial = CreateRuntimeMaterial("Xianxia/Ink/InkGroundRich", "MAT_Ink_Ground_Rich_Runtime");
                // 增强版 Shader 不可用时退回已验证的 InkGround（Unlit，避免黑/纯色），
                // 最后才退 Built-in Diffuse（见 CreateRuntimeMaterial 的兜底链）。
                if (groundMaterial == null)
                {
                    groundMaterial = CreateRuntimeMaterial("Xianxia/Ink/InkGround", "MAT_Ink_Ground_Runtime");
                }
                if (groundMaterial != null)
                {
                    SetColorIfHas(groundMaterial, "_PaperColor", new Color(0.92f, 0.90f, 0.84f, 1.0f));
                    SetColorIfHas(groundMaterial, "_InkColor", new Color(0.42f, 0.39f, 0.33f, 1.0f));
                    SetColorIfHas(groundMaterial, "_InkDeep", new Color(0.12f, 0.11f, 0.09f, 1.0f));
                    SetFloatIfHas(groundMaterial, "_BlotScale", 2.0f);
                    SetFloatIfHas(groundMaterial, "_BlotStrength", 0.85f);
                    SetFloatIfHas(groundMaterial, "_StrokeAngle", 35.0f);
                    SetFloatIfHas(groundMaterial, "_StrokeScale", 2.5f);
                    SetFloatIfHas(groundMaterial, "_StrokeStrength", 0.65f);
                    SetFloatIfHas(groundMaterial, "_FineScale", 90.0f);
                    SetFloatIfHas(groundMaterial, "_FineStrength", 0.14f);
                    SetColorIfHas(groundMaterial, "_TintColor", new Color(0.84f, 0.86f, 0.92f, 1.0f));
                    SetFloatIfHas(groundMaterial, "_TintStrength", 0.05f);
                    SetColorIfHas(groundMaterial, "_Color", new Color(0.90f, 0.88f, 0.82f, 1.0f));
                }
                else
                {
                    Debug.LogError("[2.5D] 地面材质创建失败，地面将保持默认纯色。请检查 Xianxia/Ink/* 系列 Shader 是否编译通过。");
                }
            }

            // 强制 Inspector 拖进来的外部材质也开启 Instancing（用户拖的材质可能没勾选）。
            ForceInstancing(trunkMaterial);
            ForceInstancing(leafMaterial);
            ForceInstancing(groundMaterial);
        }

        private static void ForceInstancing(Material mat)
        {
            if (mat != null && !mat.enableInstancing)
            {
                mat.enableInstancing = true;
            }
        }

        /// <summary>
        /// 按 Shader 名建运行时材质；Shader 找不到时退到 Built-in 内置 Shader，
        /// 保证不会因为缺 Shader 就整片粉红。创建出来的材质登记进
        /// <see cref="_ownedMaterials"/>，Unload 时销毁。
        /// </summary>
        private Material CreateRuntimeMaterial(string shaderName, string materialName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning(string.Format(
                    "[2.5D] 找不到 Shader \"{0}\"，退回 Built-in 兜底。打包前请把 Xianxia/Ink/* 加进 "
                    + "Project Settings > Graphics > Always Included Shaders。", shaderName));
                shader = Shader.Find("Legacy Shaders/Diffuse");
            }

            if (shader == null)
            {
                shader = Shader.Find("Diffuse");
            }

            if (shader == null)
            {
                Debug.LogError("[2.5D] 连 Built-in 兜底 Shader 都找不到，竹子将使用 primitive 默认材质。");
                return null;
            }

            Material mat = new Material(shader);
            mat.name = materialName;
            mat.hideFlags = HideFlags.DontSave;
            // 性能：竹竿/竹叶/竹节都复用同一份共享材质 + 同一份内置 primitive 网格，
            // 开启 GPU Instancing 后，所有同网格实例塌成 1 个 Draw Call（1600+ → 个位数）。
            // 若所用 Shader 不支持实例化则 Unity 自动忽略，无副作用。
            mat.enableInstancing = true;
            _ownedMaterials.Add(mat);
            return mat;
        }

        private static void SetColorIfHas(Material m, string prop, Color value)
        {
            if (m != null && m.HasProperty(prop))
            {
                m.SetColor(prop, value);
            }
        }

        private static void SetFloatIfHas(Material m, string prop, float value)
        {
            if (m != null && m.HasProperty(prop))
            {
                m.SetFloat(prop, value);
            }
        }

        // =====================================================================
        // 相机 / 深度轴 / 惰性解析
        // =====================================================================

        /// <summary>
        /// 推导深度轴 = 「从玩法平面指向相机」的方向。
        /// 工程 2D 相机在 z = -100（WorldBuilder.cs:662），所以默认是 -Z。
        /// </summary>
        private void ResolveDepthAxis()
        {
            float sign = depthSign >= 0.0f ? 1.0f : -1.0f;

            if (autoDepthAxisFromCamera)
            {
                Camera cam = ResolveCamera();
                if (cam != null)
                {
                    float camZ = cam.transform.position.z;
                    if (Mathf.Abs(camZ) > 1e-3f)
                    {
                        sign = camZ >= 0.0f ? 1.0f : -1.0f;
                    }
                }
            }

            depthSign = sign;
            _depthAxis = new Vector3(0.0f, 0.0f, sign);
        }

        /// <summary>
        /// 覆写相机取景：正交尺寸对齐 2D（352），并按 cameraTiltDeg 做 2.5D 斜俯视。
        ///
        /// 关键：IsometricCameraRig.Awake 里写死的 Euler(45,45,0) 会让 XY 玩法平面
        /// 严重变形、女主精灵几乎侧过去。这里在 Awake 之后覆写 rig 的 offset，
        /// 并直接设置相机的 orthographicSize / rotation，得到「正视 XY 板 + 轻微俯仰」
        /// 的 2.5D 观感。rig 的 LateUpdate 只写 position，不会把旋转改回去。
        /// </summary>
        private void ApplyCameraFraming()
        {
            if (!overrideCameraFraming)
            {
                return;
            }

            ResolveCameraRig();
            Camera cam = ResolveCamera();
            if (cam == null)
            {
                return;
            }

            // 2.5D 模式由 IsometricCameraRig 独占相机跟随。禁用旧的 2D CameraFollow / CameraShake，
            // 否则它们每帧在 LateUpdate 里用 Z=-100 覆盖相机位置，与 2.5D 取景互相打架 → 画面抖动/卡住。
            CameraFollow follow2d = cam.GetComponent<CameraFollow>();
            if (follow2d != null && follow2d.enabled)
            {
                follow2d.enabled = false;
            }
            CameraShake shake2d = cam.GetComponent<CameraShake>();
            if (shake2d != null && shake2d.enabled)
            {
                shake2d.enabled = false;
            }

            cam.orthographic = true;
            cam.orthographicSize = orthographicSize;
            // 深度轴上要能装下最高的竹子 + 相机距离，否则近/远裁剪面会切掉竹梢。
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = Mathf.Max(1000.0f, cameraDistance + heightMax * 4.0f);
            // 透明精灵（女主、飘字）按深度轴排序，避免与竹子的次序抖动。
            cam.transparencySortMode = TransparencySortMode.CustomAxis;
            cam.transparencySortAxis = new Vector3(0.0f, 0.0f, 1.0f);

            float tilt = Mathf.Clamp(cameraTiltDeg, 0.0f, 70.0f);
            float rad = tilt * Mathf.Deg2Rad;
            float sin = Mathf.Sin(rad);
            float cos = Mathf.Cos(rad);

            // 相机沿深度轴退到 cameraDistance，并在 +Y 方向抬高，形成俯视斜角
            // （邓注：原 -Y 会把相机放到玩家下方往上看，变成"仰视平面图"；
            //  2.5D 应是从上方俯看，故取 +Y）。
            Vector3 offset = new Vector3(
                0.0f,
                cameraDistance * sin,
                _depthAxis.z * cameraDistance * cos);

            // 朝向：看向玩法平面原点方向。
            //
            // up 必须是「深度轴对 forward 做正交化」的结果，推导：
            //   forward = (0,  sin,  -dz*cos)        （dz = _depthAxis.z = ±1）
            //   up      = normalize(depthAxis - forward * dot(depthAxis, forward))
            //           = (0,  cos,   dz*sin)
            // 验算正交性：forward·up = sin*cos + (-dz*cos)(dz*sin) = 0 ✓（dz² = 1）
            //
            // 【这里踩过一个坑，别改回去】up 的 Z 分量若写成 -dz*sin，
            // forward·up 就变成 sin(2·tilt)，在 tilt = 45° 时两者完全平行，
            // Quaternion.LookRotation 直接退化报错、相机朝向失效；
            // 且默认 34° 时夹角已经很病态（sin68° ≈ 0.93）。
            // 用正交化后的 up，任意 tilt ∈ [0,70] 都稳定。
            // 注意：offset.y 改为 + 后，up 的 Z 分量须取 -_depthAxis.z*sin 才与 forward 正交
            // （推导见上方注释，原 +_depthAxis.z*sin 只在 -Y 偏移时成立）。
            Vector3 forward = (-offset).normalized;
            Vector3 upHint = new Vector3(0.0f, cos, -_depthAxis.z * sin);
            Quaternion rot = forward.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(forward, upHint)
                : Quaternion.identity;

            if (cameraRig != null)
            {
                cameraRig.orthographicSize = orthographicSize;
                cameraRig.offset = offset;
            }

            cam.transform.rotation = rot;

            if (_player != null)
            {
                cam.transform.position = _player.position + offset;
            }
        }

        /// <summary>惰性解析 IsometricCameraRig（Inspector 未拖引用时）。</summary>
        private void ResolveCameraRig()
        {
            if (cameraRig != null)
            {
                return;
            }

#if UNITY_2023_1_OR_NEWER
            cameraRig = Object.FindFirstObjectByType<IsometricCameraRig>();
#else
            cameraRig = Object.FindObjectOfType<IsometricCameraRig>();
#endif

            // 2.5D 模式下相机跟随必须由 IsometricCameraRig 独占。
            // 它此前既不在场景、也没被任何代码 AddComponent（本方法只 Find 不建），
            // 导致 2.5D 等距跟随从未运行、旧的 2D CameraFollow 每帧覆盖相机位置 → 画面错乱/抖动。
            // 这里补建到 Main Camera 上，让 2.5D 跟随真正生效。
            if (cameraRig == null)
            {
                Camera main = Camera.main;
                if (main != null)
                {
                    cameraRig = main.gameObject.AddComponent<IsometricCameraRig>();
                }
            }
        }

        /// <summary>取当前生效的相机：优先 rig 自身的 Camera，其次 Camera.main。</summary>
        private Camera ResolveCamera()
        {
            if (cameraRig != null)
            {
                Camera rigCam = cameraRig.GetComponent<Camera>();
                if (rigCam != null)
                {
                    return rigCam;
                }
            }
            return Camera.main;
        }

        /// <summary>
        /// 玩家未绑定时按节流间隔自动找一次 PlayerController 并绑定。
        /// 因为玩家由 WorldBuilder.BuildPlayer 生成，时序上可能晚于本组件 OnEnable。
        /// </summary>
        private void EnsurePlayerBound()
        {
            // 已绑定且引用有效时彻底跳过：不做 Time.deltaTime 减法、不进入重试计时。
            if (_player != null && _attack != null && _playerController != null)
            {
                return;
            }

            _playerRetry -= Time.deltaTime;
            if (_playerRetry > 0.0f)
            {
                return;
            }
            _playerRetry = ResolveRetryInterval;

#if UNITY_2023_1_OR_NEWER
            PlayerController pc = Object.FindFirstObjectByType<PlayerController>();
#else
            PlayerController pc = Object.FindObjectOfType<PlayerController>();
#endif
            if (pc != null)
            {
                BindPlayer(pc.transform);
            }
        }

        /// <summary>
        /// 惰性解析 CombatBridge（节流重试，模式与 HeroineAnimator.ResolveBridge 一致）。
        /// 只用来读 IsGameplayBlocked / PlayerAtkBonus 两个只读属性。
        /// </summary>
        private CombatBridge ResolveBridge()
        {
            if (_bridge != null)
            {
                return _bridge;
            }

            _bridgeRetry -= Time.deltaTime;
            if (_bridgeRetry > 0.0f)
            {
                return null;
            }
            _bridgeRetry = ResolveRetryInterval;

#if UNITY_2023_1_OR_NEWER
            _bridge = Object.FindFirstObjectByType<CombatBridge>();
#else
            _bridge = Object.FindObjectOfType<CombatBridge>();
#endif
            return _bridge;
        }

        // -----------------------------------------------------------------
        // T3 事件订阅：让 T3 开启后的普攻技能也能触发竹子检测
        // -----------------------------------------------------------------

        /// <summary>把 _eventsT3 挂到 CombatBridge.EventsT3 上，随 CombatBridge 重建自动重挂。</summary>
        private void ResolveT3Subscription(CombatBridge bridge)
        {
            if (bridge == null)
            {
                return;
            }
            CombatEventsT3Unity ev = bridge.EventsT3;
            if (ev == null)
            {
                return;
            }
            if (ev == _eventsT3)
            {
                return;
            }
            UnsubscribeT3();
            _eventsT3 = ev;
            _eventsT3.SkillCast += OnT3SkillCast;
        }

        private void UnsubscribeT3()
        {
            if (_eventsT3 != null)
            {
                _eventsT3.SkillCast -= OnT3SkillCast;
                _eventsT3 = null;
            }
        }

        /// <summary>T3 技能施放回调。玩家近战类技能（ActionKind.Attack + 有伤害）触发扇形砍竹检测。</summary>
        private void OnT3SkillCast(Combatant caster, SkillDef def, Vector2 facing)
        {
            if (_player == null || caster == null || caster.Faction != Faction.Player)
            {
                return;
            }
            if (def == null || def.Action != ActionKind.Attack || !def.DealsDamage)
            {
                return;
            }
            if (facing.sqrMagnitude < 1e-6f)
            {
                facing = Vector2.right;
            }
            DetectHarvest(facing.normalized);
        }

        // =====================================================================
        // 工具
        // =====================================================================

        /// <summary>量一个实例在生长方向（已转到 growDir）上的包围盒长度。</summary>
        private float MeasureLocalHeight(GameObject inst, Vector3 growDir)
        {
            Renderer[] rs = inst.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0)
            {
                return 0.0f;
            }

            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++)
            {
                b.Encapsulate(rs[i].bounds);
            }

            // 投影到生长轴方向上的跨度（现在生长轴是 +Y 附近，取沿该轴的投影最稳）。
            Vector3 axis = growDir.sqrMagnitude > 1e-6f ? growDir.normalized : Vector3.up;
            float proj = Mathf.Abs(Vector3.Dot(b.size, axis));
            return proj > 1e-4f ? proj : Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        }

        /// <summary>编辑器/运行时通用销毁。不使用任何战斗内核 API。</summary>
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
