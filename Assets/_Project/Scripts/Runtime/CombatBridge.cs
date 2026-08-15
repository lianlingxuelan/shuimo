// -----------------------------------------------------------------------------
// CombatBridge.cs —— T2 侧的战斗生命周期编排（asmdef: Xianxia.Unity.T2）
//
// 【它不推进内核，这一点必须说清楚】
// 固定步长推进（Scheduler.Tick → Encounter.StepFixed）已经由既有的
// CombatController.Update 负责了。本组件**绝不**再调一次 Tick——
// 两处同时推进会让每秒步数翻倍，围攻频率从 4.300 变成 8.6，
// T1 辛苦对拍出来的基线当场作废。
//
// 本组件负责的是 CombatController 管不到的那一层：
//   1. 装配期：把 zones.json 的 enemies/boss 段喂进去，触发首波生成
//   2. 数值接线：确保玩家走模型 B 除法减伤（U1 修复的另一半）
//   3. 对外查询：HP / 敌数 / 步数，给 HUD 和攻击判定用
//
// 【执行顺序 -200 的含义】
// Awake 最早跑，但那时 CombatController(0) 还没 BuildEncounter，
// 所以真正的装配放在 Start —— 所有 Awake 都结束了，Encounter 一定存在。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xianxia.Core;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>战斗编排器。挂在 Combat 节点上，与 CombatController 同体。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public sealed class CombatBridge : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // 玩家数值（三处口径必须一致：这里 / CombatController.playerHpMax /
        // DifficultyBridge.PlayerHpMax —— 后两者由 WorldBuilder 从这里注入）
        // ---------------------------------------------------------------------

        /// <summary>玩家血量上限。同时是模型 B 的 d_eff 靶子分子（U1）。</summary>
        public const float PlayerHpMax = 260.0f;

        /// <summary>玩家防御。0 = 对拍基线，keep 系数为 1。</summary>
        public const float PlayerDef = 0.0f;

        [Header("引用")]
        [SerializeField] private CombatController controller;
        [SerializeField] private Transform playerTransform;

        // ---------------------------------------------------------------------
        // T3 战斗深化（T3-T05 确定性护栏的总开关就在这里）
        //
        // 【baselineMode 是什么】
        // 一个"把 T3 整体拔掉"的开关。开启后：
        //   · 不给玩家装配 Action / Skills / Status / Qi / Stamina 任何一个组件
        //   · 不建 PlayerIntent，不接 EventsT3
        //   · CombatController.UnbindT3() 把内核侧的三个装配位一起清空
        // 于是 Encounter.TickPlayerFrame 的 PlayerHasT3 恒为 false，①-A / ③-A / ⑤-A
        // 三个插入点全部退化成几次判空 —— 执行路径与 T2 逐指令一致，
        // U1 的 38.2s / 15.1s / 2.5294x 可以逐字节复现。
        //
        // 【为什么开关放在 Bridge 而不是 Controller】
        // Controller 是内核接缝，它只该知道"有没有被绑定"；
        // "要不要绑定"是一个装配期决策，属于编排层。
        // ---------------------------------------------------------------------

        [Header("T3 战斗深化")]
        [Tooltip("基线对拍模式。开启后完全不装配 T3，执行路径退回 T2（T3-T05 护栏）。")]
        [SerializeField] private bool baselineMode;

        [Tooltip("软索敌总开关（P0-07 验收③：可整体关闭）。")]
        [SerializeField] private bool softAimEnabled = SkillConfig.SOFT_AIM_DEFAULT_ENABLED;

        [Tooltip("软索敌最大吸附距离。缺省取最大技能射程，避免吸向够不着的敌人；<=0 表示不限距离。")]
        [SerializeField] private float softAimMaxRange = SkillConfig.BURST_RANGE;

        private ZoneData _zone;
        private EnemySpawner _spawner;
        private DeterminismDump _dump;
        private bool _ready;

        // 胜负结算面板（P0-2）。由 Start 动态创建并 Build，终局事件触发后由
        // OnRunPhaseChanged 调 Show 显示。缓存引用以便 Hide / 退订时复用。
        private GameOverHud _gameOverHud;

        // P0-5 / P0-6 的三层界面。同样由 Start 动态创建 —— 本工程不落场景文件，
        // 所有 UI 都是运行时搭出来的（见 GameOverHud 文件头"零美术资源"的理由）。
        private MainMenuHud _mainMenuHud;
        private PauseMenuHud _pauseMenuHud;
        private ControlsGuideHud _controlsGuideHud;

        /// <summary>菜单类暂停（主菜单未开始 / ESC 暂停面板打开）。</summary>
        private bool _menuPaused;

        private PlayerIntent _intent;
        private SkillTable _skillTable;
        private StatusTable _statusTable;
        private CombatEventsT3Unity _eventsT3;
        private bool _t3Enabled;

        /// <summary>
        /// P1-6 玩家成长状态机（纯逻辑，Xianxia.Combat 程序集）。
        /// 在 <see cref="Start"/> 里创建，生命周期跟随本组件 —— 也就是跟随**一局**。
        /// 场景重载后它随 CombatBridge 一起被重建，玩家天然回到 1 级（PRD R-10 局内成长）。
        /// </summary>
        private PlayerProgression _progression;

        /// <summary>
        /// P1-2 受击反馈总调度。与本组件同体（同一个 GameObject）。
        ///
        /// 【为什么同体而不是单独一个对象】
        /// 它要读本组件的 IsGameplayBlocked / IsRunOver / ViewOf，同体时
        /// GetComponent 一次即得，也让"暂停判定权在 Bridge、转达权在 Director"
        /// 这条拓扑在场景层级上一眼可见。
        /// </summary>
        private HitFeedbackDirector _feedback;

        /// <summary>
        /// P1-2 伤害飘字层。**独立** GameObject —— 它自带一个 Canvas，
        /// 与 GameOverHud / MainMenuHud 是同款套路（本工程不落场景文件，
        /// 所有 UI 都在运行时搭出来）。
        /// </summary>
        private DamagePopupLayer _popupLayer;

        // 状态查询的复用缓冲。给 DeterminismDump 这类"自己不带缓冲"的调用方用，
        // HUD 有自己的 List，两者不共用 —— 共用会在同一帧里互相清空。
        private readonly List<ActiveStatus> _statusScratch = new List<ActiveStatus>(8);

        /// <summary>内核战场。未初始化时为 null。</summary>
        public Encounter Encounter
        {
            get { return controller != null ? controller.Encounter : null; }
        }

        /// <summary>
        /// 本局是否已分出胜负（内核 <c>Encounter.IsRunOver</c> 的只读快捷方式）。
        ///
        /// 【为什么要有它】PlayerController 每帧只读 b.IsRunOver 来决定要不要
        /// 屏蔽移动输入，GameOverHud / CombatBridge.Update 也依赖它判断"该不该弹重开"。
        /// 内核的胜负结论只在 Encounter 上，这里转发一层，避免上层到处写
        /// `bridge.Encounter != null &amp;&amp; bridge.Encounter.IsRunOver` 这种冗长且易错的判断。
        /// </summary>
        public bool IsRunOver
        {
            get { return Encounter != null && Encounter.IsRunOver; }
        }

        /// <summary>
        /// 玩法是否被冻结：终局或菜单，任一成立即冻结。
        ///
        /// 【为什么要有这个"合流"属性】暂停来源不止一个（终局 / 主菜单 / ESC 面板），
        /// 如果各自直接写 Scheduler.Paused，就会互相覆盖：典型事故是玩家死亡后打开
        /// 又关闭暂停菜单，关闭时那句 Paused = false 会把终局暂停一并解除，尸体继续挨打。
        /// 所以把"是否冻结"做成一个**只读的合流结论**，写入调度器只走 ApplyPauseState
        /// 一个出口，来源之间就不可能再打架。
        ///
        /// 所有输入闸门（PlayerController / AttackController / SkillController /
        /// DodgeController）读的都是它，而不是各读各的 IsRunOver。
        /// </summary>
        public bool IsGameplayBlocked
        {
            get { return IsRunOver || _menuPaused; }
        }

        /// <summary>菜单是否打开（诊断用只读暴露）。与 IsGameplayBlocked 同源。</summary>
        public bool IsMenuPaused
        {
            get { return _menuPaused; }
        }

        /// <summary>玩家实体。未初始化时为 null。</summary>
        public Combatant Player
        {
            get { return controller != null ? controller.Player : null; }
        }

        /// <summary>
        /// P1-6 玩家成长状态机。供 <c>Hud</c> 与集成测试读取。
        /// <see cref="Start"/> 之前为 null，调用方必须判空。
        /// </summary>
        public PlayerProgression Progression
        {
            get { return _progression; }
        }

        /// <summary>
        /// 当前等级带来的攻击力加成（加法项，1 级恒为 0.0f）。
        ///
        /// 🚨【这是 AttackController 读取加成的**唯一出口**】
        /// 不要让 AttackController 自己去持有 PlayerProgression。
        /// 本项目的既有拓扑是「表现层只认编排层」：控制器认识 CombatBridge，
        /// 但不认识内核里的任何状态对象。开了这个口子，以后每加一个成长属性
        /// 就要在每个控制器里各接一根线，接线图会迅速烂掉。
        ///
        /// 【为什么 null 时返回 0 而不是抛异常】
        /// baselineMode 对拍、以及 Start() 之前的极早期帧都可能读到 null。
        /// 返回 0.0f 意味着「没有加成」，此时 raw = 12 + 0.0f ≡ 12，
        /// 与未接入成长系统之前**逐位等价**，对拍基线不会因此漂移。
        /// </summary>
        public float PlayerAtkBonus
        {
            get { return _progression != null ? _progression.AtkBonus : 0.0f; }
        }

        /// <summary>本区域定义。</summary>
        public ZoneData Zone
        {
            get { return _zone; }
        }

        /// <summary>装配是否完成（首波已生成）。</summary>
        public bool IsReady
        {
            get { return _ready; }
        }

        // ---------------------------------------------------------------------
        // T3 对外状态
        // ---------------------------------------------------------------------

        /// <summary>
        /// 基线对拍模式。运行中切换会立刻重新装配 / 拆卸 T3。
        /// </summary>
        public bool BaselineMode
        {
            get { return baselineMode; }
            set
            {
                if (baselineMode == value)
                {
                    return;
                }
                baselineMode = value;
                if (!_ready)
                {
                    return;
                }
                if (baselineMode)
                {
                    TeardownT3();
                }
                else
                {
                    SetupT3();
                }
            }
        }

        /// <summary>
        /// T3 是否已装配完成。所有 T3 控制器（技能 / 闪避 / HUD）都必须先查它，
        /// 为 false 时一律走 T2 原路径。
        /// </summary>
        public bool T3Enabled
        {
            get { return _t3Enabled; }
        }

        /// <summary>输入意图缓冲。未装配 T3 时为 null。</summary>
        public PlayerIntent Intent
        {
            get { return _intent; }
        }

        /// <summary>T3 事件出口（Unity 实现）。未装配 T3 时为 null。</summary>
        public CombatEventsT3Unity EventsT3
        {
            get { return _eventsT3; }
        }

        /// <summary>技能定义表。未装配 T3 时为 null。</summary>
        public SkillTable SkillTable
        {
            get { return _skillTable; }
        }

        /// <summary>状态定义表。未装配 T3 时为 null。</summary>
        public StatusTable StatusTable
        {
            get { return _statusTable; }
        }

        /// <summary>由 <see cref="WorldBuilder"/> 在激活前注入。</summary>
        public void Configure(CombatController ctrl, Transform player, ZoneData zone)
        {
            controller = ctrl;
            playerTransform = player;
            _zone = zone;
        }

        /// <summary>
        /// 装配期注入 T3 开关。必须在 <c>Start</c> 之前调用（WorldBuilder 的
        /// SetActive(false) → Configure → SetActive(true) 流程天然满足）。
        /// </summary>
        /// <param name="baseline">true = 基线对拍模式，完全不装配 T3。</param>
        /// <param name="softAim">软索敌总开关。</param>
        /// <param name="softAimRange">软索敌最大吸附距离，&lt;=0 表示不限距离。</param>
        public void ConfigureT3(bool baseline, bool softAim, float softAimRange)
        {
            baselineMode = baseline;
            softAimEnabled = softAim;
            softAimMaxRange = softAimRange;
        }

        private void Awake()
        {
            if (controller == null)
            {
                controller = GetComponent<CombatController>();
            }
            _spawner = GetComponent<EnemySpawner>();
            _dump = GetComponent<DeterminismDump>();
        }

        private void Start()
        {
            if (controller == null || controller.Encounter == null)
            {
                Debug.LogError("[T2] CombatBridge 找不到已初始化的 CombatController，战斗未启动。");
                return;
            }

            // 域重载会清空 WorldBuilder 的静态状态。这里补一刀，保证落点校验拿到的是
            // 真地形而不是「整张图都是岩石」。BuildScene 走过来时它是空操作。
            WorldBuilder.EnsureRuntimeState();
            if (_zone == null)
            {
                _zone = WorldBuilder.Zone;
            }

            ApplyPlayerDamageModel();
            InjectZoneConfig();
            SpawnFirstWave();

            // ★P2-1 R-1 建场即置位。
            //   必须在首波之后：ArmBossPending 的前提之一是 Encounter 已就绪，
            //   而且置位后 PendingAwareEnemyCount 立刻多 1，放在首波之前会让
            //   "首波尚未生成的那一小段"也带着债 —— 虽然结果一样，但语义上说不清。
            //   同时必须早于第一次 FixedUpdate（Start 天然满足），否则头一步就可能被判胜。
            ArmBossPending();

            // T3 装配放在首波之后：SetupT3 会按当前存活敌人数决定不了什么，
            // 但 SeedSkillRng 必须晚于 SpawnFirstWave —— 首波生成消耗的是
            // Encounter.Rng，两条流物理隔离，先后顺序不影响任何一方，
            // 放在这里只是为了让"装配完成"是一条直线，读代码时无需回头。
            SetupT3();

            // ★ P1-6 玩家成长曲线接线。
            //   必须晚于 SetupT3()：RefreshSkillRawFromLevel 要写 _skillTable 的 Raw，
            //   而 _skillTable 是在 SetupT3 里建出来的。放在前面会在首次升级时
            //   静默跳过技能路径（判空跳过不报错），变成"普攻涨了、技能没涨"的半瘫。
            SetupProgression();

            // ★ P1-2 受击反馈接线。
            //   放在 SetupProgression 之后：两者都要订阅 CombatEventsUnity.EnemyDied，
            //   而委托是按订阅顺序调用的 —— 先入账经验（可能触发升级、改玩家血上限），
            //   再播击杀强调。反过来的话，击杀强调会在"玩家属性尚未更新"的瞬间播出，
            //   将来若给升级加一档专属反馈，两者的先后就会显出差别。现在先把顺序钉死。
            SetupHitFeedback();

            _ready = true;

            // ★ P0-2 胜负结算面板接线。
            //   动态造一个 GameOverHud 并 Build（和 Hud 同款套路），然后订阅内核的
            //   终局事件。注意：Build() 必须显式调用——AddComponent 只挂脚本，
            //   不自动建 Canvas/Text；不调 Build 的话 Show 会因为 _titleText 为 null 静默失效。
            _gameOverHud = new GameObject("GameOverHud").AddComponent<GameOverHud>();
            _gameOverHud.Build();
            var enc = Encounter;
            if (enc != null && enc.RunState != null)
            {
                enc.RunState.PhaseChanged += OnRunPhaseChanged;
            }

            // ★P2-1 GAP-4 / GAP-5 表现层装配。
            //   为什么卡在这个位置：
            //   ① 必须晚于 ArmBossPending —— 装配失败（比如特效模板没传进来）只该掉表现，
            //      不该连"欠债"这条判定口径一起掉；置位在前，出岔子也仍有兜底。
            //   ② 必须晚于 _gameOverHud —— 两者都要摸 Encounter，让"订阅动作"集中成一段，
            //      日后查订阅/退订是否成对，只需看这一屏。
            //   ③ 必须早于 _mainMenuHud —— 主菜单那一串会立刻 SetMenuPaused(true) 并弹面板，
            //      此后 TickBossFlow 被 IsGameplayBlocked 挡住；装配放在闸门落下之后
            //      虽然也能跑（Update 里还会惰性解析），但"先装配、后开闸"读起来才是一条直线。
            SetupBossFlow();

            // ★ P0-5 主菜单 / 暂停菜单 + P0-6 操作引导接线。
            //   与 GameOverHud 同款套路：AddComponent → Build()（Build 内部才建 Canvas）。
            //   回调用 System.Action 字段绑定而不是 UnityEvent —— 这三个面板只被
            //   CombatBridge 一家使用，走委托可以把"谁负责改状态"钉死在编排层，
            //   面板自己一行战斗逻辑都不碰。
            _mainMenuHud = new GameObject("MainMenuHud").AddComponent<MainMenuHud>();
            _mainMenuHud.Build();
            _pauseMenuHud = new GameObject("PauseMenuHud").AddComponent<PauseMenuHud>();
            _pauseMenuHud.Build();
            _controlsGuideHud = new GameObject("ControlsGuideHud").AddComponent<ControlsGuideHud>();
            _controlsGuideHud.Build();

            // 引导面板是全屏遮罩：读说明期间玩法必须保持冻结，否则玩家边看边挨打。
            // 所以"解冻"这个动作被推迟到面板真正关闭的那一刻，由面板回调发起。
            // 走 SetMenuPaused 而不是直写 Scheduler.Paused —— 后者会绕过 IsRunOver 取或，
            // 破坏"终局后恒为暂停"这条不变量。
            _controlsGuideHud.OnPanelClosed = () => { SetMenuPaused(false); };

            _mainMenuHud.OnStartClicked = () =>
            {
                _mainMenuHud.Hide();
                SetMenuPaused(true);             // 保持冻结，解冻交给 OnPanelClosed
                _controlsGuideHud.ShowPanel();   // 开始游戏后立刻给一次操作说明
            };
            _mainMenuHud.OnQuitClicked = QuitGame;

            _pauseMenuHud.OnResumeClicked = ClosePauseMenu;
            _pauseMenuHud.OnRestartClicked = () =>
            {
                // 重开＝直接回到战斗，不该再逼玩家点一次"开始游戏"。
                MainMenuHud.SkipOnNextLoad = true;
                ReloadScene();
            };
            _pauseMenuHud.OnMainMenuClicked = () =>
            {
                // 返回主菜单＝重载场景后**要**看到主菜单，所以显式清旗标（防上一次残留）。
                MainMenuHud.SkipOnNextLoad = false;
                ReloadScene();
            };
            _pauseMenuHud.OnQuitClicked = QuitGame;

            // 决定这一局的初始状态。旗标是一次性的：读完立刻复位，
            // 否则"返回主菜单"那一路会被上一次重开留下的 true 吃掉。
            bool skip = MainMenuHud.SkipOnNextLoad;
            MainMenuHud.SkipOnNextLoad = false;
            if (skip)
            {
                SetMenuPaused(true);             // 同上：引导期间保持冻结
                _controlsGuideHud.ShowPanel();   // 重开也给一次引导（玩家可任意键秒关）
            }
            else
            {
                // 冷启动直接进入可玩状态：不弹主菜单、不冻结玩法。
                // 当前阶段以「逛水墨世界 / PlayMode 演示」为主目标，开局冻结（主菜单 + 操作引导）
                // 会直接表现为「玩家动不了」。菜单与引导组件仍已在上方 Build 完成，
                // 未来若需要开始菜单，可在暂停流程或单独入口重新唤起，不改此默认行为。
                // （重开路径的 skip 分支保持不变，仍走「引导期间冻结」流程。）
            }

            if (_dump != null)
            {
                _dump.OnWorldReady();
            }
        }

        // ---------------------------------------------------------------------
        // 装配
        // ---------------------------------------------------------------------

        /// <summary>
        /// 接线玩家侧的模型 B 减伤（U1 修复的 Unity 半边）。
        ///
        /// CombatController.BuildEncounter 里已经接过一次；这里**重新断言**一遍，
        /// 是因为上层随时可能通过 BuildEncounter 重建战场（切区、重生），
        /// 而重建后的 Player 是一个全新的 Combatant，滤镜不会自己跟过去。
        /// 把它做成一个可反复调用的幂等方法，重建路径就不需要记得这件事。
        ///
        /// 【为什么不能不接】
        /// DamageFilter 为 null 时 WCore.TakeDamageFrom 直接扣 raw，
        /// 玩家的 def 就成了一个只在数值表上存在、战场上毫无作用的字段。
        /// </summary>
        public void ApplyPlayerDamageModel()
        {
            Combatant p = Player;
            if (p == null || p.WCore == null)
            {
                return;
            }

            float def = PlayerDef;
            p.WCore.DamageFilter = raw => Difficulty.DamageTaken(raw, def);

            // 🚨【N-1 · P1-6 推翻旧口径】旧注释写的是「反调靶子必须与玩家真实血上限同源」，
            // 那句话在没有等级系统的年代成立，现在**已经作废**，不要照着它改回去。
            //
            // 新口径：反调靶子分子 = 平衡基准血量，**永久钉死为常量 260**，
            //         与玩家实时血上限（会随等级涨到 494）彻底解耦。
            //
            // 【为什么必须解耦 —— 不解耦会发生什么】
            // Difficulty 的模型 B 用 d_eff = PlayerHpMax / 65 反推「怪该打多疼」，
            // 目标是让玩家大约挨 4 下死（d_eff = 260/65 = 4.0）。
            // 如果这里回写玩家实时血上限：玩家升到 2 级 → 286 血 → d_eff 变成 4.4
            // → 怪的伤害同步上调 10% → **玩家白升了这一级**，体感强度纹丝不动，
            // 甚至因为 60Hz 量化边界还可能变难。这叫「用橡皮尺量身高」。
            // 这是 PRD R-11，标了「最高优先级」，做反了整个 P1-6 等于没做。
            //
            // 【会不会动数值指纹】不会，而且比改动前更安全：
            // 玩家 1 级血上限本来就是 260，p.HpMax 与常量恒等 ⇒ 对既有基线严格位等价；
            // 改动前 2 级即漂移，改动后任何等级下 d_eff 都恒为 4.0（AC-23 哨兵盯着这条）。
            if (Encounter != null && Encounter.Bridge != null)
            {
                Encounter.Bridge.PlayerHpMax = PlayerHpMax;
                Encounter.Bridge.PlayerDef = def;
            }
        }

        // =====================================================================
        // P1-2 受击反馈（编排层：只负责"造出来 + 接上线 + 拆干净"）
        // =====================================================================

        /// <summary>
        /// 造出反馈四件套的调度者与飘字层，并把内核事件接上去。
        ///
        /// 【拓扑 —— 命中事件只有一个订阅者】
        ///   CombatEventsUnity.HitFeedback ──► HitFeedbackDirector.OnHitFeedback
        ///                                       ├─► FeedbackClock.Frozen（顿帧）
        ///                                       ├─► CameraShake.Kick（屏震）
        ///                                       ├─► CombatView.PlayHitFlash（闪白）
        ///                                       └─► DamagePopupLayer.Push（飘字）
        ///   CombatEventsUnity.EnemyDied   ──► HitFeedbackDirector.OnEnemyDied（R-07）
        ///
        /// 四件套**不各自订阅**：分档逻辑抄四遍，迟早算出四个不一致的档位
        /// （典型症状是"飘字说重击、屏震却是轻的"）。理由详见 Director 文件头。
        ///
        /// 【为什么不在这里找 CameraShake】
        /// 相机由 WorldBuilder 装配，CameraShake 完全可能晚于本方法才被 AddComponent。
        /// Director 自带惰性解析（带 0.25s 节流），交给它比在这里赌时序稳。
        /// Bind 的契约是"传 null 表示这一项保持现状"，正是为这种分批接线设计的。
        ///
        /// 【为什么飘字层要显式传进去，而不是也靠惰性解析】
        /// 它就是本方法**刚刚造出来的**，此刻引用最确定。惰性解析
        /// （FindFirstObjectByType）只是给 PlayMode 测试手工摆场景兜底的。
        /// </summary>
        private void SetupHitFeedback()
        {
            // 飘字层：独立对象，Awake 里自建 Canvas 与 12 个常驻槽位。
            // 不调 Build() —— 与 GameOverHud 那套"AddComponent 之后还要手动 Build"
            // 不同，DamagePopupLayer 在 Awake 里就搭完了，因为它没有任何
            // 需要调用方决定的构建参数。
            _popupLayer = new GameObject("DamagePopupLayer").AddComponent<DamagePopupLayer>();

            // Director 与本组件同体。先 GetComponent 再 AddComponent：
            // 允许将来有人在场景/测试里手工挂一个，不会被这里覆盖成第二份
            // （它带 [DisallowMultipleComponent]，硬加会直接失败）。
            _feedback = GetComponent<HitFeedbackDirector>();
            if (_feedback == null)
            {
                _feedback = gameObject.AddComponent<HitFeedbackDirector>();
            }

            // 批次 3（R-03 玩家受击朱砂闪白）：把玩家身上的染色器显式注入 Director。
            //
            // 【为什么显式传，而不是只靠 Director 的惰性解析】
            // 飘字层就是本方法刚刚 new 出来的，此刻引用最确定，所以上面那行
            // 把它显式 Bind 进来；玩家染色器同理——它挂在玩家 GameObject 上，
            // 由 WorldBuilder.BuildPlayer 在运行时程序化生成。若此刻玩家已经建好，
            // 这里一次 FindFirstObjectByType 就能拿到最确定的引用并直接 Bind；
            // 若还没建好（装配次序不由本方法保证），传 null，Director 自己的
            // 0.25s 节流惰性解析会在下一轮把它补上。两种路径与 _popupLayer 一致。
            //
            // 【为什么不把这段写进 WorldBuilder】
            // 注入的"动作"属于 CombatBridge（受击反馈的装配者），WorldBuilder 的
            // 职责是"把组件挂到玩家身上"。挂上去就好，接线交给该管的人。
            PlayerHitFlash playerFlash;
#if UNITY_2023_1_OR_NEWER
            playerFlash = Object.FindFirstObjectByType<PlayerHitFlash>();
#else
            playerFlash = Object.FindObjectOfType<PlayerHitFlash>();
#endif
            _feedback.Bind(this, null, _popupLayer, playerFlash);

            if (controller != null && controller.EventsUnity != null)
            {
                controller.EventsUnity.HitFeedback += _feedback.OnHitFeedback;
                controller.EventsUnity.EnemyDied += _feedback.OnEnemyDied;
            }
        }

        /// <summary>
        /// 拆掉受击反馈的全部接线，并把表现层恢复到"什么都没发生"的状态。
        ///
        /// 【★ 最后那一句 FeedbackClock.Frozen = false 是防死机的兜底】
        /// Frozen 是**静态**字段，活得比本组件久。若本组件在顿帧进行中被销毁
        /// （换场景 / 按 R 重开 / 退出 PlayMode），Director 也一并没了，
        /// 就再没有人去递减倒计时 —— 下一局开局即全局冻结，画面与死机无异。
        /// 这不是重复保险，是 FeedbackClock 文件里点名要求的第 3 道闸
        /// （见其 Frozen 字段注释"谁负责复位"的第 3 条）。
        ///
        /// 【为什么不 Destroy 掉飘字层的 GameObject】
        /// 它是场景根对象，场景卸载会一并销毁。在 OnDestroy 阶段手动 Destroy
        /// 反而会引入销毁顺序依赖（Unity 不保证同帧内的销毁次序），
        /// 而 ClearAll() 已经把 12 个 Text 全部熄灭，视觉上没有任何残留。
        /// 这也与 GameOverHud / MainMenuHud 的既有做法保持一致。
        /// </summary>
        private void TeardownHitFeedback()
        {
            // 与 SetupHitFeedback 里的两次 += 严格一一对应。
            // CombatEventsUnity 的生命周期跟随 CombatController，可能比本组件活得久，
            // 不退订的话重开后旧回调仍会打到已销毁的 Director 上。
            if (_feedback != null && controller != null && controller.EventsUnity != null)
            {
                controller.EventsUnity.HitFeedback -= _feedback.OnHitFeedback;
                controller.EventsUnity.EnemyDied -= _feedback.OnEnemyDied;
            }

            if (_feedback != null)
            {
                // 顿帧结束 + 屏震归零回正 + 飘字清空 + 去重记录清零。
                _feedback.ClearAll();
            }

            if (_popupLayer != null)
            {
                // Director 为 null（Setup 失败）时的独立兜底：飘字层是本方法的
                // 同伴造出来的，它的收尾不该依赖另一个可能不存在的对象。
                _popupLayer.ClearAll();
            }

            FeedbackClock.Frozen = false;
        }

        // =====================================================================
        // P1-6 玩家成长曲线（编排层：内核只算数，改玩家的活全在这一段）
        // =====================================================================

        /// <summary>
        /// 创建成长状态机并接上两根线：敌人死亡 → 入账经验；升级 → 改玩家属性。
        ///
        /// 【拓扑】
        ///   CombatEventsUnity.EnemyDied ──► OnEnemyDied ──► _progression.GainExpFrom
        ///   _progression.LeveledUp      ──► OnPlayerLeveledUp ──► 改 HpMax / Hp / 技能 raw
        ///
        /// 内核那一侧（PlayerProgression）自始至终不认识 Combatant，
        /// 拆包和写玩家都发生在这个文件里 —— 这就是"只算数，不碰战场"的落地位置。
        ///
        /// 【baselineMode 下要不要禁用】不需要。成长系统不改变执行路径条数，
        /// 只在敌人死亡回调里多做几次整数加法；且基线对拍全程 1 级，
        /// 加成恒为 0，两条伤害路径都逐位等价。
        /// </summary>
        private void SetupProgression()
        {
            _progression = new PlayerProgression();
            _progression.LeveledUp += OnPlayerLeveledUp;

            if (controller != null && controller.EventsUnity != null)
            {
                controller.EventsUnity.EnemyDied += OnEnemyDied;
            }
        }

        /// <summary>
        /// 敌人死亡回调：把经验入账给玩家。
        ///
        /// 【这里是"拆包"发生的地方】
        /// 内核的 GainExpFrom 只收 (int, float) 两个基元，不收 Combatant ——
        /// 于是"成长系统不写敌人字段"从规约变成了编译期性质（AC-26）。
        /// 拆包这一步必须在 Unity 层做，也只能在这里做。
        ///
        /// 【为什么不在这里乘精英/BOSS 倍率】
        /// 因为 ExpValue 出厂时就已经乘过了：DifficultyBridge 刷怪时
        /// 精英 ×2.0、BOSS ×3.0 都算进了 c.ExpValue。这里再乘一次
        /// 精英就会给出 4 倍经验（AC-33 / AC-34 专门盯这条）。
        /// </summary>
        private void OnEnemyDied(Combatant e)
        {
            if (e == null || _progression == null)
            {
                return;
            }

            _progression.GainExpFrom(e.Id, e.ExpValue);
        }

        /// <summary>
        /// 升级回调：把这一级的收益真正写到玩家身上。连升多级会被调用多次，每级一次。
        ///
        /// 【HpMax 先于 Hp 赋值：防御性写法，不是为了绕过钳制】
        /// ⚠️ 事实澄清（2026-08-09 QA 复审订正）：早期注释与架构文档曾声称
        /// "<c>Combatant.Hp</c> 的 setter 会按 HpMax 钳制，颠倒顺序会静默截血"——**这是错的**。
        /// 实测证据：<c>Combatant.cs:176-205</c> 的 Hp / HpMax 两个 setter 都只是透传代理
        /// （<c>WCore != null ? WCore.X : _x</c>），**不含任何钳制逻辑**；
        /// 终点 <c>WCore.cs:65 public float Hp;</c> 与 <c>WCore.cs:68 public float HpMax = 1.0f;</c>
        /// 是**裸公开字段，同样无钳制**。所以就当前实现而言，两行顺序颠倒**不会**产生任何差异。
        ///
        /// 那为什么仍然写死"先上限、后当前血"？——**防御性写法**。
        /// 给血量字段补上钳制（<c>Hp = Min(Hp, HpMax)</c>）是血量系统极常见的演进方向；
        /// 一旦将来有人给 <c>WCore.Hp</c> 加上钳制，本顺序能让这段代码**免于被动返工**，
        /// 而颠倒的顺序会在那一刻突然开始静默截血。顺序零成本，保留即可。
        /// 结论：顺序要求**保留**，但理由是"面向未来的防御"，不是"当下的必需"。
        ///
        /// 【真正需要守住的不变式：Hp 与 HpMax 同额 +ΔHpMax，缺口恒定】（PRD R-07「不回满」）
        /// 这一条才是本方法的核心契约，改动时优先保它——上面的赋值顺序只是保险，它才是正确性本体。
        /// 回满血会诱导玩家残血硬扛去刷怪赌升级，战斗的紧张感直接崩掉。
        /// 同额补偿保证"缺口不变"：260/260 → 286/286，100/260 → 126/286（满血升级仍满血，
        /// 濒死升级仍濒死）。因此 Hp 走 <c>+= ΔHpMax</c> 而**不是** <c>= NewHpMax</c>。
        /// </summary>
        private void OnPlayerLeveledUp(LevelUpInfo info)
        {
            Combatant p = Player;
            if (p != null)
            {
                // 先上限、后当前血：当前实现下两者无钳制、顺序无差异（见方法注释的事实澄清），
                // 保持此序是为将来 WCore.Hp 若被加上钳制而预留的防御。真正的不变式是下一行的同额 +Δ。
                p.HpMax = info.NewHpMax;
                p.Hp = p.Hp + info.DeltaHpMax;
            }

            // 技能路径的 raw 是"写时注入"，必须在升级瞬间刷新。
            // 每级都刷一次是冗余但对称的（幂等），别优化成循环外只刷一次。
            RefreshSkillRawFromLevel();
        }

        /// <summary>
        /// 按当前等级重算技能表里三个伤害技能的 raw。
        ///
        /// 🚨【必须从常量基数重算，禁止用 +=】
        /// 写成 <c>def.Raw += bonus</c> 的话，连升两级会累加两次增量之外，
        /// 还会在任何一次重复调用（比如重开后再调一次）里继续往上叠，
        /// raw 会单调爬升且永远回不来。这是"写时注入"这类做法的经典事故。
        /// 正确姿势：<c>def.Raw = SkillConfig.X_RAW + bonus</c>，
        /// 无论调多少次结果都一样（幂等），也天然支持 Reset 回落。
        ///
        /// 【为什么只刷三项，不刷第 4 槽】
        /// 第 4 槽是踏雪闪避（dodge_roll），它没有 *_RAW 常量、也不造成伤害。
        /// 给它加攻击力加成会凭空造出"闪避也能打人"的新行为。
        ///
        /// 【为什么判空后静默跳过而不是报错】
        /// baselineMode 不装配 T3，<c>_skillTable</c> 恒为 null。
        /// 那是一条合法路径，不该刷日志更不该抛异常。
        /// </summary>
        private void RefreshSkillRawFromLevel()
        {
            if (_skillTable == null)
            {
                return;
            }

            float bonus = PlayerAtkBonus;

            SkillDef basic = _skillTable.Get(SkillConfig.SKILL_BASIC_SLASH);
            if (basic != null)
            {
                basic.Raw = SkillConfig.BASIC_RAW + bonus;
            }

            SkillDef burst = _skillTable.Get(SkillConfig.SKILL_CIRCLE_BURST);
            if (burst != null)
            {
                burst.Raw = SkillConfig.BURST_RAW + bonus;
            }

            SkillDef lotus = _skillTable.Get(SkillConfig.SKILL_BLOOD_LOTUS);
            if (lotus != null)
            {
                lotus.Raw = SkillConfig.LOTUS_RAW + bonus;
            }
        }

        /// <summary>
        /// 重开一局时把成长状态**完整**复位（内核状态 + 玩家身上的外部影响）。
        ///
        /// 【为什么不能只调 _progression.Reset()】
        /// PlayerProgression 是纯观察者，它不持有 Combatant，改不了玩家。
        /// 只调它的 Reset()，会得到一个"显示 1 级、实际 494 血 21 攻"的玩家。
        /// 外部复位这三步（血上限 / 当前血 / 技能 raw）只能由编排层补做，
        /// 这个约束同时写在 <c>PlayerProgression.Reset()</c> 的注释里。
        ///
        /// 【生产路径其实用不到它】
        /// P0-4 重开走的是场景重载，CombatBridge 连同 _progression 一起重建，
        /// 天然回到 1 级。本方法是给「不重载场景直接重开」和集成测试准备的，
        /// 属于把语义补完整，而不是当前有调用方。
        /// </summary>
        public void ResetProgression()
        {
            if (_progression != null)
            {
                _progression.Reset();
            }

            Combatant p = Player;
            if (p != null)
            {
                // 同样是先上限后当前血。这里是回满，因为"重开"本就该满血起步。
                p.HpMax = ProgressionCurve.HpMaxAt(1);
                p.Hp = ProgressionCurve.HpMaxAt(1);
            }

            RefreshSkillRawFromLevel();
        }

        /// <summary>把 zones.json 的 enemies / boss 段喂进 CombatController。</summary>
        private void InjectZoneConfig()
        {
            if (_zone == null)
            {
                Debug.LogWarning("[T2] 区域配置为空，敌人将使用内核缺省值。");
                return;
            }

            // ★P2-1 GAP-1：BOSS 段现在**必须传真**。
            //
            //   这里过去写的是 null，注释理由是"T2 切片不含 BOSS，传了配置就怕误调
            //   SpawnBoss 刷出竹魈王"。那个顾虑在当时成立，现在已经过期：
            //   BOSS 战正是本切片要交付的东西，而"什么时候刷"由 BossFlow 状态机
            //   单向管控（Disabled→Pending→Entering→Fighting→Done），不存在误调。
            //
            //   还传 null 的后果不是"安全"，是 CombatController.SpawnBoss 的
            //   `if (_zoneBoss == null) return null;` 早退分支永远命中 ——
            //   BOSS 永远生不出来，而且一声不吭。
            //
            //   ⚠️ 改这行的人请连注释一起改。上一版就是因为注释没跟着改，
            //      下一个人照着"刻意传 null"又给改了回去。
            controller.SetZoneConfig(_zone.Enemies, _zone.Boss, _zone.BaseLevel);
        }

        private void SpawnFirstWave()
        {
            if (_spawner == null)
            {
                Debug.LogWarning("[T2] 没有 EnemySpawner，本局不会有敌人。");
                return;
            }
            Vector2 center = playerTransform != null
                ? new Vector2(playerTransform.position.x, playerTransform.position.y)
                : WorldBuilder.PlayerSpawn;
            _spawner.SpawnInitialWave(center);
        }

        // ---------------------------------------------------------------------
        // T3 装配 / 拆卸
        // ---------------------------------------------------------------------

        /// <summary>
        /// 装配 T3（幂等，可反复调用；重建战场后需要重新调一次）。
        ///
        /// 【顺序说明】先造数据表 → 再挂玩家组件 → 最后 BindT3。
        /// 反过来的话，BindT3 之后到组件挂完之前存在一个"内核已接受意图、
        /// 但玩家没有 Skills"的窗口期，那一帧的按键会被静默吞掉。
        ///
        /// 【baselineMode 时它做什么】立刻转调 <see cref="TeardownT3"/>。
        /// 把"开关判断"收在方法内部而不是让每个调用方自己判，
        /// 是为了让"只要调了 SetupT3，状态就一定正确"这句话无条件成立。
        /// </summary>
        public void SetupT3()
        {
            if (baselineMode)
            {
                TeardownT3();
                return;
            }
            if (controller == null || controller.Encounter == null)
            {
                return;
            }

            Combatant p = Player;
            if (p == null)
            {
                return;
            }

            if (_skillTable == null)
            {
                _skillTable = SkillConfig.BuildDefaultTable();
            }
            if (_statusTable == null)
            {
                _statusTable = StatusConfig.BuildDefaultTable();
            }
            if (_intent == null)
            {
                _intent = new PlayerIntent();
            }
            _intent.Clear();

            if (p.Action == null)
            {
                p.Action = new ActionState();
            }
            p.Action.Reset();

            if (p.Skills == null)
            {
                p.Skills = new SkillRuntime();
            }
            p.Skills.Table = _skillTable;
            p.Skills.Reset();

            if (p.Status == null)
            {
                p.Status = new StatusComponent();
            }
            p.Status.Table = _statusTable;
            p.Status.Clear();

            if (p.Qi == null)
            {
                p.Qi = ResourcePool.CreateQi();
            }
            p.Qi.Reset();

            if (p.Stamina == null)
            {
                p.Stamina = ResourcePool.CreateStamina();
            }
            p.Stamina.Reset();

            if (_eventsT3 == null)
            {
                _eventsT3 = new CombatEventsT3Unity();
            }
            _eventsT3.ViewOf = ViewOf;
            _eventsT3.PlayerTransform = playerTransform;
            _eventsT3.ResetCounters();

            controller.Encounter.SoftAimEnabled = softAimEnabled;
            controller.BindT3(_intent, _eventsT3, softAimMaxRange, ResolveSkillSeed());

            EnsurePlayerT3Controllers();

            _t3Enabled = true;
        }

        /// <summary>
        /// 补挂玩家侧的 T3 输入控制器（幂等，可反复调用）。
        ///
        /// 【为什么需要它】T3 的 SkillController / DodgeController 曾经漏挂在场景装配里，
        /// 导致技能键与闪避键完全无响应。更麻烦的是，因为本工程"空组件 = 原路径"的设计，
        /// 缺组件不会报任何错，控制台干干净净，只是按键没反应 —— 极难发现。
        /// 这里在装配期补一刀：有就跳过，没有就补上。这样无论玩家对象是新建的，
        /// 还是从旧场景文件反序列化出来的，T3 的输入面一定是接通的。
        /// </summary>
        private void EnsurePlayerT3Controllers()
        {
            if (playerTransform == null)
            {
                return;
            }
            GameObject go = playerTransform.gameObject;
            if (go.GetComponent<SkillController>() == null)
            {
                go.AddComponent<SkillController>();
            }
            if (go.GetComponent<DodgeController>() == null)
            {
                go.AddComponent<DodgeController>();
            }
        }

        /// <summary>
        /// 拆卸 T3，回到纯 T2 路径。**必须把玩家身上的五个组件一起置 null**：
        /// 只解内核侧的绑定而留着组件，<c>PlayerHasT3</c> 仍为 true，
        /// ①-A 会继续跑资源回复与动作推进 —— 那就不是基线了。
        /// </summary>
        public void TeardownT3()
        {
            _t3Enabled = false;

            if (_intent != null)
            {
                _intent.Clear();
            }

            Combatant p = Player;
            if (p != null)
            {
                p.Action = null;
                p.Skills = null;
                p.Status = null;
                p.Qi = null;
                p.Stamina = null;
            }

            if (controller != null)
            {
                controller.UnbindT3();
            }
        }

        /// <summary>
        /// 派生技能随机流的种子。与世界种子同源（同区域同访问次数 → 同技能流），
        /// 但经 <c>SeedSkillRng</c> 内部的 SKILL_STREAM_MIX/INC 变换后与
        /// <c>Encounter.Rng</c> 数学独立。
        /// </summary>
        /// <returns>区域种子。</returns>
        private long ResolveSkillSeed()
        {
            if (_zone != null && !string.IsNullOrEmpty(_zone.ZoneId))
            {
                return ZoneSeed.Derive(_zone.ZoneId, _zone.IsSafe, Bootstrap.ZoneVisits);
            }
            return WorldBuilder.Seed;
        }

        // ---------------------------------------------------------------------
        // T3 输入转发（控制器唯一入口）
        //
        // 控制器不直接碰 CombatController：多一层转发换来的是"T3 是否开启"
        // 这个判断只有一份实现。否则每个控制器都要自己判一遍 baselineMode，
        // 漏掉一个就会在基线模式下把意图写进内核。
        // ---------------------------------------------------------------------

        /// <summary>投递一次施法意图。</summary>
        /// <param name="slot">技能槽位。</param>
        /// <param name="facing">按下瞬间的朝向。</param>
        /// <returns>true = 已写入缓冲。</returns>
        public bool RequestCast(IntentSlot slot, Vector2 facing)
        {
            if (!_t3Enabled || baselineMode || controller == null)
            {
                return false;
            }
            return controller.RequestPlayerCast(slot, facing);
        }

        /// <summary>投递一次闪避意图。</summary>
        /// <param name="dir">闪避方向。</param>
        /// <returns>true = 已写入缓冲。</returns>
        public bool RequestDodge(Vector2 dir)
        {
            return RequestCast(IntentSlot.Dodge, dir);
        }

        // ---------------------------------------------------------------------
        // T3 查询（HUD 用）
        // ---------------------------------------------------------------------

        /// <summary>玩家技能运行时。未装配时为 null。</summary>
        private SkillRuntime PlayerSkills
        {
            get { Combatant p = Player; return p != null ? p.Skills : null; }
        }

        /// <summary>某槽位的技能定义。未装配返回 null。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>技能定义。</returns>
        public SkillDef SkillOf(IntentSlot slot)
        {
            SkillRuntime s = PlayerSkills;
            if (s != null)
            {
                return s.GetDef(slot);
            }
            return _skillTable != null ? _skillTable.GetSlot(slot) : null;
        }

        /// <summary>某槽位的冷却剩余比例：1 = 刚进 CD，0 = 就绪（HUD 扇形遮罩直接用）。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>[0,1]。</returns>
        public float SkillCdRatio(IntentSlot slot)
        {
            SkillRuntime s = PlayerSkills;
            return s != null ? s.CdRatio((int)slot) : 0.0f;
        }

        /// <summary>某槽位的剩余冷却秒数。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>秒数。</returns>
        public float SkillCdSeconds(IntentSlot slot)
        {
            SkillRuntime s = PlayerSkills;
            return s != null ? s.CdRemainSeconds((int)slot) : 0.0f;
        }

        /// <summary>
        /// 某槽位当前是否可施放。**包含资源与动作检查**，不只是 CD ——
        /// HUD 的"亮 / 灰"必须与内核的实际判定同源，否则界面会说谎。
        /// </summary>
        /// <param name="slot">槽位。</param>
        /// <returns>true = 现在按下去能放出来。</returns>
        public bool SkillReady(IntentSlot slot)
        {
            return SkillReject(slot) == CastReject.Ok;
        }

        /// <summary>某槽位不可施放的原因（HUD 区分 CD / 缺蓝 / 缺体力）。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>拒绝原因。</returns>
        public CastReject SkillReject(IntentSlot slot)
        {
            Combatant p = Player;
            if (p == null || p.Skills == null)
            {
                return CastReject.NoSkill;
            }
            return p.Skills.CanCast((int)slot, p.Action, p.Qi, p.Stamina);
        }

        /// <summary>玩家灵力当前值。</summary>
        public float QiCurrent
        {
            get { Combatant p = Player; return p != null && p.Qi != null ? p.Qi.Current : 0.0f; }
        }

        /// <summary>玩家灵力上限。</summary>
        public float QiMax
        {
            get { Combatant p = Player; return p != null && p.Qi != null ? p.Qi.Max : SkillConfig.QI_MAX; }
        }

        /// <summary>玩家灵力比例 [0,1]。未装配时返回 0。</summary>
        public float QiRatio
        {
            get { Combatant p = Player; return p != null && p.Qi != null ? p.Qi.Ratio : 0.0f; }
        }

        /// <summary>玩家体力当前值。</summary>
        public float StaminaCurrent
        {
            get { Combatant p = Player; return p != null && p.Stamina != null ? p.Stamina.Current : 0.0f; }
        }

        /// <summary>玩家体力上限。</summary>
        public float StaminaMax
        {
            get { Combatant p = Player; return p != null && p.Stamina != null ? p.Stamina.Max : SkillConfig.STAM_MAX; }
        }

        /// <summary>玩家体力比例 [0,1]。未装配时返回 0。</summary>
        public float StaminaRatio
        {
            get { Combatant p = Player; return p != null && p.Stamina != null ? p.Stamina.Ratio : 0.0f; }
        }

        /// <summary>当前连击数。P0 只显示不增伤。</summary>
        public int Combo
        {
            get
            {
                return controller != null && controller.Encounter != null
                    ? controller.Encounter.Combo
                    : 0;
            }
        }

        /// <summary>连击增伤倍率。P0 恒为 1.0。</summary>
        public float ComboMult
        {
            get
            {
                return controller != null && controller.Encounter != null
                    ? controller.Encounter.ComboMult
                    : 1.0f;
            }
        }

        /// <summary>玩家当前动作类型。未装配时返回 <see cref="ActionKind.Idle"/>。</summary>
        public ActionKind PlayerAction
        {
            get { Combatant p = Player; return p != null && p.Action != null ? p.Action.Kind : ActionKind.Idle; }
        }

        /// <summary>玩家当前是否处于无敌帧。</summary>
        public bool PlayerIframeActive
        {
            get { Combatant p = Player; return p != null && p.Action != null && p.Action.IsIframeActive; }
        }

        /// <summary>
        /// 把玩家身上的状态填进 <paramref name="outList"/>。
        /// **不清空调用方的列表**——调用方常常要把玩家与目标的状态拼在一起。
        /// </summary>
        /// <param name="outList">输出列表。null 时忽略。</param>
        /// <returns>本次追加的数量。</returns>
        public int PlayerStatuses(List<ActiveStatus> outList)
        {
            return StatusesOf(Player, outList);
        }

        /// <summary>把某个实体身上的状态填进 <paramref name="outList"/>。</summary>
        /// <param name="who">目标实体。</param>
        /// <param name="outList">输出列表。</param>
        /// <returns>本次追加的数量。</returns>
        public int StatusesOf(Combatant who, List<ActiveStatus> outList)
        {
            if (who == null || who.Status == null || outList == null)
            {
                return 0;
            }
            int n = who.Status.Count;
            for (int i = 0; i < n; i++)
            {
                ActiveStatus s = who.Status.At(i);
                if (s != null)
                {
                    outList.Add(s);
                }
            }
            return n;
        }

        /// <summary>某实体身上的状态数量（不产生分配，给 DeterminismDump 用）。</summary>
        /// <param name="who">目标实体。</param>
        /// <returns>状态数量。</returns>
        public int StatusCountOf(Combatant who)
        {
            return who != null && who.Status != null ? who.Status.Count : 0;
        }

        /// <summary>
        /// 离玩家最近的存活敌人（HUD 目标状态栏用）。
        ///
        /// ⚠️ 这**不是**锁定索敌（P1-05），只是一个纯只读的显示辅助：
        /// 它不写任何内核状态，也不参与命中判定。
        /// </summary>
        /// <returns>最近的敌人；无敌人时返回 null。</returns>
        public Combatant NearestEnemy()
        {
            if (controller == null || controller.Encounter == null)
            {
                return null;
            }
            Combatant p = Player;
            if (p == null)
            {
                return null;
            }

            List<Combatant> all = controller.Encounter.Combatants;
            Combatant best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                Combatant c = all[i];
                if (!c.IsEnemy || !c.IsAlive)
                {
                    continue;
                }
                float dx = c.Position.X - p.Position.X;
                float dy = c.Position.Y - p.Position.Y;
                float sq = dx * dx + dy * dy;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// 玩家状态的紧凑摘要（DeterminismDump 的 [t3] 段用）。
        /// 形如 <c>se_poison x3/2.10s</c>，多条以 <c>,</c> 分隔。
        /// </summary>
        /// <returns>摘要字符串；无状态时返回 <c>-</c>。</returns>
        public string PlayerStatusSummary()
        {
            _statusScratch.Clear();
            PlayerStatuses(_statusScratch);
            if (_statusScratch.Count == 0)
            {
                return "-";
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < _statusScratch.Count; i++)
            {
                ActiveStatus s = _statusScratch[i];
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(s.Id).Append(" x").Append(s.Stacks)
                  .Append('/').Append(s.RemainSeconds.ToString("F2")).Append('s');
            }
            _statusScratch.Clear();
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // 查询（HUD / 攻击判定用）
        // ---------------------------------------------------------------------

        /// <summary>玩家当前血量。</summary>
        public float PlayerHp
        {
            get { Combatant p = Player; return p != null ? p.Hp : 0.0f; }
        }

        /// <summary>玩家血量上限。</summary>
        public float PlayerHpMaxNow
        {
            get { Combatant p = Player; return p != null && p.HpMax > 0.0f ? p.HpMax : PlayerHpMax; }
        }

        /// <summary>玩家血量比例 [0,1]。</summary>
        public float PlayerHpRatio
        {
            get { Combatant p = Player; return p != null ? p.HpRatio() : 0.0f; }
        }

        // 下面几个查询都直接走 controller 而不是先 `Encounter e = Encounter;`。
        // 后者要靠 C# 的 "Color Color" 特例（属性名与类型名相同）才能编译，
        // 能过是能过，但它会让任何一个读代码的人先愣三秒 —— 不值得。

        /// <summary>存活敌人数。</summary>
        public int AliveEnemyCount
        {
            get
            {
                return controller != null && controller.Encounter != null
                    ? controller.Encounter.AliveEnemyCount
                    : 0;
            }
        }

        /// <summary>内核已推进的固定步数。</summary>
        public int StepCount
        {
            get
            {
                return controller != null && controller.Encounter != null
                    ? controller.Encounter.StepCount
                    : 0;
            }
        }

        /// <summary>内核已模拟的逻辑秒数。</summary>
        public float ElapsedTime
        {
            get
            {
                return controller != null && controller.Encounter != null
                    ? controller.Encounter.ElapsedTime
                    : 0.0f;
            }
        }

        /// <summary>存活敌人快照。返回内核的新列表，遍历中可安全击杀。</summary>
        public List<Combatant> AliveEnemies()
        {
            if (controller == null || controller.Encounter == null)
            {
                return new List<Combatant>(0);
            }
            return controller.Encounter.AliveEnemies();
        }

        /// <summary>按 id 取视图（攻击特效 / 闪白用）。</summary>
        public CombatView ViewOf(int combatantId)
        {
            return controller != null ? controller.FindView(combatantId) : null;
        }

        // ---------------------------------------------------------------------
        // P0-2 终局事件 / P0-4 重开
        // ---------------------------------------------------------------------

        /// <summary>
        /// 终局事件回调（订阅自 Encounter.RunState.PhaseChanged）。
        ///
        /// 【职责边界】本方法只做"表现层该做的事"：把战斗调度器暂停、把结算面板弹出来。
        /// 它**绝不**去判定胜负——那是内核 RunPhaseTracker 的事（已逐指令验证）。
        /// 暂停放在"上层"而不是内核里，正是 RunPhase.cs 反复强调的"只观察，不干预"：
        /// 内核一旦在死亡后多跑或少跑一步，64 条对拍断言就会整体错位。
        ///
        /// 【PhaseChanged 至多触发一次】RunPhaseTracker 有幂等闸门，从 Playing 到
        /// 分出胜负一局只喊一次，所以这里不需要再加"我是不是已经弹过了"的标注位。
        /// </summary>
        /// <param name="p">新阶段。Playing 表示还在打（理论上本回调不会收到，仍防御性 return）。</param>
        private void OnRunPhaseChanged(RunPhase p)
        {
            if (p == RunPhase.Playing)
            {
                return;
            }

            // 暂停战斗：调度器 Paused 后累加器不再吃 deltaTime，怪和伤害一起停。
            // 注意这一句必须在 Show 之前——先停再弹，避免"面板出来后还有一帧怪在动"的观感。
            //
            // 【为什么不再直接写 Paused = true】走统一闸门 ApplyPauseState：此刻
            // IsRunOver 已为 true，IsGameplayBlocked 必然为 true，结果与原来逐字等价；
            // 但它不会把 _menuPaused 的状态踩掉，也不会被后来的菜单关闭动作反向踩掉。
            ApplyPauseState();

            // ★P2-1：终局必收 BOSS 血条。
            //   BOSS 被打死那一路血条会自愈（HudBossBar.Update 见 _boss.IsAlive == false 就 Hide），
            //   但**玩家先死**那一路不会：BOSS 还活蹦乱跳，血条自然不收，于是结算面板顶上
            //   横着一条满血 BOSS 条，看着像"没打完却弹了结算"。
            //   这里只动显示，不碰 _bossState —— 终局后 IsGameplayBlocked 恒为 true，
            //   TickBossFlow 本来就一步都不会再走，状态机停在哪儿都无所谓，重开时整场重建。
            HideBossBar();

            if (_gameOverHud != null)
            {
                _gameOverHud.Show(p);
            }
        }

        /// <summary>
        /// 场景卸载 / 重开时退订终局事件，避免回调指向已销毁的面板。
        ///
        /// 【为什么必须手动退订】RunPhaseTracker 的订阅者生命周期跟随场景，
        /// 而 Reset 跟随"一局"——它刻意不清订阅（见 RunPhase.cs 注释），所以退订的
        /// 责任落在这里。不这么做，重开后的旧回调还可能被旧订阅链触发，弹错面板。
        /// </summary>
        private void OnDestroy()
        {
            var enc = Encounter;
            if (enc != null && enc.RunState != null)
            {
                enc.RunState.PhaseChanged -= OnRunPhaseChanged;
            }

            // ★ P1-6：与 SetupProgression 里的两次 += 严格对称。
            //   EnemyDied 挂在 CombatEventsUnity 上，它的生命周期跟随 CombatController，
            //   可能比本组件活得久；不退订的话，重开后旧回调仍会把经验记到
            //   一个已被销毁的 _progression 上（虽不崩溃，但会白白吃掉击杀事件）。
            if (controller != null && controller.EventsUnity != null)
            {
                controller.EventsUnity.EnemyDied -= OnEnemyDied;
            }

            if (_progression != null)
            {
                _progression.LeveledUp -= OnPlayerLeveledUp;
            }

            // ★ P1-2：与 SetupHitFeedback 里的两次 += 严格对称，
            //   并在最后兜底放开 FeedbackClock.Frozen（防"下一局开局即全局冻结"）。
            TeardownHitFeedback();

            // ★P2-1：与 SetupBossFlow 的 += 严格对称。
            //   BossPhaseChanged 挂在 CombatEventsUnity 上（生命周期跟随 CombatController），
            //   不退订的话，重开后旧回调会拿着一个已销毁的 HudBossBar 去 SetPhase，
            //   在 Unity 里就是一条 MissingReferenceException —— 而它抛在事件分发链里，
            //   会把同一事件的后续订阅者一起带走。顺手清掉 _bossBar / _boss 两个引用，
            //   免得下一局的惰性解析摸到上一局的残骸（Unity 的 == null 重载能识破，
            //   但"看起来非空、其实已死"的对象没必要留着让人猜）。
            TeardownBossFlow();
        }

        /// <summary>
        /// P0-4 重开：对局结束后按 R 重新加载当前场景。
        ///
        /// 【为什么放在 CombatBridge 的 Update】
        /// 死亡/胜利后战斗已暂停（Scheduler.Paused），但 Unity 的场景循环还在跑，
        /// Update 照常执行，于是能在这里持续监听 R 键。按下即重载场景，世界从头
        /// 重建（Encounter.Clear 会 Reset 裁判，第二局照常判定胜负）。
        /// </summary>
        private void Update()
        {
            // ESC 放在 R 之前处理：两者互斥（ESC 只在未终局时生效、R 只在终局后生效），
            // 顺序其实不影响结果，但"菜单类输入优先"读起来更符合直觉。
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                // 终局面板已经在了就不叠暂停菜单，避免两层面板打架
                if (!IsRunOver)
                {
                    // 主菜单开着时 ESC 不响应（否则会把主菜单解冻）
                    if (_mainMenuHud == null || !_mainMenuHud.gameObject.activeSelf)
                    {
                        // 引导面板开着时 ESC 只负责关引导，不再叠一层暂停菜单。
                        // 解冻由 ControlsGuideHud.OnPanelClosed 回调发起，这里不重复写状态。
                        if (_controlsGuideHud != null && _controlsGuideHud.PanelShown)
                        {
                            _controlsGuideHud.HidePanel();
                        }
                        else
                        {
                            TogglePauseMenu();
                        }
                    }
                }
            }

            if (IsRunOver && UnityEngine.Input.GetKeyDown(KeyCode.R))
            {
                // 按 R 重开＝直接回战斗，与暂停菜单的"重新开始"走同一条路。
                MainMenuHud.SkipOnNextLoad = true;
                ReloadScene();
            }

            // ★P2-1 BOSS 出场编排推进。
            //
            // 【为什么放在 Update 而不是 FixedUpdate】
            // 这里做的全是表现层与场景层的活（生成实体、换贴图、开血条），不参与
            // 内核的确定性推进。判据本身取自内核的 BossPendingIdleSeconds ——
            // 那个数是在 StepFixed 里按固定 dt 累加的，暂停时自动冻结，
            // 所以"用 Update 的频率去读一个 FixedUpdate 的时钟"不会带来任何抖动：
            // 快慢只影响"发现超时"的延迟上限（一帧），不影响触发阈值本身。
            //
            // 【为什么放在最后】ESC / R 两段是输入响应，可能改变闸门状态
            // （开暂停菜单、重载场景）。先结算输入再推进编排，TickBossFlow 开头那句
            // IsGameplayBlocked 判的就是本帧的最新结论，不会用上一帧的旧状态多走一步。
            TickBossFlow();
        }

        // ---------------------------------------------------------------------
        // ★P2-1 BOSS 出场编排（唯一置位者 + 唯一清位者 + 唯一兜底者）
        //
        // 【为什么这一整块只能有一个主人】
        // BossPending 是"胜利抑制开关"。只要有第二个地方能置位或清位，
        // 就必然出现两种灾难之一：
        //   ① 该清没清 → PendingAwareEnemyCount 永远 ≥ 1 → 玩家清光全场却永远判不出 Won，
        //      卡死在空地图上。这是本切片唯一的致命故障模式；
        //   ② 不该清却清了 → 清完杂兵当场判 Won，BOSS 永远不出场，
        //      而且玩家多半以为"设计如此"，连报障都不会来。
        // 所以置位、清位、兜底三件事全部锁在本文件本区块，别处一行都不许写。
        //
        // 【四道纵深防线】
        //   R-1 置位与出场条件强绑定：三个前提缺一就退化成 Disabled，全程不置位；
        //   R-2 状态机单向推进：Disabled / Pending→Entering→Fighting→Done，杜绝抖动；
        //   R-3 重开必复位：Encounter.Clear() 内已带 ClearBossPending()（内核侧）；
        //   R-4 双层兜底超时：4s 重试（≤2 次）→ 12s 强制清位 + LogError。
        // ---------------------------------------------------------------------

        /// <summary>
        /// BOSS P3 冲击波的特效模板（GAP-5）。由 <c>WorldBuilder.BuildCombat</c> 在装配期写入，
        /// 本组件在 <see cref="SetupBossFlow"/> 里把它转交给 <c>CombatEventsUnity.ShockwaveFxPrefab</c>。
        ///
        /// 【为什么要在这里中转一手】WorldBuilder 建好模板时，Combat 节点还是未激活状态，
        /// <c>CombatController.Awake</c> 尚未执行，<c>EventsUnity</c> 根本还不存在，
        /// 当场赋值只会写到一个 null 上。
        /// </summary>
        [HideInInspector] public GameObject ShockwaveFxTemplate;

        // BOSS 出场状态机。单向推进，见 BossFlowState 的注释。
        private BossFlowState _bossState = BossFlowState.Disabled;

        // 下一次生成尝试的**截止时刻**，单位是 Encounter.BossPendingIdleSeconds 的秒数。
        // 之所以拿内核的 idle 计时而不是 Time.deltaTime 自己攒：
        // 内核计时在暂停时自动冻结（StepFixed 不被调），玩家开菜单泡茶不会误触发兜底。
        private float _bossEntryTimer;

        // 已用掉的重试次数。上限 BossFlowConfig.MaxSpawnRetries。
        private int _bossSpawnRetries;

        // 当前 BOSS 实体。未出场 / 已阵亡时为 null。
        private Combatant _boss;

        // BOSS 血条。由 Hud 在自己的 Build() 里建出来，这里惰性解析引用
        // —— Hud.Start 与本组件 Start 的先后顺序是 Unity 不保证的，不能在 Start 里一次性取死。
        private HudBossBar _bossBar;

        // 诊断日志去重旗标（DiagTimeoutSeconds 一局只打一次）。
        private bool _bossDiagLogged;

        /// <summary>当前 BOSS 编排状态名（供 HUD 调试面板 C3 显示）。</summary>
        public string BossFlowStateName
        {
            get { return _bossState.ToString(); }
        }

        /// <summary>当前是否欠着 BOSS 债（供 HUD 调试面板 C3 显示）。</summary>
        public bool IsBossPending
        {
            get
            {
                var enc = Encounter;
                return enc != null && enc.BossPending;
            }
        }

        /// <summary>"欠债且场上零敌人"的连续滞留秒数（供 HUD 调试面板 C3 与兜底判据使用）。</summary>
        public float BossPendingIdleSeconds
        {
            get
            {
                var enc = Encounter;
                return enc != null ? enc.BossPendingIdleSeconds : 0.0f;
            }
        }

        /// <summary>当前 BOSS 实体（未出场 / 已阵亡为 null）。供集成测试读取。</summary>
        public Combatant CurrentBoss
        {
            get { return _boss; }
        }

        /// <summary>BOSS 血条（可能尚未解析，返回 null）。供集成测试读取。</summary>
        public HudBossBar BossBar
        {
            get { return _bossBar; }
        }

        /// <summary>
        /// ★R-1 建场即置位。在 <see cref="Start"/> 里首波生成之后调用一次。
        ///
        /// 【为什么必须在建场时就置位，而不是等"快清完了"再置】
        /// 因为不存在"快清完了"这个可靠时刻。范围技能一步能把最后 3 只全带走，
        /// <c>AliveEnemyCount</c> 从 3 直接跳到 0，中间没有任何一帧给上层反应。
        /// 而 <c>StepFixed</c> 的步 ⑦ 就在同一步里判定 —— 上层无论多勤快都来不及插手。
        /// 唯一安全的做法就是"这一局从头到尾都欠着一只 BOSS"，等真身入列再销账。
        ///
        /// 【三个前提缺一不可】任一为假就退化成 <see cref="BossFlowState.Disabled"/>，
        /// 全程不置位、行为与无 BOSS 关卡完全一致 —— 宁可这局没 BOSS，绝不软锁。
        /// </summary>
        private void ArmBossPending()
        {
            var enc = Encounter;

            if (_zone == null || _zone.Boss == null || enc == null)
            {
                _bossState = BossFlowState.Disabled;

                // 不是错误：安全区 / 无 BOSS 的区域走到这里是完全正常的。
                // 但要留一行痕，免得"zone_youhuang 打完没见到 BOSS"时无从下手。
                Debug.Log(string.Format(
                    "[T2] BOSS 流程未启用（zone={0} bossCfg={1} encounter={2}），本局按无 BOSS 关卡进行。",
                    _zone != null ? _zone.ZoneId : "null",
                    _zone != null && _zone.Boss != null ? "ok" : "null",
                    enc != null ? "ok" : "null"));
                return;
            }

            enc.MarkBossPending();
            _bossState = BossFlowState.Pending;
            _bossEntryTimer = BossFlowConfig.EntryDelaySeconds;
            _bossSpawnRetries = 0;
            _bossDiagLogged = false;
        }

        /// <summary>
        /// 装配 BOSS 的表现层接线（GAP-4 / GAP-5）。在 <see cref="Start"/> 里
        /// <c>_gameOverHud</c> 建完之后、主菜单建出来之前调用。
        /// </summary>
        private void SetupBossFlow()
        {
            if (controller == null || controller.EventsUnity == null)
            {
                return;
            }

            // ★GAP-4：BossPhaseChanged 事件在桥接层早就实现了，只是**一个订阅者都没有** ——
            //   BOSS 从 P1 打到 P3，内核老老实实抛了两次事件，全都掉在地上。
            controller.EventsUnity.BossPhaseChanged += OnBossPhaseChanged;

            // ★GAP-5：ShockwaveFxPrefab 同理，字段一直在，就是没人赋值，
            //   于是 SpawnFx 每次都因为 prefab == null 静默 return，冲击波全程无特效。
            if (ShockwaveFxTemplate != null)
            {
                controller.EventsUnity.ShockwaveFxPrefab = ShockwaveFxTemplate;
            }
            else
            {
                Debug.LogWarning("[T2] 冲击波特效模板未装配，BOSS P3 冲击波将无视觉表现（伤害仍正常结算）。" +
                                 "请检查 WorldBuilder.BuildCombat 是否给 bridge.ShockwaveFxTemplate 赋了值。");
            }

            // 血条这里先试一次；取不到不算错（Hud.Start 可能还没跑），
            // 真正需要时 ResolveBossBar() 会再找一遍。
            ResolveBossBar();
        }

        /// <summary>惰性解析 BOSS 血条引用。</summary>
        /// <returns>血条组件；场景里还没有则返回 null。</returns>
        private HudBossBar ResolveBossBar()
        {
            if (_bossBar == null)
            {
#if UNITY_2023_1_OR_NEWER
                _bossBar = Object.FindFirstObjectByType<HudBossBar>();
#else
                _bossBar = Object.FindObjectOfType<HudBossBar>();
#endif
            }
            return _bossBar;
        }

        /// <summary>
        /// BOSS 出场编排的每帧推进。由 <see cref="Update"/> 调用。
        ///
        /// 【计时基准】全部判据都是内核的 <c>BossPendingIdleSeconds</c>
        /// （"欠债 **且** 场上零敌人"的连续滞留时长），**不是**"自置位起的总时长"。
        /// 后者在建场即置位的语义下必然误触发：玩家正常清杂兵要几十秒到几分钟，
        /// 照抄 PRD 的"10 秒兜底"会 100% 复现"BOSS 永远不出场"—— 那比软锁更隐蔽。
        /// </summary>
        private void TickBossFlow()
        {
            // 统一闸门。菜单打开 / 终局之后不推进 BOSS 编排 ——
            // 与内核侧对齐：那种状态下 StepFixed 也不跑，idle 同样冻结。
            // 注意这里读的是只读结论 IsGameplayBlocked，**不碰** Scheduler.Paused。
            if (IsGameplayBlocked)
            {
                return;
            }

            if (_bossState == BossFlowState.Disabled || _bossState == BossFlowState.Done)
            {
                return;
            }

            var enc = Encounter;
            if (enc == null)
            {
                return;
            }

            // 诊断留痕（C 类防线）：只打日志，**不改任何状态**。
            // 用途是把"玩家挂机十分钟"这类非缺陷场景在日志里区分出来。
            if (!_bossDiagLogged && enc.BossPendingTotalSeconds > BossFlowConfig.DiagTimeoutSeconds)
            {
                _bossDiagLogged = true;
                Debug.LogWarning(string.Format(
                    "[T2] BOSS 债已挂起 {0:F0} 秒（state={1}，场上敌人 {2}）。" +
                    "若玩家仍在正常清怪，这是预期行为，本条仅供日志分辨。",
                    enc.BossPendingTotalSeconds, _bossState, enc.AliveEnemyCount));
            }

            if (_bossState == BossFlowState.Pending)
            {
                // 还有杂兵没清完 —— 这正是 BossPending 存在的意义，安静等着就行。
                if (enc.AliveEnemyCount > 0)
                {
                    return;
                }

                // 场上清空，进入入场倒计时。idle 此刻从 0 开始涨，
                // 截止时刻直接用秒数表示，与 idle 同一标尺。
                _bossState = BossFlowState.Entering;
                _bossEntryTimer = BossFlowConfig.EntryDelaySeconds;
                return;
            }

            if (_bossState == BossFlowState.Entering)
            {
                TickBossEntering(enc);
                return;
            }

            // Fighting：只负责收尾。BOSS 阵亡后把血条收起来、状态推到终态。
            // 判胜由内核照常完成（此刻已销债，PendingAwareEnemyCount == AliveEnemyCount）。
            if (_bossState == BossFlowState.Fighting)
            {
                if (_boss == null || !_boss.IsAlive)
                {
                    _boss = null;
                    _bossState = BossFlowState.Done;
                    HideBossBar();
                }
            }
        }

        /// <summary>
        /// Entering 态的推进：入场倒计时 → 生成 → 失败重试 → 硬超时放行。
        /// </summary>
        /// <param name="enc">当前战场（调用方已判非空）。</param>
        private void TickBossEntering(Encounter enc)
        {
            float idle = enc.BossPendingIdleSeconds;

            // ★R-4 二级（最后防线）：认定 BOSS 系统不可用，主动放弃本局 BOSS 战。
            //   必须放在最前面判：无论卡在倒计时还是卡在重试，12 秒一到无条件放行。
            //   宁可这局没打到 BOSS，也绝不让玩家卡在空地图上。
            if (idle >= BossFlowConfig.HardTimeoutSeconds)
            {
                Debug.LogError(string.Format(
                    "[T2] BOSS 出场失败：已滞留 {0:F1} 秒、重试 {1} 次仍未生成，强制解除胜利抑制。" +
                    "本局将不会有 BOSS。请检查 zones.json 的 boss 段与 CombatController.SetZoneConfig。",
                    idle, _bossSpawnRetries));

                enc.ClearBossPending();

                // ★必须落到终态：否则下一帧 TickBossFlow 又会把流程重新拉起来，
                //   形成"清位 → 置位 → 清位"的抖动，日志会被刷爆。
                _bossState = BossFlowState.Done;
                _boss = null;
                HideBossBar();
                return;
            }

            // 还没到这一次尝试的时刻。首次是 EntryDelaySeconds(1.5s)，
            // 之后每失败一次就退到 EntryTimeoutSeconds × 重试序号（4s / 8s）。
            if (idle < _bossEntryTimer)
            {
                return;
            }

            if (TrySpawnBossNow())
            {
                _bossState = BossFlowState.Fighting;
                return;
            }

            // ★R-4 一级：生成失败，安排下一次重试。
            if (_bossSpawnRetries < BossFlowConfig.MaxSpawnRetries)
            {
                _bossSpawnRetries++;
                _bossEntryTimer = BossFlowConfig.EntryTimeoutSeconds * _bossSpawnRetries;

                Debug.LogWarning(string.Format(
                    "[T2] BOSS 生成失败（第 {0}/{1} 次重试），已滞留 {2:F1} 秒，将在 idle={3:F1}s 时再试。",
                    _bossSpawnRetries, BossFlowConfig.MaxSpawnRetries, idle, _bossEntryTimer));
            }
            else
            {
                // 机会用尽，不再尝试，也不要每帧重试刷屏 —— 把闸门推到硬超时之后，
                // 剩下的交给上面那段二级兜底收场。
                _bossEntryTimer = BossFlowConfig.HardTimeoutSeconds;
            }
        }

        /// <summary>
        /// 立刻尝试生成 BOSS。**销债严格发生在实体入列之后**（不变量 I-3）。
        /// </summary>
        /// <returns>成功生成并完成接线返回 true；否则 false（调用方负责重试 / 兜底）。</returns>
        private bool TrySpawnBossNow()
        {
            var enc = Encounter;
            if (controller == null || enc == null)
            {
                Debug.LogError("[T2] TrySpawnBossNow：controller 或 Encounter 为空，无法生成 BOSS。");
                return false;
            }

            Vector2 at = PickBossSpawnPos();
            Combatant boss = controller.SpawnBoss(at);

            if (boss == null)
            {
                // ★C2 防线：把 SpawnBoss 两个早退分支的判定结果直接摊出来，
                //   一眼就能看出是"zones.json 的 boss 段没传进去"还是"战场没建起来"。
                Debug.LogError(string.Format(
                    "[T2] SpawnBoss 返回 null（zone={0}，zone.Boss={1}，Encounter={2}）。" +
                    "最可能的原因是 InjectZoneConfig 又把 boss 段传成了 null（GAP-1 复发）。",
                    _zone != null ? _zone.ZoneId : "null",
                    _zone != null && _zone.Boss != null ? "ok" : "null",
                    "ok"));
                return false;
            }

            // ① 视图：模板是未激活的，不 Dress 就等于"BOSS 在打你但屏幕上什么都没有"（GAP-6）。
            //    放在销债之前：万一 DressBoss 里出岔子，此刻债还挂着，兜底照样能兜。
            if (_spawner != null)
            {
                _spawner.DressBoss(boss);
            }

            // ② ★不变量 I-3：销债必须严格在 SpawnBoss 返回非 null 之后。
            //    SpawnBoss 内部的 Encounter.Add 是**直接** Combatants.Add（不走 _pendingAdd 队列），
            //    所以此刻 AliveEnemyCount 已经 +1；先加后减，握手区间内
            //    PendingAwareEnemyCount 始终 ≥ 1，中间不存在任何"债清了怪没到"的空窗。
            //    而这两行同处一个 Update，中间也不可能插进一次 StepFixed。
            enc.ClearBossPending();

            _boss = boss;

            // ③ 血条。Hud 可能比本组件晚 Start，这里再解析一次。
            HudBossBar bar = ResolveBossBar();
            if (bar != null)
            {
                bar.Show(boss);
            }

            return true;
        }

        /// <summary>
        /// 抽一个 BOSS 落点：以玩家为中心的圆环采样 + 地形校验。
        ///
        /// 【为什么只用 Encounter.Rng，不用 UnityEngine.Random】
        /// 后者是全局静态流，既不受区域种子控制、也不参与 DeterminismDump 的记录，
        /// 掺进来同种子重放就当场作废 —— 而"同种子同世界"是这个工程的地基之一。
        ///
        /// 【为什么落点要过一遍地形】圆环采样完全不知道地形，BOSS 有相当概率
        /// 刷进水里或岩石里。校验口径与 <c>EnemySpawner.RelocateIfBlocked</c> 完全一致：
        /// 先夹回地图内，再用**不消耗随机流**的确定性螺旋搜索挪到最近的可站立格。
        /// 用不消耗随机流的方式修正，随机流的形状才与地形彻底解耦。
        /// </summary>
        /// <returns>已通过地形校验的世界坐标。</returns>
        private Vector2 PickBossSpawnPos()
        {
            Vector2 center = playerTransform != null
                ? new Vector2(playerTransform.position.x, playerTransform.position.y)
                : WorldBuilder.PlayerSpawn;

            Vector2 pos = center;

            var enc = Encounter;
            if (enc != null)
            {
                // 圆环口径沿用首波生成（SpawnRingMin/Max），BOSS 出场距离与杂兵一致，
                // 玩家的空间预期不会因为"这次是 BOSS"而突然改变。
                float ang = enc.Rng.NextRange(0.0f, 360.0f);
                float rad = enc.Rng.NextRange(WorldBuilder.SpawnRingMin, WorldBuilder.SpawnRingMax);
                Vec2 offset = Vec2.Right.RotatedDeg(ang) * rad;
                pos = new Vector2(center.x + offset.X, center.y + offset.Y);
            }

            float half = WorldBuilder.TileUnit * 0.5f;
            pos.x = Mathf.Clamp(pos.x, half, WorldBuilder.WorldWidth - half);
            pos.y = Mathf.Clamp(pos.y, half, WorldBuilder.WorldHeight - half);

            if (!WorldBuilder.IsWalkableWorld(pos))
            {
                Vector2Int tile = WorldBuilder.WorldToTile(pos);
                pos = WorldBuilder.FindSpawnableNear(tile.x, tile.y);
            }

            return pos;
        }

        /// <summary>
        /// BOSS 阶段变化回调（订阅自 <c>CombatEventsUnity.BossPhaseChanged</c>）。
        ///
        /// 内核的 <c>BossController.CheckPhaseTransition</c> 保证只降不升，
        /// 且 P1 直接跌破 30% 的连跳只抛一次 P3，所以这里不需要自己去重。
        /// </summary>
        /// <param name="phase">新阶段。</param>
        private void OnBossPhaseChanged(BossPhase phase)
        {
            HudBossBar bar = ResolveBossBar();
            if (bar != null)
            {
                bar.SetPhase(phase);
            }

            Debug.Log(string.Format("[T2] BOSS 进入 {0} 阶段。", phase));
        }

        /// <summary>收起 BOSS 血条（可重复调用）。</summary>
        private void HideBossBar()
        {
            if (_bossBar != null)
            {
                _bossBar.Hide();
            }
        }

        /// <summary>退订 BOSS 相关事件。与 <see cref="SetupBossFlow"/> 严格对称。</summary>
        private void TeardownBossFlow()
        {
            if (controller != null && controller.EventsUnity != null)
            {
                controller.EventsUnity.BossPhaseChanged -= OnBossPhaseChanged;
            }
            _bossBar = null;
            _boss = null;
        }

        // ---------------------------------------------------------------------
        // P0-5 暂停闸门 / 菜单
        // ---------------------------------------------------------------------

        /// <summary>
        /// 把当前闸门状态同步到调度器。所有暂停来源变化后都必须调它 ——
        /// 这是**唯一**允许写 Scheduler.Paused 的地方，别处再写就会重演"来源互相覆盖"。
        /// </summary>
        private void ApplyPauseState()
        {
            if (controller != null && controller.Scheduler != null)
            {
                controller.Scheduler.Paused = IsGameplayBlocked;
            }
        }

        /// <summary>
        /// 菜单开关暂停的唯一入口。
        /// </summary>
        /// <param name="paused">true = 菜单占用中，冻结玩法。</param>
        public void SetMenuPaused(bool paused)
        {
            _menuPaused = paused;
            ApplyPauseState();
        }

        /// <summary>ESC 切换暂停面板。打开即冻结，关闭即解冻（终局时不解冻，见 IsGameplayBlocked）。</summary>
        private void TogglePauseMenu()
        {
            if (_pauseMenuHud == null)
            {
                return;
            }
            if (_pauseMenuHud.IsShown)
            {
                ClosePauseMenu();
            }
            else
            {
                _pauseMenuHud.Show();
                SetMenuPaused(true);
            }
        }

        /// <summary>关闭暂停面板并解冻。</summary>
        private void ClosePauseMenu()
        {
            if (_pauseMenuHud != null)
            {
                _pauseMenuHud.Hide();
            }
            SetMenuPaused(false);
        }

        /// <summary>重载当前场景（重开一局 / 返回主菜单共用）。</summary>
        private void ReloadScene()
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        /// <summary>
        /// 退出游戏。编辑器里 Application.Quit 是空操作，必须改停 PlayMode，
        /// 否则点"退出"毫无反应会被当成 bug。UnityEditor 只存在于编辑器程序集，
        /// 引用它的代码必须被 #if UNITY_EDITOR 包住，否则打包直接编译失败。
        /// </summary>
        private void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
