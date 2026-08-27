// -----------------------------------------------------------------------------
// PersonalityTypes.cs —— 性格系统的共享类型（引擎无关，asmdef: Xianxia.Personality）
//
// 【它是什么】
// 蓝图阶段 1.5「性格属性系统」的纯数据载体：
//   · PersonalityTrait —— 五大性格维度
//   · PersonalityCombatModifiers —— 性格换算出的战斗系数
// 全部 readonly struct / enum，零方法、零状态。
//
// 【禁止事项】不得引用 UnityEngine；本程序集 noEngineReferences=true。
// -----------------------------------------------------------------------------

namespace Xianxia.Personality
{
    /// <summary>五大性格维度（蓝图 1.5）。值越高表示该项特质越强。</summary>
    public enum PersonalityTrait
    {
        /// <summary>悟性：学技快、易识破破绽、抗骗入魔。</summary>
        Wuxing,

        /// <summary>冲动：先手攻击加成，但忍不住拔刀。</summary>
        Chongdong,

        /// <summary>隐忍：受击蓄力反击，持久战越强。</summary>
        Yinren,

        /// <summary>冷静：命中率高、不易中幻术。</summary>
        Lengjing,

        /// <summary>贪婪：拾取更多，但反噬更高、易惑。</summary>
        Tanlan
    }

    /// <summary>性格换算出的战斗系数（蓝图 1.5「性格影响战斗」）。</summary>
    public readonly struct PersonalityCombatModifiers
    {
        /// <summary>先手加成（冲动）。作用于起手攻速 / 首击伤害。</summary>
        public readonly float FirstStrike;

        /// <summary>受击蓄力反击倍率（隐忍）。</summary>
        public readonly float CounterMul;

        /// <summary>命中率 / 抗幻加成（冷静）。</summary>
        public readonly float HitRateBonus;

        /// <summary>拾取数量倍率（贪婪）。</summary>
        public readonly float PickupMul;

        /// <summary>额外反噬系数（贪婪）。叠加到正魔反噬概率上。</summary>
        public readonly float BacklashExtra;

        public PersonalityCombatModifiers(float firstStrike, float counterMul, float hitRateBonus, float pickupMul, float backlashExtra)
        {
            FirstStrike = firstStrike;
            CounterMul = counterMul;
            HitRateBonus = hitRateBonus;
            PickupMul = pickupMul;
            BacklashExtra = backlashExtra;
        }
    }
}
