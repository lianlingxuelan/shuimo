// -----------------------------------------------------------------------------
// 2.5D/EnemyContactAttack.cs —— 敌人接触伤害「是否该结算」决策核（feature/2.5d，敌人 AI 续）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过。决策核的正确性由 EditMode 单测 P2_3_EnemyContactAttackTests
// 覆盖，并用 Node.js 对拍脚本复刻逻辑（见 ai_contact_check.js，PASS 16 / FAIL 0）。
//
// 【它解决什么问题】
// EnemyPatrol 贴身起手攻击时，要把「这一帧该不该真的掉玩家血」这一个判定从表现层里
// 抽出来 —— 原因和 EnemyAiBrain 一样：冷却 + 距离的双重门槛最容易出隐性 bug
// （每秒掉血次数不对、贴身却砍空、冷却没生效导致一帧多次掉血）。抽成纯函数后，
// 用 P2_3 把「贴身 && 冷却到点才结算、且每次只结算一次」这条规则钉死。
//
// 【它不动内核】本核只做「决策」，不持有任何 Combatant、不调用 DamageResolver、
// 不引用 CombatScheduler。真正把伤害落进内核的是 CombatBridge（通过 IDamageRequester）。
// EnemyPatrol 只在冷却到点且贴身时，经注入的 IDamageRequester 把请求抛出去，
// 自己**永远不直接碰战斗内核** —— 严守与 EnemyAiBrain / EnemyNpcSpawner 同源的红线。
//
// 【冷却谁管】
// 冷却的"推进"（按传入 dt 递减）由 EnemyPatrol.Tick 顶部统一负责（与暂停/顿帧同闸门）；
// 本核只消费"已经递减过的剩余冷却"，决定「此刻要不要开火 + 开火后回填多少冷却」。
// 这样冷却所有权唯一，不会双扣。
//
// 【红线】纯函数：无 MonoBehaviour、不碰 Transform、不读时钟、无副作用。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 伤害请求出口（解耦接口）。EnemyPatrol 只认这个接口，不认内核类型。
    /// 实现方（CombatBridge）负责把请求真正投递进战斗内核（模型 B 减伤 / W-CORE 闸门）。
    /// 为空时 EnemyPatrol 只播攻击表现、不掉血 —— 表现与逻辑彻底解耦。
    /// </summary>
    public interface IDamageRequester
    {
        /// <summary>
        /// 请求对玩家施加一次接触伤害。
        /// </summary>
        /// <param name="rawDamage">原始伤害（未套减伤）。真减伤由内核结算。</param>
        void RequestContactDamage(float rawDamage);
    }

    /// <summary>敌人接触攻击决策核的输入。</summary>
    public sealed class EnemyContactInput
    {
        /// <summary>敌人到玩家的距离（世界单位）。</summary>
        public float DistanceToPlayer;

        /// <summary>攻击距离（贴身阈值）。</summary>
        public float AttackRange;

        /// <summary>已由调用方按 dt 推进后的剩余冷却（秒）。</summary>
        public float CooldownRemaining;

        /// <summary>开火后回填的冷却（秒）。</summary>
        public float AttackInterval;
    }

    /// <summary>敌人接触攻击决策核的输出。</summary>
    public sealed class EnemyContactDecision
    {
        /// <summary>true = 本帧应结算一次接触伤害（并已回填冷却）。</summary>
        public bool ShouldFire;

        /// <summary>回填后的剩余冷却（秒）。</summary>
        public float CooldownRemaining;
    }

    /// <summary>
    /// 敌人接触伤害决策核。纯静态、无状态、无副作用。
    /// 规则：贴身（dist &lt;= attackRange）且冷却已到点（cd &lt;= 0）→ 开火并回填 attackInterval；
    /// 否则不开火，冷却原样回传（由调用方继续按 dt 递减）。
    /// </summary>
    public static class EnemyContactAttack
    {
        /// <summary>
        /// 给定当前状态，决定本帧是否应结算一次接触伤害。
        /// </summary>
        /// <param name="input">决策输入（冷却应为已按 dt 推进后的值）。</param>
        /// <returns>决策结果。</returns>
        public static EnemyContactDecision Decide(EnemyContactInput input)
        {
            float cd = Mathf.Max(0.0f, input.CooldownRemaining);
            float atkR = Mathf.Max(0.0f, input.AttackRange);
            float dist = input.DistanceToPlayer;
            if (float.IsNaN(dist) || float.IsInfinity(dist))
            {
                dist = float.MaxValue;
            }

            bool inRange = dist <= atkR;

            if (inRange && cd <= 0.0f)
            {
                return new EnemyContactDecision
                {
                    ShouldFire = true,
                    CooldownRemaining = Mathf.Max(0.1f, input.AttackInterval),
                };
            }

            return new EnemyContactDecision
            {
                ShouldFire = false,
                CooldownRemaining = cd,
            };
        }
    }
}
