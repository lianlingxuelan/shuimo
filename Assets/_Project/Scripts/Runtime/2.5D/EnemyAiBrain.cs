// -----------------------------------------------------------------------------
// 2.5D/EnemyAiBrain.cs —— 敌人感知/追击决策核（feature/2.5d，地图升级 选项A 续）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过；逻辑正确性由 EditMode 单测 P2_2_EnemyAiBrainTests 保证。
//
// 【职责】纯决策：给定敌人位置、玩家位置、巡逻圈与若干半径参数，算出敌人本帧
// 应处于哪个状态（巡逻/追击/返巢）、应朝哪个点移动、是否该起手攻击。
//
// 【为什么抽成纯函数】
//   AI 的状态转移是最容易出「抖动 / 卡死 / 追出天边」这类隐性 bug 的地方，
//   而这些 bug 在 PlayMode 里靠肉眼观察极难复现定位。本文件不含 MonoBehaviour、
//   不碰 Transform、不读时钟、无任何副作用 —— 同输入必定同输出，因此可以在
//   EditMode 单测里把每条转移边和边界条件全部钉死，无需进游戏试。
//
// 【红线】（与 EnemyPatrol / EnemyNpcSpawner 同）
//   1. 不引用 CombatScheduler / RunPhase / DamageResolver / RequestHitstop / KickHitstop；
//   2. 不写 Time.timeScale、不写 FeedbackClock.Frozen，本文件根本不读任何时钟；
//   3. 不改动任何内核类型；
//   4. **不造成任何伤害**：ShouldAttack 只是「该播攻击表现了」的信号，
//      真正的伤害流转须走内核，留待后续任务接入（见文末 TODO）。
//
// 【设计要点】
//   · 滞回（hysteresis）：进入追击用 alertRadius，脱离追击用更大的 loseRadius。
//     两者相等会让敌人在边界上疯狂 Chase/Return 抖动，故内部强制 lose >= alert。
//   · 拴绳（leash）：敌人离巡逻圈圆心超过 leashRadius 即强制返巢，无论玩家多近，
//     避免玩家把整张地图的怪拖成一串。leash 内部强制 >= patrolRadius，
//     否则敌人在自己圈里也会被判定「跑太远」而永久返巢。
//   · 返巢不可打断：Return 期间不响应警戒，必须先回到圈内才恢复巡逻，
//     否则会在 leash 边界与 Chase 反复互切。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>敌人 AI 状态（巡逻 / 追击 / 返巢）。</summary>
    public enum EnemyAiState
    {
        /// <summary>在所属区域内随机游走（默认态）。</summary>
        Patrol = 0,

        /// <summary>已发现玩家，朝玩家逼近。</summary>
        Chase = 1,

        /// <summary>脱战返巢：朝巡逻圈圆心走，回到圈内才恢复巡逻。</summary>
        Return = 2,
    }

    /// <summary>
    /// 决策输入快照（值类型，无引用、无副作用）。
    /// 由 <see cref="EnemyPatrol"/> 每帧填好后交给 <see cref="EnemyAiBrain.Decide"/>。
    /// </summary>
    public struct EnemyAiInput
    {
        /// <summary>本帧之前所处的状态。</summary>
        public EnemyAiState State;

        /// <summary>敌人当前位置（世界 XY）。</summary>
        public Vector2 SelfPos;

        /// <summary>玩家当前位置（世界 XY）；<see cref="PlayerValid"/> 为 false 时忽略。</summary>
        public Vector2 PlayerPos;

        /// <summary>玩家是否可作为目标（找不到玩家 / 玩家已亡 / 终局时为 false）。</summary>
        public bool PlayerValid;

        /// <summary>巡逻圈圆心（世界 XY）。</summary>
        public Vector2 PatrolCenter;

        /// <summary>巡逻圈半径。</summary>
        public float PatrolRadius;

        /// <summary>当前巡逻目标点（Patrol 状态下原样沿用，由调用方自行挑选）。</summary>
        public Vector2 PatrolTarget;

        /// <summary>警戒半径：玩家进入此距离则由巡逻转追击。</summary>
        public float AlertRadius;

        /// <summary>脱战半径：追击中玩家超出此距离则返巢。内部强制 &gt;= AlertRadius。</summary>
        public float LoseRadius;

        /// <summary>拴绳半径：敌人离圆心超此距离即强制返巢。内部强制 &gt;= PatrolRadius。</summary>
        public float LeashRadius;

        /// <summary>攻击距离：追击中进入此距离则停步并起手攻击。</summary>
        public float AttackRange;
    }

    /// <summary>决策输出（值类型）。</summary>
    public struct EnemyAiDecision
    {
        /// <summary>本帧应处于的状态。</summary>
        public EnemyAiState State;

        /// <summary>本帧应朝之移动的目标点（<see cref="Moving"/> 为 false 时无意义）。</summary>
        public Vector2 MoveTarget;

        /// <summary>本帧是否应该移动（false = 原地，如已贴身或巡逻等待）。</summary>
        public bool Moving;

        /// <summary>
        /// 本帧是否应起手攻击表现。
        /// **仅表现信号，不代表造成伤害**；由调用方按冷却节流后驱动 CharacterView。
        /// </summary>
        public bool ShouldAttack;
    }

    /// <summary>
    /// 敌人感知/追击决策核：纯静态、无状态、无副作用。
    /// 同一份 <see cref="EnemyAiInput"/> 必定得到同一份 <see cref="EnemyAiDecision"/>。
    /// </summary>
    public static class EnemyAiBrain
    {
        /// <summary>
        /// 计算本帧决策。
        ///
        /// 判定优先级（顺序即语义，测试逐条覆盖）：
        ///   1. 拴绳最高：离圆心超 leash → 强制 Return（玩家再近也不追）；
        ///   2. Return 不可打断：未回到圈内则继续 Return；回到圈内转 Patrol；
        ///   3. Chase 维持/脱离：玩家失效或超出 lose → Return；
        ///   4. Patrol 起警：玩家有效且在 alert 内 → Chase；
        ///   5. Chase 贴身：距玩家 &lt;= attackRange → 停步 + 起手攻击。
        /// </summary>
        /// <param name="input">本帧输入快照。</param>
        /// <returns>本帧决策结果。</returns>
        public static EnemyAiDecision Decide(EnemyAiInput input)
        {
            // ---- 防呆规整：把配错的半径关系掐回可用区间，避免抖动/卡死 ----
            float patrolR = Mathf.Max(0.0f, input.PatrolRadius);
            float alertR = Mathf.Max(0.0f, input.AlertRadius);
            // 滞回硬约束：脱战半径不得小于警戒半径，否则边界上会 Chase/Return 互切。
            float loseR = Mathf.Max(input.LoseRadius, alertR);
            // 拴绳硬约束：不得小于巡逻半径，否则敌人在自家圈内也被判「跑太远」而永久返巢。
            float leashR = Mathf.Max(input.LeashRadius, patrolR);
            float atkR = Mathf.Max(0.0f, input.AttackRange);

            float distFromHome = Vector2.Distance(input.SelfPos, input.PatrolCenter);
            bool hasPlayer = input.PlayerValid;
            float distToPlayer = hasPlayer
                ? Vector2.Distance(input.SelfPos, input.PlayerPos)
                : float.MaxValue;

            EnemyAiDecision d = new EnemyAiDecision();

            // ---- 1. 拴绳优先：被拖出地盘，立刻回家，不再理玩家 ----
            if (distFromHome > leashR)
            {
                d.State = EnemyAiState.Return;
                d.MoveTarget = input.PatrolCenter;
                d.Moving = true;
                d.ShouldAttack = false;
                return d;
            }

            // ---- 2. 返巢不可打断：先回到圈内，再谈警戒 ----
            if (input.State == EnemyAiState.Return)
            {
                if (distFromHome <= patrolR)
                {
                    // 已回到圈内：恢复巡逻，本帧就走既有巡逻目标。
                    d.State = EnemyAiState.Patrol;
                    d.MoveTarget = input.PatrolTarget;
                    d.Moving = true;
                    d.ShouldAttack = false;
                    return d;
                }

                d.State = EnemyAiState.Return;
                d.MoveTarget = input.PatrolCenter;
                d.Moving = true;
                d.ShouldAttack = false;
                return d;
            }

            // ---- 3/4. 决定本帧是否处于追击 ----
            bool chasing;
            if (input.State == EnemyAiState.Chase)
            {
                // 维持追击：玩家仍有效且未超出脱战半径。
                chasing = hasPlayer && distToPlayer <= loseR;
                if (!chasing)
                {
                    // 追丢了：返巢（而不是就地转巡逻，避免敌人散落在圈外发呆）。
                    d.State = EnemyAiState.Return;
                    d.MoveTarget = input.PatrolCenter;
                    d.Moving = true;
                    d.ShouldAttack = false;
                    return d;
                }
            }
            else
            {
                // 从巡逻起警：玩家进入警戒半径。
                chasing = hasPlayer && distToPlayer <= alertR;
            }

            if (!chasing)
            {
                // ---- 巡逻：沿用调用方给的巡逻目标 ----
                d.State = EnemyAiState.Patrol;
                d.MoveTarget = input.PatrolTarget;
                d.Moving = true;
                d.ShouldAttack = false;
                return d;
            }

            // ---- 5. 追击中：贴身则停步起手，否则继续逼近 ----
            d.State = EnemyAiState.Chase;
            if (distToPlayer <= atkR)
            {
                d.MoveTarget = input.SelfPos;
                d.Moving = false;
                d.ShouldAttack = true;
            }
            else
            {
                d.MoveTarget = input.PlayerPos;
                d.Moving = true;
                d.ShouldAttack = false;
            }
            return d;
        }

        // TODO（后续任务，需接内核）：
        //   ShouldAttack 目前只驱动攻击「表现」。要让敌人真正打掉玩家血量，
        //   必须经 CombatBridge 暴露的接口投递到战斗内核（内核推进权唯一红线），
        //   不能在本文件或 EnemyPatrol 里直接改玩家 HP / 调 DamageResolver。
    }
}
