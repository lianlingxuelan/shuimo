// -----------------------------------------------------------------------------
// BossController.cs —— BOSS 三阶段控制器（引擎无关，继承 EnemyAI）
//
// 【为什么继承而不是另起炉灶】
// BOSS 的位移与 STRIKE 节奏与小怪**完全一致**，差异只在「阶段乘区」与「周期技能」。
// 复制一份 FSM 意味着以后每改一次 STRIKE 时序都要改两处，迟早漂。这里继承 EnemyAI，
// 只覆写两个乘区属性（EffectiveSpeed / CurrentDamageMult）与 Update 的前后钩子。
//
// 【阶段只降不升】
// CheckPhaseTransition 只在血量比例**向下**跨阈值时推进阶段并抛一次事件。
// 若允许回升（比如 BOSS 吸血），玩家会看到"狂暴又冷静下来"的荒诞表现，
// 且 OnBossPhase 会被反复触发，音效/UI 全部抖动。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Xianxia.Combat
{
    /// <summary>BOSS 阶段。</summary>
    public enum BossPhase
    {
        /// <summary>第一阶段：ratio &gt; 0.65。</summary>
        P1 = 0,

        /// <summary>第二阶段：0.30 &lt; ratio ≤ 0.65。提速 + 周期召唤。</summary>
        P2 = 1,

        /// <summary>第三阶段：ratio ≤ 0.30。再提速 + 攻击提升 + 周期冲击波。</summary>
        P3 = 2
    }

    /// <summary>
    /// 召唤请求。内核只描述「要什么」，具体怎么造由注入的 <see cref="BossController.Spawn"/>
    /// 委托决定（测试桩直接 new Combatant，Unity 壳则走对象池 + Instantiate）。
    /// </summary>
    public struct SpawnRequest
    {
        /// <summary>发起召唤的 BOSS。</summary>
        public Combatant Summoner;

        /// <summary>本次召唤的第几只（0 基）。</summary>
        public int Index;

        /// <summary>本次召唤的总数。</summary>
        public int Count;

        /// <summary>被召唤者的等级。R5：当前 zone 的 normal 敌人降一级。</summary>
        public int Level;

        /// <summary>建议的出生位置（BOSS 周围）。</summary>
        public Vec2 Position;
    }

    /// <summary>BOSS 三阶段控制器。</summary>
    public sealed class BossController : EnemyAI
    {
        /// <summary>当前阶段。</summary>
        public BossPhase Phase = BossPhase.P1;

        /// <summary>召唤冷却剩余（秒）。</summary>
        public float SummonCd;

        /// <summary>冲击波冷却剩余（秒）。</summary>
        public float ShockCd;

        /// <summary>当前阶段的攻击倍率。</summary>
        public float AtkMult = 1.0f;

        /// <summary>当前阶段的速度倍率。</summary>
        public float SpeedMult = 1.0f;

        /// <summary>事件出口。为 null 时自动回落到 <see cref="NullCombatEvents"/>。</summary>
        public ICombatEvents Events = NullCombatEvents.Instance;

        /// <summary>
        /// 召唤委托。返回 null 表示本次召唤失败（例如场上敌人已达上限），内核容忍。
        /// R5：被召唤的是「当前 zone 的 normal 敌人降一级」，由委托实现方负责。
        /// </summary>
        public Func<SpawnRequest, Combatant> Spawn;

        /// <summary>
        /// 本步新召唤出来、等待 <see cref="Encounter"/> 收编的实体。
        /// 用「待办队列」而不是让 BossController 直接持有 Encounter：
        /// AI 不该有能力往战场里塞东西，那会让「谁改了 Combatants 列表」无从追查。
        /// </summary>
        public readonly List<Combatant> PendingSpawns = new List<Combatant>(4);

        /// <summary>本步是否触发了冲击波（由 Encounter 消费后清零）。</summary>
        public bool ShockwavePending;

        /// <summary>本步冲击波的中心点。</summary>
        public Vec2 ShockwaveCenter;

        /// <summary>本步冲击波的半径。</summary>
        public float ShockwaveRadius;

        /// <summary>构造。</summary>
        public BossController(Combatant owner) : base(owner)
        {
        }

        /// <inheritdoc />
        public override float EffectiveSpeed
        {
            get { return SpeedBase * SpeedMult; }
        }

        /// <inheritdoc />
        public override float CurrentDamageMult
        {
            get { return base.CurrentDamageMult * AtkMult; }
        }

        /// <summary>
        /// 推进一个固定步：先判阶段（可能改乘区），再跑三态 FSM，最后跑阶段技能。
        /// 顺序不可调换——阶段技能的冷却必须用**本阶段**的参数递减。
        /// </summary>
        public override void Update(float dt, Vec2 targetPos, Vec2 targetVel)
        {
            CheckPhaseTransition();
            base.Update(dt, targetPos, targetVel);

            if (Phase >= BossPhase.P2)
            {
                RunP2Abilities(dt);
            }
            if (Phase >= BossPhase.P3)
            {
                RunP3Abilities(dt);
            }
        }

        /// <summary>
        /// 阶段检测。只在血量比例向下跨阈值时推进一级并抛一次 <c>OnBossPhase</c>。
        /// 一次扣血同时跨过两个阈值（例如秒掉 70%）时会连跳到 P3，
        /// 并**只**抛 P3 一次事件——中间那次 P2 的召唤没有意义，抛了反而会让
        /// UI 在同一帧闪两条阶段提示。
        /// </summary>
        private void CheckPhaseTransition()
        {
            if (Owner == null)
            {
                return;
            }

            float ratio = Owner.HpRatio();
            BossPhase target;
            if (ratio <= CombatConfig.BOSS_PHASE_P3_THRESHOLD)
            {
                target = BossPhase.P3;
            }
            else if (ratio <= CombatConfig.BOSS_PHASE_P2_THRESHOLD)
            {
                target = BossPhase.P2;
            }
            else
            {
                target = BossPhase.P1;
            }

            if (target <= Phase)
            {
                return;   // 只降不升
            }

            Phase = target;
            ApplyPhaseMultipliers();

            ICombatEvents ev = Events ?? NullCombatEvents.Instance;
            ev.OnBossPhase(Phase);
        }

        /// <summary>按当前阶段刷新乘区与技能冷却。</summary>
        private void ApplyPhaseMultipliers()
        {
            switch (Phase)
            {
                case BossPhase.P2:
                    SpeedMult = CombatConfig.BOSS_P2_SPEED_MULT;
                    AtkMult = CombatConfig.BOSS_P2_ATK_MULT;
                    SummonCd = CombatConfig.BOSS_P2_SUMMON_CD;
                    break;

                case BossPhase.P3:
                    SpeedMult = CombatConfig.BOSS_P3_SPEED_MULT;
                    AtkMult = CombatConfig.BOSS_P3_ATK_MULT;
                    // 连跳 P1→P3 时 SummonCd 还没被初始化过，这里补上，
                    // 否则 P3 的召唤会在进场瞬间立刻放一次。
                    if (SummonCd <= 0.0f)
                    {
                        SummonCd = CombatConfig.BOSS_P2_SUMMON_CD;
                    }
                    ShockCd = CombatConfig.BOSS_P3_SHOCK_CD;
                    break;

                case BossPhase.P1:
                default:
                    SpeedMult = 1.0f;
                    AtkMult = 1.0f;
                    break;
            }
        }

        /// <summary>P2 周期技能：每 8.0s 召唤 2 只小怪。</summary>
        private void RunP2Abilities(float dt)
        {
            SummonCd -= dt;
            if (SummonCd > 0.0f)
            {
                return;
            }

            // D-4：进位式复位，而不是 `SummonCd = 周期`。
            // 硬复位会把"这一步多减掉的那点残差"扔掉：8.0s 用 1/60 步长减不出整数步，
            // 每个周期都要多花一步才跨过零点，于是召唤实际落在 8.017 / 16.033 / 24.050——
            // 误差**逐周期累加**，一场 5 分钟的 BOSS 战能漂出大半秒，节奏和音画都对不上。
            // 把残差带进下一周期，漂移就被钉成恒定的 1 步以内，长期节拍严格等于 8.0s。
            SummonCd += CombatConfig.BOSS_P2_SUMMON_CD;
            if (SummonCd <= 0.0f)
            {
                // dt 异常大（断点、卡顿补帧）时退化为硬复位，绝不允许连喷。
                SummonCd = CombatConfig.BOSS_P2_SUMMON_CD;
            }

            int n = CombatConfig.BOSS_P2_SUMMON_N;
            if (Spawn != null && Owner != null)
            {
                for (int i = 0; i < n; i++)
                {
                    SpawnRequest req = new SpawnRequest();
                    req.Summoner = Owner;
                    req.Index = i;
                    req.Count = n;
                    req.Level = Owner.Level + CombatConfig.BOSS_SUMMON_LEVEL_OFFSET;
                    if (req.Level < CombatConfig.ENEMY_LEVEL_MIN)
                    {
                        req.Level = CombatConfig.ENEMY_LEVEL_MIN;
                    }
                    // 均匀分布在 BOSS 周围一圈，避免全部叠在同一点。
                    float deg = 360.0f * i / (n > 0 ? n : 1);
                    req.Position = Owner.Position + Vec2.Right.RotatedDeg(deg) * CombatConfig.AI_STRIKE_DIST;

                    Combatant minion = Spawn(req);
                    if (minion != null)
                    {
                        PendingSpawns.Add(minion);
                    }
                }
            }

            ICombatEvents ev = Events ?? NullCombatEvents.Instance;
            ev.OnSummon(n);
        }

        /// <summary>
        /// P3 周期技能：每 6.0s 释放半径 200 的冲击波。
        /// 命中判定不在这里做——本类只负责"什么时候放"，
        /// "打不打得到玩家"由 <see cref="Encounter"/> 交给 <see cref="DamageResolver"/> 纯几何裁决。
        /// </summary>
        private void RunP3Abilities(float dt)
        {
            ShockCd -= dt;
            if (ShockCd > 0.0f)
            {
                return;
            }

            // D-4：同 RunP2Abilities，进位式复位以消除逐周期累加的节拍漂移。
            ShockCd += CombatConfig.BOSS_P3_SHOCK_CD;
            if (ShockCd <= 0.0f)
            {
                ShockCd = CombatConfig.BOSS_P3_SHOCK_CD;
            }

            ShockwavePending = true;
            ShockwaveCenter = Owner != null ? Owner.Position : Vec2.Zero;
            ShockwaveRadius = CombatConfig.BOSS_P3_SHOCK_RADIUS;

            ICombatEvents ev = Events ?? NullCombatEvents.Instance;
            ev.OnShockwave(ShockwaveRadius, ShockwaveCenter);
        }

        /// <summary>消费本步的冲击波标记（由 Encounter 调用，保证一步只结算一次）。</summary>
        public bool ConsumeShockwave(out Vec2 center, out float radius)
        {
            center = ShockwaveCenter;
            radius = ShockwaveRadius;
            if (!ShockwavePending)
            {
                return false;
            }
            ShockwavePending = false;
            return true;
        }

        /// <inheritdoc />
        public override void Reset()
        {
            base.Reset();
            Phase = BossPhase.P1;
            SummonCd = 0.0f;
            ShockCd = 0.0f;
            AtkMult = 1.0f;
            SpeedMult = 1.0f;
            ShockwavePending = false;
            PendingSpawns.Clear();
        }
    }
}
