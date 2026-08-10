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
        /// P1-3 音效总调度。与本组件同体（同一个 GameObject）。
        ///
        /// 【为什么同体 —— 与 _feedback 完全同一条理由】
        /// 它要读本组件的 IsGameplayBlocked / IsRunOver 做暂停三态收敛，同体时
        /// GetComponent 一次即得；也让"暂停判定权在 Bridge、发声权在 Director"
        /// 这条拓扑在场景层级上一眼可见。
        ///
        /// 【为什么不做成静态单例】
        /// 它持有 16 路 AudioSource 与一批 AudioClip（非托管资源）。做成静态单例
        /// 就必须自己管跨场景的释放时机，而挂成组件则天然跟随"一局"的生命周期，
        /// 释放点唯一且确定（见 TeardownAudio）。
        /// </summary>
        private AudioDirector _audio;

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

            // ★ P1-3 音效接线。
            //   放在 SetupHitFeedback 之后有两条硬理由，不要随手上移：
            //   1) 两者都订阅 CombatEventsUnity.EnemyDied 一族的事件，委托按订阅
            //      顺序调用。先屏震/飘字、后发声，与"看见了才听见"的直觉一致；
            //      反过来则会在极端帧里出现"声音先于画面"的错位感。
            //   2) SetupAudio 内部会同步跑一次全量预合成（约几十毫秒）。本方法
            //      末尾无论走哪条分支都会 SetMenuPaused(true) 并弹面板，
            //      也就是说这一卡顿必然落在"玩家正在看静态面板"的窗口里。
            //      若把它挪到 SpawnFirstWave 之前，卡顿就会暴露在世界淡入的那一刻。
            SetupAudio();

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
                SetMenuPaused(true);
                _mainMenuHud.Show();
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
        // P1-3 音效（编排层：同样只负责"造出来 + 接上线 + 拆干净"）
        // =====================================================================

        /// <summary>
        /// 造出音效总调度并把两个 PlaySfx 出口接上去，然后同步做一次全量预合成。
        ///
        /// 【拓扑 —— 两个出口，一个入口】
        ///   CombatEventsUnity.PlaySfx   ──┐
        ///                                 ├──► AudioDirector.Play(string)
        ///   CombatEventsT3Unity.PlaySfx ──┘
        ///
        /// 内核那两个事件都是 <c>Action&lt;string&gt;</c>，签名与 <c>Play</c> 逐字匹配，
        /// 所以直接挂方法组，不包 lambda —— 包了就再也 -= 不掉（lambda 每次
        /// 求值都是一个新委托实例，退订必然失败，这是 C# 事件最经典的一个坑）。
        ///
        /// 【为什么两个出口都接，而不是只接一个】
        /// 它们是两套独立的事件面：CombatEventsUnity 出的是 T2 的命中 / 死亡 /
        /// BOSS 阶段（8 个 key，内核里写死字面量），CombatEventsT3Unity 出的是
        /// T3 的技能 / 闪避（4 个 key，引 SkillConfig 常量）。少接一个的症状是
        /// "普攻有声、技能没声"，而且控制台干干净净 —— 与 T3 控制器漏挂那次
        /// （见 EnsurePlayerT3Controllers 的注释）是同一类静默失效。
        ///
        /// 【为什么不改 CombatEventsT3Unity / CombatEventsUnity】
        /// 它们已经**自带** PlaySfx 出口，只是从来没人订阅（内核侧 `if (PlaySfx != null)`
        /// 一直为假，等于空转）。本批要做的只是在编排层把订阅者补上，
        /// 事件面一行都不用碰 —— 这正是当初把 PlaySfx 预留在那里的用意。
        ///
        /// 【★ baselineMode 的边界，先说清楚免得被当成 bug】
        /// baselineMode 开启时 SetupT3 会立刻转调 TeardownT3 并直接返回，
        /// <c>_eventsT3</c> 保持为 null，所以下面第二根线接不上。这是**正确**的：
        /// 基线模式下玩家根本没有 Skills / Action 组件，技能与闪避不可能触发，
        /// 也就没有任何 T3 音效需要转达。唯一的残留边界是"运行期把 baselineMode
        /// 从 true 改回 false" —— 那条路径下 _eventsT3 是在本方法之后才被 new 出来的，
        /// 技能音效要等下一次场景重载才恢复。baselineMode 是对拍开关（看数字，不听声），
        /// 为它加一套重接线机制不值当，这里只把结论写明。
        /// </summary>
        private void SetupAudio()
        {
            // Director 与本组件同体。先 GetComponent 再 AddComponent —— 与
            // SetupHitFeedback 里对 HitFeedbackDirector 的处理完全同款：
            // 允许别人（场景 / PlayMode 测试）先手工挂一个，不会被这里覆盖成第二份
            // （它带 [DisallowMultipleComponent]，硬加会直接失败）。
            _audio = GetComponent<AudioDirector>();
            if (_audio == null)
            {
                _audio = gameObject.AddComponent<AudioDirector>();
            }

            // 注入暂停状态来源。Director 自己也带惰性解析（0.25 s 节流）做兜底，
            // 但此刻 this 的引用最确定，显式传比赌解析稳 —— 与 _popupLayer 同理。
            _audio.Bind(this);

            // 出口 ①：T2 事件面（8 个 key）。
            if (controller != null && controller.EventsUnity != null)
            {
                controller.EventsUnity.PlaySfx += _audio.Play;
            }

            // 出口 ②：T3 事件面（4 个 key）。baselineMode 下为 null，见方法头。
            if (_eventsT3 != null)
            {
                _eventsT3.PlaySfx += _audio.Play;
            }

            // 全量预合成。放在接线**之后**：万一某张配方抛异常，前面的线已经接好，
            // 那些能合成的音效照常发声（AudioClipFactory 对失败 key 单独拉黑）。
            // 反过来先预合成再接线的话，一次异常会把整条接线一起带走。
            _audio.Prewarm();
        }

        /// <summary>
        /// 拆掉音效的全部接线，并把 AudioClip 真正释放掉。
        ///
        /// 【★ 为什么音频的收尾比飘字严格得多】
        /// 飘字层清空只是"熄灯"，漏了顶多留几个看不见的 Text；而 AudioClip 是
        /// **非托管资源**，Unity 的 GC 不会替你回收由 AudioClip.Create 造出来的那块
        /// PCM 缓冲。13 条音效里光环境衬底一条就是 12 s × 44100 × 4 B ≈ 2.1 MB，
        /// 每重开一局漏一份，按 R 连点二十次就是 40 MB 有去无回。
        ///
        /// 【三步顺序不可交换】Stop() → source.clip = null → Destroy(clip)。
        /// 具体理由写在 AudioDirector.ClearAll 的注释里（在 AudioSource 仍持有并
        /// 播放某个 clip 时销毁它，Unity 的行为未定义，可能播出一段刺耳噪声）。
        /// 本方法只负责按顺序发起，不重复实现。
        ///
        /// 【为什么不 Destroy voice 池的 GameObject】
        /// 与上面 TeardownHitFeedback 对飘字层的裁定同款：GameObject / Component
        /// 归场景管，手动销毁反而引入销毁顺序依赖。分界线是"AudioClip 不归任何
        /// GameObject 管，必须显式 Destroy"。
        /// </summary>
        private void TeardownAudio()
        {
            // 与 SetupAudio 里的两次 += 严格一一对应，连判空条件都逐字相同。
            // CombatEventsUnity 的生命周期跟随 CombatController，可能比本组件活得久；
            // 不退订的话重开后旧回调会打到已销毁的 Director 上，Play 里那句
            // isActiveAndEnabled 守卫会挡住发声，但订阅链本身会一直堆积。
            if (_audio != null && controller != null && controller.EventsUnity != null)
            {
                controller.EventsUnity.PlaySfx -= _audio.Play;
            }

            if (_audio != null && _eventsT3 != null)
            {
                _eventsT3.PlaySfx -= _audio.Play;
            }

            if (_audio != null)
            {
                // 第 ① 步（全停）在编排层显式写出来，让"三步释放"在这里也读得出来。
                // ClearAll 内部会再全停一次 —— 它是幂等的，重复执行零代价，
                // 而少写这一句就得让读者跳到另一个文件才能确认顺序对不对。
                _audio.StopAllVoices();

                // 第 ②③ 步（摘引用 + 销毁 clip）。含环境衬底那一份引用。
                _audio.ClearAll();
            }
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

            // ★ BOSS 刻意传 null：T2 切片不含 BOSS（属 P1-04）。
            //   传了配置，任何一处误调 SpawnBoss 就会在切片里刷出竹魈王。
            controller.SetZoneConfig(_zone.Enemies, null, _zone.BaseLevel);
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

            // ★ P1-3：与 SetupAudio 里的两次 += 严格对称，并在最后按
            //   Stop → clip = null → Destroy(clip) 的顺序真正释放全部 AudioClip。
            //   这一句是**唯一**在正常路径上释放非托管音频资源的地方（Director 的
            //   OnDestroy 里还有一道幂等兜底，防止本组件之外的销毁路径漏掉）。
            TeardownAudio();
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
