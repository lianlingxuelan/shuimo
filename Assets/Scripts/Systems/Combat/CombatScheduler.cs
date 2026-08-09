// -----------------------------------------------------------------------------
// CombatScheduler.cs —— 把任意帧率的 deltaTime 切成整数个 1/60 逻辑步（引擎无关）
//
// 【为什么必须有这一层】
// 两层闸门（PerSourceHitCd=0.6 / GlobalHitGap=0.2167）是靠**步数**量化的：
//     13 步 × (1/60) = 0.216667 < 0.2167  → 第 13 步不放行，必须等第 14 步
//   围攻频率因此稳定在 4.300 次/s。
// 如果直接把 Time.deltaTime 喂给闸门，60Hz 与 144Hz 会落在不同的量化边界上，
// 高刷玩家挨打更频繁——这是最难被发现、也最伤口碑的一类"性能即难度"。
//
// 【★ 硬性约束：绝不出现 0.01667f 字面量】
//     13 × 0.01667 = 0.21671 > 0.2167  → 第 13 步就放行 → 围攻 4.65 次/s
// 一个"看起来等价"的十进制近似，直接把承伤基线抬高 8%。步长只能取
// FixedStepAccumulator.FixedStep（= 1.0f / 60.0f）。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using System;
using Xianxia.Core;

namespace Xianxia.Combat
{
    /// <summary>
    /// 固定步长调度器。Unity 壳每帧调 <see cref="Tick"/>，内核只看见 1/60。
    /// </summary>
    public sealed class CombatScheduler
    {
        /// <summary>
        /// 固定逻辑步长（秒）。**唯一真源是 <see cref="FixedStepAccumulator.FixedStep"/>**，
        /// 这里只做转发，禁止在任何地方另写十进制近似值。
        /// </summary>
        public static readonly float FixedStep = FixedStepAccumulator.FixedStep;

        /// <summary>单次 <see cref="Tick"/> 最多推进的步数（防卡顿螺旋，来自 T0）。</summary>
        public const int MaxStepsPerTick = FixedStepAccumulator.MaxStepsPerAdvance;

        /// <summary>步长累加器。</summary>
        public readonly FixedStepAccumulator Accumulator = new FixedStepAccumulator();

        /// <summary>被驱动的战场。为 null 时 <see cref="Tick"/> 仍会推进计数与事件。</summary>
        public Encounter Encounter;

        /// <summary>暂停开关。暂停期间累加器不吃 deltaTime，恢复后不会"补帧雪崩"。</summary>
        public bool Paused;

        /// <summary>累计推进的逻辑步数。</summary>
        public int TotalSteps;

        /// <summary>累计模拟时长（秒）= <see cref="TotalSteps"/> × <see cref="FixedStep"/>。</summary>
        public float SimulatedTime;

        /// <summary>
        /// 每个逻辑步结束后触发（参数为固定步长）。
        /// 表现层（相机抖动、命中特效队列）挂这里，就能与逻辑严格同拍，
        /// 而不是在 Update 里按真实帧率自己数时间。
        /// </summary>
        public event Action<float> Stepped;

        // 缓存委托，避免每帧为 Advance 装箱一个新的 Action<float>。
        private readonly Action<float> _onStep;

        /// <summary>构造一个空调度器。</summary>
        public CombatScheduler()
        {
            _onStep = OnStep;
        }

        /// <summary>构造并绑定战场。</summary>
        public CombatScheduler(Encounter encounter) : this()
        {
            Encounter = encounter;
        }

        /// <summary>
        /// 喂入一帧真实时间，内部切成 0~<see cref="MaxStepsPerTick"/> 个固定步。
        /// </summary>
        /// <param name="realDeltaTime">真实帧间隔（Unity 侧传 <c>Time.deltaTime</c>）。</param>
        /// <returns>本次实际推进的逻辑步数。</returns>
        public int Tick(float realDeltaTime)
        {
            if (Paused || realDeltaTime <= 0.0f)
            {
                return 0;
            }
            return Accumulator.Advance(realDeltaTime, _onStep);
        }

        /// <summary>
        /// 单个逻辑步：先推进战场，再抛表现层事件。
        /// 顺序不可颠倒——表现层读到的必须是**本步结算完**的状态。
        /// </summary>
        private void OnStep(float dt)
        {
            TotalSteps++;
            SimulatedTime += dt;

            if (Encounter != null)
            {
                Encounter.StepFixed(dt);
            }

            Action<float> handler = Stepped;
            if (handler != null)
            {
                handler(dt);
            }
        }

        /// <summary>
        /// 强制推进 <paramref name="steps"/> 个逻辑步（跳过累加器）。
        /// 仅供对拍 / 单元测试使用：它绕过了帧率解耦这层保护，
        /// 生产代码请一律走 <see cref="Tick"/>。
        /// </summary>
        public void StepExact(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                OnStep(FixedStep);
            }
        }

        /// <summary>清空累加余量与统计（切场景时调用）。</summary>
        public void Reset()
        {
            Accumulator.Clear();
            TotalSteps = 0;
            SimulatedTime = 0.0f;
            Paused = false;
        }
    }
}
