// -----------------------------------------------------------------------------
// EnemyAI.cs —— 普通 / 精英敌人的三态状态机（引擎无关，移植自 godot/scripts/enemy.gd）
//
// 【三态设计的意图】
//   PATROL —— 玩家跑出牵引距离（480）后回锚出生点。没有它，玩家可以把全图的怪
//             拉成一条长龙，然后在门口一个个点掉，「区域」就失去了空间意义。
//   CHASE  —— 带 ±35° 侧向偏移的追击。纯直线追击会让 4 只怪叠成一个点，
//             围攻在视觉上退化为单挑，2.53x 的承伤倍率也就白设计了。
//   STRIKE —— 蓄力 0.35s（不动，玩家的反应窗口）→ 突进 0.25s（3.2 倍速）→ 冷却 3.5s。
//             蓄力期不移动是关键：它把「被怪贴脸」从连续掉血变成有节奏的攻防。
//
// 【normal 与 elite 共用本类】
// 精英不换 AI，只换数值（HP×2.6 / ATK×1.5）。引入「精英专属行为」等于引入一套
// 未经原型验收的手感，风险远大于收益（设计文档 §3.5）。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using Xianxia.Core;

namespace Xianxia.Combat
{
    /// <summary>AI 状态。数值与 <c>enemy.gd</c> 的 <c>AIState</c> 枚举逐值对齐。</summary>
    public enum AIState
    {
        /// <summary>脱战回锚。</summary>
        PATROL = 0,

        /// <summary>追击（默认态）。</summary>
        CHASE = 1,

        /// <summary>蓄力突进。</summary>
        STRIKE = 2
    }

    /// <summary>
    /// AI 本步的对外意图快照。供 <see cref="Encounter"/> / <see cref="DamageResolver"/>
    /// 读取，避免它们反过来窥探 AI 的内部相位字段。
    /// </summary>
    public struct AICommand
    {
        /// <summary>本步的状态。</summary>
        public AIState State;

        /// <summary>本步的期望速度（像素/秒）。硬直中为零向量。</summary>
        public Vec2 DesiredVelocity;

        /// <summary>本步的接触伤害倍率（突进中为 <c>AI_STRIKE_DMG_MULT</c>）。</summary>
        public float DamageMult;

        /// <summary>本步是否处于突进相位。</summary>
        public bool Dashing;

        /// <summary>本步是否刚进入蓄力（壳可据此播放闪白预警）。</summary>
        public bool StrikeStarted;

        /// <summary>本步是否刚结束突进（壳可据此收起预警）。</summary>
        public bool StrikeEnded;
    }

    /// <summary>
    /// 敌人三态 FSM。每个固定步（1/60s）由 <see cref="Encounter"/> 调用一次
    /// <see cref="Update"/>；本类只算「速度意图」，位移积分交给 Encounter，
    /// 这样击退 / 硬直 / 墙体裁剪的优先级由一个地方统一裁决。
    /// </summary>
    public class EnemyAI
    {
        // --- 相位常量（对应 enemy.gd 的 _strike_phase）---

        /// <summary>STRIKE 相位：未开始。</summary>
        public const int STRIKE_PHASE_IDLE = 0;

        /// <summary>STRIKE 相位：蓄力中。</summary>
        public const int STRIKE_PHASE_WINDUP = 1;

        /// <summary>STRIKE 相位：突进中。</summary>
        public const int STRIKE_PHASE_DASH = 2;

        // --- 状态 ---

        /// <summary>当前状态。**默认 CHASE**（对齐 enemy.gd:40 的 `ai_state := AIState.CHASE`）。</summary>
        public AIState State = AIState.CHASE;

        /// <summary>STRIKE 冷却剩余（秒）。</summary>
        public float StrikeCd;

        /// <summary>STRIKE 蓄力配置时长（秒）。默认取 <c>CombatConfig.AI_STRIKE_WINDUP</c>。</summary>
        public float StrikeWindup = CombatConfig.AI_STRIKE_WINDUP;

        /// <summary>STRIKE 突进配置时长（秒）。默认取 <c>CombatConfig.AI_STRIKE_DASH</c>。</summary>
        public float StrikeDash = CombatConfig.AI_STRIKE_DASH;

        /// <summary>STRIKE 当前相位（0 idle / 1 windup / 2 dash）。</summary>
        public int StrikePhase = STRIKE_PHASE_IDLE;

        /// <summary>当前相位剩余时间（秒）。</summary>
        public float StrikeTimer;

        /// <summary>突进方向（进入蓄力时锁定，突进中不再修正——这是可被走位躲开的关键）。</summary>
        public Vec2 StrikeDir = Vec2.Zero;

        /// <summary>CHASE 重新掷侧向偏移角的冷却剩余（秒）。</summary>
        public float RedirCd;

        /// <summary>当前生效的侧向偏移角（度）。由 <see cref="RedirCd"/> 到期时重掷。</summary>
        public float ChaseOffsetDeg;

        /// <summary>移动速度基准（像素/秒）。词缀 swift 直接乘它。</summary>
        public float SpeedBase = 70.0f;

        /// <summary>出生点。PATROL 的回锚目标。</summary>
        public Vec2 SpawnPos = Vec2.Zero;

        /// <summary>宿主实体。</summary>
        public Combatant Owner;

        /// <summary>
        /// 侧向偏移用的随机流。**必须由 <see cref="Encounter"/> 注入**，
        /// 保证同种子重放逐位一致；为 null 时偏移恒为 0（对拍锁步模式）。
        /// </summary>
        public PCG32 Rng;

        private AICommand _cmd;

        /// <summary>
        /// 本步是否实际执行了突进位移。**这是「本步在不在突进」的唯一真源**，
        /// 位移、伤害倍率、表现三者只认它，任何地方都不要再去读 <see cref="StrikePhase"/> 判定。
        ///
        /// 【为什么不能直接看 <see cref="StrikePhase"/>】
        /// 相位字段在两个方向上都和"实际位移"错拍一步：
        ///
        /// · D-2（迟一步熄灭）：突进的最后一步里，<see cref="ComputeStrike"/> 会先把相位重置为
        ///   IDLE 再返回突进速度。于是那一步「以 3.2 倍速冲过去，却只结算 1.0 倍伤害」。
        ///   恰恰是这最后一步最容易撞上玩家（冲刺终点），算成普通接触，玩家会觉得
        ///   "明明被撞飞了却没痛感"。
        ///
        /// · D-3（早一步点亮）：蓄力的最后一步里，WINDUP 分支把相位翻成 DASH 之后
        ///   **返回的仍是零速度**（那一步还在原地站桩）。若把相位当判据，1.5 倍伤害
        ///   会在敌人一个像素都没动的时候就先亮起来——蓄力本是留给玩家的反应窗口，
        ///   在窗口里就吃满突进伤害，等于窗口不存在；Unity 侧的突进拖影也会提前一帧亮。
        ///
        /// 只在 DASH 分支真正产出突进速度时置位，两头的错拍就同时消失了。
        /// </summary>
        private bool _dashingThisStep;

        /// <summary>构造。</summary>
        public EnemyAI(Combatant owner)
        {
            Owner = owner;
            if (owner != null)
            {
                SpawnPos = owner.Position;
            }
            _cmd.State = State;
            _cmd.DamageMult = 1.0f;
        }

        /// <summary>
        /// 本步实际生效的移动速度。BOSS 覆写它以叠加阶段提速。
        ///
        /// 【T3 增量】叠加 <c>se_slow</c> 等移速修饰（架构 §1.6e）。
        /// **短路先行**：未挂 <see cref="Combatant.Status"/> 或身上没有移速修饰时，
        /// 原样返回 <see cref="SpeedBase"/>，一次浮点运算都不做 ——
        /// 这条路径正是 U1 基线跑的那条，必须逐位等价而不是数值近似。
        /// P0 阶段只有玩家能施加移速 debuff，方向严格是「玩家 → 敌人」，
        /// 玩家承伤链路不受影响。
        /// </summary>
        public virtual float EffectiveSpeed
        {
            get
            {
                StatusComponent st = Owner != null ? Owner.Status : null;
                if (st == null || !st.HasSpeedMod)
                {
                    return SpeedBase;
                }
                float v = SpeedBase * st.MoveSpeedMult + st.MoveSpeedDelta;
                return v < 0.0f ? 0.0f : v;
            }
        }

        /// <summary>
        /// 本步的接触伤害倍率。突进中为 1.5；BOSS 覆写它以叠加阶段攻击倍率。
        /// </summary>
        public virtual float CurrentDamageMult
        {
            get
            {
                // 只认 _dashingThisStep（见其注释里的 D-2 / D-3）。
                return _dashingThisStep ? CombatConfig.AI_STRIKE_DMG_MULT : 1.0f;
            }
        }

        /// <summary>取本步的意图快照（<see cref="Update"/> 之后调用才有意义）。</summary>
        public AICommand PollCommand()
        {
            return _cmd;
        }

        // ---------------------------------------------------------------------
        // 主循环
        // ---------------------------------------------------------------------

        /// <summary>
        /// 推进一个固定步。
        /// </summary>
        /// <param name="dt">固定步长（恒为 1/60，由 CombatScheduler 保证）。</param>
        /// <param name="targetPos">追击目标（玩家）的位置。</param>
        /// <param name="targetVel">追击目标的速度。当前仅作预留（预判拦截留待后续）。</param>
        public virtual void Update(float dt, Vec2 targetPos, Vec2 targetVel)
        {
            _cmd = default(AICommand);
            _cmd.DamageMult = 1.0f;
            _dashingThisStep = false;

            if (Owner == null)
            {
                return;
            }

            TickTimers(dt);
            SelectState(targetPos);

            // 硬直中：不追击、不出手，但击退位移由 Encounter 照常推进（enemy.gd:222-228）。
            if (Owner.HitStunTimer > 0.0f)
            {
                _cmd.State = State;
                _cmd.DesiredVelocity = Vec2.Zero;
                _cmd.DamageMult = CurrentDamageMult;
                Owner.Velocity = Vec2.Zero;
                return;
            }

            Vec2 vel = ComputeMovement(dt, targetPos);

            Owner.Velocity = vel;
            _cmd.State = State;
            _cmd.DesiredVelocity = vel;
            _cmd.Dashing = _dashingThisStep;
            _cmd.DamageMult = CurrentDamageMult;
        }

        /// <summary>递减各计时器。</summary>
        protected void TickTimers(float dt)
        {
            if (StrikeCd > 0.0f)
            {
                StrikeCd -= dt;
                if (StrikeCd < 0.0f)
                {
                    StrikeCd = 0.0f;
                }
            }

            if (RedirCd > 0.0f)
            {
                RedirCd -= dt;
                if (RedirCd < 0.0f)
                {
                    RedirCd = 0.0f;
                }
            }
        }

        /// <summary>
        /// 状态选择。逐条对齐 <c>enemy.gd:_tick_ai</c>：
        /// 脱战 &gt; STRIKE 冷却 &gt; 进入 STRIKE 距离 &gt; 否则 CHASE。
        ///
        /// 【一处刻意保留的原型行为】
        /// 突进途中若玩家被拉开到 &gt; AI_STRIKE_DIST，状态会切回 CHASE，而
        /// <see cref="StrikePhase"/> 保持不变（冻结）。等玩家再次靠近，突进会从
        /// 冻结处继续。这是 Godot 原型的实际表现，保留以维持手感一致。
        /// </summary>
        protected void SelectState(Vec2 targetPos)
        {
            float dist = Owner.Position.DistanceTo(targetPos);

            if (dist > CombatConfig.AI_LEASH_DIST)
            {
                State = AIState.PATROL;
                return;
            }

            if (StrikeCd > 0.0f)
            {
                State = AIState.CHASE;
                return;
            }

            State = dist <= CombatConfig.AI_STRIKE_DIST ? AIState.STRIKE : AIState.CHASE;
        }

        /// <summary>按当前状态算出本步的期望速度。</summary>
        protected virtual Vec2 ComputeMovement(float dt, Vec2 targetPos)
        {
            switch (State)
            {
                case AIState.PATROL:
                    return ComputePatrol();
                case AIState.STRIKE:
                    return ComputeStrike(dt, targetPos);
                case AIState.CHASE:
                default:
                    return ComputeChase(targetPos);
            }
        }

        /// <summary>PATROL：以 30% 速度回锚出生点，到达 20 像素内即停。</summary>
        protected Vec2 ComputePatrol()
        {
            float distToSpawn = Owner.Position.DistanceTo(SpawnPos);
            if (distToSpawn < CombatConfig.AI_PATROL_ANCHOR_RADIUS)
            {
                return Vec2.Zero;
            }
            Vec2 dir = Owner.Position.DirectionTo(SpawnPos);
            return dir * (EffectiveSpeed * CombatConfig.AI_PATROL_SPEED_MULT);
        }

        /// <summary>
        /// CHASE：朝玩家追击，方向叠加一个 ±35° 的侧向偏移。
        ///
        /// 【与 Godot 原型的一处刻意订正 · D-1】
        /// <c>enemy.gd:_process_chase</c> 维护了 <c>_ai_chase_redirect_cd</c>，
        /// 却在**每一帧**都重掷偏移角——计时器实际没有起到任何门控作用。后果有二：
        ///   1. 每帧重掷等价于在 ±35° 上做高频抖动，期望方向仍是直指玩家
        ///      （cos 的均值 ≈ 0.94），「多只怪不叠成一条线」的设计意图完全落空；
        ///   2. 每只怪每秒消耗 60 次随机流，锁步重放的确定性被淹没在噪声里。
        /// 这里按其**显然的原意**实现：偏移角只在 <see cref="RedirCd"/> 到期时重掷，
        /// 期间保持不变。宏观位移几乎一致（都朝玩家逼近），但怪群会真的散开，
        /// 且随机流消耗从 60/s 降到 0.83/s。
        /// </summary>
        protected Vec2 ComputeChase(Vec2 targetPos)
        {
            if (RedirCd <= 0.0f)
            {
                RedirCd = CombatConfig.AI_CHASE_REDIR_CD;
                ChaseOffsetDeg = Rng != null
                    ? Rng.NextRange(-CombatConfig.AI_CHASE_OFFSET_DEG, CombatConfig.AI_CHASE_OFFSET_DEG)
                    : 0.0f;
            }

            Vec2 dir = Owner.Position.DirectionTo(targetPos);
            if (dir.IsZero())
            {
                return Vec2.Zero;
            }
            return dir.RotatedDeg(ChaseOffsetDeg) * EffectiveSpeed;
        }

        /// <summary>
        /// STRIKE：蓄力 → 突进 → 冷却。
        ///
        /// 时序与 <c>enemy.gd:_process_strike</c> 逐步对齐：进入蓄力的那一步
        /// **只设定计时器、不递减**，因此蓄力实际持续 ceil(0.35 / dt) 个逻辑步。
        /// 这一帧的差别在 60Hz 下是 16.7ms，恰好是原型手感的一部分，不做"优化"。
        /// </summary>
        protected Vec2 ComputeStrike(float dt, Vec2 targetPos)
        {
            switch (StrikePhase)
            {
                case STRIKE_PHASE_IDLE:
                    StrikePhase = STRIKE_PHASE_WINDUP;
                    StrikeTimer = StrikeWindup;
                    StrikeDir = Owner.Position.DirectionTo(targetPos);
                    if (StrikeDir.IsZero())
                    {
                        StrikeDir = Vec2.Right;
                    }
                    _cmd.StrikeStarted = true;
                    return Vec2.Zero;

                case STRIKE_PHASE_WINDUP:
                    StrikeTimer -= dt;
                    if (StrikeTimer <= 0.0f)
                    {
                        StrikePhase = STRIKE_PHASE_DASH;
                        StrikeTimer = StrikeDash;
                    }
                    return Vec2.Zero;

                case STRIKE_PHASE_DASH:
                    StrikeTimer -= dt;
                    _dashingThisStep = true;   // D-2：先记账，再允许相位重置
                    Vec2 dashVel = StrikeDir * (EffectiveSpeed * CombatConfig.AI_STRIKE_SPEED_MULT);
                    if (StrikeTimer <= 0.0f)
                    {
                        StrikePhase = STRIKE_PHASE_IDLE;
                        StrikeCd = CombatConfig.AI_STRIKE_CD;
                        State = AIState.CHASE;
                        _cmd.StrikeEnded = true;
                    }
                    return dashVel;

                default:
                    StrikePhase = STRIKE_PHASE_IDLE;
                    return Vec2.Zero;
            }
        }

        /// <summary>重置到初始态（复用实例 / 切区时调用）。</summary>
        public virtual void Reset()
        {
            State = AIState.CHASE;
            StrikeCd = 0.0f;
            StrikePhase = STRIKE_PHASE_IDLE;
            StrikeTimer = 0.0f;
            StrikeDir = Vec2.Zero;
            RedirCd = 0.0f;
            ChaseOffsetDeg = 0.0f;
            _dashingThisStep = false;
            _cmd = default(AICommand);
            _cmd.DamageMult = 1.0f;
        }
    }
}
