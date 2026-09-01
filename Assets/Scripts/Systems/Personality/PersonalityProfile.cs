// -----------------------------------------------------------------------------
// PersonalityProfile.cs —— 性格状态机（引擎无关，asmdef: Xianxia.Personality）
//
// 【它是什么】
// 游戏灵魂系统之二。维护五大性格维度（悟性/冲动/隐忍/冷静/贪婪），值随战斗选择
// "玩出来"（非开局选），并换算为战斗系数（先手/反击/命中/拾取/反噬）。
// 另含「性格 × 正魔值联动」：悟性低 + 魔道高 → 入魔风险高。
//
// 【为什么初始为 0 而非随机】
// 蓝图强调"玩出来"，所以开局中性（全 0）最干净；具体初始微调走配置。当前
// 默认构造全 0，战斗中选择才让性格显形。你若想给角色一点底色，改 Init 即可。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Personality
{
    /// <summary>性格状态机。线程不安全，单进程主线程够用。</summary>
    public sealed class PersonalityProfile
    {
        private readonly PersonalityConfig _cfg;

        /// <summary>悟性。</summary>
        public float Wuxing { get; private set; }

        /// <summary>冲动。</summary>
        public float Chongdong { get; private set; }

        /// <summary>隐忍。</summary>
        public float Yinren { get; private set; }

        /// <summary>冷静。</summary>
        public float Lengjing { get; private set; }

        /// <summary>贪婪。</summary>
        public float Tanlan { get; private set; }

        /// <summary>构造。默认全 0（中性开局，玩出来）。</summary>
        public PersonalityProfile(PersonalityConfig cfg,
            float wuxing = 0f, float chongdong = 0f, float yinren = 0f, float lengjing = 0f, float tanlan = 0f)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            Wuxing = Clamp(wuxing);
            Chongdong = Clamp(chongdong);
            Yinren = Clamp(yinren);
            Lengjing = Clamp(lengjing);
            Tanlan = Clamp(tanlan);
        }

        // ---------------------------------------------------------------------
        // 玩出来：行动入口（由 Unity 桥接层在战斗选择时调用）
        // ---------------------------------------------------------------------

        /// <summary>克己普攻 → 悟性+。</summary>
        public void RegisterRestrainedHit()
        {
            Wuxing = Add(Wuxing, _cfg.RestrainedHit_Wuxing);
            ClampAll();
        }

        /// <summary>
        /// 击杀。heavy=true（杀招 / 重击）额外推高冲动、压低隐忍；
        /// 任意击杀都推高冷静。
        /// </summary>
        public void RegisterKill(bool heavy)
        {
            if (heavy)
            {
                Chongdong = Add(Chongdong, _cfg.HeavyKill_Chongdong);
                Yinren = Add(Yinren, _cfg.HeavyKill_Yinren);
            }
            Lengjing = Add(Lengjing, _cfg.Kill_Lengjing);
            ClampAll();
        }

        /// <summary>吸功（魔道技能）→ 贪婪++。</summary>
        public void RegisterDrain()
        {
            Tanlan = Add(Tanlan, _cfg.Drain_Tanlan);
            ClampAll();
        }

        /// <summary>拾取 → 贪婪+。</summary>
        public void RegisterPickup()
        {
            Tanlan = Add(Tanlan, _cfg.Pickup_Tanlan);
            ClampAll();
        }

        /// <summary>原始增量（测试 / 特殊事件用）。</summary>
        public void ApplyRaw(PersonalityTrait trait, float delta)
        {
            switch (trait)
            {
                case PersonalityTrait.Wuxing: Wuxing = Add(Wuxing, delta); break;
                case PersonalityTrait.Chongdong: Chongdong = Add(Chongdong, delta); break;
                case PersonalityTrait.Yinren: Yinren = Add(Yinren, delta); break;
                case PersonalityTrait.Lengjing: Lengjing = Add(Lengjing, delta); break;
                case PersonalityTrait.Tanlan: Tanlan = Add(Tanlan, delta); break;
            }
            ClampAll();
        }

        // ---------------------------------------------------------------------
        // 战斗系数 & 入魔风险（蓝图 1.5）
        // ---------------------------------------------------------------------

        /// <summary>把五性格换算为战斗系数。纯函数，可高频调用。</summary>
        public PersonalityCombatModifiers ComputeCombat()
        {
            float max = Math.Max(1f, _cfg.Max);
            float cf = Chongdong / max;
            float yf = Yinren / max;
            float lf = Lengjing / max;
            float tf = Tanlan / max;
            return new PersonalityCombatModifiers(
                1f + cf * _cfg.ChongdongFirstStrike,
                1f + yf * _cfg.YinrenCounter,
                lf * _cfg.LengjingHitRate,
                1f + tf * _cfg.TanlanPickupMul,
                tf * _cfg.TanlanBacklashExtra);
        }

        /// <summary>
        /// 入魔风险 0..1：悟性越低、魔道越高 → 风险越高（蓝图 1.5「悟性低更容易被骗入魔」）。
        /// 表现层可据其做"微妙心理暗示"或后续自动偏航，本方法只算数值。
        /// </summary>
        public float EnchantRisk(float mo)
        {
            float wuxingDeficit = 1f - Wuxing / Math.Max(1f, _cfg.Max); // 0..1，悟性低→接近 1
            float moFrac = Math.Min(1f, Math.Max(0f, mo) / 100f);        // 魔道量程固定 0..100
            return wuxingDeficit * moFrac * _cfg.EnchantRiskWuxingWeight * _cfg.EnchantRiskMoWeight;
        }

        // ---------------------------------------------------------------------
        // 内部
        // ---------------------------------------------------------------------

        private float Add(float value, float delta) => Clamp(value + delta);

        private void ClampAll()
        {
            Wuxing = Clamp(Wuxing);
            Chongdong = Clamp(Chongdong);
            Yinren = Clamp(Yinren);
            Lengjing = Clamp(Lengjing);
            Tanlan = Clamp(Tanlan);
        }

        private float Clamp(float v) => v < 0f ? 0f : (v > _cfg.Max ? _cfg.Max : v);
    }
}
