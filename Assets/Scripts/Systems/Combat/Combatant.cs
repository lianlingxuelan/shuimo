// -----------------------------------------------------------------------------
// Combatant.cs —— 统一战斗实体（引擎无关）
//
// 【为什么玩家与敌人用同一个类】
// 原型里 Player 与 Enemy 是两棵完全不同的节点树，于是「谁能被击退」「谁有霸体」
// 「谁走哪套承伤」散落在两份代码里，每加一个同伴/召唤物就要复制一遍。这里收敛成
// 一个 Combatant + 一个 Faction 枚举：
//   · 玩家：持有 WCoreState（两层闸门的除法减伤模型）
//   · 敌人：走减法承伤 real = max(1, d − max(0, armor − breakDef)) + 霸体/击退
// 两条承伤路径刻意不同构（见 enemy.gd:482-494 的注释），由 DamageResolver 路由。
//
// 【位置为什么自持而不读 Transform】
// 内核必须能在没有引擎的情况下跑完整场战斗（Python 对拍 / dotnet test）。
// Unity 壳每帧把 transform.position 写进 Position，内核算完再由 CombatView 写回，
// 内核永远不认识 Transform。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using System;
using Xianxia.Core;

namespace Xianxia.Combat
{
    /// <summary>阵营。</summary>
    public enum Faction
    {
        /// <summary>玩家。走 W-CORE 两层闸门 + 除法软上限减伤。</summary>
        Player = 0,

        /// <summary>敌人。走减法承伤 + 霸体 / 击退。</summary>
        Enemy = 1,

        /// <summary>
        /// 同伴（R6 保留位）。
        /// 蓝图里同伴受到的伤害应为 0.5x，但 T1 是单人版本，
        /// <see cref="DamageResolver"/> 刻意**不加** 0.5x 分支——未经验收的数值分支
        /// 混进对拍基线，会让「频率 4.300」这类断言失去意义。
        /// 实装同伴时只需在 DamageResolver.ApplyToPlayer 里按 Faction 分流，
        /// 其余结构无需改动。
        /// </summary>
        Companion = 2
    }

    /// <summary>词缀。对齐 <c>game_config.gd</c> AFFIX_TABLE。</summary>
    public enum AffixKind
    {
        /// <summary>无词缀。</summary>
        None = 0,

        /// <summary>迅捷：移动速度 ×1.35。</summary>
        Swift = 1,

        /// <summary>坚甲：护甲 +12（减法模型下非常硬）。</summary>
        Ironhide = 2,

        /// <summary>炽焰：接触伤害 ×1.25。</summary>
        Blaze = 3
    }

    /// <summary>
    /// 战斗实体。玩家 / 敌人 / BOSS / 召唤物共用一个类型，靠 <see cref="Faction"/>
    /// 与 <see cref="WCore"/>/<see cref="AI"/> 是否为 null 区分行为。
    /// </summary>
    public sealed class Combatant
    {
        // ---------------------------------------------------------------------
        // 身份
        // ---------------------------------------------------------------------

        /// <summary>
        /// 实例 id。**这是 W-CORE 每来源冷却表的 key**，同一场战斗内必须唯一。
        /// 切区 / 重生后 id 可能被复用，因此必须配合 <c>WCoreState.Reset()</c>
        /// 清表，否则会出现「新区怪打不动我」（见 WCore.cs Reset 的注释）。
        /// </summary>
        public int Id;

        /// <summary>阵营。</summary>
        public Faction Faction = Faction.Enemy;

        /// <summary>种类 key（blood / witch / sword / alchemy），掉落与韧性表的反查依据。</summary>
        public string Kind = CombatConfig.KIND_WITCH;

        /// <summary>中文显示名。</summary>
        public string DisplayName = string.Empty;

        /// <summary>等级。由 <see cref="DifficultyBridge"/> 按 zone.base_level + jitter 赋值。</summary>
        public int Level = 1;

        /// <summary>是否 BOSS。</summary>
        public bool IsBoss;

        /// <summary>是否精英。</summary>
        public bool IsElite;

        /// <summary>词缀。T1 每只怪最多一个（与 Godot 的 affixes 数组等价，因原型只 append 一次）。</summary>
        public AffixKind Affix = AffixKind.None;

        /// <summary>BOSS id（非 BOSS 为空串）。</summary>
        public string BossId = string.Empty;

        /// <summary>击杀经验。</summary>
        public float ExpValue;

        // ---------------------------------------------------------------------
        // 空间状态（纯几何，内核自持）
        // ---------------------------------------------------------------------

        /// <summary>世界坐标（像素）。</summary>
        public Vec2 Position = Vec2.Zero;

        /// <summary>本步的移动速度（像素/秒）。由 AI 写入，Encounter 做欧拉积分。</summary>
        public Vec2 Velocity = Vec2.Zero;

        /// <summary>
        /// 击退速度脉冲（像素/秒）。与 <see cref="Velocity"/> 分离存放：
        /// 击退必须在硬直期间照常推进（enemy.gd:222-228 的语义），
        /// 而硬直恰恰会把 AI 的 Velocity 清零。
        /// </summary>
        public Vec2 KnockbackVel = Vec2.Zero;

        // ---------------------------------------------------------------------
        // 生命 / 防御
        // ---------------------------------------------------------------------

        private float _hp;
        private float _hpMax = 1.0f;

        /// <summary>
        /// W-CORE 承伤状态机。**仅玩家持有**（其余为 null）。
        /// 持有时 <see cref="Hp"/>/<see cref="HpMax"/> 自动代理到它，
        /// 避免「两份血量各扣各的」这种最难查的状态分裂。
        /// </summary>
        public WCoreState WCore;

        /// <summary>AI 控制器。敌人 / BOSS 持有，玩家为 null。</summary>
        public EnemyAI AI;

        // ---------------------------------------------------------------------
        // T3 可空组件（架构 §1.1 铁律三：空组件即原路径）
        //
        // 下面五个字段**默认全部为 null**。为 null 时，Encounter.StepFixed 的
        // ①-A / ③-A / ⑤-A 三个新增阶段整段 skip，执行路径与 T2 逐指令一致 ——
        // 这正是「U1 平衡口径一字不改」与「88 条既有断言零修改」的机制保证，
        // 而不是靠"我们小心一点"这种承诺。
        //
        // 装配点唯一：Unity 侧 CombatBridge 在建好 Encounter 之后一次性挂上（玩家）
        // 或按需挂上（敌人只挂 Status）。headless 单测里不挂 = 跑的就是 T2 内核。
        // ---------------------------------------------------------------------

        /// <summary>
        /// 动作帧机（前摇 / 判定 / 后摇 / i-frame / 取消窗）。**可空**。
        /// P0 阶段只有玩家持有；敌人的帧机是 P1-01。
        /// </summary>
        public ActionState Action;

        /// <summary>
        /// 技能冷却与施法校验。**可空**。P0 阶段只有玩家持有。
        /// </summary>
        public SkillRuntime Skills;

        /// <summary>
        /// 状态容器（DOT / 属性修饰）。**可空**。
        /// P0 阶段玩家与敌人都会持有：玩家侧目前只用于承接 P1 的敌方 debuff，
        /// 敌人侧承接蛊毒 / 破防。
        /// </summary>
        public StatusComponent Status;

        /// <summary>灵力池（管技能）。**可空**。P0 阶段只有玩家持有。</summary>
        public ResourcePool Qi;

        /// <summary>体力池（管闪避）。**可空**。P0 阶段只有玩家持有。</summary>
        public ResourcePool Stamina;

        /// <summary>当前血量。玩家自动代理到 <see cref="WCore"/>。</summary>
        public float Hp
        {
            get { return WCore != null ? WCore.Hp : _hp; }
            set
            {
                if (WCore != null)
                {
                    WCore.Hp = value;
                }
                else
                {
                    _hp = value;
                }
            }
        }

        /// <summary>血量上限。玩家自动代理到 <see cref="WCore"/>。</summary>
        public float HpMax
        {
            get { return WCore != null ? WCore.HpMax : _hpMax; }
            set
            {
                if (WCore != null)
                {
                    WCore.HpMax = value;
                }
                else
                {
                    _hpMax = value;
                }
            }
        }

        /// <summary>护甲（减法模型的平坦减伤）。</summary>
        public float Armor;

        /// <summary>破防（穿透对方护甲）。R1：T1 玩家输出未实装，默认 0。</summary>
        public float BreakDef;

        /// <summary>当前韧性。归零即破韧并立刻回满（不停在 0，否则霸体形同虚设）。</summary>
        public float Poise = CombatConfig.POISE_MAX_DEFAULT;

        /// <summary>韧性上限。BOSS 为基础值 ×3.0。</summary>
        public float PoiseMax = CombatConfig.POISE_MAX_DEFAULT;

        /// <summary>韧性回复冷却剩余（秒）。受击后置为 POISE_REGEN_DELAY。</summary>
        public float PoiseRegenTimer;

        /// <summary>硬直剩余时间（秒）。&gt; 0 时 AI 不驱动移动，但击退位移照常推进。</summary>
        public float HitStunTimer;

        // ---------------------------------------------------------------------
        // 攻击
        // ---------------------------------------------------------------------

        /// <summary>攻击力（玩家技能输出的基数；R1 中玩家侧公式留 T2）。</summary>
        public float Atk;

        /// <summary>接触伤害。敌人碰到玩家时的单次原始伤害（模型 B 的 d_eff）。</summary>
        public float ContactDamage;

        private float _moveSpeed = 70.0f;

        /// <summary>
        /// 移动速度基准（像素/秒）。
        /// 有 AI 时以 <see cref="EnemyAI.SpeedBase"/> 为唯一真源（词缀 swift 改的是它），
        /// 这里做双向代理，避免出现「改了 Combatant 的速度但 AI 没跟着变」。
        /// </summary>
        public float MoveSpeed
        {
            get { return AI != null ? AI.SpeedBase : _moveSpeed; }
            set
            {
                _moveSpeed = value;
                if (AI != null)
                {
                    AI.SpeedBase = value;
                }
            }
        }

        // ---------------------------------------------------------------------
        // 查询
        // ---------------------------------------------------------------------

        /// <summary>是否存活。</summary>
        public bool IsAlive
        {
            get { return Hp > 0.0f; }
        }

        /// <summary>是否为敌对单位（BOSS 也算）。</summary>
        public bool IsEnemy
        {
            get { return Faction == Faction.Enemy; }
        }

        /// <summary>
        /// 计入状态修饰后的有效护甲（<c>se_break_def</c> 的落地点）。
        ///
        /// 【为什么是短路而不是"乘以 1.0"】（架构 §1.6e）
        /// 没有状态时必须**原样返回 <see cref="Armor"/>**，一次浮点乘法都不做。
        /// 若写成 <c>Armor * Status.ArmorMult</c> 并让 ArmorMult 默认 1.0f，
        /// 数值上确实相等，但它把「T3 未装配时零改动」从**结构保证**降级成
        /// 「IEEE754 恰好帮我们兜住了」的巧合 —— 一旦将来有人加了个 0.999f 的
        /// 全局系数，U1 基线就会静默漂移，而且没有任何一条测试会报警。
        /// </summary>
        public float EffectiveArmor
        {
            get
            {
                if (Status == null || !Status.HasArmorMod)
                {
                    return Armor;
                }
                float v = Armor * Status.ArmorMult + Status.ArmorDelta;
                return v < 0.0f ? 0.0f : v;
            }
        }

        /// <summary>血量比例，夹在 [0,1]。BOSS 阶段判定的唯一输入。</summary>
        public float HpRatio()
        {
            float max = HpMax > 0.0f ? HpMax : 1.0f;
            float r = Hp / max;
            if (r < 0.0f)
            {
                return 0.0f;
            }
            return r > 1.0f ? 1.0f : r;
        }

        // ---------------------------------------------------------------------
        // 承伤（敌人侧：减法模型）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 敌人承伤（减法模型），攻击方破防按 0 计。
        /// 保留这个单参重载是为了与设计类图签名一致；生产路径请用带 breakDef 的重载。
        /// </summary>
        /// <param name="raw">攻击方已算好的输出伤害。</param>
        /// <returns>实际扣除的血量。</returns>
        public float ApplyEnemyDamage(float raw)
        {
            return ApplyEnemyDamage(raw, 0.0f);
        }

        /// <summary>
        /// 敌人承伤（减法模型）：<c>real = max(1, raw − max(0, armor − attackerBreakDef))</c>。
        ///
        /// 【为什么怪走减法、玩家走除法】
        /// 减法让「破防」这一属性对小怪立竿见影（+10 破防 = 每刀多 10 伤）；
        /// 除法（<c>Difficulty.DamageTaken</c>）让玩家堆防御永远逼近但不到免疫。
        /// 两边刻意不同构，见 enemy.gd:482-494。
        ///
        /// 【T3 增量】护甲基数由 <see cref="Armor"/> 改读 <see cref="EffectiveArmor"/>，
        /// 使 <c>se_break_def</c> 生效。未挂 <see cref="Status"/> 时 EffectiveArmor
        /// 原样返回 Armor，本行的行为与 T2 逐位相同（U1 基线不受影响）。
        /// </summary>
        /// <param name="raw">攻击方已算好的输出伤害（含暴击，不再二次乘攻击力）。</param>
        /// <param name="attackerBreakDef">攻击方的破防值。</param>
        /// <returns>实际扣除的血量（下限 1）。</returns>
        public float ApplyEnemyDamage(float raw, float attackerBreakDef)
        {
            float effectiveArmor = EffectiveArmor - attackerBreakDef;
            if (effectiveArmor < 0.0f)
            {
                effectiveArmor = 0.0f;
            }

            float real = raw - effectiveArmor;
            if (real < CombatConfig.ENEMY_DAMAGE_FLOOR)
            {
                real = CombatConfig.ENEMY_DAMAGE_FLOOR;
            }

            Hp = Hp - real;
            if (Hp < 0.0f)
            {
                Hp = 0.0f;
            }
            return real;
        }

        // ---------------------------------------------------------------------
        // 霸体 / 硬直 / 击退
        // ---------------------------------------------------------------------

        /// <summary>
        /// 扣韧性。返回 true 表示**本次破韧**（调用方需施加硬直 + 击退）。
        /// 破韧后立刻回满而不是停在 0：留 0 会让紧随其后的每一下普攻都判定为破韧。
        /// </summary>
        public bool DamagePoise(float amount)
        {
            PoiseRegenTimer = CombatConfig.POISE_REGEN_DELAY;
            Poise -= amount > 0.0f ? amount : 0.0f;
            if (Poise <= 0.0f)
            {
                Poise = PoiseMax;
                return true;
            }
            return false;
        }

        /// <summary>韧性自然回复。REGEN_DELAY 内不回，之后按 POISE_REGEN/s 线性回满。</summary>
        public void TickPoise(float dt)
        {
            if (PoiseRegenTimer > 0.0f)
            {
                PoiseRegenTimer -= dt;
                if (PoiseRegenTimer < 0.0f)
                {
                    PoiseRegenTimer = 0.0f;
                }
                return;
            }
            if (Poise < PoiseMax)
            {
                Poise += CombatConfig.POISE_REGEN * dt;
                if (Poise > PoiseMax)
                {
                    Poise = PoiseMax;
                }
            }
        }

        /// <summary>
        /// 施加硬直。取 max 而非累加：多段 AOE 同帧命中不该叠出一个几秒的假死怪。
        /// </summary>
        public void ApplyHitStun(float seconds)
        {
            if (seconds <= 0.0f)
            {
                return;
            }
            if (seconds > HitStunTimer)
            {
                HitStunTimer = seconds;
            }
        }

        /// <summary>硬直倒计时。</summary>
        public void TickHitStun(float dt)
        {
            if (HitStunTimer <= 0.0f)
            {
                return;
            }
            HitStunTimer -= dt;
            if (HitStunTimer < 0.0f)
            {
                HitStunTimer = 0.0f;
            }
        }

        /// <summary>
        /// 施加击退。R2 拍板：**只写速度脉冲，不做墙体解算**（内核按开阔地模拟）。
        ///
        /// 【为什么不直接改 Position】
        /// 直接 <c>Position += dir * dist</c> 会一帧穿过整面墙——这正是 Godot R-07
        /// 前冲踩过的坑。这里写入速度，位移由 <see cref="IntegrateKnockback"/> 逐步推进；
        /// 将来 Unity 壳接入碰撞后，只需在壳里对每步位移做逐轴裁剪，内核零改动。
        /// </summary>
        public void TakeKnockback(Vec2 impulse)
        {
            KnockbackVel = KnockbackVel + impulse;
        }

        /// <summary>
        /// 由「击退方向 + 期望距离」算出速度脉冲：匀减速走完 dist 所需初速
        /// <c>v0 = sqrt(2 · KNOCKBACK_FRICTION · dist)</c>。
        /// 方向退化时兜底为 <see cref="Vec2.Right"/>（对齐 enemy.gd:380-386）。
        /// </summary>
        public static Vec2 KnockbackImpulse(Vec2 dir, float dist)
        {
            if (dist <= 0.0f)
            {
                return Vec2.Zero;
            }
            Vec2 d = dir;
            if (d.IsZero())
            {
                d = Vec2.Right;
            }
            float v0 = (float)Math.Sqrt(2.0 * CombatConfig.KNOCKBACK_FRICTION * dist);
            return d.Normalized() * v0;
        }

        /// <summary>
        /// 推进击退位移并按摩擦线性衰减。返回 true 表示本步仍在被击退。
        /// </summary>
        public bool IntegrateKnockback(float dt)
        {
            if (KnockbackVel.IsZero())
            {
                KnockbackVel = Vec2.Zero;
                return false;
            }

            Position = Position + KnockbackVel * dt;

            float sp = KnockbackVel.Length() - CombatConfig.KNOCKBACK_FRICTION * dt;
            if (sp <= CombatConfig.KNOCKBACK_STOP_SPEED)
            {
                KnockbackVel = Vec2.Zero;
            }
            else
            {
                KnockbackVel = KnockbackVel.Normalized() * sp;
            }
            return true;
        }

        // ---------------------------------------------------------------------
        // 生命周期
        // ---------------------------------------------------------------------

        /// <summary>
        /// 满血复位（含 W-CORE 闸门清空）。
        ///
        /// 【T3 增量】追加五个可空组件的复位。全部包在 null 判断里，
        /// 未装配时这段代码等价于五条 <c>if(false)</c>，不改变任何既有行为。
        /// 复位顺序无关紧要（各组件互不依赖），按声明顺序写以便与字段区对读。
        /// </summary>
        public void FullRestore()
        {
            if (WCore != null)
            {
                WCore.FullRestore();
            }
            else
            {
                Hp = HpMax;
            }
            Poise = PoiseMax;
            PoiseRegenTimer = 0.0f;
            HitStunTimer = 0.0f;
            KnockbackVel = Vec2.Zero;
            Velocity = Vec2.Zero;

            if (Action != null)
            {
                Action.Reset();
            }
            if (Skills != null)
            {
                Skills.Reset();
            }
            if (Status != null)
            {
                Status.Clear();
            }
            if (Qi != null)
            {
                Qi.Reset();
            }
            if (Stamina != null)
            {
                Stamina.Reset();
            }
        }

        /// <summary>
        /// 工厂：造一个玩家（自带 <see cref="WCoreState"/>，对拍默认关闪避）。
        /// </summary>
        public static Combatant CreatePlayer(int id, float hpMax, Vec2 pos)
        {
            WCoreState core = new WCoreState();
            core.HpMax = hpMax;
            core.Hp = hpMax;
            core.DodgeEnabled = false;   // R3：对拍默认关，避免随机闪避污染频率统计

            Combatant c = new Combatant();
            c.Id = id;
            c.Faction = Faction.Player;
            c.WCore = core;
            c.Position = pos;
            c.DisplayName = "玩家";
            c.Kind = "player";
            return c;
        }

        /// <summary>
        /// 工厂：造一个敌人（自带 <see cref="EnemyAI"/>）。
        /// 数值一律由 <see cref="DifficultyBridge"/> 后续覆写，这里只搭骨架。
        /// </summary>
        public static Combatant CreateEnemy(int id, Vec2 pos, float hpMax, float contactDamage, float speed)
        {
            Combatant c = new Combatant();
            c.Id = id;
            c.Faction = Faction.Enemy;
            c.Position = pos;
            c.HpMax = hpMax;
            c.Hp = hpMax;
            c.ContactDamage = contactDamage;
            c.Poise = CombatConfig.POISE_MAX_DEFAULT;
            c.PoiseMax = CombatConfig.POISE_MAX_DEFAULT;

            EnemyAI ai = new EnemyAI(c);
            ai.SpeedBase = speed;
            c.AI = ai;
            return c;
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("[{0}#{1} {2} hp={3:F1}/{4:F1} pos={5}]",
                Faction, Id, string.IsNullOrEmpty(DisplayName) ? Kind : DisplayName,
                Hp, HpMax, Position);
        }
    }
}
