// -----------------------------------------------------------------------------
// BacklashResolver.cs —— 魔道反噬结算（引擎无关，asmdef: Xianxia.Morality）
//
// 【它是什么】
// 蓝图 1.4「魔道反噬机制」的纯函数结算：每次用魔道技能 / 杀招，有概率反噬
// （掉血）；魔道值越高，概率与伤害越高；魔道满（入魔）→ 反噬消失。
//
// 【为什么是静态纯函数 + Func&lt;float&gt; rng】
// 反噬需要随机性，但内核绝不能依赖 UnityEngine.Random（会破坏 headless 对拍）。
// 用 Func&lt;float&gt; 注入随机源：游戏里传 System.Random，测试里传固定值
// （() =&gt; 0f 必触发 / () =&gt; 1f 必不触发），做到判定可复现、可断言。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Morality
{
    /// <summary>魔道反噬结算器。无状态。</summary>
    public static class BacklashResolver
    {
        /// <summary>
        /// 结算一次魔道技能 / 杀招的反噬。
        /// </summary>
        /// <param name="cfg">正魔配置（为 null 直接返回未触发）。</param>
        /// <param name="mo">当前魔道值。</param>
        /// <param name="playerMaxHp">玩家最大生命，用于把比例换算成实际伤害。</param>
        /// <param name="rng">随机源，返回 [0,1)。传 null 视为不触发。</param>
        /// <returns>反噬结果（是否触发 + 伤害）。</returns>
        public static BacklashResult Resolve(MoralityConfig cfg, float mo, float playerMaxHp, Func<float> rng)
        {
            if (cfg == null)
            {
                return new BacklashResult(false, 0f);
            }
            // 彻底入魔 → 反噬消失（但你也不是"你"了）。
            if (mo >= cfg.EnchantThreshold)
            {
                return new BacklashResult(false, 0f);
            }
            float chance = Math.Min(cfg.BacklashChanceMax, cfg.BacklashBaseChance + cfg.BacklashChancePerMo * mo);
            if (rng != null && rng() < chance)
            {
                float frac = cfg.BacklashDmgBaseFrac + cfg.BacklashDmgPerMoFrac * (mo / Math.Max(1f, cfg.MoMax));
                float dmg = playerMaxHp * frac;
                return new BacklashResult(true, dmg);
            }
            return new BacklashResult(false, 0f);
        }
    }
}
