// -----------------------------------------------------------------------------
// RealmTypes.cs —— 境界系统的「数据类型」（纯数据，不含行为）
//
// 【它解决什么问题】
// 蓝图第4周「角色养成」要落地「境界/修为」。但境界名字（炼气/筑基/金丹…）
// 属于**内容决策**，按你 08-18 的边界「内容(物品/技能/数值)暂不定」，
// 所以本文件**只定义结构，不写死任何内容**：
//   · 层数、门槛、是否需要突破 —— 都是「结构」。
//   · 具体叫什么名字、门槛数值多少 —— 都是可填的「配置」，留给你。
//
// 【类比前端，帮你建立直觉】
//   这份配置就像一份「路由表 + i18n 文案」：
//   - Thresholds 像每个 section 的 scrollTop 阈值，决定当前高亮哪一层；
//   - Names 像 i18n 的 locale 文案，逻辑只认索引、不认字；
//   - NeedsBreakthrough 像「路由守卫 beforeEnter」——到了门槛但没 confirm
//     就停在门外（这就是蓝图深潜里说的「双层 gate」）。
//
// 【红线】本文件及 RealmSystem 均为纯逻辑、noEngineReferences，
//   可 headless 跑 NUnit；绝不引用 Combatant / 改玩家血攻（数值红线）。
// -----------------------------------------------------------------------------

namespace Xianxia.Cultivation
{
    /// <summary>
    /// 境界配置——纯数据。你后续在这里填内容（名字/数值），逻辑代码不变。
    /// </summary>
    public sealed class RealmConfig
    {
        /// <summary>每一层的显示名。占位为「境界N」，你替换成炼气/筑基/… 即可。</summary>
        public string[] Names = { "境界一", "境界二", "境界三", "境界四", "境界五" };

        /// <summary>
        /// 进入第 i 层所需的「累计修炼点」门槛。Thresholds[0] 应为 0（出生即第 0 层）。
        /// 长度应与 Names 一致。类比：路由表里每个 section 的 scrollTop 阈值。
        /// </summary>
        public float[] Thresholds = { 0f, 100f, 300f, 700f, 1500f };

        /// <summary>
        /// 第 i 层是否「需突破」（双层 gate 的第二层）。
        /// 到门槛只是「可突破」，还需一次突破事件（确认/剧情）才真正踏入。
        /// 类比前端：路由守卫 beforeEnter——到了门槛但没 confirm 就停在门外。
        /// </summary>
        public bool[] NeedsBreakthrough = { false, false, true, false, true };

        public int TierCount => Names != null ? Names.Length : 0;
    }

    /// <summary>境界系统的运行时状态——随修炼累积而变化。</summary>
    public sealed class RealmState
    {
        public int TierIndex;             // 当前所在层（索引）
        public float Accumulated;         // 累计修炼点
        public bool BreakthroughPending;  // 已到门槛、等待突破事件
        public bool[] BrokenThrough;      // 各层是否已完成突破（仅对 NeedsBreakthrough 层有意义）
    }
}
