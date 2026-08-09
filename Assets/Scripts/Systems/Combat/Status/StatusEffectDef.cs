// -----------------------------------------------------------------------------
// StatusEffectDef.cs —— 状态效果的静态定义与状态表（引擎无关）
//
// 【为什么 DOT 与属性修饰要用同一个结构】
// 「灼烧每 0.5s 掉血」与「迟滞移速 −30%」在实现上有 80% 是同一件事：都要挂在单位
// 身上、都要按帧递减、都要在到期时干净地移除、都要有叠层/刷新规则。
// 拆成两个系统的结果是「到期移除」这段逻辑写两遍，然后其中一遍忘了发事件。
// 这里用 StatusKind 区分行为分支，其余全部共用。
//
// 【为什么修饰值不写回基准值】（架构 §1.6e —— 这是 P0-06 验收④的关键）
// se_slow 绝不去改 Combatant.MoveSpeed 的基准值。经典 bug 就在这儿：多个来源叠加后
// 按顺序回滚会产生浮点漂移（v*0.7/0.7 ≠ v），验收要的 <1e-4 会随机失败。
// 正确做法是「基准值只读、修饰按需聚合」：状态到期即从列表移除 → 聚合值自然回到
// 1.0f / 0.0f，**误差恒为 0**（不是 <1e-4，是 0）。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是「中毒 / 灼烧 / 减速 / 破甲」这类状态效果的说明书总目录。
//
// · 什么是 Debuff（负面状态）？
//   打中敌人后额外挂上去的、会持续一段时间的坏效果。本期做四种：
//     se_burn      灼烧 —— 每隔一会儿掉一次血（这类叫 DOT）
//     se_poison    蛊毒 —— 也是掉血，但最多能叠 5 层，层数越高掉得越多
//     se_slow      迟滞 —— 移动速度降低 30%
//     se_break_def 破防 —— 护甲降低 40%，等于后续所有攻击都打得更疼
//
// · 名词解释
//     DOT (Damage over Time) —— 持续伤害。不是一下扣完，而是每隔 N 帧扣一点。
//     叠层 (Stack)   —— 同一个 Debuff 重复命中时层数 +1，效果按层数翻倍。
//     刷新 (Refresh) —— 同一个 Debuff 重复命中时层数不变，只把剩余时间重置回满。
//   这两种规则由 StackRule 决定：蛊毒用叠层（最多 5 层），灼烧用刷新。
//
// · ⚠️ 一个价值百万的经验教训：属性修饰绝不能"改回原值"
//   新手最直觉的写法是：中了减速就 speed = speed * 0.7，状态结束再 speed = speed / 0.7。
//   这在数学上没问题，在计算机里却是灾难：
//   小数在电脑里存的是近似值，乘 0.7 再除 0.7 得到的不是原来那个数，会有微小误差。
//   多来几次、多个来源叠加，速度就会莫名其妙地越来越慢（或越来越快），
//   而且没人能复现 —— 这是业界最经典的疑难 bug 之一。
//   本项目的做法是：**基准速度永远不动**，需要用的时候临时算
//   "基准速度 × 当前所有减速效果的乘积"。状态一到期就从列表里删掉，
//   乘积自然回到 1.0，误差不是"小于 0.0001"，而是**精确等于 0**。
// =============================================================================
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Xianxia.Combat
{
    /// <summary>
    /// 状态大类。决定 <see cref="StatusComponent"/> 走哪条 tick 分支。
    ///
    /// 【新手解释】把状态按"它到底干了什么"分三类：持续掉血、改属性、限制行动。
    /// 本期只做前两类，第三类（冰冻、麻痹这种让你完全动不了的）留到下一期 ——
    /// 因为硬控会大幅改变战斗节奏，加进来就必须把所有平衡数值重测一遍。
    /// </summary>
    public enum StatusKind
    {
        /// <summary>持续伤害。按 TickIntervalFrames 产出跳伤。</summary>
        Dot = 0,

        /// <summary>属性修饰。按 Modifiers 聚合到 MoveSpeed / Armor。</summary>
        StatMod = 1,

        /// <summary>
        /// 行为禁制（冰冻 / 麻痹 / 封技）。
        /// **P0 首批 4 种刻意全部不含 CONTROL** —— 硬控会改变交战节奏，
        /// 引入即需重跑 U1 平衡回归（P1-04）。枚举位先留着，分支 P1 再写。
        /// </summary>
        Control = 2
    }

    /// <summary>
    /// 重复附着时的处理规则。
    ///
    /// 【新手解释】"已经中毒的怪又被下了一次毒，该怎么算？"
    /// 两种答案：叠加层数（毒变浓），或只把时间重置回满（毒不变浓但更久）。
    /// </summary>
    public enum StackRule
    {
        /// <summary>刷新时长，层数恒为 1（灼烧 / 迟滞 / 破防）。</summary>
        Refresh = 0,

        /// <summary>叠加层数（上限 MaxStacks）并刷新时长（蛊毒）。</summary>
        Stack = 1
    }

    /// <summary>可被状态修饰的属性。</summary>
    public enum StatId
    {
        /// <summary>移动速度。</summary>
        MoveSpeed = 0,

        /// <summary>护甲。</summary>
        Armor = 1
    }

    /// <summary>
    /// 一条属性修饰。
    /// <see cref="IsMultiplicative"/> = true 时按 <c>mult *= (1 + Value)</c> 聚合
    /// （所以 −30% 写作 Value = −0.30f）；false 时按 <c>delta += Value</c> 聚合。
    /// 叠层状态每一层各聚合一次。
    /// </summary>
    public struct StatModifier
    {
        /// <summary>被修饰的属性。</summary>
        public StatId Stat;

        /// <summary>true = 乘法修饰（百分比），false = 加法修饰（平坦值）。</summary>
        public bool IsMultiplicative;

        /// <summary>修饰量。乘法时为百分比增量（−0.30 = −30%）。</summary>
        public float Value;

        /// <summary>构造。</summary>
        /// <param name="stat">被修饰属性。</param>
        /// <param name="isMultiplicative">是否乘法修饰。</param>
        /// <param name="value">修饰量。</param>
        public StatModifier(StatId stat, bool isMultiplicative, float value)
        {
            Stat = stat;
            IsMultiplicative = isMultiplicative;
            Value = value;
        }
    }

    /// <summary>
    /// 一个状态效果的静态定义。只读数据。
    ///
    /// 【新手解释】一张"Debuff 说明书"：持续多久、多久掉一次血、一次掉多少、
    /// 最多叠几层、会改哪些属性。和技能说明书一样，它是只读的共享数据，
    /// "某只怪现在中了几层毒、还剩几秒"存在 ActiveStatus 里。
    /// </summary>
    public sealed class StatusEffectDef
    {
        private static readonly StatModifier[] NoModifiers = new StatModifier[0];

        /// <summary>状态 id（唯一）。</summary>
        public string Id = string.Empty;

        /// <summary>中文显示名。</summary>
        public string DisplayName = string.Empty;

        /// <summary>状态大类。</summary>
        public StatusKind Kind = StatusKind.StatMod;

        /// <summary>持续帧数（60Hz）。</summary>
        public int DurationFrames;

        /// <summary>重复附着规则。</summary>
        public StackRule Rule = StackRule.Refresh;

        /// <summary>最大层数。<see cref="StackRule.Refresh"/> 时恒为 1。</summary>
        public int MaxStacks = 1;

        /// <summary>跳伤间隔帧数。0 = 不跳伤。</summary>
        public int TickIntervalFrames;

        /// <summary>单层单跳的伤害。总跳伤 = 本值 × 层数 × 施加者快照的 PowerScale。</summary>
        public float TickDamage;

        /// <summary>属性修饰列表。空数组表示不修饰任何属性。</summary>
        public StatModifier[] Modifiers = NoModifiers;

        /// <summary>HUD 图标的着色提示（RGB，各分量 [0,1]）。零美术资源约束下用纯色块区分。</summary>
        public float TintR = 1.0f;

        /// <summary>图标着色 G 分量。</summary>
        public float TintG = 1.0f;

        /// <summary>图标着色 B 分量。</summary>
        public float TintB = 1.0f;

        /// <summary>持续时长（秒）。仅供 UI 展示。</summary>
        public float DurationSeconds
        {
            get { return DurationFrames * CombatScheduler.FixedStep; }
        }

        /// <summary>是否会产出跳伤。</summary>
        public bool IsDot
        {
            get { return TickIntervalFrames > 0 && TickDamage > 0.0f; }
        }

        /// <summary>是否携带属性修饰。</summary>
        public bool HasModifiers
        {
            get { return Modifiers != null && Modifiers.Length > 0; }
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("[{0} {1} dur={2}f rule={3} max={4}]",
                Id, Kind, DurationFrames, Rule, MaxStacks);
        }
    }

    /// <summary>状态表：id → 定义。</summary>
    public sealed class StatusTable
    {
        private readonly Dictionary<string, StatusEffectDef> _byId = new Dictionary<string, StatusEffectDef>(16);

        /// <summary>已注册的状态数量。</summary>
        public int Count
        {
            get { return _byId.Count; }
        }

        /// <summary>注册一个状态定义。同 id 覆盖。</summary>
        /// <param name="def">状态定义。null 或空 id 时忽略。</param>
        public void Register(StatusEffectDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id))
            {
                return;
            }
            _byId[def.Id] = def;
        }

        /// <summary>批量装载。**T4 的 JSON 驱动接入点**（与 <see cref="SkillTable.LoadFrom"/> 同构）。</summary>
        /// <param name="defs">状态定义集合。null 时忽略。</param>
        public void LoadFrom(IEnumerable<StatusEffectDef> defs)
        {
            if (defs == null)
            {
                return;
            }
            foreach (StatusEffectDef d in defs)
            {
                Register(d);
            }
        }

        /// <summary>按 id 取状态定义。</summary>
        /// <param name="id">状态 id。</param>
        /// <returns>状态定义；不存在时返回 null。</returns>
        public StatusEffectDef Get(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            StatusEffectDef def;
            return _byId.TryGetValue(id, out def) ? def : null;
        }

        /// <summary>清空全表。</summary>
        public void Clear()
        {
            _byId.Clear();
        }
    }
}
