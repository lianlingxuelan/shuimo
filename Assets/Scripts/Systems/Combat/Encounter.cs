// -----------------------------------------------------------------------------
// Encounter.cs —— 一场战斗的世界状态与单步推进（引擎无关）
//
// 【它是内核唯一的"权力中心"】
// AI 只表达意图（DesiredVelocity / PendingSpawns / ShockwavePending），
// DamageResolver 只做纯函数结算，谁都不能直接改 Combatants 列表、也不能自己积分位移。
// 全部收敛到 StepFixed 一个方法里，好处是：
//   1. 「先动还是先打」这类顺序问题只有一份答案，锁步重放才可能逐位一致；
//   2. 将来接入墙体碰撞，只需改 IntegrateMotion 一处；
//   3. 出现「怪凭空消失 / 血量对不上」时，嫌疑范围只有这一个文件。
//
// 【单步顺序（不可随意调换，改了就要重跑对拍）】
//   ①   闸门 tick（玩家 W-CORE）                                [T2 原有]
//   ①-A ★T3 玩家帧（起手 → 动作推进 → CD/资源 → i-frame → 命中 → 缓冲/状态）
//   ②   玩家位移积分                                            [T2 原有]
//   ③   敌人：硬直/韧性 tick →〔③-A 状态 tick〕→ AI.Update → 位移积分 → 击退积分
//   ④   BOSS 技能收编：召唤入列、冲击波结算                       [T2 原有]
//   ⑤   接触伤害：按列表顺序逐个 ResolveContact                   [T2 原有]
//   ⑤-A ★T3 DOT 待办统一结算
//   ⑥   收尸                                                    [T2 原有]
//   ⑦   ★P0-3 胜负判定（只读观察，不写任何战斗状态）              [新增]
// ①→⑤ 的顺序与 T0 wcore_selfcheck.simulate_lockstep（先 tick 再让各来源依次尝试）
// 严格同构，这是 T1-14 端到端锁步能对上 4.300 次/s 的前提。
//
// 【⑦ 为什么排在最后、又为什么不改变任何数值】（见 RunPhase.cs 文件头）
// 必须排在 ⑥ 之后：收尸跑完，Combatants 列表才是本步的最终形态，
// AliveEnemyCount 才是"结算完的"而不是"结算到一半的"。
// 同时它**只读不写**：整个 ⑦ 阶段不碰血量、不碰位置、不碰任何计时器，
// 更不会因为"已经判负"就提前 return 少跑后面的步 —— 死后战斗照常空转，
// 要不要暂停是表现层的决定。于是 64 条 T1 断言的模拟轨迹逐指令不变。
//
// 【T3 三个插入点的顺序理由（架构 §1.6c，评审须逐条过）】
//   1. ①-A 必须在 ① 之后：i-frame 是「每帧续期」的，若早于 WCore.Tick，
//      本帧刚置上的值会被同一帧的衰减减掉。
//   2. ①-A 的命中结算早于 ⑤ 接触伤害：T2 里 AttackController(-50) 本就跑在
//      CombatController(0) 之前，「玩家先手」是既有语义，不能因为搬进内核就反转。
//   3. ③-A 在 AI.Update 之前：迟滞（移速 −30%）要在本帧就压住敌人的输出速度。
//   4. DOT「tick 与结算分离」：③-A 只登记待办，扣血放到 ⑤-A。若在 ③-A 就扣血，
//      敌人可能在 ③ 中途死亡，从而改变它在 ⑤ 是否造成接触伤害 —— 那就动了
//      U1 的承伤链路。分离之后，⑤ 看到的存活集合与 T2 完全一致。
//
// 【空组件即原路径】T3 的一切都挂在可空组件上（Combatant.Action/Skills/Status/Qi/Stamina
// 与 Encounter.Intent）。全部未装配时，①-A 只做 5 次引用判空后 return，
// ③-A / ⑤-A 各只做一次判空 —— 不写任何状态，执行路径与 T2 逐指令一致。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是**整场战斗的总导演**。每 1/60 秒喊一次"开始"，
// 按固定顺序让每个角色依次做完自己那一小步，然后收工。
//
// · 为什么所有事都要挤在一个方法里做？
//   因为战斗里最难查的 bug 是"顺序 bug"：怪先动还是我先打？我这一刀打出去的瞬间，
//   那只怪到底在哪个位置？如果这些决定散落在十几个文件里，答案就有十几个版本，
//   录像回放会对不上、同一个种子跑两遍结果不一样。收敛到一个方法，答案就只有一份。
//
// · 什么是"固定步长"？
//   游戏画面可能 30 帧也可能 144 帧，但战斗逻辑永远严格每秒算 60 次。
//   画面快了就一帧里算两次，慢了就跳过 —— 这样"这一招打多少下"就跟你的显卡无关了。
//
// · 那些带 -A 的阶段是什么？
//   T3 新增的技能/状态/闪避逻辑。它们像插件一样插在原有阶段之间，
//   而不是把原有代码改掉 —— 这样万一新功能有问题，把插件拔掉就能回到 T2 的样子。
//   "拔插件"具体怎么做？不给角色装 Action/Skills/Status 这些组件就行，
//   代码会自动全部跳过（这叫"空组件即原路径"）。
//
// · 为什么"中毒掉血"不在中毒那一刻就扣？
//   因为敌人一旦提前死掉，它就不会在后面的"接触伤害"阶段碰到你了 ——
//   等于中毒间接改变了你挨打的次数。而"你会挨多少打"是整个游戏平衡的基准线，
//   已经被 64 组自动测试锁死。所以毒伤只在阶段 ③-A 记个账，
//   等阶段 ⑤ 结算完接触伤害之后，才在 ⑤-A 统一扣。
// =============================================================================
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Xianxia.Core;

namespace Xianxia.Combat
{
    /// <summary>
    /// 一场战斗（一个区域实例）的完整状态。持有全部参战实体，并负责把
    /// 固定步长推进拆解成确定顺序的若干阶段。
    /// </summary>
    public sealed class Encounter
    {
        // ---------------------------------------------------------------------
        // 世界状态
        // ---------------------------------------------------------------------

        /// <summary>
        /// 全部参战实体（含玩家）。**顺序即结算顺序**，因此不要用 Sort / 交换元素的
        /// 方式做"优化"，否则同种子重放会产生不同的承伤序列。
        /// </summary>
        public readonly List<Combatant> Combatants = new List<Combatant>(16);

        /// <summary>玩家。为 null 时 <see cref="StepFixed"/> 只推进敌人位移，不结算伤害。</summary>
        public Combatant Player;

        /// <summary>
        /// 本场战斗的随机流。由区域种子（<c>ZoneSeed.Derive</c>）播种，
        /// 所有 AI 侧向偏移、精英/词缀判定共用它——共用才能保证"同种子同世界"。
        /// </summary>
        public PCG32 Rng = new PCG32();

        /// <summary>事件出口。默认空实现，Unity 壳换成自己的实现即可。</summary>
        public ICombatEvents Events = NullCombatEvents.Instance;

        /// <summary>难度桥。召唤小怪时用它按 R5 规则造实体。</summary>
        public DifficultyBridge Bridge = new DifficultyBridge();

        /// <summary>接触判定半径。默认 <c>CombatConfig.TOUCH_RANGE</c>(44)。</summary>
        public float TouchRange = CombatConfig.TOUCH_RANGE;

        /// <summary>已模拟的逻辑时长（秒）。只由 <see cref="StepFixed"/> 累加。</summary>
        public float ElapsedTime;

        /// <summary>已推进的固定步数。</summary>
        public int StepCount;

        /// <summary>
        /// 场上敌人数量上限。召唤触顶时静默丢弃，避免 BOSS 战被无限增殖的小怪拖垮。
        /// </summary>
        public int MaxEnemies = 32;

        // ---------------------------------------------------------------------
        // ★P0-3 胜负 / 对局阶段
        // ---------------------------------------------------------------------

        /// <summary>
        /// 胜负判定器。由 <see cref="StepFixed"/> 的阶段 ⑦ 每步驱动一次。
        ///
        /// 【Unity 层怎么用】订阅它的事件即可，内核不需要知道你要干什么：
        /// <code>
        /// encounter.RunState.PhaseChanged += p =>
        /// {
        ///     if (p == RunPhase.Lost) { 弹死亡界面(); scheduler.Paused = true; }
        ///     else if (p == RunPhase.Won) { 弹结算界面(); }
        /// };
        /// </code>
        /// 注意"暂停"这件事是**上层**做的（<c>scheduler.Paused</c>），内核不自作主张 ——
        /// 理由见 RunPhase.cs 文件头「只观察，不干预」。
        ///
        /// 【readonly 的含义】引用不可替换（防止有人半路换掉裁判导致订阅者全掉线），
        /// 但对象内部状态照常变化。重开一局请调 <c>RunState.Reset()</c> 或 <see cref="Clear"/>。
        /// </summary>
        public readonly RunPhaseTracker RunState = new RunPhaseTracker();

        /// <summary>当前对局阶段。<see cref="RunState"/>.Phase 的只读快捷方式（方便轮询）。</summary>
        public RunPhase Phase
        {
            get { return RunState.Phase; }
        }

        /// <summary>本局是否已分出胜负。<see cref="RunState"/>.IsSettled 的只读快捷方式。</summary>
        public bool IsRunOver
        {
            get { return RunState.IsSettled; }
        }

        // ---------------------------------------------------------------------
        // T3 装配位（全部可空 / 有安全默认值 —— 不装配时 StepFixed 走 T2 原路径）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 玩家输入缓冲。null = 没有 T3 输入层，①-A 的起手段整段跳过。
        /// 由 Unity 侧 <c>CombatBridge</c> 在装配阶段注入。
        /// </summary>
        public PlayerIntent Intent;

        /// <summary>
        /// 技能随机流。**与 <see cref="Rng"/> 物理隔离**：不同的 increment 才是
        /// 数学上独立的两条序列。
        ///
        /// ⚠️ 绝对禁止用 <c>Rng.Fork()</c> 派生它 —— <c>PCG32.Fork</c> 内部会调一次
        /// <c>NextUInt()</c>，凭空消耗父流一次抽样，所有敌人的侧向偏移角当场推偏，
        /// 同种子逐字节 diff 直接作废。正确姿势是 <see cref="SeedSkillRng"/>。
        ///
        /// 默认值用固定种子而非 null：只有真正的概率附着才会碰它，
        /// 非 null 让 headless 单测无需装配即可跑通概率分支。
        /// </summary>
        public PCG32 SkillRng = new PCG32(SkillConfig.SKILL_STREAM_MIX, SkillConfig.SKILL_STREAM_INC);

        /// <summary>T3 事件出口。默认空实现（见 <see cref="ICombatEventsT3"/> 的偏离说明 D-1）。</summary>
        public ICombatEventsT3 EventsT3 = NullCombatEventsT3.Instance;

        /// <summary>
        /// 软索敌总开关（P0-07 验收③：可整体关闭）。开关的**真源在这里**，
        /// <c>SkillResolver.SoftAim</c> 保持纯函数、只接受参数，
        /// 这样单测切换开关不会污染其它用例。
        /// </summary>
        public bool SoftAimEnabled = SkillConfig.SOFT_AIM_DEFAULT_ENABLED;

        /// <summary>软索敌的最大吸附距离。&lt;= 0 表示不限距离（只受 ±15° 角度约束）。</summary>
        public float SoftAimMaxRange;

        /// <summary>
        /// 当前连击数（P0-08 HUD 显示用）。计数规则见 <see cref="SkillConfig.COMBO_RESET_FRAMES"/>：
        /// 己方每命中一次 +1，被击中或连续 4s 无命中归零。
        /// </summary>
        public int Combo;

        /// <summary>
        /// 连击增伤倍率。**P0 恒为 1.0f，对伤害零影响**；P1-06 才换成
        /// <c>1 + min(combo,50) × 0.004</c>。现在就把它做成属性，是为了让
        /// 结算处的调用点（<c>ResolveSkillHit(..., ComboMult, ...)</c>）在 P1 无需改签名。
        /// </summary>
        public float ComboMult
        {
            get { return 1.0f; }
        }

        // 本步产生的待入列实体。延迟到步末统一入列，避免"边遍历边改集合"。
        private readonly List<Combatant> _pendingAdd = new List<Combatant>(8);

        // 收尸缓冲，复用以避免每步分配。
        private readonly List<Combatant> _deadBuffer = new List<Combatant>(8);

        // 本步登记的 DOT 待办（③-A 与 ①-A 写入，⑤-A 统一结算后清空）。
        private readonly List<DotTick> _dotBuffer = new List<DotTick>(16);

        // 命中输出缓冲，复用以避免每次挥砍都分配一个 List。
        private readonly List<Combatant> _hitBuffer = new List<Combatant>(16);

        // 存活敌人快照缓冲。与公开的 AliveEnemies() 分开：后者返回新列表给外部随便用，
        // 这个只在单个阶段内部短暂持有，绝不跨阶段存活。
        private readonly List<Combatant> _aliveBuffer = new List<Combatant>(16);

        // i-frame 是否由本内核持有。用于在闪避结束/被打断的那一帧精确清零，
        // 避免误清由其它来源（P2 的护体法术等）写入的 Iframe。
        private bool _iframeHeld;

        // 上一步末的玩家血量，用于「本步是否挨打了」的判定（断连 + 资源池脱战计时复位）。
        // -1 = 尚未采样（首步不判定，否则会把"初始化"误判成一次受击）。
        private float _lastPlayerHp = -1.0f;

        // 连击空窗计数（帧）。
        private int _comboIdleFrames;

        // 槽位起手优先级：闪避 > 技能1 > 技能2 > 普攻。
        //
        // 【为什么闪避排第一】保命动作必须能抢在攻击前面。玩家在混战里常常
        // 「攻击键还没松就发现要挨打」，两个意图同时躺在缓冲里 —— 此时先出攻击
        // 等于把玩家按闪避的那一下延后了 6 帧，观感上就是"翻滚没反应"。
        //
        // 【为什么写成 static readonly 数组而不是每次现构】
        // 现构数组等于每帧分配一次垃圾；而 foreach 一个 static 数组是零分配的。
        private static readonly IntentSlot[] CastPriority =
        {
            IntentSlot.Dodge,
            IntentSlot.Skill1,
            IntentSlot.Skill2,
            IntentSlot.Basic
        };

        // ---------------------------------------------------------------------
        // 编队
        // ---------------------------------------------------------------------

        /// <summary>
        /// 设置玩家。会顺带清空其 W-CORE 冷却表：换场景后 id 可能被新怪复用，
        /// 不清表会出现"新区的怪打不动我"（见 WCore.Reset 的注释）。
        /// </summary>
        public void SetPlayer(Combatant player)
        {
            Player = player;

            // T3：血量采样归零。不重置的话，换人后第一步会把
            // 「新玩家血量 < 旧玩家血量」误判成一次受击，白白断掉连击。
            _lastPlayerHp = -1.0f;
            _iframeHeld = false;

            if (player == null)
            {
                return;
            }
            if (player.WCore != null)
            {
                player.WCore.Reset();
            }
            if (!Combatants.Contains(player))
            {
                Combatants.Add(player);
            }
        }

        /// <summary>
        /// 播种技能随机流（P0-02 概率附着 / P2-05 暴击的唯一随机源）。
        ///
        /// 【为什么必须是这个写法】
        /// <c>seed ^ SKILL_STREAM_MIX</c> 保证「同区域同种子 → 同技能流」，
        /// 而独立的 <c>SKILL_STREAM_INC</c>（奇数且 ≠ PCG32.DefaultIncrement）
        /// 保证它与 <see cref="Rng"/> 是数学上独立的两条序列 ——
        /// 于是「玩家多打中一只怪」不会推偏任何一只敌人的 AI 抽样。
        /// </summary>
        /// <param name="zoneSeed">区域种子，来自 <c>ZoneSeed.Derive(zoneId, isSafe, visits)</c>。</param>
        public void SeedSkillRng(long zoneSeed)
        {
            SkillRng = new PCG32((ulong)zoneSeed ^ SkillConfig.SKILL_STREAM_MIX, SkillConfig.SKILL_STREAM_INC);
        }

        /// <summary>
        /// 加入一个实体。敌人会被自动注入随机流与出生锚点——漏注入会让
        /// CHASE 的侧向偏移恒为 0，怪群重新叠成一条线。
        /// </summary>
        public void Add(Combatant c)
        {
            if (c == null || Combatants.Contains(c))
            {
                return;
            }
            if (c.AI != null)
            {
                if (c.AI.Rng == null)
                {
                    c.AI.Rng = Rng;
                }
                if (c.AI.SpawnPos.IsZero())
                {
                    c.AI.SpawnPos = c.Position;
                }
                BossController boss = c.AI as BossController;
                if (boss != null && ReferenceEquals(boss.Events, NullCombatEvents.Instance))
                {
                    boss.Events = Events;
                }
            }
            Combatants.Add(c);
        }

        /// <summary>移出一个实体。</summary>
        public bool Remove(Combatant c)
        {
            return c != null && Combatants.Remove(c);
        }

        /// <summary>清空整场战斗。</summary>
        public void Clear()
        {
            Combatants.Clear();
            _pendingAdd.Clear();
            _deadBuffer.Clear();
            Player = null;
            ElapsedTime = 0.0f;
            StepCount = 0;

            // T3 清理。**注意不重置 SkillRng**：随机流的生命周期跟随"区域"，
            // 由 SeedSkillRng 显式管理；在这里顺手重播种会让"清场重来"
            // 与"换区"两件事产生不同的随机序列，同种子重放当场失效。
            _dotBuffer.Clear();
            _hitBuffer.Clear();
            _aliveBuffer.Clear();
            if (Intent != null)
            {
                Intent.Clear();
            }
            _iframeHeld = false;
            _lastPlayerHp = -1.0f;
            Combo = 0;
            _comboIdleFrames = 0;

            // ★P0-3：清场 = 重开一局，裁判必须回到"比赛进行中"并撤销布防。
            // 不重置的话，上一局判过负之后，这个 Encounter 被复用来打第二局时
            // 幂等闸门会一直卡在 Lost —— 第二局无论怎么打都不会再有任何胜负回调。
            RunState.Reset();
        }

        /// <summary>存活敌人数量。</summary>
        public int AliveEnemyCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Combatants.Count; i++)
                {
                    Combatant c = Combatants[i];
                    if (c.IsEnemy && c.IsAlive)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>
        /// 取存活敌人快照。**返回新列表**而不是 yield：调用方常常会在遍历中
        /// 击杀目标，惰性枚举会当场抛 InvalidOperationException。
        /// </summary>
        public List<Combatant> AliveEnemies()
        {
            List<Combatant> list = new List<Combatant>(Combatants.Count);
            for (int i = 0; i < Combatants.Count; i++)
            {
                Combatant c = Combatants[i];
                if (c.IsEnemy && c.IsAlive)
                {
                    list.Add(c);
                }
            }
            return list;
        }

        // ---------------------------------------------------------------------
        // 单步推进
        // ---------------------------------------------------------------------

        /// <summary>
        /// 推进一个固定步。<paramref name="dt"/> 恒为 1/60，由
        /// <see cref="CombatScheduler"/> 保证；本方法不接受可变步长
        /// （可变步长会让两层闸门的量化边界漂移，围攻频率从 4.300 变成 4.65）。
        /// </summary>
        public void StepFixed(float dt)
        {
            if (dt <= 0.0f)
            {
                return;
            }

            StepCount++;
            ElapsedTime += dt;

            // ① 玩家闸门 tick（必须先于任何结算，与 T0 simulate_lockstep 同构）
            if (Player != null && Player.WCore != null)
            {
                Player.WCore.Tick(dt);
            }

            // ①-A ★T3 玩家帧。未装配任何 T3 组件时内部立即 return（空组件即原路径）。
            TickPlayerFrame(dt);

            // ② 玩家位移。Velocity 由上层（输入/Unity 壳/测试桩）写入，内核只积分。
            if (Player != null && Player.IsAlive)
            {
                Player.TickHitStun(dt);
                Player.Position = Player.Position + Player.Velocity * dt;
                Player.IntegrateKnockback(dt);
            }

            Vec2 targetPos = Player != null ? Player.Position : Vec2.Zero;
            Vec2 targetVel = Player != null ? Player.Velocity : Vec2.Zero;

            // ③ 敌人：计时器 → AI → 位移
            for (int i = 0; i < Combatants.Count; i++)
            {
                Combatant e = Combatants[i];
                if (!e.IsEnemy || !e.IsAlive)
                {
                    continue;
                }

                e.TickHitStun(dt);
                e.TickPoise(dt);

                // ③-A ★T3 状态 tick。**必须早于 AI.Update**：迟滞（移速 −30%）
                //     要在本帧就压住 EffectiveSpeed，否则减速永远慢一帧生效。
                //     本调用只递减时长/层数并登记 DOT 待办，**不扣血**（理由见文件头 4）。
                if (e.Status != null)
                {
                    e.Status.TickFrame(e, _dotBuffer, EventsT3);
                }

                if (e.AI != null)
                {
                    e.AI.Update(dt, targetPos, targetVel);
                }

                IntegrateMotion(e, dt);
            }

            // ④ BOSS 技能收编。放在位移之后：冲击波中心取的是 BOSS **本步移动后**的
            //    位置，与玩家看到的特效位置一致（否则会出现"波从上一帧的残影发出"）。
            for (int i = 0; i < Combatants.Count; i++)
            {
                BossController boss = Combatants[i].AI as BossController;
                if (boss == null)
                {
                    continue;
                }
                CollectSummons(boss);
                ResolveBossShockwave(Combatants[i], boss);
            }

            // ⑤ 接触伤害。按列表顺序逐个尝试——顺序固定是锁步可复现的关键。
            if (Player != null && Player.IsAlive)
            {
                for (int i = 0; i < Combatants.Count; i++)
                {
                    Combatant e = Combatants[i];
                    if (!e.IsEnemy || !e.IsAlive)
                    {
                        continue;
                    }
                    DamageResolver.ResolveContact(e, Player, TouchRange, Events);
                }
            }

            // ⑤-A ★T3 DOT 待办统一结算。放在 ⑤ 之后，是为了让 ⑤ 看到的
            //      "存活敌人集合"与 T2 完全一致（详见文件头顺序理由 4）。
            FlushDotTicks();

            // ⑥ 入列新召唤 + 收尸
            FlushPendingAdds();
            RemoveDead();

            // ⑦ ★P0-3 胜负判定。**只读观察**：读 Player.IsAlive 与 AliveEnemyCount，
            //    写一个枚举字段，必要时抛一次事件。不碰任何战斗数值，也不提前 return，
            //    因此对 U1 平衡口径（HP 260 / d_eff 4.0 / raw 12 / CD 0.4s）零影响。
            //    放在 ⑥ 之后：收尸跑完，存活敌人数才是本步的最终答案。
            RunState.Evaluate(this);
        }

        // ---------------------------------------------------------------------
        // ①-A ★T3 玩家帧
        // ---------------------------------------------------------------------

        /// <summary>
        /// 玩家是否装配了任何 T3 组件。全都没有 = 这是一场纯 T2 战斗，①-A 整段跳过。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"这个角色装了新系统的零件吗？" 一个都没装就直接跳过所有新逻辑。
        /// 这就是"空组件即原路径"的守门员：它保证 T3 的代码在旧场景里
        /// 除了做几次"这个字段是不是空的"判断之外，**什么都不做**。
        /// </remarks>
        /// <param name="p">玩家。</param>
        /// <returns>true = 需要执行 ①-A。</returns>
        private static bool PlayerHasT3(Combatant p)
        {
            return p.Action != null
                || p.Skills != null
                || p.Status != null
                || p.Qi != null
                || p.Stamina != null;
        }

        /// <summary>
        /// ①-A 玩家帧。内部顺序（每一步的理由见各段注释）：
        /// <list type="number">
        /// <item>受击回执（断连 + 资源池脱战计时复位）</item>
        /// <item>破韧硬直同步（P1 接线口，P0 恒不成立）</item>
        /// <item>起手：从输入缓冲里挑一个能执行的动作</item>
        /// <item>推进动作一帧</item>
        /// <item>技能 CD 递减</item>
        /// <item>双资源池回复</item>
        /// <item>i-frame 逐帧续期</item>
        /// <item>若本帧刚进入判定段 → 命中结算（玩家 → 敌人）</item>
        /// <item>输入缓冲递减</item>
        /// <item>玩家自身状态 tick + 连击空窗计数</item>
        /// </list>
        ///
        /// 【为什么"起手"排在"推进动作"之前】
        /// 这是为了让普攻保持 T2 的**零延迟**手感。T2 里 <c>AttackController(-50)</c>
        /// 在按下的那一个 Unity 帧就结算了伤害；搬进内核后，同一帧的调用链是
        /// 「按下 → Intent.Request → StepFixed → ①-A」。水剑斩的前摇是 0 帧，
        /// 于是「先起手（Cursor=0）→ 再推进（Cursor=1，进入判定段）」恰好在同一步里
        /// 打出伤害，与 T2 逐帧对齐。若顺序反过来，每一刀都会晚 1 帧（16.7ms），
        /// 老玩家一定能感觉到"变粘了"。
        /// </summary>
        /// <param name="dt">固定步长，恒为 1/60。</param>
        private void TickPlayerFrame(float dt)
        {
            Combatant p = Player;
            if (p == null || !PlayerHasT3(p))
            {
                return;
            }

            if (!p.IsAlive)
            {
                // 玩家已死：不推进任何动作，并释放内核持有的 i-frame，
                // 否则复活后会带着一段"幽灵无敌"。
                ReleaseIframe(p);
                return;
            }

            ActionState action = p.Action;

            // ---- 1. 受击回执 -------------------------------------------------
            // 掉血发生在上一步的 ⑤（接触伤害），这里滞后一帧检出。
            // 一帧的滞后是确定性的（不随帧率变化），不影响任何验收口径。
            bool tookDamage = _lastPlayerHp >= 0.0f && p.Hp < _lastPlayerHp;
            _lastPlayerHp = p.Hp;
            if (tookDamage)
            {
                if (p.Qi != null)
                {
                    p.Qi.NotifyActivity();
                }
                if (p.Stamina != null)
                {
                    p.Stamina.NotifyActivity();
                }
                ResetCombo();
            }

            // ---- 2. 破韧硬直同步（P0-01 验收③的运行时接线口）----------------
            // P0 的敌人只有接触伤害，走 ApplyToPlayer → WCore.TakeDamageFrom，
            // **完全不碰玩家韧性**，所以 HitStunTimer 恒为 0、这个分支全程不成立。
            // 现在就放进来，是因为"谁负责把 Combatant 的硬直同步进动作机"必须只有
            // 一个答案；等 P1 敌方技能上线再补，极易演变成 AI 侧和内核侧各写一份。
            if (action != null && action.Kind != ActionKind.HitStun && p.HitStunTimer > 0.0f)
            {
                action.ForceHitStun(SecondsToFrames(p.HitStunTimer));
            }

            // ---- 3. 起手 -----------------------------------------------------
            if (action != null)
            {
                TryBeginBufferedAction(p, action);
            }

            // ---- 4. 推进动作一帧 ---------------------------------------------
            ActionTickResult tick = ActionTickResult.None;
            if (action != null)
            {
                tick = action.TickFrame();
            }

            // ---- 5. 技能 CD 递减 ---------------------------------------------
            if (p.Skills != null)
            {
                p.Skills.TickFrame();
            }

            // ---- 6. 双资源池回复 ---------------------------------------------
            // 传 dt 而不是字面量：dt 由 CombatScheduler 保证恒为 1/60，
            // 而 0.01667f 这种近似值累积 13 次就能把围攻频率从 4.300 推到 4.65。
            if (p.Qi != null)
            {
                p.Qi.TickFrame(dt);
            }
            if (p.Stamina != null)
            {
                p.Stamina.TickFrame(dt);
            }

            // ---- 7. i-frame 逐帧续期 ------------------------------------------
            RefreshIframe(p, action);

            // ---- 8. 命中结算 --------------------------------------------------
            if (tick == ActionTickResult.EnteredActive && action != null)
            {
                ResolveActiveFrame(p, action);
            }

            // ---- 9. 输入缓冲递减 ----------------------------------------------
            // 放在起手之后：这样"缓冲帧数 = N"意味着"接下来 N 帧内都还有机会起手"，
            // 若先递减，最后一帧的意图会在还没被尝试过的情况下就作废。
            if (Intent != null)
            {
                Intent.TickFrame();
            }

            // ---- 10. 玩家自身状态 tick + 连击空窗 ------------------------------
            // 传 _dotBuffer 而不是 null：数据流与敌人侧保持一致，
            // 玩家侧 DOT 的"能不能扣血"这个决定收敛到 ⑤-A 一处（见 FlushDotTicks）。
            if (p.Status != null)
            {
                p.Status.TickFrame(p, _dotBuffer, EventsT3);
            }
            TickComboWindow();
        }

        /// <summary>
        /// 从输入缓冲里按优先级挑出**第一个真正能执行**的动作并起手。
        ///
        /// 【核心约定：起手失败绝不消费意图】
        /// 流程是「只读校验 → 只读预览朝向 → 试着起手 → **成功之后**才撕便签」。
        /// 任何一步失败都保持缓冲原样，于是这次按键还能在剩余的缓冲帧里继续尝试 ——
        /// 这正是输入缓冲存在的全部意义（PRD P0-01 验收②：落在 cancelWindow 内的输入被接受）。
        ///
        /// 【为什么扣资源在 TryBegin 之后】
        /// 技能没放出来却扣了蓝、进了 CD，是 ARPG 最招骂的 bug 之一。
        /// <c>ResourcePool.TrySpend</c> 与 <c>SkillRuntime.BeginCast</c> 的文档都明确
        /// 要求"确认起手成功后再调"，这里是唯一的调用点。
        /// </summary>
        /// <param name="p">玩家。</param>
        /// <param name="action">动作帧机（非 null）。</param>
        /// <returns>true = 本帧起手了一个新动作。</returns>
        private bool TryBeginBufferedAction(Combatant p, ActionState action)
        {
            PlayerIntent intent = Intent;
            SkillRuntime skills = p.Skills;
            if (intent == null || skills == null)
            {
                return false;
            }

            for (int i = 0; i < CastPriority.Length; i++)
            {
                IntentSlot slot = CastPriority[i];
                if (!intent.HasPending(slot))
                {
                    continue;
                }

                SkillDef def;
                if (skills.CanCast((int)slot, action, p.Qi, p.Stamina, out def) != CastReject.Ok)
                {
                    // CD 没好 / 蓝不够 / 动作锁着 —— 都不消费，留给后面几帧继续等。
                    continue;
                }

                Vec2 rawFacing = intent.PendingFacing(slot);

                // 软索敌（P0-07）：只在**起手这一刻**吸附一次，结果写进 LockedFacing。
                //
                // 【偏离说明 D-2 · 相对架构 §1.6c】
                // 架构骨架把 SoftAim 列在 "EnteredActive 时"。我改到起手，理由是
                // ActionState.LockedFacing 的契约是「起手瞬间锁定、命中判定一律用它」——
                // 若在判定帧重新吸附，血莲 14 帧前摇期间敌人走位就能把 AOE 拽偏，
                // 与"锁定朝向"的语义直接冲突。对唯一 0 前摇的水剑斩而言两者完全等价
                // （起手与判定发生在同一步），差异只体现在有前摇的技能上。
                Vec2 facing;
                Combatant snapped = null;
                if (def.DealsDamage)
                {
                    facing = SkillResolver.SoftAim(
                        p.Position,
                        rawFacing,
                        CollectAliveEnemies(),
                        SkillConfig.SOFT_AIM_MAX_DEG,
                        SoftAimMaxRange,
                        SoftAimEnabled,
                        out snapped);
                }
                else
                {
                    // 闪避不产出伤害，也就不该被敌人"吸"走方向 ——
                    // 玩家按哪个方向翻就往哪翻，这是保命动作的底线。
                    facing = rawFacing.IsZero() ? action.LockedFacing : rawFacing.Normalized();
                }

                if (!action.TryBegin(def.Action, (int)slot, def.Frames, facing))
                {
                    // CanCast 已经查过 CanCancelInto，正常走不到这里。
                    // 真走到了说明有别的代码在两次调用之间改了 action —— 保持缓冲不动，下帧重试。
                    continue;
                }

                // 确认起手成功，此刻才付代价。
                Vec2 consumed;
                intent.Consume(slot, out consumed);
                SpendCost(p, def);
                skills.BeginCast((int)slot, def);

                ICombatEventsT3 evt = EventsT3 ?? NullCombatEventsT3.Instance;
                if (snapped != null)
                {
                    evt.OnFacingSnapped(p, facing);
                }
                if (def.Action == ActionKind.Dodge)
                {
                    evt.OnDodgeStart(p, facing);
                }
                else
                {
                    evt.OnSkillCast(p, def);
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// 扣除技能开销并广播资源变化。<see cref="SkillRuntime.CanCast"/> 已经验过余额，
        /// 这里的 <c>TrySpend</c> 必定成功；保留返回值检查只是防御性写法。
        /// </summary>
        /// <param name="p">玩家。</param>
        /// <param name="def">技能定义。</param>
        private void SpendCost(Combatant p, SkillDef def)
        {
            ICombatEventsT3 evt = EventsT3 ?? NullCombatEventsT3.Instance;

            if (p.Qi != null && def.QiCost > 0.0f && p.Qi.TrySpend(def.QiCost))
            {
                evt.OnResourceChanged(p, true, p.Qi.Current, p.Qi.Max);
            }
            if (p.Stamina != null && def.StaminaCost > 0.0f && p.Stamina.TrySpend(def.StaminaCost))
            {
                evt.OnResourceChanged(p, false, p.Stamina.Current, p.Stamina.Max);
            }

            // 出手即视为"在战斗中"，两个池的脱战加速一起清零。
            // 只清消耗掉的那个池是不对的：放技能的时候体力当然也不该享受脱战回复。
            if (p.Qi != null)
            {
                p.Qi.NotifyActivity();
            }
            if (p.Stamina != null)
            {
                p.Stamina.NotifyActivity();
            }
        }

        /// <summary>
        /// i-frame 逐帧续期（P0-05 验收①④）。
        ///
        /// 【为什么不是"闪避开始时一次性写 0.20f"】
        /// <c>WCore.Tick(dt)</c> 每帧按 float 累减 <c>Iframe</c>，12 次累减存在舍入，
        /// 无敌窗的结束点会在 ±1 帧之间漂移。改成"每帧重置为一个步长"之后，
        /// <c>Tick</c> 下一步恰好把它减到 0，本步的 ①-A 再重新置上 ——
        /// 起止**精确到帧**，零漂移。
        ///
        /// 【为什么这条路径不消耗 W-CORE 冷却表】
        /// <c>WCoreState.TakeDamageFrom</c> 里 <c>Iframe &gt; 0</c> 的分支在**落闸之前**
        /// 就 return false，冷却表一个字节都不会写。于是 P0-05 验收①的后半句
        /// "不能出现翻滚吃掉下一次真实伤害的冷却"是现成的，<c>WCore.cs</c> 一字不用改。
        ///
        /// 【DodgeEnabled 全程保持 false】
        /// W-CORE 自带的"闪避骰"（<c>DodgeEnabled</c> + <c>DodgeRoll</c>）是**概率闪避**，
        /// 与 T3 的"确定性无敌帧"是两套东西。两者物理隔离，本方法只写 <c>Iframe</c>。
        /// </summary>
        /// <param name="p">玩家。</param>
        /// <param name="action">动作帧机，可为 null。</param>
        private void RefreshIframe(Combatant p, ActionState action)
        {
            WCoreState core = p.WCore;
            if (core == null)
            {
                return;
            }

            if (action != null && action.IsIframeActive)
            {
                core.Iframe = CombatScheduler.FixedStep;
                _iframeHeld = true;
                return;
            }

            // 刚离开无敌窗（含被打断）的那一帧显式清零。
            // 用 _iframeHeld 做门控而不是无条件写 0，是为了不误清别人写入的 Iframe。
            if (_iframeHeld)
            {
                core.Iframe = 0.0f;
                _iframeHeld = false;
            }
        }

        /// <summary>强制释放内核持有的 i-frame（玩家死亡 / 清场）。</summary>
        /// <param name="p">玩家。</param>
        private void ReleaseIframe(Combatant p)
        {
            if (_iframeHeld && p != null && p.WCore != null)
            {
                p.WCore.Iframe = 0.0f;
            }
            _iframeHeld = false;
        }

        /// <summary>
        /// 判定帧命中结算（玩家 → 敌人，单向）。
        ///
        /// 【为什么"一刀只打一次"不需要额外的已结算标记】
        /// <c>ActionState.TickFrame</c> 只在**相位从非 Active 跃迁到 Active** 的那一帧
        /// 返回 <c>EnteredActive</c>，一次动作里这件事至多发生一次。
        /// 判定段有 2 帧的技能（法阵冲击）也只结算一次，不会打两遍。
        ///
        /// 【为什么用 LockedFacing 而不是当前朝向】
        /// 前摇期间玩家可以自由转身（我们没有锁住移动），若结算时才读朝向，
        /// 血莲的 120° 扇形会被甩到背后去 —— 玩家看到的起手方向与实际打中的方向不一致。
        /// </summary>
        /// <param name="p">玩家。</param>
        /// <param name="action">动作帧机。</param>
        private void ResolveActiveFrame(Combatant p, ActionState action)
        {
            SkillRuntime skills = p.Skills;
            if (skills == null)
            {
                return;
            }

            SkillDef def = skills.GetDef(action.SkillSlot);
            if (def == null || !def.DealsDamage)
            {
                // 闪避走的就是这条路：有判定段（位移主体），但不产出任何伤害。
                return;
            }

            _hitBuffer.Clear();
            int hits = SkillResolver.QueryHits(
                def, p.Position, action.LockedFacing, CollectAliveEnemies(), _hitBuffer);
            if (hits == 0)
            {
                return;
            }

            // 遍历顺序即结算顺序 —— 不排序，否则同种子重放会产生不同的死亡序列。
            for (int i = 0; i < _hitBuffer.Count; i++)
            {
                DamageResolver.ResolveSkillHit(p, _hitBuffer[i], def, ComboMult, SkillRng, EventsT3, Events);
                AddCombo();
            }
            _hitBuffer.Clear();
        }

        /// <summary>
        /// 填充并返回**内部复用**的存活敌人缓冲。
        ///
        /// ⚠️ 返回值只在同一个阶段内短暂使用，**绝不能跨阶段持有** ——
        /// 下一次调用就会把它清空重填。需要长期持有请用公开的 <see cref="AliveEnemies"/>。
        /// 分成两个方法而不是让 AliveEnemies 也复用缓冲，是因为后者是公开 API，
        /// 外部调用方完全可能把它存起来慢慢用。
        /// </summary>
        /// <returns>存活敌人列表（复用缓冲）。</returns>
        private List<Combatant> CollectAliveEnemies()
        {
            _aliveBuffer.Clear();
            for (int i = 0; i < Combatants.Count; i++)
            {
                Combatant c = Combatants[i];
                if (c.IsEnemy && c.IsAlive)
                {
                    _aliveBuffer.Add(c);
                }
            }
            return _aliveBuffer;
        }

        // ---------------------------------------------------------------------
        // ⑤-A ★T3 DOT 结算
        // ---------------------------------------------------------------------

        /// <summary>
        /// 统一结算本步登记的全部 DOT 跳伤。
        ///
        /// 【⚠️ 红线：玩家侧 DOT 在 P0 一律不结算】
        /// <c>DamageResolver.ApplyDotTick</c> 内部走的是 <c>ApplyToEnemyWithPoise</c>
        /// → <c>ApplyEnemyDamage</c>，那是**减法承伤模型**，完全绕开 W-CORE 的两层闸门。
        /// 把它用在玩家身上，等于在"敌人 → 玩家"方向凭空开了一条不受 4.300 次/s
        /// 约束的伤害通道 —— U1 的平衡口径当场作废。
        /// P0 的敌人没有任何施加状态的手段，所以这个分支实际不可达；
        /// 显式写出来是为了把这条红线变成**代码里一眼可见、单测可断言**的东西，
        /// 而不是一句只存在于文档里的约定。
        /// P1 敌方 DOT 上线时，这里改为走 <c>ApplyToPlayer</c> + 每状态独立 srcId。
        /// </summary>
        private void FlushDotTicks()
        {
            if (_dotBuffer.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _dotBuffer.Count; i++)
            {
                DotTick tick = _dotBuffer[i];
                Combatant target = tick.Target;
                if (target == null || !target.IsEnemy)
                {
                    continue;
                }
                DamageResolver.ApplyDotTick(tick, Events, EventsT3);
            }

            _dotBuffer.Clear();
        }

        // ---------------------------------------------------------------------
        // 连击计数（P0-08 只显示，P1-06 才增伤）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 命中 +1 连击。
        ///
        /// 【为什么 DOT 跳伤不计入连击】
        /// 蛊毒 5 层每 0.6s 跳一次、灼烧每 0.5s 跳一次，全算进去的话
        /// 连击数会在挂满 debuff 时自动疯涨，"连击"从操作水平的度量退化成 debuff 计数器。
        /// 只有玩家主动打出的直接命中才 +1。
        /// </summary>
        private void AddCombo()
        {
            Combo++;
            _comboIdleFrames = 0;
            ICombatEventsT3 evt = EventsT3 ?? NullCombatEventsT3.Instance;
            evt.OnComboChanged(Combo, ComboMult);
        }

        /// <summary>断连归零。已经是 0 时不重复抛事件（避免 HUD 每帧无谓刷新）。</summary>
        private void ResetCombo()
        {
            if (Combo == 0)
            {
                _comboIdleFrames = 0;
                return;
            }
            Combo = 0;
            _comboIdleFrames = 0;
            ICombatEventsT3 evt = EventsT3 ?? NullCombatEventsT3.Instance;
            evt.OnComboChanged(0, 1.0f);
        }

        /// <summary>连击空窗计时：连续 <see cref="SkillConfig.COMBO_RESET_FRAMES"/> 帧无命中即断连。</summary>
        private void TickComboWindow()
        {
            if (Combo <= 0)
            {
                return;
            }
            _comboIdleFrames++;
            if (_comboIdleFrames >= SkillConfig.COMBO_RESET_FRAMES)
            {
                ResetCombo();
            }
        }

        /// <summary>
        /// 秒 → 逻辑帧（向上取整）。
        ///
        /// 【为什么要向上取整】<c>0.05f / (1/60f)</c> 在 IEEE754 下是 2.9999997，
        /// 直接截断会得到 2 帧 —— 硬直凭空短一帧。宁可多一帧也不能少一帧：
        /// 少一帧意味着"理论上能被打断的时机"在实机里打不断，玩家会觉得判定飘。
        /// </summary>
        /// <param name="seconds">秒数。</param>
        /// <returns>帧数，&lt;= 0 时返回 0。</returns>
        private static int SecondsToFrames(float seconds)
        {
            if (seconds <= 0.0f)
            {
                return 0;
            }
            int frames = (int)(seconds / CombatScheduler.FixedStep);
            if (frames * CombatScheduler.FixedStep < seconds)
            {
                frames++;
            }
            return frames;
        }

        /// <summary>
        /// 位移积分。硬直期间 AI 速度已被清零，但击退位移照常推进
        /// （enemy.gd:222-228 的语义：被打飞的怪在硬直里也要飞出去）。
        /// </summary>
        private static void IntegrateMotion(Combatant e, float dt)
        {
            if (e.HitStunTimer <= 0.0f)
            {
                e.Position = e.Position + e.Velocity * dt;
            }
            e.IntegrateKnockback(dt);
        }

        /// <summary>把 BOSS 本步的召唤请求收编进待入列队列（含上限裁剪）。</summary>
        private void CollectSummons(BossController boss)
        {
            if (boss.PendingSpawns.Count == 0)
            {
                return;
            }

            int room = MaxEnemies - (AliveEnemyCount + _pendingAdd.Count);
            for (int i = 0; i < boss.PendingSpawns.Count; i++)
            {
                if (room <= 0)
                {
                    break;
                }
                Combatant minion = boss.PendingSpawns[i];
                if (minion == null)
                {
                    continue;
                }
                _pendingAdd.Add(minion);
                room--;
            }
            boss.PendingSpawns.Clear();
        }

        /// <summary>结算 BOSS 本步的冲击波（若有）。</summary>
        private void ResolveBossShockwave(Combatant owner, BossController boss)
        {
            Vec2 center;
            float radius;
            if (!boss.ConsumeShockwave(out center, out radius))
            {
                return;
            }
            if (Player == null || !Player.IsAlive)
            {
                return;
            }
            DamageResolver.ResolveShockwave(owner, Player, center, radius, Events);
        }

        /// <summary>把本步产生的实体正式入列。</summary>
        private void FlushPendingAdds()
        {
            if (_pendingAdd.Count == 0)
            {
                return;
            }
            for (int i = 0; i < _pendingAdd.Count; i++)
            {
                Add(_pendingAdd[i]);
            }
            _pendingAdd.Clear();
        }

        /// <summary>
        /// 移除已死亡的敌人。玩家即使 Hp=0 也**不移除**——死亡流程（复活/结算界面）
        /// 属于上层状态机，内核擅自把玩家从列表里删掉会让上层拿不到尸体。
        /// </summary>
        /// <returns>本次移除的数量。</returns>
        public int RemoveDead()
        {
            _deadBuffer.Clear();
            for (int i = 0; i < Combatants.Count; i++)
            {
                Combatant c = Combatants[i];
                if (c.IsEnemy && !c.IsAlive)
                {
                    _deadBuffer.Add(c);
                }
            }
            if (_deadBuffer.Count == 0)
            {
                return 0;
            }
            for (int i = 0; i < _deadBuffer.Count; i++)
            {
                Combatants.Remove(_deadBuffer[i]);
            }
            int n = _deadBuffer.Count;
            _deadBuffer.Clear();
            return n;
        }

        /// <summary>
        /// 造一只 BOSS 召唤物（R5：当前 zone 的 normal 敌人**降一级**）。
        /// 把它做成公开方法而不是内部逻辑，是为了让 Unity 壳能直接把它挂到
        /// <c>BossController.Spawn</c> 上，测试桩也能复用同一条路径。
        /// </summary>
        /// <param name="cfg">当前区域的 enemies 配置。</param>
        /// <param name="req">召唤请求（含等级与建议落点）。</param>
        /// <returns>新实体；触及 <see cref="MaxEnemies"/> 时返回 null。</returns>
        public Combatant SpawnMinion(ZoneEnemies cfg, SpawnRequest req)
        {
            if (AliveEnemyCount + _pendingAdd.Count >= MaxEnemies)
            {
                return null;
            }
            Combatant minion = Bridge.BuildEnemy(cfg, req.Level, null, Rng);
            minion.Position = req.Position;
            if (minion.AI != null)
            {
                minion.AI.SpawnPos = req.Position;
                minion.AI.Rng = Rng;
            }
            return minion;
        }
    }
}
