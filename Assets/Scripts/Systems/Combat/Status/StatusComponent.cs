// -----------------------------------------------------------------------------
// StatusComponent.cs —— 每单位的状态容器 + 修饰聚合（引擎无关）
//
// 【为什么 tick 与结算要分离】（架构 §1.6c 的四个顺序决策之一）
// TickFrame 只做两件事：递减计时/层数，把到期的跳伤压进 outDots 缓冲。
// **它绝不扣血**。真正扣血放在 Encounter 的 ⑤-A 阶段统一结算。
// 理由是硬的：如果在 ③-A（敌人循环内）直接扣血，敌人可能在 ③ 的中途死亡，
// 从而改变它在 ⑤ 是否造成接触伤害 —— 那就动了 U1 的承伤链路。
// 分离之后，DOT 只影响「玩家→敌人」方向，⑤ 看到的存活集合与 T2 完全一致。
//
// 【为什么聚合值要 _dirty 缓存而不是每帧重算】
// EnemyAI.EffectiveSpeed 是 60Hz × N 敌人的热路径。状态增删是低频事件，
// 属性查询是高频事件 —— 典型的"写少读多"，缓存 + 脏标记是唯一合理解。
//
// 【为什么用 List 而不是 Dictionary】（§7.3-5 确定性红线）
// 状态的遍历顺序直接决定 DOT 的结算顺序，进而决定谁先死。Dictionary 的遍历顺序
// 在不同 .NET 运行时/不同插入历史下可能不同，那会让同种子 diff 随机失败。
// List 的顺序是插入序，确定、可复现、可在日志里逐条对上。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：每个角色身上挂一个这个东西，用来管理"我身上现在有哪些 Debuff"。
//
// · 它每帧干三件事（TickFrame）：
//     ① 到点了就产出一次"掉血待办"（注意：只是登记待办，**不当场扣血**）
//     ② 所有 Debuff 的剩余时间各减 1 帧
//     ③ 时间归零的 Debuff 从列表里删掉，并通知 UI 把图标去掉
//
// · 为什么"登记待办"而不是当场扣血？这是本文件最重要的设计。
//   战斗的每一帧是有严格顺序的，简化说是：
//       ③ 敌人行动 → ⑤ 敌人碰到你造成接触伤害 → ⑥ 清理尸体
//   Debuff 的时间递减发生在 ③ 里面。如果在 ③ 就把怪毒死了，
//   那么这只怪在 ⑤ 就不会碰到你、不会对你造成伤害 —— 玩家挨打的节奏被改变了。
//   而"玩家挨打的节奏"恰恰是我们已经用 64 组自动测试锁死的核心数值（U1 基线）。
//   所以我们把扣血统一挪到 ⑤ 之后（⑤-A 阶段）：
//   这样 ⑤ 看到的存活敌人名单和加 Debuff 系统之前一模一样，
//   新功能只影响"玩家打怪"这个方向，绝不反向影响"怪打玩家"。
//   —— 这种"先登记、后统一结算"的模式，是所有需要保证时序正确的系统的通用解法。
//
// · 为什么属性聚合要缓存（_dirty 脏标记）？
//   "这只怪现在移速多少"这个问题，每秒要被问 60 次 × 场上怪的数量。
//   但"这只怪身上的 Debuff 变了"这件事可能十几秒才发生一次。
//   典型的"读得多、写得少"，所以：只在真正变化时才重算，
//   平时直接返回上次算好的结果。_dirty = true 就是"下次记得重算"的小旗子。
//
// · 为什么用 List 而不是 Dictionary（字典）？
//   因为 Debuff 的遍历顺序会决定伤害的结算顺序，进而决定哪只怪先死。
//   List 的顺序永远是插入顺序，稳定可复现；
//   Dictionary 的遍历顺序在不同电脑、不同插入历史下可能不同，
//   那会让"同一个随机种子跑出同样结果"这条铁律失效。
// =============================================================================
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Xianxia.Combat
{
    /// <summary>
    /// 一条待结算的 DOT 跳伤。由 <see cref="StatusComponent.TickFrame"/> 产出，
    /// 由 <see cref="Encounter"/> 的 ⑤-A 阶段交给
    /// <see cref="DamageResolver.ApplyDotTick"/> 统一落地。
    ///
    /// 【新手解释】一张"掉血待办便签"：写着"该扣谁、扣多少、是谁下的毒"。
    /// 中毒的怪每到跳伤时机就生成一张，攒够一批后在固定的时机统一执行。
    /// </summary>
    public struct DotTick
    {
        /// <summary>承伤目标。</summary>
        public Combatant Target;

        /// <summary>本跳伤害（已含层数与威力缩放）。</summary>
        public float Damage;

        /// <summary>施加者快照。</summary>
        public SourceSnapshot Source;

        /// <summary>来源状态 id（用于事件与日志）。</summary>
        public string StatusId;

        /// <summary>构造。</summary>
        /// <param name="target">目标。</param>
        /// <param name="damage">伤害。</param>
        /// <param name="source">施加者快照。</param>
        /// <param name="statusId">状态 id。</param>
        public DotTick(Combatant target, float damage, SourceSnapshot source, string statusId)
        {
            Target = target;
            Damage = damage;
            Source = source;
            StatusId = statusId;
        }
    }

    /// <summary>
    /// 每单位一个的状态容器。**可空组件**：未挂载时
    /// <see cref="Encounter"/> 的 ③-A 阶段直接跳过，
    /// <see cref="Combatant.EffectiveArmor"/> 与 <see cref="EnemyAI.EffectiveSpeed"/>
    /// 全部短路回基准值。
    ///
    /// 【新手解释】角色身上的"Debuff 收纳盒"。里面装着若干张 ActiveStatus。
    /// 没装这个盒子的角色（比如跑老测试时的怪），
    /// 所有查询都会走"短路"直接返回原始数值，整套 Debuff 系统对它完全不存在 ——
    /// 这就是我们敢在已验收的战斗内核上加新功能的底气。
    /// </summary>
    public sealed class StatusComponent
    {
        private readonly List<ActiveStatus> _active = new List<ActiveStatus>(4);
        private readonly List<ActiveStatus> _expiredBuffer = new List<ActiveStatus>(4);

        private bool _dirty = true;
        private float _speedMult = 1.0f;
        private float _speedDelta;
        private float _armorMult = 1.0f;
        private float _armorDelta;
        private bool _hasSpeedMod;
        private bool _hasArmorMod;

        /// <summary>状态定义表。装配阶段注入；为 null 时无法附着任何状态。</summary>
        public StatusTable Table;

        /// <summary>当前在体状态数量。</summary>
        public int Count
        {
            get { return _active.Count; }
        }

        /// <summary>按下标取在体状态（HUD 遍历用，顺序稳定）。</summary>
        /// <param name="index">下标。</param>
        /// <returns>在体状态；越界时返回 null。</returns>
        public ActiveStatus At(int index)
        {
            return index >= 0 && index < _active.Count ? _active[index] : null;
        }

        /// <summary>是否存在移速修饰。为 false 时调用方必须短路回基准值。</summary>
        public bool HasSpeedMod
        {
            get
            {
                EnsureAggregate();
                return _hasSpeedMod;
            }
        }

        /// <summary>是否存在护甲修饰。为 false 时调用方必须短路回基准值。</summary>
        public bool HasArmorMod
        {
            get
            {
                EnsureAggregate();
                return _hasArmorMod;
            }
        }

        /// <summary>移速乘区（无修饰时恒为 1.0f）。</summary>
        public float MoveSpeedMult
        {
            get
            {
                EnsureAggregate();
                return _speedMult;
            }
        }

        /// <summary>移速加区（无修饰时恒为 0.0f）。</summary>
        public float MoveSpeedDelta
        {
            get
            {
                EnsureAggregate();
                return _speedDelta;
            }
        }

        /// <summary>护甲乘区（无修饰时恒为 1.0f）。</summary>
        public float ArmorMult
        {
            get
            {
                EnsureAggregate();
                return _armorMult;
            }
        }

        /// <summary>护甲加区（无修饰时恒为 0.0f）。</summary>
        public float ArmorDelta
        {
            get
            {
                EnsureAggregate();
                return _armorDelta;
            }
        }

        /// <summary>
        /// 附着一个状态。已存在同 id 时按 <see cref="StackRule"/> 处理。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"给这个角色挂一个 Debuff"。
        /// 什么时候被调用：技能命中敌人、并且骰子判定要附着状态的那一刻。
        /// 内部逻辑：先看身上有没有同名的 —— 没有就新建一张；
        /// 有的话就按说明书上的规则处理（叠层 or 只刷新时间）。
        /// </remarks>
        /// <param name="def">状态定义（Debuff 说明书）。null 时忽略。</param>
        /// <param name="src">施加者快照（下毒者的能力复印件）。</param>
        /// <param name="ev">事件出口，可为 null。用来通知 UI 加图标。</param>
        /// <returns>true = 本次是**首次附着**（而非叠层/刷新）。</returns>
        public bool Apply(StatusEffectDef def, SourceSnapshot src, ICombatEventsT3 ev)
        {
            if (def == null)
            {
                return false;
            }
            ICombatEventsT3 evt = ev ?? NullCombatEventsT3.Instance;

            ActiveStatus exist = Find(def.Id);
            if (exist == null)
            {
                ActiveStatus st = new ActiveStatus();
                st.Init(def, src, 1);
                _active.Add(st);
                if (def.HasModifiers)
                {
                    _dirty = true;
                }
                evt.OnStatusApplied(null, st);
                return true;
            }

            // 已在体：刷新时长 + 更新快照（新施加者的破防/威力应当接管后续跳伤）
            exist.Def = def;
            exist.Source = src;
            exist.RemainFrames = def.DurationFrames;

            if (def.Rule == StackRule.Stack)
            {
                int max = def.MaxStacks < 1 ? 1 : def.MaxStacks;
                if (exist.Stacks < max)
                {
                    exist.Stacks++;
                    if (def.HasModifiers)
                    {
                        _dirty = true;
                    }
                }
            }
            else
            {
                exist.Stacks = 1;
            }

            evt.OnStatusStackChanged(null, exist);
            return false;
        }

        /// <summary>
        /// 推进一个逻辑帧：产出到期跳伤、递减时长、移除到期状态。
        /// **不扣血**（见文件头"tick 与结算分离"）。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"让身上所有 Debuff 的时间流逝一帧"。每秒被调用 60 次。
        /// 再强调一次：它**只登记待办、不扣血**（原因见文件头新手向导）。
        /// </remarks>
        /// <param name="owner">宿主，也就是"这些 Debuff 挂在谁身上"。</param>
        /// <param name="outDots">跳伤输出缓冲。本方法只往里 Add，
        /// 这个 List 由 Encounter 复用（复用是为了避免每帧新建对象产生内存垃圾）。</param>
        /// <param name="ev">事件出口，可为 null。用来通知 UI 更新/移除图标。</param>
        public void TickFrame(Combatant owner, List<DotTick> outDots, ICombatEventsT3 ev)
        {
            if (_active.Count == 0)
            {
                return;
            }
            ICombatEventsT3 evt = ev ?? NullCombatEventsT3.Instance;

            _expiredBuffer.Clear();

            for (int i = 0; i < _active.Count; i++)
            {
                ActiveStatus st = _active[i];
                StatusEffectDef def = st.Def;
                if (def == null)
                {
                    _expiredBuffer.Add(st);
                    continue;
                }

                // 跳伤先于时长递减：这样"时长恰为跳伤间隔整数倍"的状态能跳满最后一次。
                if (def.IsDot)
                {
                    st.NextTickFrames--;
                    if (st.NextTickFrames <= 0)
                    {
                        st.NextTickFrames = def.TickIntervalFrames;
                        if (outDots != null)
                        {
                            outDots.Add(new DotTick(owner, st.TickDamageNow, st.Source, def.Id));
                        }
                    }
                }

                st.RemainFrames--;
                if (st.RemainFrames <= 0)
                {
                    _expiredBuffer.Add(st);
                }
            }

            if (_expiredBuffer.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _expiredBuffer.Count; i++)
            {
                ActiveStatus st = _expiredBuffer[i];
                _active.Remove(st);
                if (st.Def != null && st.Def.HasModifiers)
                {
                    _dirty = true;
                }
                evt.OnStatusExpired(owner, st.Def != null ? st.Def.Id : string.Empty);
            }
            _expiredBuffer.Clear();
        }

        /// <summary>按 id 查找在体状态。</summary>
        /// <param name="id">状态 id。</param>
        /// <returns>在体状态；不存在时返回 null。</returns>
        public ActiveStatus Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            for (int i = 0; i < _active.Count; i++)
            {
                ActiveStatus st = _active[i];
                if (st.Def != null && st.Def.Id == id)
                {
                    return st;
                }
            }
            return null;
        }

        /// <summary>按 id 取层数。不存在时返回 0。</summary>
        /// <param name="id">状态 id。</param>
        /// <returns>层数。</returns>
        public int StacksOf(string id)
        {
            ActiveStatus st = Find(id);
            return st != null ? st.Stacks : 0;
        }

        /// <summary>
        /// 主动移除一个状态（P1 的驱散 / 控制免疫窗会用到）。
        /// </summary>
        /// <param name="id">状态 id。</param>
        /// <param name="owner">宿主，仅用于事件回传。</param>
        /// <param name="ev">事件出口，可为 null。</param>
        /// <returns>true = 确实移除了一条。</returns>
        public bool Remove(string id, Combatant owner, ICombatEventsT3 ev)
        {
            ActiveStatus st = Find(id);
            if (st == null)
            {
                return false;
            }
            _active.Remove(st);
            if (st.Def != null && st.Def.HasModifiers)
            {
                _dirty = true;
            }
            ICombatEventsT3 evt = ev ?? NullCombatEventsT3.Instance;
            evt.OnStatusExpired(owner, id);
            return true;
        }

        /// <summary>清空全部在体状态（不发事件；用于复位 / 切区）。</summary>
        public void Clear()
        {
            _active.Clear();
            _expiredBuffer.Clear();
            _dirty = true;
            EnsureAggregate();
        }

        // ---------------------------------------------------------------------
        // 修饰聚合
        // ---------------------------------------------------------------------

        /// <summary>
        /// 重算聚合值。只在状态增删 / 叠层时被标脏，因此这段循环的实际执行频率
        /// 远低于 60Hz。**空列表时聚合值精确回到 1.0f / 0.0f，误差恒为 0。**
        /// </summary>
        private void EnsureAggregate()
        {
            if (!_dirty)
            {
                return;
            }
            _dirty = false;

            _speedMult = 1.0f;
            _speedDelta = 0.0f;
            _armorMult = 1.0f;
            _armorDelta = 0.0f;
            _hasSpeedMod = false;
            _hasArmorMod = false;

            for (int i = 0; i < _active.Count; i++)
            {
                ActiveStatus st = _active[i];
                if (st.Def == null || !st.Def.HasModifiers)
                {
                    continue;
                }
                StatModifier[] mods = st.Def.Modifiers;
                int stacks = st.Stacks < 1 ? 1 : st.Stacks;

                for (int m = 0; m < mods.Length; m++)
                {
                    StatModifier mod = mods[m];
                    for (int s = 0; s < stacks; s++)
                    {
                        if (mod.Stat == StatId.MoveSpeed)
                        {
                            _hasSpeedMod = true;
                            if (mod.IsMultiplicative)
                            {
                                _speedMult *= 1.0f + mod.Value;
                            }
                            else
                            {
                                _speedDelta += mod.Value;
                            }
                        }
                        else
                        {
                            _hasArmorMod = true;
                            if (mod.IsMultiplicative)
                            {
                                _armorMult *= 1.0f + mod.Value;
                            }
                            else
                            {
                                _armorDelta += mod.Value;
                            }
                        }
                    }
                }
            }

            if (_speedMult < 0.0f)
            {
                _speedMult = 0.0f;
            }
            if (_armorMult < 0.0f)
            {
                _armorMult = 0.0f;
            }
        }
    }
}
