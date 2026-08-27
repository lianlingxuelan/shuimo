// -----------------------------------------------------------------------------
// MoralityPersonalityConfig.cs —— 正魔 / 性格 的可调配置（ScriptableObject 覆写层）
// （asmdef: Xianxia.Unity.T2.Morality）
//
// 【它解决什么】
// 纯逻辑内核 Xianxia.Morality / Xianxia.Personality 不能引用 UnityEngine，
// 所以它们的数值住在普通类里。本 ScriptableObject 住在 Unity 侧，把编辑器里
// 调好的数通过 ToMoralityConfig() / ToPersonalityConfig() 写进内核——这是
// 「Unity 侧覆写内核常量」的标准做法（参见 CombatConfig.cs 的注释）。
//
// 【内容决策权归用户】这是你唯一需要碰的文件：调数值、改阈值，无需动内核逻辑。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Morality;
using Xianxia.Personality;

namespace Xianxia.Unity.T2.Morality
{
    /// <summary>正魔 / 性格 系统的可调参数表。挂到 MoralityBridge 上即可生效。</summary>
    [CreateAssetMenu(menuName = "Shuimo/正魔性格配置", fileName = "MoralityPersonalityConfig")]
    public sealed class MoralityPersonalityConfig : ScriptableObject
    {
        // ---- 正魔：行动增量（此消彼长）----
        public float RestrainedHitZheng = 1.5f;
        public float RestrainedHitMo = -0.5f;
        public float HeavyKillZheng = -4.0f;
        public float HeavyKillMo = 8.0f;
        public float DrainZheng = -6.0f;
        public float DrainMo = 12.0f;
        public float RighteousActZheng = 3.0f;
        public float RighteousActMo = -1.0f;

        // ---- 正魔：路线 / 入魔 ----
        public float XiaBand = 30f;
        public float EnchantThreshold = 100f;

        // ---- 正魔：反噬 ----
        public float BacklashBaseChance = 0.05f;
        public float BacklashChancePerMo = 0.006f;
        public float BacklashChanceMax = 0.85f;
        public float BacklashDmgBaseFrac = 0.04f;
        public float BacklashDmgPerMoFrac = 0.12f;

        // ---- 正魔：防御侧落地 ----
        public float DefenseBonusMax = 10f;

        // ---- 性格：玩出来 ----
        public float RestrainedHit_Wuxing = 0.3f;
        public float HeavyKill_Chongdong = 0.6f;
        public float HeavyKill_Yinren = -0.3f;
        public float Kill_Lengjing = 0.2f;
        public float Drain_Tanlan = 1.0f;
        public float Pickup_Tanlan = 0.2f;

        // ---- 性格：战斗修正 ----
        public float ChongdongFirstStrike = 0.40f;
        public float YinrenCounter = 0.50f;
        public float LengjingHitRate = 0.30f;
        public float TanlanPickupMul = 0.50f;
        public float TanlanBacklashExtra = 0.30f;

        // ---- 性格 × 正魔联动 ----
        public float EnchantRiskWuxingWeight = 1.0f;
        public float EnchantRiskMoWeight = 1.0f;

        /// <summary>把数值写进纯逻辑正魔配置。</summary>
        public MoralityConfig ToMoralityConfig()
        {
            return new MoralityConfig
            {
                RestrainedHitZheng = RestrainedHitZheng,
                RestrainedHitMo = RestrainedHitMo,
                HeavyKillZheng = HeavyKillZheng,
                HeavyKillMo = HeavyKillMo,
                DrainZheng = DrainZheng,
                DrainMo = DrainMo,
                RighteousActZheng = RighteousActZheng,
                RighteousActMo = RighteousActMo,
                XiaBand = XiaBand,
                EnchantThreshold = EnchantThreshold,
                BacklashBaseChance = BacklashBaseChance,
                BacklashChancePerMo = BacklashChancePerMo,
                BacklashChanceMax = BacklashChanceMax,
                BacklashDmgBaseFrac = BacklashDmgBaseFrac,
                BacklashDmgPerMoFrac = BacklashDmgPerMoFrac,
                DefenseBonusMax = DefenseBonusMax
            };
        }

        /// <summary>把数值写进纯逻辑性格配置。</summary>
        public PersonalityConfig ToPersonalityConfig()
        {
            return new PersonalityConfig
            {
                RestrainedHit_Wuxing = RestrainedHit_Wuxing,
                HeavyKill_Chongdong = HeavyKill_Chongdong,
                HeavyKill_Yinren = HeavyKill_Yinren,
                Kill_Lengjing = Kill_Lengjing,
                Drain_Tanlan = Drain_Tanlan,
                Pickup_Tanlan = Pickup_Tanlan,
                ChongdongFirstStrike = ChongdongFirstStrike,
                YinrenCounter = YinrenCounter,
                LengjingHitRate = LengjingHitRate,
                TanlanPickupMul = TanlanPickupMul,
                TanlanBacklashExtra = TanlanBacklashExtra,
                EnchantRiskWuxingWeight = EnchantRiskWuxingWeight,
                EnchantRiskMoWeight = EnchantRiskMoWeight
            };
        }
    }
}
