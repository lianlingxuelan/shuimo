// -----------------------------------------------------------------------------
// WorldBuilder.cs —— 世界生成总装配（asmdef: Xianxia.Unity.T2）
//
// 【一句话职责】把 zones.json 的 zone_youhuang 变成一个能玩的场景：
// 地形 Tilemap、玩家、相机、战斗内核、敌人、HUD，全部由代码长出来。
//
// 【坐标系约定（架构文档 §7）】
//   1 tile = 32 世界单位；zone_youhuang 是 120×80 tile ⇒ 3840×2560 单位。
//   世界原点在左下角 (0,0)，tile(0,0) 的中心在 (16,16)。
//   常量集中在本文件顶部，任何地方都不许再写 32 这个字面量。
//
// 【确定性】
// 全部随机来自 ZoneSeed.CreateRng(zoneId, isSafe, visits) 派生的同一条 PCG32 流。
// 禁止 UnityEngine.Random、禁止 System.Random —— 它们让「同种子同世界」这条
// 验收（PRD P0-03）当场失效，而且失效得毫无征兆。
//
// 【地形与怪群各走一条独立的流，而不是共用一条】
// 两者都用 ZoneSeed.CreateRng(同一区域, 同一 visits) 各自 new 一条 PCG32
// （地形在这里、怪群在 CombatController.BuildEncounter 里）。
// 同种子必然同结果，但两条流互不干扰——改一版地形算法不会让怪群花名册跟着变。
// 若共用一条流，「地形多抽了一个随机数」这种无害改动会连锁改掉所有敌人的
// 等级与词缀，回归时根本分不清是地形问题还是数值问题。
//
// 【幂等】
// BuildScene 开头先自己清一遍已生成的根节点，再清贴图缓存。这样无论是
// 菜单（先 Clean 再 Build）还是运行时兜底（直接 Build），连点两次都不会堆叠。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using Shuimo;
using Xianxia.Core;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>地块类型。索引即 <see cref="WorldBuilder.Grid"/> 里存的值。</summary>
    public enum TileKind
    {
        /// <summary>地面主色，可通行。</summary>
        Ground = 0,

        /// <summary>地面次色（斑驳感），可通行。</summary>
        Ground2 = 1,

        /// <summary>水面，不可作为出生点。</summary>
        Water = 2,

        /// <summary>岩石，不可作为出生点。</summary>
        Rock = 3
    }

    /// <summary>世界生成器。全静态：一个场景同时只有一个世界，没必要做成实例。</summary>
    public static class WorldBuilder
    {
        // ---------------------------------------------------------------------
        // 常量（架构文档 §7，禁止在别处重复定义）
        // ---------------------------------------------------------------------

        /// <summary>1 tile 对应的世界单位数。</summary>
        public const int TileUnit = 32;

        /// <summary>生成根节点名。Clean And Rebuild 靠 ShuimoGenerated 标记识别，名字仅为可读性。</summary>
        public const string RootName = "Shuimo_T2World";

        /// <summary>相机正交半高。704 单位可视高 ≈ 22 tile，与原型取景一致。</summary>
        public const float CameraOrthoSize = 352.0f;

        /// <summary>玩家占位方块边长（世界单位）。放大约 1.25 格（≈屏上 6%）以修复「玩家看不见」的比例缺陷。</summary>
        public const int PlayerBodySize = 40;

        /// <summary>敌人占位圆直径（世界单位）。</summary>
        public const int EnemyBodySize = 26;

        /// <summary>敌人首波生成的最小 / 最大半径（相对玩家出生点）。</summary>
        public const float SpawnRingMin = 260.0f;

        /// <summary>敌人首波生成的最大半径。</summary>
        public const float SpawnRingMax = 620.0f;

        // ---------------------------------------------------------------------
        // 生成结果（供 EnemySpawner / PlayerController / DeterminismDump 读取）
        // ---------------------------------------------------------------------

        /// <summary>地形格子。行主序，索引 = <c>y * Width + x</c>。未生成时为 null。</summary>
        public static TileKind[] Grid { get; private set; }

        /// <summary>地图宽（tile）。</summary>
        public static int Width { get; private set; }

        /// <summary>地图高（tile）。</summary>
        public static int Height { get; private set; }

        /// <summary>本次生成使用的区域定义。</summary>
        public static ZoneData Zone { get; private set; }

        /// <summary>本次生成使用的种子（<c>ZoneSeed.Derive</c> 的结果）。</summary>
        public static long Seed { get; private set; }

        /// <summary>玩家出生点（世界单位）。</summary>
        public static Vector2 PlayerSpawn { get; private set; }

        /// <summary>世界宽（单位）。</summary>
        public static float WorldWidth
        {
            get { return Width * (float)TileUnit; }
        }

        /// <summary>世界高（单位）。</summary>
        public static float WorldHeight
        {
            get { return Height * (float)TileUnit; }
        }

        // ---------------------------------------------------------------------
        // 入口
        // ---------------------------------------------------------------------

        /// <summary>
        /// 生成整个切片世界。由 <see cref="Bootstrap"/> 注册到
        /// <c>ShuimoSceneBuilder.BuildScene</c>，菜单与运行时兜底共用这一条路径。
        /// </summary>
        public static void BuildScene()
        {
            // ① 自清理 + 释放上一批贴图。放在最前面，保证本方法自身幂等：
            //    连点两次 Clean And Rebuild、或菜单清理后运行时又兜底一次，都不会堆叠。
            int removed = DestroyGeneratedRoots();
            SpriteFactory.Clear();

            // ② 区域数据 + 地形矩阵（纯数据，不碰 GameObject）
            ComputeWorldData();

            // ③ 世界根节点。所有生成物都挂它下面，只需给根打一个标记。
            GameObject root = new GameObject(RootName);
            root.transform.position = Vector3.zero;
            ShuimoGenerated.Mark(root);

            // 输入总线：单例，DefaultExecutionOrder(-300)，必须早于所有控制器采样。
            // 挂在世界根上而不是 Player 上 —— 它是全局服务，不该随玩家对象重建而丢失。
            // 没有它时 InputBinder 的静态门面会退回"仅键鼠"的 fallback，手柄完全读不到。
            root.AddComponent<InputBinder>();

            // ④ 地形渲染
            BuildTilemap(root.transform, Zone.Theme);

            // ⑤ 玩家
            Transform player = BuildPlayer(root.transform, PlayerSpawn);

            // ⑥ 相机（场景自带 Main Camera，不是生成物，因此只配置不创建）
            SetupCamera(player);

            // ⑦ 战斗内核 + 敌人 + HUD
            GameObject enemyTemplate = BuildEnemyTemplate(root.transform);
            BuildCombat(root.transform, player, enemyTemplate);
            BuildHud(root.transform);

            Debug.Log(string.Format(
                "[T2] 世界已生成：{0}（{1}×{2} tile / {3:F0}×{4:F0} 单位）seed={5} visits={6}，清理旧根节点 {7} 个。",
                Zone.DisplayName, Width, Height, WorldWidth, WorldHeight, Seed, Bootstrap.ZoneVisits, removed));
        }

        /// <summary>
        /// 只算数据、不建对象：区域定义、种子、地形矩阵、玩家出生点。
        /// 抽成独立方法是为了让 <see cref="EnsureRuntimeState"/> 能在不重建场景的
        /// 前提下把这批静态状态原样复现。
        /// </summary>
        private static void ComputeWorldData()
        {
            Zone = LoadZone(Bootstrap.ZoneId);

            // 把「是否安全区」这件事的唯一真源定为 zones.json，并回写进 Bootstrap。
            // 不同步的话就会出现：地形按 Zone.IsSafe 派种子、内核按 Bootstrap.IsSafeZone
            // 派种子——zone_youhuang 下两者恰好都是 false 所以看不出问题，
            // 一旦切到安全区，地形与怪群会静默用上两个不同的种子。
            Bootstrap.IsSafeZone = Zone.IsSafe;

            Seed = ZoneSeed.Derive(Zone.ZoneId, Zone.IsSafe, Bootstrap.ZoneVisits);

            ZoneTheme theme = Zone.Theme;
            Width = theme != null && theme.Width > 0 ? theme.Width : 120;
            Height = theme != null && theme.Height > 0 ? theme.Height : 80;

            PCG32 rng = ZoneSeed.CreateRng(Zone.ZoneId, Zone.IsSafe, Bootstrap.ZoneVisits);
            Grid = GenerateTerrain(theme, rng);

            PlayerSpawn = FindSpawnableNear(Width / 2, Height / 2);
        }

        /// <summary>
        /// 补齐运行时静态状态（Grid / Zone / Seed）。
        ///
        /// 【为什么必须有这一步】
        /// 在编辑器里点 Clean And Rebuild 生成好世界之后再按 Play，Unity 会做一次
        /// **域重载**：场景对象（Tilemap、Player）原样保留并反序列化，但**所有静态字段
        /// 被清空**。于是 Grid 变 null，落点校验把每一格都当成岩石、HUD 区域名变问号。
        /// 这个 bug 只在「先构建后 Play」的顺序下出现，纯 Play 或纯编辑器都看不到，
        /// 是最容易漏到 QA 的一类。
        ///
        /// 【为什么重算就是对的】
        /// 地形是 (zoneId, isSafe, visits) 的纯函数，重算必然逐格等于场景里那份，
        /// 不需要也不应该把 9600 格序列化进场景文件。
        /// </summary>
        public static void EnsureRuntimeState()
        {
            if (Grid != null && Zone != null && Grid.Length == Width * Height)
            {
                return;
            }
            ComputeWorldData();
        }

        /// <summary>场景里是否已存在生成的世界（<see cref="Bootstrap"/> 的兜底判定依据）。</summary>
        public static bool HasGeneratedWorld()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return false;
            }
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] != null && roots[i].GetComponent<ShuimoGenerated>() != null)
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // 区域数据
        // ---------------------------------------------------------------------

        /// <summary>
        /// 读取 zones.json 里的目标区域。
        ///
        /// 【为什么带一份内置兜底】
        /// 切片的价值在于「clone 下来点两下就能跑」。如果 Assets/Data/zones.json
        /// 被人挪走或改坏，这里直接抛异常会让场景生成到一半留下半个世界，
        /// 排查成本远高于「用内置默认值继续跑并打一条警告」。
        /// </summary>
        public static ZoneData LoadZone(string zoneId)
        {
            string path = Path.Combine(Application.dataPath, "Data/zones.json");
            try
            {
                if (File.Exists(path))
                {
                    ZoneDatabase db = ZoneLoader.LoadFromFile(path);
                    ZoneData z = db != null ? db.Get(zoneId) : null;
                    if (z != null)
                    {
                        return z;
                    }
                    Debug.LogWarning("[T2] zones.json 里没有区域 " + zoneId + "，改用内置兜底配置。");
                }
                else
                {
                    Debug.LogWarning("[T2] 找不到 " + path + "，改用内置兜底配置。");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[T2] 解析 zones.json 失败（" + e.Message + "），改用内置兜底配置。");
            }
            return FallbackZone(zoneId);
        }

        /// <summary>内置兜底区域，逐值对齐 zones.json 的 zone_youhuang。</summary>
        private static ZoneData FallbackZone(string zoneId)
        {
            ZoneData z = new ZoneData();
            z.ZoneId = string.IsNullOrEmpty(zoneId) ? "zone_youhuang" : zoneId;
            z.DisplayName = "幽篁竹海";
            z.Desc = "万竿修竹蔽日，溪流穿石而过。";
            z.Order = 2;
            z.IsSafe = false;
            z.Tier = 1;
            z.BaseLevel = 3;

            ZonePalette pal = new ZonePalette();
            pal.Ground = "#42563f";
            pal.Ground2 = "#4d6349";
            pal.Water = "#3a6b6e";
            pal.Rock = "#4a4f42";
            pal.Accent = "#8fd97a";

            ZoneTheme theme = new ZoneTheme();
            theme.Size = new[] { 120, 80 };
            theme.Algo = "forest";
            theme.Palette = pal;
            theme.WaterRate = 0.05f;
            theme.RockRate = 0.16f;
            theme.Weather = "none";
            z.Theme = theme;

            ZoneEnemies en = new ZoneEnemies();
            en.Count = 6;
            en.Kinds = new List<string> { CombatConfig.KIND_BLOOD, CombatConfig.KIND_WITCH };
            en.HpMult = 1.0f;
            en.AtkMult = 1.0f;
            en.ArmorAdd = 0;
            en.EliteRate = 0.05f;
            en.AffixRate = 0.0f;
            z.Enemies = en;

            // T2 切片不含 BOSS（属 P1），刻意留 null。
            z.Boss = null;
            return z;
        }

        // ---------------------------------------------------------------------
        // 地形
        // ---------------------------------------------------------------------

        /// <summary>
        /// 简化地形生成（PRD Q6）：按 water_rate / rock_rate 用 PCG32 洒点。
        ///
        /// 【为什么不实现 zones.json 里的 operators 算子链】
        /// vein / grove / scatter 那套是 P1 的地形质量问题；P0 只需要「地图不是一整块
        /// 纯色、且有不可站立区域供落点校验用」。用完整算子链会把 T02 拖成一个
        /// 独立的地形模块，而它并不服务于「垂直切片能不能跑通」这个目标。
        ///
        /// 【每格恰好消耗 2 个随机数】
        /// 第一个定类型、第二个定地面斑驳。固定消耗量让随机流的位置可预测，
        /// 将来在地形和敌人之间插入新的生成步骤时，能一眼看出会不会打乱后续。
        /// </summary>
        public static TileKind[] GenerateTerrain(ZoneTheme theme, PCG32 rng)
        {
            float waterRate = theme != null ? Mathf.Clamp01(theme.WaterRate) : 0.05f;
            float rockRate = theme != null ? Mathf.Clamp01(theme.RockRate) : 0.16f;

            TileKind[] grid = new TileKind[Width * Height];

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float r = rng.NextFloat();
                    float s = rng.NextFloat();

                    TileKind kind;
                    if (r < waterRate)
                    {
                        kind = TileKind.Water;
                    }
                    else if (r < waterRate + rockRate)
                    {
                        kind = TileKind.Rock;
                    }
                    else
                    {
                        kind = s < 0.28f ? TileKind.Ground2 : TileKind.Ground;
                    }

                    grid[y * Width + x] = kind;
                }
            }

            // 边界一圈强制岩石：给这张开阔地一个可见的边，玩家不会走到"世界之外"。
            // 放在洒点之后覆写，而不是在循环里特判——特判会让随机消耗量随位置变化。
            for (int x = 0; x < Width; x++)
            {
                grid[x] = TileKind.Rock;
                grid[(Height - 1) * Width + x] = TileKind.Rock;
            }
            for (int y = 0; y < Height; y++)
            {
                grid[y * Width] = TileKind.Rock;
                grid[y * Width + (Width - 1)] = TileKind.Rock;
            }

            return grid;
        }

        /// <summary>
        /// 把地形矩阵刷进一个 Tilemap。
        ///
        /// 【单 DrawCall 的三个前提（PRD Q9）】
        ///   1. 四种地块共用同一张白色贴图（<see cref="SpriteFactory.WhiteTile"/>），
        ///      颜色写在 Tile.color 上 —— 不同贴图会被拆成不同批次；
        ///   2. 一次性 <c>SetTiles</c> 而不是 9600 次 <c>SetTile</c> —— 后者每次都触发
        ///      一遍 Tilemap 的脏标记与网格重建，慢两个数量级；
        ///   3. TilemapRenderer 的 Chunk 模式（默认）自行合并区块网格。
        /// </summary>
        private static void BuildTilemap(Transform parent, ZoneTheme theme)
        {
            GameObject gridGo = new GameObject("Terrain");
            gridGo.transform.SetParent(parent, false);

            // 必须写全 UnityEngine.Grid：本类有一个静态属性也叫 Grid（地形矩阵），
            // 裸写 Grid 会被解析成那个属性，报「是属性但被当作类型使用」。
            UnityEngine.Grid gridComp = gridGo.AddComponent<UnityEngine.Grid>();
            gridComp.cellSize = new Vector3(TileUnit, TileUnit, 0.0f);
            gridComp.cellLayout = GridLayout.CellLayout.Rectangle;
            gridComp.cellSwizzle = GridLayout.CellSwizzle.XYZ;

            GameObject mapGo = new GameObject("Tilemap");
            mapGo.transform.SetParent(gridGo.transform, false);

            Tilemap map = mapGo.AddComponent<Tilemap>();
            TilemapRenderer renderer = mapGo.AddComponent<TilemapRenderer>();
            renderer.sortingOrder = -100;                       // 地形永远在最底层
            renderer.mode = TilemapRenderer.Mode.Chunk;         // Chunk 才会合批
            renderer.sortOrder = TilemapRenderer.SortOrder.TopLeft;

            ZonePalette pal = theme != null ? theme.Palette : null;
            Sprite white = SpriteFactory.WhiteTile();

            // 四种地块 = 四个 Tile 实例，共用一张贴图，只有 color 不同。
            Tile[] tiles = new Tile[4];
            tiles[(int)TileKind.Ground] = MakeTile(white, ParseColor(pal != null ? pal.Ground : null, new Color32(0x42, 0x56, 0x3f, 0xff)), "ground");
            tiles[(int)TileKind.Ground2] = MakeTile(white, ParseColor(pal != null ? pal.Ground2 : null, new Color32(0x4d, 0x63, 0x49, 0xff)), "ground2");
            tiles[(int)TileKind.Water] = MakeTile(white, ParseColor(pal != null ? pal.Water : null, new Color32(0x3a, 0x6b, 0x6e, 0xff)), "water");
            tiles[(int)TileKind.Rock] = MakeTile(white, ParseColor(pal != null ? pal.Rock : null, new Color32(0x4a, 0x4f, 0x42, 0xff)), "rock");

            int total = Width * Height;
            Vector3Int[] positions = new Vector3Int[total];
            TileBase[] payload = new TileBase[total];

            for (int y = 0; y < Height; y++)
            {
                int rowBase = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    int i = rowBase + x;
                    positions[i] = new Vector3Int(x, y, 0);
                    payload[i] = tiles[(int)Grid[i]];
                }
            }

            map.ClearAllTiles();
            map.SetTiles(positions, payload);   // ← 9600 格一次刷完
            map.RefreshAllTiles();
        }

        /// <summary>造一个仅颜色不同的 Tile。<c>hideFlags</c> 防止它被当成资产存盘。</summary>
        private static Tile MakeTile(Sprite sprite, Color color, string name)
        {
            Tile t = ScriptableObject.CreateInstance<Tile>();
            t.name = "tile_" + name;
            t.sprite = sprite;
            t.color = color;
            t.colliderType = Tile.ColliderType.None;   // P0 不做地形碰撞（属 P1-05）
            t.hideFlags = HideFlags.DontSave;
            return t;
        }

        // ---------------------------------------------------------------------
        // 格子 ↔ 世界坐标
        // ---------------------------------------------------------------------

        /// <summary>tile 中心的世界坐标。</summary>
        public static Vector2 TileToWorld(int tx, int ty)
        {
            return new Vector2((tx + 0.5f) * TileUnit, (ty + 0.5f) * TileUnit);
        }

        /// <summary>世界坐标落在哪个 tile 上（不做越界钳制）。</summary>
        public static Vector2Int WorldToTile(Vector2 world)
        {
            return new Vector2Int(Mathf.FloorToInt(world.x / TileUnit), Mathf.FloorToInt(world.y / TileUnit));
        }

        /// <summary>取某格类型。越界返回 <see cref="TileKind.Rock"/>（等价于不可用）。</summary>
        public static TileKind KindAt(int tx, int ty)
        {
            if (Grid == null || tx < 0 || ty < 0 || tx >= Width || ty >= Height)
            {
                return TileKind.Rock;
            }
            return Grid[ty * Width + tx];
        }

        /// <summary>该格能否作为出生点 / 站立点（水与岩石不行）。架构文档 U2 的口径。</summary>
        public static bool IsWalkable(int tx, int ty)
        {
            TileKind k = KindAt(tx, ty);
            return k == TileKind.Ground || k == TileKind.Ground2;
        }

        /// <summary>世界坐标处能否站立。</summary>
        public static bool IsWalkableWorld(Vector2 world)
        {
            Vector2Int t = WorldToTile(world);
            return IsWalkable(t.x, t.y);
        }

        /// <summary>
        /// 从 (tx,ty) 出发按方形螺旋找最近的可站立格，返回其世界坐标。
        /// **确定性**：不消耗随机流，同样的输入永远得到同样的输出——
        /// 落点修正若引入随机，同种子重放就会漂移。
        /// </summary>
        public static Vector2 FindSpawnableNear(int tx, int ty)
        {
            if (IsWalkable(tx, ty))
            {
                return TileToWorld(tx, ty);
            }

            int maxRing = Mathf.Max(Width, Height);
            for (int r = 1; r <= maxRing; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        // 只看当前环，环内格子在更小的 r already 检查过了。
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r)
                        {
                            continue;
                        }
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (IsWalkable(nx, ny))
                        {
                            return TileToWorld(nx, ny);
                        }
                    }
                }
            }
            // 整张图没有一格可站立只可能是配置错误；回落到中心，别让调用方拿到 NaN。
            return TileToWorld(Width / 2, Height / 2);
        }

        // ---------------------------------------------------------------------
        // 玩家 / 相机
        // ---------------------------------------------------------------------

        private static Transform BuildPlayer(Transform parent, Vector2 spawn)
        {
            GameObject go = new GameObject("Player");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(spawn.x, spawn.y, 0.0f);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            // ★兜底第一行：**无条件**先设程序化蓝方块。
            //   下面的水墨精灵是"升级"，不是"替代"——精灵资源缺失 / 换平台读不到盘时，
            //   这一行保证玩家身上永远有一个可见的 Sprite，行为与接入精灵之前逐字节一致。
            //   千万不要因为"现在有精灵了"就删掉它（见设计 K10）。
            sr.sprite = SpriteFactory.SolidRect("player", PlayerBodySize, PlayerBodySize,
                                                new Color(0.42f, 0.62f, 0.92f, 1.0f));
            sr.sortingOrder = 10;

            // 朝向指示：一根从体心指向 lastFacing 的短条。占位美术里没有它，
            // 玩家就完全看不出自己朝哪边——而扇形攻击判定恰恰吃朝向。
            GameObject facing = new GameObject("FacingMarker");
            facing.transform.SetParent(go.transform, false);
            facing.transform.localPosition = new Vector3(PlayerBodySize * 0.62f, 0.0f, 0.0f);
            SpriteRenderer fsr = facing.AddComponent<SpriteRenderer>();
            int facingW = Mathf.RoundToInt(PlayerBodySize * 0.6f);
            fsr.sprite = SpriteFactory.SolidRect("player_facing", facingW, 7,
                                                 new Color(0.86f, 0.93f, 1.0f, 0.95f));
            fsr.sortingOrder = 11;

            PlayerController pc = go.AddComponent<PlayerController>();
            pc.SetFacingMarker(facing.transform);

            // ===== 批次 3（R-03 玩家受击朱砂闪白）=====
            // 给玩家挂上**只染色、不碰 transform** 的受击闪白组件。
            //
            // 【为什么是 PlayerHitFlash 而不是给玩家也 AttachView 一个 CombatView】
            // 主理人架构裁定（批次 3 硬约束）：CombatView.SyncFromKernel() 每帧写
            // transform.position 还带插值与钉位/归位逻辑，而玩家位置是 PlayerController
            // 的私有领地。两组件同帧抢写会让玩家抽搐/被拽回内核位置，回归比"没闪白"
            // 严重一个量级。所以玩家走独立的轻组件，由 HitFeedbackDirector 在受击方
            // 为玩家阵营时调用 PlayerHitFlash.Play(...) 分流派发。
            //
            // 【为什么 Awake 一定能拿到 SpriteRenderer】
            // 上面 :541 已经 AddComponent<SpriteRenderer>() 并设了蓝方块占位，
            // 早于本行。PlayerHitFlash.Awake 里 GetComponent<SpriteRenderer>() 必中，
            // 但仍自带惰性解析兜底（见 PlayerHitFlash.EnsureRenderer），装配顺序
            // 即便将来被调整也不会空引用崩。
            go.AddComponent<PlayerHitFlash>();

            go.AddComponent<AttackController>();

            // ===== T3 接线（BugFix）=====
            // 下面两个组件此前漏挂，导致 K/L/右键技能与闪避（Shift/Space）全部静默。
            // 它们都能自己 GetComponent<PlayerController>() 取玩家、用 FindFirstObjectByType 找 CombatBridge，
            // 所以这里只需 AddComponent，无需在 Inspector 里做任何赋值或 Configure 调用。
            //
            // 【为什么普攻没有这两个也能用？】
            //    普攻走 AttackController，它自带 T2 内联退路 Swing()，即使 T3 没开启也能直接结算伤害；
            //    而技能没有退路——SkillController.Update 里有四道闸门，一旦 CombatBridge 为 null（b == null）
            //    就直接 return，所以 SkillController 没挂载时它的 Update 根本不存在，技能键彻底静默。
            //
            // 【输入总线 InputBinder 为什么不在这里挂？】
            //    它是全局服务（单例），挂在世界根节点上，见 BuildScene。挂玩家身上的话，
            //    玩家对象一被销毁重建，单例就断了，手柄会莫名其妙退回"仅键鼠"。
            go.AddComponent<SkillController>();   // K / 鼠标右键 → Skill1，L → Skill2
            go.AddComponent<DodgeController>();   // Shift / Space 闪避

            // ===== 美术阶段 E：把蓝方块「升级」为 39 帧水墨女主精灵 =====
            //
            // 【为什么挂在最后】HeroineAnimator.Awake() 会 GetComponent 上面这些战斗组件
            // 来做状态轮询（AddComponent 会立刻触发 Awake），先挂就一个都拿不到。
            //
            // 【三层 fallback 的第三层】前两层在 SpriteFactory.TryLoadPng（失败返 null 不抛）
            // 和 HeroineAnimator.Warmup（读不到 idle 首帧则 IsReady=false）。这里是最后一层：
            // 蓝方块已经在上面无条件设过了，精灵只是"能升级就升级"。
            HeroineAnimator anim = go.AddComponent<HeroineAnimator>();
            if (anim.IsReady)
            {
                // 精灵接管渲染。朝向条**弱化保留**而非删除：
                // 攻击扇形吃 8 向 lastFacing，而走路动画只有 4 向（上/下/左/右），
                // 站桩不动时 idle 图完全不表达朝向——删了玩家就看不出自己在往哪打。
                // 所以留一根更细更淡的：看得见，但不跟角色抢戏。
                // （这里能安全复用 "player_facing" 这个 key，正是因为 SolidRect 的
                //   缓存 key 已修成包含 w/h/fill——否则会静默拿回上面那根粗的。）
                int slimW = Mathf.Max(1, facingW / 2);
                fsr.sprite = SpriteFactory.SolidRect("player_facing", slimW, 7,
                                                     new Color(0.86f, 0.93f, 1.0f, 0.45f));
            }
            else
            {
                // 39 帧读不到（StreamingAssets 没拷过去 / 文件损坏 / 平台不支持同步读盘）：
                // 卸掉动画机，保留 40×40 蓝方块 + 原样朝向条，行为与接入精灵之前完全一致。
                if (Application.isPlaying)
                {
                    Object.Destroy(anim);
                }
                else
                {
                    Object.DestroyImmediate(anim);
                }
            }

            return go.transform;
        }

        /// <summary>
        /// 配置相机。相机是**场景自带**对象（不打生成标记），所以这里只改属性、
        /// 不创建也不销毁——否则 Clean And Rebuild 会把它删掉，场景变成黑屏。
        /// </summary>
        private static void SetupCamera(Transform player)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                // 场景被人删了相机时的兜底。这个相机会带生成标记，下次 Clean 会连它一起删，
                // 但紧接着的 Build 又会补一个回来，净效果依然正确。
                GameObject go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                ShuimoGenerated.Mark(go);
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
                Debug.LogWarning("[T2] 场景里没有 MainCamera，已补建一个。");
            }

            cam.orthographic = true;
            cam.orthographicSize = CameraOrthoSize;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.094f, 0.102f, 0.090f, 1.0f);   // 墨色底
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000.0f;
            cam.transform.position = new Vector3(player.position.x, player.position.y, -100.0f);

            CameraFollow follow = cam.GetComponent<CameraFollow>();
            if (follow == null)
            {
                follow = cam.gameObject.AddComponent<CameraFollow>();
            }
            follow.Configure(player, WorldWidth, WorldHeight);

            // 屏震与跟随挂在**同一台**相机上，靠 ExecutionOrder（100 → 150）
            // 分先后：跟随先算出无偏移中心，屏震再在它之上叠一层偏移。
            //
            // 【为什么不给相机套一个"抖动父节点"】
            // 2D 正交下 Main Camera 就是根节点。插一层中间节点，
            // 本方法的装配、CameraFollow.ClampToBounds 里基于 orthographicSize
            // 的边界计算都要跟着换坐标系，收益为零，风险为正。
            //
            // Configure 必须在 follow.Configure 之后调：CameraShake 会立刻读
            // follow.BaseCenter 做一次回正，早于跟随吸附的话会回正到旧位置。
            CameraShake shake = cam.GetComponent<CameraShake>();
            if (shake == null)
            {
                shake = cam.gameObject.AddComponent<CameraShake>();
            }
            shake.Configure(follow);
        }

        // ---------------------------------------------------------------------
        // 战斗
        // ---------------------------------------------------------------------

        /// <summary>
        /// 造敌人「预制体」模板。运行时没有 Prefab 资产，就用一个 SetActive(false) 的
        /// 场景对象当模板 —— <c>Instantiate</c> 出来的副本同样是未激活的，
        /// 由 <see cref="EnemySpawner"/> 在按 kind 上完色之后再激活，
        /// 避免玩家看到一帧白色圆点然后突然变色。
        /// </summary>
        private static GameObject BuildEnemyTemplate(Transform parent)
        {
            GameObject go = new GameObject("EnemyTemplate");
            go.transform.SetParent(parent, false);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpriteFactory.Circle("enemy", Color.white, new Color(0, 0, 0, 0), 0.0f);
            sr.sortingOrder = 5;

            go.AddComponent<CombatView>();
            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// 装配战斗节点。这里**不传随机流**：CombatController.BuildEncounter 会用
        /// 同样的 (zoneId, isSafe, visits) 自行 CreateRng，得到一条与地形独立但同源的流。
        /// </summary>
        private static void BuildCombat(Transform parent, Transform player, GameObject enemyTemplate)
        {
            GameObject enemyRoot = new GameObject("Enemies");
            enemyRoot.transform.SetParent(parent, false);

            // 先建成未激活对象再 Bind* 再激活：CombatController 在 Awake 里就会
            // BuildEncounter，必须让它带着正确配置跑第一次（见 CombatController.BindScene 注释）。
            GameObject combatGo = new GameObject("Combat");
            combatGo.transform.SetParent(parent, false);
            combatGo.SetActive(false);

            CombatController ctrl = combatGo.AddComponent<CombatController>();
            ctrl.BindScene(player, enemyRoot.transform, enemyTemplate, null);
            ctrl.BindZone(Bootstrap.ZoneId, Bootstrap.ZoneVisits, Bootstrap.IsSafeZone);
            ctrl.BindPlayerStats(CombatBridge.PlayerHpMax, CombatBridge.PlayerDef, false);

            CombatBridge bridge = combatGo.AddComponent<CombatBridge>();
            bridge.Configure(ctrl, player, Zone);

            combatGo.AddComponent<EnemySpawner>();
            combatGo.AddComponent<DeterminismDump>();

            combatGo.SetActive(true);
        }

        // ---------------------------------------------------------------------
        // HUD
        // ---------------------------------------------------------------------

        private static void BuildHud(Transform parent)
        {
            GameObject go = new GameObject("HUD");
            go.transform.SetParent(parent, false);
            go.AddComponent<Hud>();
        }

        // ---------------------------------------------------------------------
        // 工具
        // ---------------------------------------------------------------------

        /// <summary>
        /// 销毁场景里所有带 <see cref="ShuimoGenerated"/> 的根节点，返回销毁数量。
        /// 与编辑器工具的 CleanGeneratedObjects 同义 —— 之所以再写一遍而不是复用，
        /// 是因为那个方法在 Editor 程序集里，运行时兜底路径够不着。
        /// </summary>
        public static int DestroyGeneratedRoots()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return 0;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            int n = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject go = roots[i];
                if (go == null || go.GetComponent<ShuimoGenerated>() == null)
                {
                    continue;
                }
                if (Application.isPlaying)
                {
                    Object.Destroy(go);
                }
                else
                {
                    Object.DestroyImmediate(go);
                }
                n++;
            }
            return n;
        }

        /// <summary>
        /// 解析 <c>#RRGGBB</c>。zones.json 的调色板是字符串，Core 层刻意不做颜色解析
        /// （ZoneData.cs 的注释写明了「交给渲染层」），这里就是那个渲染层。
        /// </summary>
        public static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return fallback;
            }
            Color parsed;
            return ColorUtility.TryParseHtmlString(hex, out parsed) ? parsed : fallback;
        }
    }
}
