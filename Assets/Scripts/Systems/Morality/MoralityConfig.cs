// -----------------------------------------------------------------------------
// MoralityConfig.cs —— 正魔值系统的可调参数（引擎无关，asmdef: Xianxia.Morality）
//
// 【它是什么】
// 纯数据类，承载蓝图 1.4 的全部数值旋钮。Unity 侧用 ScriptableObject 覆写层
// （见 Xianxia.Unity.T2.Morality 的 MoralityPersonalityConfig）把数写进来，
// 但本类本身绝不引用 UnityEngine——保证内核可 headless 编译与对拍。
//
// 【内容决策权归用户】
// 这些默认值是我按蓝图「正道稳 / 魔道涨 / 入魔满」方向给的一套能跑的占位，
// 你后续在编辑器里调，我不替你定内容方向。
// -----------------------------------------------------------------------------

namespace Xianxia.Morality
{
    /// <summary>正魔值系统的全部常量。蓝图 1.4 的数值真源。</summary>
    public sealed class MoralityConfig
    {
        // =====================================================================
        // 量程
        // =====================================================================
        /// <summary>正道值上限。</summary>
        public float ZhengMax = 100f;

        /// <summary>魔道值上限。</summary>
        public float MoMax = 100f;

        // =====================================================================
        // 行动增量（此消彼长：多数行动一升一降，并非严格零和）
        // 蓝图 1.4「克己 vs 拔刀」：
        //   普通攻击 + 留手 = 正道稳（伤害低，不涨魔道）
        //   杀招 / 重击 + 击杀 = 魔道涨（伤害高，魔道值+）
        // =====================================================================
        /// <summary>普攻留手（克己）：正道+，魔道轻微回落。</summary>
        public float RestrainedHitZheng = 1.5f;

        /// <summary>普攻留手（克己）：魔道轻微回落。</summary>
        public float RestrainedHitMo = -0.5f;

        /// <summary>杀招 / 重击 + 击杀：正道-。</summary>
        public float HeavyKillZheng = -4.0f;

        /// <summary>杀招 / 重击 + 击杀：魔道大幅+。</summary>
        public float HeavyKillMo = 8.0f;

        /// <summary>吸功（魔道技能）：正道-。</summary>
        public float DrainZheng = -6.0f;

        /// <summary>吸功（魔道技能）：魔道大幅+。</summary>
        public float DrainMo = 12.0f;

        /// <summary>点化 / 放过（正道善举）：正道+，魔道轻微回落。</summary>
        public float RighteousActZheng = 3.0f;

        /// <summary>点化 / 放过（正道善举）：魔道轻微回落。</summary>
        public float RighteousActMo = -1.0f;

        // =====================================================================
        // 路线 / 入魔阈值
        // Balance = Mo − Zheng；|Balance| 落在 ±XiaBand 内即"侠道"中间地带。
        // =====================================================================
        /// <summary>侠道带宽：|Balance| &lt; 该值 → 侠道路线。</summary>
        public float XiaBand = 30f;

        /// <summary>入魔阈值：魔道值 ≥ 该值 → 彻底入魔（反噬消失）。</summary>
        public float EnchantThreshold = 100f;

        // =====================================================================
        // 魔道反噬（蓝图 1.4「魔道反噬机制」）
        //   每次用魔道技能 / 杀招，有概率反噬（掉血）；
        //   魔道值越高，概率与伤害越高；魔道满 → 入魔 → 反噬消失。
        // =====================================================================
        /// <summary>反噬基础概率（魔道=0 时仍有微乎其微的可能）。</summary>
        public float BacklashBaseChance = 0.05f;

        /// <summary>每点魔道值追加的反噬概率。</summary>
        public float BacklashChancePerMo = 0.006f;

        /// <summary>反噬概率上限（避免必反噬碾压）。</summary>
        public float BacklashChanceMax = 0.85f;

        /// <summary>反噬基础伤害占最大生命比例。</summary>
        public float BacklashDmgBaseFrac = 0.04f;

        /// <summary>满魔道时额外追加的反噬伤害比例。</summary>
        public float BacklashDmgPerMoFrac = 0.12f;

        // =====================================================================
        // 防御侧落地（蓝图 1.4「正道高→防御高」）
        // 玩家 Armor 是合法承伤路径（玩家攻击用 AttackController.AttackRaw，不走
        // Combatant.Atk，故攻击倍率另需武器/技能系统接入——见 changelog 阶段81）。
        // 这里把"正道高→防高"实现为对玩家 Armor 的加法护甲：正道满 → +DefenseBonusMax。
        // 数值保守，请按手感在配置里调。
        // =====================================================================
        /// <summary>正道满时给玩家的加法护甲（魔道高时自然趋近于 0）。</summary>
        public float DefenseBonusMax = 10f;
    }
}
