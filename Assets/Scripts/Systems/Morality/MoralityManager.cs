// -----------------------------------------------------------------------------
// MoralityManager.cs —— 正魔值状态机（引擎无关，asmdef: Xianxia.Morality）
//
// 【它是什么】
// 游戏灵魂系统之一。维护正道值 / 魔道值（0–100，多数行动此消彼长），
// 推导当前路线（正道 / 侠道 / 魔道 / 入魔），并把正魔值换算成战斗系数
// （攻/防倍率）。全部纯函数，无引擎依赖，可被 NUnit / 对拍直接验证。
//
// 【为什么是"两个 0–100 条 + 此消彼长"，而不是单轴】
// 蓝图原文："正道值 ↔ 魔道值，0-100 此消彼长"+"正道值越高 / 魔道值越高"
// 作为能各自拉满的两个维度描述，但用行动增量让它们天然对冲（一升一降）。
// Balance = Mo − Zheng 决定路线，二者均可独立观测，比单一轴更灵活，
// 也更好做"正魔墨条"两条可视化。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Morality
{
    /// <summary>正魔值状态机。线程不安全，单进程主线程够用。</summary>
    public sealed class MoralityManager
    {
        private readonly MoralityConfig _cfg;

        /// <summary>正道值 0..ZhengMax。</summary>
        public float Zheng { get; private set; }

        /// <summary>魔道值 0..MoMax。</summary>
        public float Mo { get; private set; }

        /// <summary>构造。初始给一套"偏正道"的默认值（正道 50 / 魔道 0）。</summary>
        public MoralityManager(MoralityConfig cfg, float zheng = 50f, float mo = 0f)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            Zheng = Clamp(zheng, 0f, _cfg.ZhengMax);
            Mo = Clamp(mo, 0f, _cfg.MoMax);
        }

        /// <summary>是否已彻底入魔（魔道值满）。</summary>
        public bool IsEnchanted => Mo >= _cfg.EnchantThreshold;

        /// <summary>正魔拉扯净值：Mo − Zheng。&gt;0 偏魔，&lt;0 偏正。</summary>
        public float Balance => Mo - Zheng;

        /// <summary>当前路线（蓝图 1.4「正魔路线对比 / 中间地带：侠道路线」）。</summary>
        public MoralityRoute Route
        {
            get
            {
                if (IsEnchanted)
                {
                    return MoralityRoute.Enchanted;
                }
                if (Balance <= -_cfg.XiaBand)
                {
                    return MoralityRoute.Zheng;
                }
                if (Balance >= _cfg.XiaBand)
                {
                    return MoralityRoute.Mo;
                }
                return MoralityRoute.Xia;
            }
        }

        // ---------------------------------------------------------------------
        // 行动入口（由 Unity 桥接层在击杀 / 施法 / 吸功时调用）
        // ---------------------------------------------------------------------

        /// <summary>普攻留手（克己）：正道稳，魔道轻微回落。</summary>
        public void ApplyRestrainedHit() => ApplyDelta(_cfg.RestrainedHitZheng, _cfg.RestrainedHitMo);

        /// <summary>杀招 / 重击 + 击杀：魔道大幅+，正道-。</summary>
        public void ApplyHeavyKill() => ApplyDelta(_cfg.HeavyKillZheng, _cfg.HeavyKillMo);

        /// <summary>吸功（魔道技能）：魔道大幅+，正道-。</summary>
        public void ApplyDrain() => ApplyDelta(_cfg.DrainZheng, _cfg.DrainMo);

        /// <summary>点化 / 放过（正道善举）：正道+，魔道轻微回落。</summary>
        public void ApplyRighteousAct() => ApplyDelta(_cfg.RighteousActZheng, _cfg.RighteousActMo);

        /// <summary>原始增量（测试 / 特殊事件用）。</summary>
        public void ApplyRaw(float dz, float dm) => ApplyDelta(dz, dm);

        // ---------------------------------------------------------------------
        // 战斗系数（蓝图 1.4「正魔值影响战斗参数」）
        //   正道高：防御高、攻击被压制、心性稳固
        //   魔道高：攻击高、防御低、有反噬
        // ---------------------------------------------------------------------

        /// <summary>把正魔值换算为战斗系数。纯函数，可高频调用。</summary>
        public MoralityCombatModifiers ComputeCombat()
        {
            float zf = _cfg.ZhengMax > 0f ? Zheng / _cfg.ZhengMax : 0f;
            float mf = _cfg.MoMax > 0f ? Mo / _cfg.MoMax : 0f;
            float atkMul = 1f + mf * 0.50f - zf * 0.20f;   // 魔道+50%攻 / 正道-20%攻
            float defMul = 1f + zf * 0.50f - mf * 0.30f;   // 正道+50%防 / 魔道-30%防
            return new MoralityCombatModifiers(atkMul, defMul, mf);
        }

        private void ApplyDelta(float dz, float dm)
        {
            Zheng = Clamp(Zheng + dz, 0f, _cfg.ZhengMax);
            Mo = Clamp(Mo + dm, 0f, _cfg.MoMax);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
