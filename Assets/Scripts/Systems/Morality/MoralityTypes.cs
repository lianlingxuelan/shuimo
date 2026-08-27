// -----------------------------------------------------------------------------
// MoralityTypes.cs —— 正魔值系统的共享类型（引擎无关，asmdef: Xianxia.Morality）
//
// 【它是什么】
// 蓝图阶段 1.4「正魔值系统（核心中的核心）」的纯数据载体：
//   · MoralityRoute —— 当前走哪条路线（正道 / 侠道 / 魔道 / 入魔）
//   · MoralityCombatModifiers —— 正魔值换算出的战斗系数（攻/防倍率）
//   · BacklashResult —— 魔道反噬结算结果
// 全部是 readonly struct / enum，零方法、零状态，方便内核与测试直接比较。
//
// 【禁止事项】不得引用 UnityEngine；本程序集 noEngineReferences=true，必须能在
// 没有引擎的情况下被 dotnet test / Python 对拍环境编译。
// -----------------------------------------------------------------------------

namespace Xianxia.Morality
{
    /// <summary>
    /// 正魔路线。由 <see cref="MoralityManager.Balance"/>（Mo − Zheng）与入魔阈值决定。
    /// </summary>
    public enum MoralityRoute
    {
        /// <summary>正道路线：克己、过招、点化，成长稳容错低。</summary>
        Zheng,

        /// <summary>侠道路线：正魔之间的中间地带，随心不滥杀。</summary>
        Xia,

        /// <summary>魔道路线：拔刀、夺宝、吸功，爽快但反噬。</summary>
        Mo,

        /// <summary>彻底入魔：魔道值满，反噬消失但"你也不是你了"。</summary>
        Enchanted
    }

    /// <summary>正魔值换算出的战斗系数（蓝图 1.4「正魔值影响战斗参数」）。</summary>
    public readonly struct MoralityCombatModifiers
    {
        /// <summary>攻击倍率。魔道高→攻高；正道高→攻被压制。</summary>
        public readonly float AttackMul;

        /// <summary>防御倍率。正道高→防高；魔道高→防低。</summary>
        public readonly float DefenseMul;

        /// <summary>魔道占比 0..1，供表现层做墨色浓淡。</summary>
        public readonly float MoFraction;

        public MoralityCombatModifiers(float attackMul, float defenseMul, float moFraction)
        {
            AttackMul = attackMul;
            DefenseMul = defenseMul;
            MoFraction = moFraction;
        }
    }

    /// <summary>魔道反噬结算结果（蓝图 1.4「魔道反噬机制」）。</summary>
    public readonly struct BacklashResult
    {
        /// <summary>本次是否触发了反噬。</summary>
        public readonly bool Triggered;

        /// <summary>触发时的反噬伤害（已按最大生命比例算好，玩家侧走 ApplyEnemyDamage）。</summary>
        public readonly float Damage;

        public BacklashResult(bool triggered, float damage)
        {
            Triggered = triggered;
            Damage = damage;
        }
    }
}
