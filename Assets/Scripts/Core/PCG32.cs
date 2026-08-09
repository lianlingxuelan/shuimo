// -----------------------------------------------------------------------------
// PCG32.cs —— 确定性伪随机数发生器（引擎无关）
//
// 【为什么是 PCG32 而不是 System.Random】
// Godot 的 RandomNumberGenerator 内部就是 PCG32（core/math/random_pcg.h）。
// 原型的全部手感数值（掉落、词缀、地形算子、闪避骰）都跑在这条流上，
// Unity 侧若换 RNG，同一个种子会长出完全不同的世界，「对拍 Godot 基线」直接作废。
// System.Random 在不同 .NET 运行时（Mono / CoreCLR / IL2CPP）实现还不保证一致，
// 而 PCG32 是纯整数位运算，任何平台上逐位相同 —— 这是可回归测试的前提。
//
// 【禁止事项】
// 本文件不得引用 UnityEngine。它必须能被 dotnet test 独立编译。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Core
{
    /// <summary>
    /// PCG32（permuted congruential generator, 32-bit output / 64-bit state）。
    /// 与 Godot <c>RandomNumberGenerator</c> 同构：同 seed + 同 inc ⇒ 同序列。
    /// </summary>
    public sealed class PCG32
    {
        /// <summary>LCG 乘数。PCG 论文与 Godot 使用同一常量，不可改。</summary>
        public const ulong Multiplier = 6364136223846793005UL;

        /// <summary>
        /// 默认流增量。取自 Godot <c>RANDOM_PCG_DEFAULT_INC_64</c>（= PCG_DEFAULT_INC_64）。
        /// inc 决定「用哪条流」，两个 PCG 即使 state 相同、inc 不同也互不相关。
        /// </summary>
        public const ulong DefaultIncrement = 1442695040888963407UL;

        /// <summary>全局默认种子，对齐 <c>GameConfig.DEFAULT_SEED</c>。</summary>
        public const ulong DefaultSeed = 20260730UL;

        private ulong _state;
        private ulong _inc;

        /// <summary>用默认种子与默认流构造。</summary>
        public PCG32() : this(DefaultSeed, DefaultIncrement) { }

        /// <summary>用指定种子、默认流构造。</summary>
        public PCG32(ulong seed) : this(seed, DefaultIncrement) { }

        /// <summary>用指定种子与流增量构造。</summary>
        public PCG32(ulong seed, ulong increment)
        {
            Reseed(seed, increment);
        }

        /// <summary>
        /// 当前内部状态。仅用于存档 / 读档时精确恢复随机流，
        /// 直接写它等于跳到流的任意位置，非存档场景不要碰。
        /// </summary>
        public ulong State
        {
            get { return _state; }
            set { _state = value; }
        }

        /// <summary>当前流增量（恒为奇数）。存档需要连它一起存。</summary>
        public ulong Increment
        {
            get { return _inc; }
            set { _inc = value | 1UL; }
        }

        /// <summary>重新播种（沿用当前流）。</summary>
        public void Reseed(ulong seed)
        {
            Reseed(seed, _inc);
        }

        /// <summary>
        /// 重新播种并指定流。
        ///
        /// 【为什么播完种要空转一次】
        /// Godot 的 <c>RandomPCG::seed()</c> 在赋值后立刻调一次 pcg32_random_r，
        /// 目的是打散「小种子 ⇒ 首个输出高度可预测」的退化（seed=0 尤其明显）。
        /// 这一步不能省，省了就和 Godot 的序列整体错开一格。
        /// </summary>
        public void Reseed(ulong seed, ulong increment)
        {
            _inc = increment | 1UL;   // PCG 要求 inc 为奇数
            _state = seed;
            NextUInt();               // 与 Godot 对齐的强制推进
        }

        /// <summary>
        /// 取下一个 32 位无符号随机数。这是唯一的熵出口，其余接口都由它派生。
        /// </summary>
        public uint NextUInt()
        {
            ulong old = _state;
            unchecked
            {
                _state = old * Multiplier + _inc;
            }
            // XSH-RR 输出置换：先异或折叠高位，再按高 5 位做循环右移。
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>
        /// [0, 1) 区间的单精度浮点。
        ///
        /// 【为什么是 &gt;&gt; 8 而不是 / 4294967295】
        /// float 只有 24 位尾数，除以 uint32 最大值会在靠近 1 的地方发生向上舍入，
        /// 真的能吐出 1.0f —— 于是 <c>if (r &lt; rate)</c> 这类判定在 rate=1.0 时
        /// 会出现「概率 100% 却偶尔不触发」的幽灵 bug。取高 24 位则严格 &lt; 1。
        /// </summary>
        public float NextFloat()
        {
            return (NextUInt() >> 8) * (1.0f / 16777216.0f);
        }

        /// <summary>[0, 1) 区间的双精度浮点。</summary>
        public double NextDouble()
        {
            return NextUInt() * (1.0 / 4294967296.0);
        }

        /// <summary>[min, max) 区间的浮点。min &gt;= max 时直接返回 min。</summary>
        public float NextRange(float min, float max)
        {
            if (max <= min)
            {
                return min;
            }
            return min + (max - min) * NextFloat();
        }

        /// <summary>
        /// [fromInclusive, toInclusive] 闭区间整数。
        /// 语义与 Godot <c>randi_range()</c> 一致（含取模带来的极轻微偏斜，
        /// 刻意保留以保证对拍结果逐值相同）。
        /// </summary>
        public int NextRangeInt(int fromInclusive, int toInclusive)
        {
            if (toInclusive <= fromInclusive)
            {
                return fromInclusive;
            }
            uint span = (uint)(toInclusive - fromInclusive) + 1u;
            return fromInclusive + (int)(NextUInt() % span);
        }

        /// <summary>按概率 p 掷骰。p &lt;= 0 恒 false，p &gt;= 1 恒 true（且不消耗随机流）。</summary>
        public bool Chance(float p)
        {
            if (p <= 0.0f)
            {
                return false;
            }
            if (p >= 1.0f)
            {
                return true;
            }
            return NextFloat() < p;
        }

        /// <summary>公平硬币。取最高位而非最低位——PCG 的低位与高位质量相同，此处只是习惯。</summary>
        public bool NextBool()
        {
            return (NextUInt() >> 31) != 0u;
        }

        /// <summary>
        /// 派生一条独立子流。用于「地形生成」「掉落」等互不干扰的子系统，
        /// 避免某个系统多摇一次骰子就把其它系统的结果全推偏。
        /// </summary>
        public PCG32 Fork(ulong streamId)
        {
            return new PCG32(NextUInt() ^ (streamId * 0x9E3779B97F4A7C15UL), _inc + (streamId << 1));
        }
    }
}
