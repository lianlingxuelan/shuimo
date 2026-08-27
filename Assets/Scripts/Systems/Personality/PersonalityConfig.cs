// -----------------------------------------------------------------------------
// PersonalityConfig.cs —— 性格系统的可调参数（引擎无关，asmdef: Xianxia.Personality）
//
// 【它是什么】
// 蓝图 1.5 的全部数值旋钮。纯数据类，不引用 UnityEngine，供 Unity 侧 ScriptableObject
// 覆写层把数写进来。
//
// 【内容决策权归用户】默认值是按蓝图「五性格玩出来」方向给的占位，你后续调。
// -----------------------------------------------------------------------------

namespace Xianxia.Personality
{
    /// <summary>性格系统的全部常量。蓝图 1.5 的数值真源。</summary>
    public sealed class PersonalityConfig
    {
        // =====================================================================
        // 量程
        // =====================================================================
        /// <summary>每个性格维度的上限。</summary>
        public float Max = 100f;

        // =====================================================================
        // 玩出来：行动增量（蓝图 1.5「性格值通过选择变化，不是开局选的」）
        // =====================================================================
        /// <summary>克己普攻 → 悟性+（静心）。</summary>
        public float RestrainedHit_Wuxing = 0.3f;

        /// <summary>杀招 / 重击击杀 → 冲动+。</summary>
        public float HeavyKill_Chongdong = 0.6f;

        /// <summary>杀招 / 重击击杀 → 隐忍-（与冲动相反）。</summary>
        public float HeavyKill_Yinren = -0.3f;

        /// <summary>任意击杀 → 冷静+。</summary>
        public float Kill_Lengjing = 0.2f;

        /// <summary>吸功（魔道技能）→ 贪婪++。</summary>
        public float Drain_Tanlan = 1.0f;

        /// <summary>拾取 → 贪婪+。</summary>
        public float Pickup_Tanlan = 0.2f;

        // =====================================================================
        // 性格 → 战斗修正系数（蓝图 1.5「性格影响战斗」）
        // =====================================================================
        /// <summary>冲动 → 先手攻 / 伤害加成上限。</summary>
        public float ChongdongFirstStrike = 0.40f;

        /// <summary>隐忍 → 受击蓄力反击倍率上限。</summary>
        public float YinrenCounter = 0.50f;

        /// <summary>冷静 → 命中 / 抗幻加成上限。</summary>
        public float LengjingHitRate = 0.30f;

        /// <summary>贪婪 → 拾取数量倍率上限。</summary>
        public float TanlanPickupMul = 0.50f;

        /// <summary>贪婪 → 额外反噬系数上限。</summary>
        public float TanlanBacklashExtra = 0.30f;

        // =====================================================================
        // 性格 × 正魔值联动（蓝图 1.5「悟性低更容易被骗入魔」）
        // =====================================================================
        /// <summary>悟性缺位对入魔风险的权重。</summary>
        public float EnchantRiskWuxingWeight = 1.0f;

        /// <summary>魔道值对入魔风险的权重。</summary>
        public float EnchantRiskMoWeight = 1.0f;
    }
}
