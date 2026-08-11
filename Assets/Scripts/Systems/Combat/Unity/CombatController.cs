// -----------------------------------------------------------------------------
// Unity/CombatController.cs —— 内核与 Unity 的唯一接缝（MonoBehaviour）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。它引用 UnityEngine，
// 无法参与 Python 对拍，也不在 CI 的 grep 守卫豁免之外——请在本地 Unity 工程
// 打开后确认编译通过再接线。内核（Combat/ 下除 Unity/ 外的全部 .cs）不依赖本文件。
//
// 【职责边界：只做三件事】
//   1. 把 Unity 的 Transform 位置**读进**内核（每帧一次，在 Tick 之前）
//   2. 把 Time.deltaTime 喂给 CombatScheduler
//   3. 把内核算完的位置**写回** Transform（交给 CombatView）
// 除此之外的任何游戏逻辑都不该出现在这里。判断标准很简单：
// 如果一段代码在 Python 对拍里也需要，它就该在内核，而不在本文件。
//
// 【命名空间为什么不是 Xianxia.Combat.Unity】
// 名为 `Unity` 的命名空间段会遮蔽真正的 `Unity.*`（Unity.Collections /
// Unity.Mathematics）：在 Xianxia.Combat.Unity 内部写 `Unity.Collections.X`，
// 编译器会先解析到 Xianxia.Combat.Unity.Collections 然后报错。目录名保持
// 设计文档约定的 `Unity/`，命名空间用 UnityBridge 规避这个坑。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Xianxia.Core;

namespace Xianxia.Combat.UnityBridge
{
    /// <summary>
    /// 战斗场景的总入口。挂在场景里的一个空 GameObject 上，
    /// 由它持有 <see cref="Encounter"/> 与 <see cref="CombatScheduler"/>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatController : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Inspector
        // ---------------------------------------------------------------------

        [Header("场景引用")]
        [Tooltip("玩家 GameObject 的 Transform。内核每帧从这里读位置。")]
        [SerializeField] private Transform playerTransform;

        [Tooltip("敌人生成的父节点。留空则挂在本物体下。")]
        [SerializeField] private Transform enemyRoot;

        [Tooltip("普通敌人预制体。必须带 CombatView。")]
        [SerializeField] private GameObject enemyPrefab;

        [Tooltip("BOSS 预制体。留空则复用 enemyPrefab。")]
        [SerializeField] private GameObject bossPrefab;

        [Header("区域配置")]
        [Tooltip("zones.json 里的区域 id，用于派生本局随机种子。")]
        [SerializeField] private string zoneId = "zone_youhuang";

        [Tooltip("本区域的访问次数，参与种子派生（ZoneSeed.Derive）。")]
        [SerializeField] private int zoneVisits = 1;

        [Tooltip("是否安全区。安全区不生成敌人，种子也不掺访问次数。")]
        [SerializeField] private bool isSafeZone;

        [Header("玩家数值")]
        [Tooltip("玩家血量上限。")]
        [SerializeField] private float playerHpMax = 260.0f;

        [Tooltip("玩家防御，参与模型 B 反调的 keep 系数。")]
        [SerializeField] private float playerDef;

        [Tooltip("是否开启闪避骰。对拍基线一律关闭（R3）。")]
        [SerializeField] private bool dodgeEnabled;

        [Header("调试")]
        [Tooltip("在 Scene 视图画出接触半径 / 牵引半径。")]
        [SerializeField] private bool drawGizmos = true;

        // ---------------------------------------------------------------------
        // 运行时
        // ---------------------------------------------------------------------

        /// <summary>内核战场。对外只读，禁止外部直接改 Combatants 列表。</summary>
        public Encounter Encounter { get; private set; }

        /// <summary>固定步长调度器。</summary>
        public CombatScheduler Scheduler { get; private set; }

        /// <summary>玩家实体。</summary>
        public Combatant Player { get; private set; }

        /// <summary>事件出口（Unity 实现）。</summary>
        public CombatEventsUnity EventsUnity { get; private set; }

        private ZoneEnemies _zoneEnemies;
        private ZoneBoss _zoneBoss;

        // Combatant ↔ 视图。用 id 做 key 而不是引用，方便死亡后延迟回收。
        private readonly Dictionary<int, CombatView> _views = new Dictionary<int, CombatView>(32);
        private readonly List<int> _recycleBuffer = new List<int>(8);

        // ---------------------------------------------------------------------
        // 生命周期
        // ---------------------------------------------------------------------

        private void Awake()
        {
            BuildEncounter();
        }

        /// <summary>
        /// 组装战场。拆成独立方法是为了让 PlayMode 测试能不经 Awake 直接调用。
        /// </summary>
        public void BuildEncounter()
        {
            EventsUnity = new CombatEventsUnity();
            EventsUnity.ViewOf = FindView;
            EventsUnity.FxRoot = enemyRoot != null ? enemyRoot : transform;

            Encounter = new Encounter();
            Encounter.Events = EventsUnity;
            // 用 T0 的工厂而不是自己 new PCG32：Derive 返回 long，自行做 (ulong) 转换
            // 很容易在负数上写成 (uint) 截断，两处种子从此分道扬镳。
            Encounter.Rng = ZoneSeed.CreateRng(zoneId, isSafeZone, zoneVisits);
            Encounter.Bridge = new DifficultyBridge();
            Encounter.Bridge.PlayerDef = playerDef;
            // U1：模型 B 的 d_eff 靶子分子是**玩家**血上限，构建期必须与玩家初始血同源，
            // 否则怪物按 260 反调、玩家实际只有 200 血，A1/A2 窗口立刻失准。
            //
            // 🚨【N-3 · P1-6 补注】上面这句「必须与 Player.HpMax 同源」现在只对
            // **构建期这一次**成立（此刻玩家必然是 1 级，playerHpMax == 260，两者同值）。
            // 运行期玩家升级后血上限会涨到 494，但**绝不会**再回写到 Bridge.PlayerHpMax ——
            // 平衡基准与玩家实时血上限的耦合已在 P1-6 解除（PRD R-11），
            // 理由见 DifficultyBridge.PlayerHpMax 的注释「标尺不能跟着被测量的人一起变」。
            // 本行代码本身**无需修改**：构建期取值恒为 260，与新口径天然一致。
            Encounter.Bridge.PlayerHpMax = playerHpMax;

            Vector3 p = playerTransform != null ? playerTransform.position : Vector3.zero;
            Player = Combatant.CreatePlayer(0, playerHpMax, new Vec2(p.x, p.y));
            Player.WCore.DodgeEnabled = dodgeEnabled;
            Player.Armor = 0.0f;
            // U1：玩家走**除法**减伤（A2 软上限 + 伤害地板 1），与敌人的减法护甲不同构。
            // 没有这一行，playerDef 就只是个反调用的假设值、对真实承伤毫无影响——
            // 堆防御在数值表上生效、在战场上不生效，是最难查的一类平衡 bug。
            float defForFilter = playerDef;
            Player.WCore.DamageFilter = raw => Difficulty.DamageTaken(raw, defForFilter);
            Encounter.SetPlayer(Player);

            Scheduler = new CombatScheduler(Encounter);
        }

        // ---------------------------------------------------------------------
        // 代码装配入口（T2 世界生成器用）
        //
        // 上面那批字段是 [SerializeField] private——手工拖引用时这是对的，但 T2 的
        // 世界完全由 WorldBuilder 在代码里长出来，没有 Inspector 可拖。下面三个方法
        // 就是给代码装配开的正门。
        //
        // 【为什么不干脆把字段改成 public】
        // public 字段谁都能在运行中途改，而 playerTransform / zoneId 这类值一旦在
        // BuildEncounter 之后被改动，内核与视图就会指向两个不同的世界。做成方法，
        // 语义上就明确了「这是装配期的一次性注入」，而且将来要加校验只需改一处。
        //
        // 【调用时机】必须在 Awake 之前。WorldBuilder 的做法是先 SetActive(false)
        // 建对象 → Bind* → SetActive(true)，让 Awake 带着正确配置跑第一次。
        // ---------------------------------------------------------------------

        /// <summary>装配期注入场景引用。null 参数表示保持原值。</summary>
        public void BindScene(Transform player, Transform enemies, GameObject enemyTemplate, GameObject bossTemplate)
        {
            if (player != null)
            {
                playerTransform = player;
            }
            if (enemies != null)
            {
                enemyRoot = enemies;
            }
            if (enemyTemplate != null)
            {
                enemyPrefab = enemyTemplate;
            }
            if (bossTemplate != null)
            {
                bossPrefab = bossTemplate;
            }
        }

        /// <summary>装配期注入区域标识（决定随机种子，进而决定整个世界）。</summary>
        public void BindZone(string zone, int visits, bool safe)
        {
            if (!string.IsNullOrEmpty(zone))
            {
                zoneId = zone;
            }
            zoneVisits = visits;
            isSafeZone = safe;
        }

        /// <summary>装配期注入玩家数值。<paramref name="hpMax"/> 同时决定模型 B 的 d_eff 靶子。</summary>
        public void BindPlayerStats(float hpMax, float def, bool dodge)
        {
            if (hpMax > 0.0f)
            {
                playerHpMax = hpMax;
            }
            playerDef = def;
            dodgeEnabled = dodge;
        }

        /// <summary>
        /// 注入本区域的 zones.json 配置。由上层区域管理器在切区时调用；
        /// 不在 Awake 里自己读文件——内核与 IO 解耦，测试才好写。
        /// </summary>
        public void SetZoneConfig(ZoneEnemies enemies, ZoneBoss boss, int baseLevel)
        {
            _zoneEnemies = enemies;
            _zoneBoss = boss;
            ZoneBaseLevel = baseLevel;
        }

        /// <summary>本区域的基准等级。</summary>
        public int ZoneBaseLevel { get; private set; } = 1;

        // ---------------------------------------------------------------------
        // T3 接线（战斗深化）
        //
        // 【为什么装配走这里而不是让上层直接改 Encounter 字段】
        // Encounter.Intent / EventsT3 / SkillRng 三者必须**同时**设置：
        // 只设 Intent 不播种 SkillRng，血莲的中毒骰就会跨局复现同一串；
        // 只设 EventsT3 不设 Intent，玩家按键全部石沉大海。
        // 收敛成一个方法，就不存在"装配漏了一半"这种状态。
        //
        // 【它不改变推进权】
        // 本区新增的方法一律只写 Intent 与配置字段，**没有任何一处调用
        // Scheduler.Tick / Encounter.StepFixed**。内核推进权仍唯一属于
        // 下面的 Update()。
        // ---------------------------------------------------------------------

        /// <summary>
        /// 装配 T3 输入意图与事件出口。可反复调用（切区 / 重建战场后需重新绑定）。
        /// </summary>
        /// <param name="intent">输入意图缓冲。null = 不接受任何技能输入。</param>
        /// <param name="events">T3 事件出口。null 回落到空实现。</param>
        /// <param name="softAimRange">软索敌最大生效距离，&lt;= 0 时保持原值。</param>
        /// <param name="skillSeed">技能随机流种子（与 Encounter.Rng 物理隔离）。</param>
        public void BindT3(PlayerIntent intent, ICombatEventsT3 events, float softAimRange, long skillSeed)
        {
            if (Encounter == null)
            {
                return;
            }
            Encounter.Intent = intent;
            Encounter.EventsT3 = events != null ? events : NullCombatEventsT3.Instance;
            if (softAimRange > 0.0f)
            {
                Encounter.SoftAimMaxRange = softAimRange;
            }
            Encounter.SeedSkillRng(skillSeed);
        }

        /// <summary>
        /// 解除 T3 接线，回到纯 T2 路径（基线对拍模式用）。
        /// </summary>
        public void UnbindT3()
        {
            if (Encounter == null)
            {
                return;
            }
            if (Encounter.Intent != null)
            {
                Encounter.Intent.Clear();
            }
            Encounter.Intent = null;
            Encounter.EventsT3 = NullCombatEventsT3.Instance;
            Encounter.SoftAimMaxRange = 0.0f;
        }

        /// <summary>
        /// 投递一次施法意图。幂等：同一逻辑帧内多次调用等价于一次。
        /// </summary>
        /// <param name="slot">技能槽位。</param>
        /// <param name="facing">按下瞬间的朝向。</param>
        /// <returns>true = 意图已写入缓冲（不代表技能一定放得出来）。</returns>
        public bool RequestPlayerCast(IntentSlot slot, Vector2 facing)
        {
            if (Encounter == null || Encounter.Intent == null)
            {
                return false;
            }
            Encounter.Intent.Request(slot, new Vec2(facing.x, facing.y));
            return true;
        }

        /// <summary>投递一次闪避意图。</summary>
        /// <param name="dir">闪避方向。</param>
        /// <returns>true = 意图已写入缓冲。</returns>
        public bool RequestPlayerDodge(Vector2 dir)
        {
            return RequestPlayerCast(IntentSlot.Dodge, dir);
        }

        private void Update()
        {
            if (Scheduler == null || Encounter == null)
            {
                return;
            }

            // ① 读入：Unity 的移动/物理是权威，内核只消费位置。
            SyncPlayerIntoKernel();

            // ② 推进：内核只看见 1/60，与帧率彻底解耦。
            Scheduler.Tick(Time.deltaTime);

            // ③ 写回：把内核算出的敌人位置刷回 Transform。
            SyncKernelIntoViews();

            // ④ 回收：内核已经把死亡敌人移出列表，这里销毁对应视图。
            RecycleOrphanViews();
        }

        /// <summary>
        /// 把玩家 Transform 的位置与位移速度读进内核。
        ///
        /// 速度用「本帧位移 / deltaTime」反算，而不是去读 Rigidbody2D.velocity：
        /// 后者在 CharacterController / 手动移动 / 动画根运动这几种实现下取值不一致，
        /// 反算位移则对任何移动方案都成立。
        /// </summary>
        private void SyncPlayerIntoKernel()
        {
            if (playerTransform == null || Player == null)
            {
                return;
            }

            Vector3 now = playerTransform.position;
            Vec2 next = new Vec2(now.x, now.y);
            float dt = Time.deltaTime;

            Player.Velocity = dt > 0.0f
                ? new Vec2((next.X - Player.Position.X) / dt, (next.Y - Player.Position.Y) / dt)
                : Vec2.Zero;

            Player.Position = next;
        }

        /// <summary>把内核里每个敌人的位置写回它的视图。</summary>
        private void SyncKernelIntoViews()
        {
            for (int i = 0; i < Encounter.Combatants.Count; i++)
            {
                Combatant c = Encounter.Combatants[i];
                if (c.Faction == Faction.Player)
                {
                    continue;   // 玩家由 Unity 侧移动，不能被内核写回，否则输入会被吞掉
                }
                CombatView view;
                if (_views.TryGetValue(c.Id, out view) && view != null)
                {
                    view.SyncFromKernel();
                }
            }
        }

        /// <summary>销毁内核里已不存在的实体所对应的视图。</summary>
        private void RecycleOrphanViews()
        {
            _recycleBuffer.Clear();
            foreach (KeyValuePair<int, CombatView> kv in _views)
            {
                CombatView view = kv.Value;
                if (view == null || view.Model == null || !Encounter.Combatants.Contains(view.Model))
                {
                    _recycleBuffer.Add(kv.Key);
                }
            }
            for (int i = 0; i < _recycleBuffer.Count; i++)
            {
                CombatView view;
                if (_views.TryGetValue(_recycleBuffer[i], out view) && view != null)
                {
                    Destroy(view.gameObject);
                }
                _views.Remove(_recycleBuffer[i]);
            }
        }

        // ---------------------------------------------------------------------
        // 生成
        // ---------------------------------------------------------------------

        /// <summary>
        /// 生成一波敌人。数量与配置来自 zones.json；落点由调用方给出的圆环采样。
        /// </summary>
        /// <param name="center">波次中心（通常是玩家出生点）。</param>
        /// <param name="minRadius">最小生成半径。</param>
        /// <param name="maxRadius">最大生成半径。</param>
        public void SpawnWave(Vector2 center, float minRadius, float maxRadius)
        {
            if (_zoneEnemies == null || Encounter == null)
            {
                return;
            }

            int count = _zoneEnemies.Count > 0 ? _zoneEnemies.Count : 1;
            for (int i = 0; i < count; i++)
            {
                int lv = DifficultyBridge.RollLevel(ZoneBaseLevel, Encounter.Rng);
                Combatant e = Encounter.Bridge.BuildEnemy(_zoneEnemies, lv, null, Encounter.Rng);

                float ang = Encounter.Rng.NextRange(0.0f, 360.0f);
                float rad = Encounter.Rng.NextRange(minRadius, maxRadius);
                Vec2 pos = new Vec2(center.x, center.y) + Vec2.Right.RotatedDeg(ang) * rad;

                e.Position = pos;
                e.AI.SpawnPos = pos;
                Encounter.Add(e);
                AttachView(e, enemyPrefab);
            }
        }

        /// <summary>
        /// 生成本区 BOSS，并接上 R5 的召唤委托。
        ///
        /// 【为什么返回 <see cref="Combatant"/> 而不是 void】T2 层（<c>CombatBridge</c>）
        /// 拿到 BOSS 实体之后还有三件必须做的事：给视图上色放大并激活（<c>EnemySpawner.DressBoss</c>）、
        /// 销掉 BOSS 债（<c>Encounter.ClearBossPending</c>）、把血条挂上（<c>HudBossBar.Show</c>）。
        /// 依赖方向是单向的 T2 → Combat.Unity → Combat，桥接层不许反向引用 T2，
        /// 所以只能把实体**交回去**让上层自己处理。
        /// </summary>
        /// <param name="at">出场世界坐标（调用方须保证可通行）。</param>
        /// <returns>生成并已入列的 BOSS 实体；未配置 BOSS 或战场未就绪时返回 <c>null</c>。</returns>
        public Combatant SpawnBoss(Vector2 at)
        {
            if (_zoneBoss == null || Encounter == null)
            {
                return null;
            }

            Combatant boss = Encounter.Bridge.BuildBoss(_zoneBoss, _zoneEnemies, ZoneBaseLevel, Encounter.Rng);
            boss.Position = new Vec2(at.x, at.y);
            boss.AI.SpawnPos = boss.Position;

            BossController ctrl = boss.AI as BossController;
            if (ctrl != null)
            {
                ctrl.Events = EventsUnity;
                // R5：召唤当前 zone 的 normal 敌人降一级。内核只负责造数据，
                // 视图在这里补挂——注意 Encounter 会在同一步把它收编进列表。
                ZoneEnemies cfg = _zoneEnemies;
                ctrl.Spawn = req =>
                {
                    Combatant minion = Encounter.SpawnMinion(cfg, req);
                    if (minion != null)
                    {
                        AttachView(minion, enemyPrefab);
                    }
                    return minion;
                };
            }

            // ★不变量 I-3 的时序基石：Add 是**直接** Combatants.Add，不走 _pendingAdd 队列，
            //   所以这一行返回的瞬间 AliveEnemyCount 就已经 +1。上层必须等到这之后才允许
            //   ClearBossPending()，否则会出现"债清了怪没到"的空窗，照样早判。
            Encounter.Add(boss);

            // ★C1 防线：bossPrefab 缺失时回落到杂兵外观是可以接受的降级（有实体总比没实体强），
            //   但**必须叫出声**。GAP-2 曾经就是这么静默复发的：BindScene 第 4 参传了 null，
            //   BOSS 顶着杂兵的皮出场，谁也没发现配置断了。
            if (bossPrefab == null)
            {
                Debug.LogWarning("[CombatController] bossPrefab 未装配，BOSS 视图回落为 enemyPrefab。" +
                                 "请检查 WorldBuilder.BuildCombat 是否把 BuildBossTemplate 的产物传给了 BindScene 第 4 参。");
            }

            AttachView(boss, bossPrefab != null ? bossPrefab : enemyPrefab);
            return boss;
        }

        /// <summary>为一个内核实体实例化视图并建立双向绑定。</summary>
        private void AttachView(Combatant model, GameObject prefab)
        {
            if (model == null || prefab == null)
            {
                return;
            }

            Transform parent = enemyRoot != null ? enemyRoot : transform;
            GameObject go = Instantiate(prefab, new Vector3(model.Position.X, model.Position.Y, 0.0f),
                                        Quaternion.identity, parent);
            go.name = string.Format("{0}#{1}", string.IsNullOrEmpty(model.DisplayName) ? model.Kind : model.DisplayName, model.Id);

            CombatView view = go.GetComponent<CombatView>();
            if (view == null)
            {
                view = go.AddComponent<CombatView>();
            }
            view.Bind(model);
            _views[model.Id] = view;
        }

        /// <summary>按 id 取视图（供 UI / FX 反查）。</summary>
        public CombatView FindView(int combatantId)
        {
            CombatView view;
            return _views.TryGetValue(combatantId, out view) ? view : null;
        }

        // ---------------------------------------------------------------------
        // 调试
        // ---------------------------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos || playerTransform == null)
            {
                return;
            }
            Vector3 p = playerTransform.position;

            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(p, CombatConfig.TOUCH_RANGE);

            Gizmos.color = new Color(1.0f, 0.6f, 0.0f, 0.5f);
            Gizmos.DrawWireSphere(p, CombatConfig.AI_STRIKE_DIST);

            Gizmos.color = new Color(0.2f, 0.6f, 1.0f, 0.3f);
            Gizmos.DrawWireSphere(p, CombatConfig.AI_LEASH_DIST);
        }
    }
}
