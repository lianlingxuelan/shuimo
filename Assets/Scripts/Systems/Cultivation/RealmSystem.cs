// -----------------------------------------------------------------------------
// RealmSystem.cs —— 境界状态机（纯逻辑，不碰 UnityEngine）
//
// 【它做什么】
// 给定「累计修炼点」，算出你当前在第几层、离下一层多远、是否需要突破。
// 它只算数、抛状态，绝不改玩家血/攻——和 PlayerProgression 同一思路（数值红线）。
//
// 【类比前端】
//   想象一个根据 scrollTop 算「当前高亮哪个 section」的函数：
//     input  = scrollTop（累计修炼点）
//     table  = 各 section 的 offsetTop（Thresholds）
//     output = 高亮索引 + 本 section 内进度%
//   双层 gate 就像「到 section 顶了，但有个弹窗要你点确认才允许滚进去」。
//
// 【为什么独立 asmdef + 纯逻辑】
//   和 Morality / Personality / Inventory 一致：内核可 headless 单测，
//   Unity 表现层（HUD/事件）以后才接，且不依赖任何内容决策。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Cultivation
{
    public sealed class RealmSystem
    {
        private readonly RealmConfig _cfg;
        public RealmState State { get; }

        public RealmSystem(RealmConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            int n = _cfg.TierCount;
            State = new RealmState
            {
                TierIndex = 0,
                Accumulated = 0f,
                BreakthroughPending = false,
                BrokenThrough = new bool[n],
            };
        }

        /// <summary>当前层显示名（你填的内容；缺省时回退为「境界N」）。</summary>
        public string CurrentName => SafeName(State.TierIndex);

        /// <summary>是否还有下一层。</summary>
        public bool HasNextTier => State.TierIndex + 1 < _cfg.TierCount;

        /// <summary>
        /// 修炼点累积：自动跨过门槛推进层数；若进入的是「需突破」层，
        /// 只置 BreakthroughPending（停在门外），等 TryBreakthrough 放行。
        /// </summary>
        public void AddCultivation(float points)
        {
            if (points < 0f) points = 0f;
            State.Accumulated += points;

            // 从当前层向上爬，跨过一个门槛就进下一层；
            // 进入需突破层时停在门外（Pending），不继续往下跨（避免跳过守卫）。
            while (HasNextTier && State.Accumulated >= _cfg.Thresholds[State.TierIndex + 1])
            {
                State.TierIndex++;
                if (NeedsBreakthroughAt(State.TierIndex))
                {
                    State.BreakthroughPending = true;
                    break; // 到守卫门口即停，不自动穿过
                }
            }
        }

        /// <summary>
        /// 尝试突破：仅当 Pending 且本层确有 gate 时成功，清除 Pending、标记已突破。
        /// 非 gate 层永远返回 false（它们不需要突破，进了就是进了）。
        /// </summary>
        public bool TryBreakthrough()
        {
            if (!State.BreakthroughPending) return false;
            if (!NeedsBreakthroughAt(State.TierIndex)) return false;
            State.BreakthroughPending = false;
            State.BrokenThrough[State.TierIndex] = true;
            return true;
        }

        /// <summary>当前层内进度 0..1（到下一层门槛的比例）。顶层恒为 1。</summary>
        public float ProgressInTier()
        {
            if (!HasNextTier) return 1f;
            float lo = _cfg.Thresholds[State.TierIndex];
            float hi = _cfg.Thresholds[State.TierIndex + 1];
            if (hi <= lo) return 1f;
            float p = (State.Accumulated - lo) / (hi - lo);
            return p < 0f ? 0f : (p > 1f ? 1f : p);
        }

        // —— 内部助手 ——

        /// <summary>第 i 层是否需要突破；配置长度不齐时越界返回 false（不崩）。</summary>
        private bool NeedsBreakthroughAt(int i)
        {
            return _cfg.NeedsBreakthrough != null
                   && i >= 0 && i < _cfg.NeedsBreakthrough.Length
                   && _cfg.NeedsBreakthrough[i];
        }

        private string SafeName(int i)
        {
            if (_cfg.Names == null || i < 0 || i >= _cfg.Names.Length) return "境界" + i;
            return _cfg.Names[i];
        }
    }
}
