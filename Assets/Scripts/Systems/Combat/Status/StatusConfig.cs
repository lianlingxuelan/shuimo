// -----------------------------------------------------------------------------
// StatusConfig.cs —— T3 状态效果常量的唯一集中地 + 默认状态表构造
//
// 【与 SkillConfig 同一原则】新常量一律不进 CombatConfig.cs（架构 §7.2）。
// 所有时长以 int 帧定义，注释标注对应秒数（@60Hz），运行时不做换算。
//
// 【P0 首批 4 种刻意全部不含行为禁制】（PRD P0-06 · 架构 §7.4-3）
// 灼烧 / 蛊毒 / 迟滞 / 破防，全部只由**玩家施加给敌人**。玩家身上恒无在体状态。
// 这条约束让 U1 平衡口径的回归面为零：新增逻辑一步都不进入 W-CORE 承伤链路。
// 硬控类（冰冻/麻痹/封技）留 P1-04，引入即需重跑平衡回归。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：四种 Debuff 的具体数值都在这儿，改效果强弱只改这个文件。
//
// · 本期四种 Debuff 一览
//     se_burn      灼烧 —— 每 0.5 秒掉 4 点血，持续 4 秒。重复命中只刷新时间。
//                          （本期没有技能会挂它，先把框架和数据备好，供测试与 P1 使用）
//     se_poison    蛊毒 —— 每 0.6 秒掉 3 点血/层，持续 5 秒，最多叠 5 层。
//                          血莲侵蚀 100% 附着。层数越高掉血越快。
//     se_slow      迟滞 —— 移速 −30%，持续 3 秒。（本期同样只备数据）
//     se_break_def 破防 —— 护甲 −40%，持续 1.5 秒。法阵冲击附着。
//                          护甲低了，你后续每一刀都打得更疼 —— 这就是"破甲起手"打法。
//
// · 为什么"只有玩家能给敌人上 Debuff"？
//   因为一旦敌人也能给玩家上 Debuff，玩家的掉血速度就变了，
//   而"玩家多久会死"是我们已经用 64 组自动测试锁死的核心数值（单挑 38.2 秒、
//   围攻 15.1 秒）。本期严格限制成单向，这些测试就一条都不用改。
//   等下一期真要做"敌人技能化"时，必须重新跑一遍全部平衡测试。
//
// · TintR/G/B 是什么？
//   Debuff 图标的颜色（红/绿/蓝三个分量，每个取 0~1）。
//   本项目不用任何美术素材，所有图标都是代码画出来的纯色块，靠颜色区分：
//   灼烧偏红、蛊毒偏绿、迟滞偏蓝、破防偏紫。
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// T3 状态效果的全部常量，以及默认状态表的构造。
    ///
    /// 【新手解释】和 SkillConfig 是一对兄弟：那个管技能数值，这个管 Debuff 数值。
    /// </summary>
    public static class StatusConfig
    {
        // =====================================================================
        // 状态 id
        // =====================================================================

        /// <summary>灼烧（DOT，刷新型）。</summary>
        public const string SE_BURN = "se_burn";

        /// <summary>蛊毒（DOT，可叠 5 层）。</summary>
        public const string SE_POISON = "se_poison";

        /// <summary>迟滞（移速 −30%）。</summary>
        public const string SE_SLOW = "se_slow";

        /// <summary>破防（护甲 −40%）。</summary>
        public const string SE_BREAK_DEF = "se_break_def";

        // =====================================================================
        // 灼烧 se_burn
        // =====================================================================

        /// <summary>灼烧持续 240 帧 = 4.0s。</summary>
        public const int BURN_DURATION_FRAMES = 240;

        /// <summary>灼烧跳伤间隔 30 帧 = 0.5s（整段共跳 8 次）。</summary>
        public const int BURN_TICK_FRAMES = 30;

        /// <summary>
        /// 灼烧单跳伤害。
        /// ⚠️ 对应新增待明确项 N8：PRD 未给 DOT 具体数值，本值为占位。
        /// </summary>
        public const float BURN_TICK_DAMAGE = 4.0f;

        // =====================================================================
        // 蛊毒 se_poison
        // =====================================================================

        /// <summary>蛊毒持续 300 帧 = 5.0s。</summary>
        public const int POISON_DURATION_FRAMES = 300;

        /// <summary>蛊毒跳伤间隔 36 帧 = 0.6s（整段共跳 8 次）。</summary>
        public const int POISON_TICK_FRAMES = 36;

        /// <summary>
        /// 蛊毒单层单跳伤害。总跳伤 = 本值 × 层数。
        /// ⚠️ 对应新增待明确项 N8（占位值）。
        /// </summary>
        public const float POISON_TICK_DAMAGE = 3.0f;

        /// <summary>蛊毒最大层数（PRD §4.4：可叠 5 层）。</summary>
        public const int POISON_MAX_STACKS = 5;

        // =====================================================================
        // 迟滞 se_slow
        // =====================================================================

        /// <summary>迟滞持续 180 帧 = 3.0s。</summary>
        public const int SLOW_DURATION_FRAMES = 180;

        /// <summary>迟滞移速修饰：−0.30 表示 ×(1 − 0.30) = ×0.70。</summary>
        public const float SLOW_SPEED_MULT_DELTA = -0.30f;

        // =====================================================================
        // 破防 se_break_def
        // =====================================================================

        /// <summary>
        /// 破防持续 90 帧 = 1.5s。
        /// **对应待明确项 N4**：架构建议「100% 附着 + 时长减半（3.0s → 1.5s）」，
        /// 本实现按建议取 1.5s。若 PM 拍回「60% + 3.0s」，
        /// 改本常量为 180 且把 <see cref="SkillConfig.BURST_BREAK_DEF_CHANCE"/> 改回 0.6f 即可。
        /// </summary>
        public const int BREAK_DEF_DURATION_FRAMES = 90;

        /// <summary>破防护甲修饰：−0.40 表示 ×(1 − 0.40) = ×0.60。</summary>
        public const float BREAK_DEF_ARMOR_MULT_DELTA = -0.40f;

        // =====================================================================
        // 构表
        // =====================================================================

        /// <summary>
        /// 构造默认状态表（首批 4 种 Debuff）。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"把上面那堆数字组装成 4 张 Debuff 说明书"。
        /// 什么时候被调用：游戏初始化时，和 SkillConfig.BuildDefaultTable() 一起调，各调一次。
        /// 组装好的表会被塞进每个角色的 StatusComponent（Debuff 收纳盒）里，
        /// 之后技能命中时就能按 id 从表里查到"蛊毒到底该怎么算"。
        /// </remarks>
        /// <returns>已注册全部 4 种状态的状态表。</returns>
        public static StatusTable BuildDefaultTable()
        {
            StatusTable table = new StatusTable();

            // ---- 灼烧：DOT，刷新型 ----
            StatusEffectDef burn = new StatusEffectDef();
            burn.Id = SE_BURN;
            burn.DisplayName = "灼烧";
            burn.Kind = StatusKind.Dot;
            burn.DurationFrames = BURN_DURATION_FRAMES;
            burn.Rule = StackRule.Refresh;
            burn.MaxStacks = 1;
            burn.TickIntervalFrames = BURN_TICK_FRAMES;
            burn.TickDamage = BURN_TICK_DAMAGE;
            burn.TintR = 1.0f;
            burn.TintG = 0.45f;
            burn.TintB = 0.20f;
            table.Register(burn);

            // ---- 蛊毒：DOT，可叠 5 层 ----
            StatusEffectDef poison = new StatusEffectDef();
            poison.Id = SE_POISON;
            poison.DisplayName = "蛊毒";
            poison.Kind = StatusKind.Dot;
            poison.DurationFrames = POISON_DURATION_FRAMES;
            poison.Rule = StackRule.Stack;
            poison.MaxStacks = POISON_MAX_STACKS;
            poison.TickIntervalFrames = POISON_TICK_FRAMES;
            poison.TickDamage = POISON_TICK_DAMAGE;
            poison.TintR = 0.35f;
            poison.TintG = 0.85f;
            poison.TintB = 0.35f;
            table.Register(poison);

            // ---- 迟滞：属性修饰，移速 −30% ----
            StatusEffectDef slow = new StatusEffectDef();
            slow.Id = SE_SLOW;
            slow.DisplayName = "迟滞";
            slow.Kind = StatusKind.StatMod;
            slow.DurationFrames = SLOW_DURATION_FRAMES;
            slow.Rule = StackRule.Refresh;
            slow.MaxStacks = 1;
            slow.TickIntervalFrames = 0;
            slow.TickDamage = 0.0f;
            slow.Modifiers = new StatModifier[]
            {
                new StatModifier(StatId.MoveSpeed, true, SLOW_SPEED_MULT_DELTA)
            };
            slow.TintR = 0.40f;
            slow.TintG = 0.65f;
            slow.TintB = 1.0f;
            table.Register(slow);

            // ---- 破防：属性修饰，护甲 −40% ----
            StatusEffectDef breakDef = new StatusEffectDef();
            breakDef.Id = SE_BREAK_DEF;
            breakDef.DisplayName = "破防";
            breakDef.Kind = StatusKind.StatMod;
            breakDef.DurationFrames = BREAK_DEF_DURATION_FRAMES;
            breakDef.Rule = StackRule.Refresh;
            breakDef.MaxStacks = 1;
            breakDef.TickIntervalFrames = 0;
            breakDef.TickDamage = 0.0f;
            breakDef.Modifiers = new StatModifier[]
            {
                new StatModifier(StatId.Armor, true, BREAK_DEF_ARMOR_MULT_DELTA)
            };
            breakDef.TintR = 0.75f;
            breakDef.TintG = 0.40f;
            breakDef.TintB = 0.95f;
            table.Register(breakDef);

            return table;
        }
    }
}
