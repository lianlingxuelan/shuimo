// -----------------------------------------------------------------------------
// Difficulty.cs —— 模型 B 难度曲线（引擎无关）
//
// 【模型 B 的核心思想：反解，不是正推】
// 传统做法是「配 atk_mult → 跑一局 → 看死得快不快」，改一个数就要重测全区。
// 模型 B 反过来：先用手感目标（A1/A2 存活时长）反解出**每次实际掉血量 d_eff**，
// 再倒推该区的 atk_mult。zones.json 里那 5 个 atk_mult（1.0/1.2/1.4/1.65/1.95）
// 只是**种子**，不是最终生效值 —— 见 docs/unity-rebuild-blueprint.md §F3。
//
// 推导（H = hp_max）：
//   A1「4 怪围攻 10–16s 死」@ 围攻频率 4.615 → H/73.8 ≤ d_eff ≤ H/46.2
//   A2「单挑 35–60s 死」    @ 单挑频率 1.667 → H/100  ≤ d_eff ≤ H/58.3
//   两区间取交集 → H/73.8 ≤ d_eff ≤ H/58.3，取中位靶子 d_eff = H/65
//   校验 H=300 → d_eff=4.62 → A2=38.2s ✅、A1=15.1s ✅
//
// 【禁止事项】不得引用 UnityEngine。必须能被 dotnet test 独立编译。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Core
{
    /// <summary>模型 B 难度靶子与 A2 软上限减伤的纯函数集合。</summary>
    public static class Difficulty
    {
        // --- 模型 B 靶子 ---

        /// <summary>d_eff 靶子除数。d_eff = hp_max / 65。</summary>
        public const float DeffDivisor = 65.0f;

        /// <summary>有效区间下界的除数（除数越大 ⇒ d_eff 越小 ⇒ 越简单）。</summary>
        public const float DeffDivisorMax = 73.8f;

        /// <summary>有效区间上界的除数（除数越小 ⇒ d_eff 越大 ⇒ 越难）。</summary>
        public const float DeffDivisorMin = 58.3f;

        // --- A2 软上限减伤（玩家侧）---

        /// <summary>防御软上限系数 K，对齐 <c>GameConfig.def_softcap_k</c>。</summary>
        public const float DefSoftcapK = 100.0f;

        /// <summary>单次承伤的下限。再厚的甲也至少掉 1 点，杜绝「免疫悬崖」。</summary>
        public const float DamageFloor = 1.0f;

        // --- W-CORE 承伤频率（次/秒）---

        /// <summary>单挑理想频率 = 1 / PerSourceHitCd。</summary>
        public const float SoloFreqIdeal = 1.667f;

        /// <summary>围攻理想频率 = 1 / GlobalHitGap。</summary>
        public const float SwarmFreqIdeal = 4.615f;

        /// <summary>单挑实测频率（60Hz 固定步长量化后，Godot r2_t01 基线）。</summary>
        public const float SoloFreqMeasured = 1.700f;

        /// <summary>围攻实测频率（60Hz 固定步长量化后，Godot r2_t01 基线）。</summary>
        public const float SwarmFreqMeasured = 4.300f;

        // --- A1 / A2 验收窗口（秒）---

        /// <summary>A1：4 怪围攻存活时长下界。</summary>
        public const float A1MinSeconds = 10.0f;

        /// <summary>A1：4 怪围攻存活时长上界。</summary>
        public const float A1MaxSeconds = 16.0f;

        /// <summary>A2：单挑存活时长下界。</summary>
        public const float A2MinSeconds = 35.0f;

        /// <summary>A2：单挑存活时长上界。</summary>
        public const float A2MaxSeconds = 60.0f;

        // ---------------------------------------------------------------------
        // 靶子计算
        // ---------------------------------------------------------------------

        /// <summary>模型 B 靶子：给定血上限，算出每次命中「应该」掉多少血。</summary>
        public static float ComputeDeff(float hpMax)
        {
            return hpMax / DeffDivisor;
        }

        /// <summary>有效区间下界（最简单仍可接受的 d_eff）。</summary>
        public static float DeffLowerBound(float hpMax)
        {
            return hpMax / DeffDivisorMax;
        }

        /// <summary>有效区间上界（最难仍可接受的 d_eff）。</summary>
        public static float DeffUpperBound(float hpMax)
        {
            return hpMax / DeffDivisorMin;
        }

        /// <summary>判定实测 d_eff 是否落在模型 B 的有效区间内。</summary>
        public static bool IsDeffInTarget(float deff, float hpMax)
        {
            return deff >= DeffLowerBound(hpMax) && deff <= DeffUpperBound(hpMax);
        }

        // ---------------------------------------------------------------------
        // 减伤（A2 软上限除法模型）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 减伤率 = def / (def + K)。单调递增、恒 &lt; 1、无免疫悬崖。
        /// 刻意与怪物侧的减法模型不同构：玩家要「越堆越稳但永远不无敌」。
        /// </summary>
        public static float MitigationOf(float def, float k = DefSoftcapK)
        {
            float d = def > 0.0f ? def : 0.0f;
            float kk = k > 1.0f ? k : 1.0f;
            return d / (d + kk);
        }

        /// <summary>玩家实际承伤 = max(1, raw × (1 − mitigation))。</summary>
        public static float DamageTaken(float raw, float def, float k = DefSoftcapK)
        {
            float reduced = raw * (1.0f - MitigationOf(def, k));
            return reduced > DamageFloor ? reduced : DamageFloor;
        }

        // ---------------------------------------------------------------------
        // TTK 正算 / 反解
        // ---------------------------------------------------------------------

        /// <summary>给定 d_eff 与承伤频率，估算存活秒数（TTK）。</summary>
        public static float EstimateTtk(float hpMax, float deff, float hitsPerSecond)
        {
            if (deff <= 0.0f || hitsPerSecond <= 0.0f)
            {
                return float.PositiveInfinity;
            }
            return hpMax / (deff * hitsPerSecond);
        }

        /// <summary>反解：想要在给定频率下正好活 ttk 秒，每次该掉多少血。</summary>
        public static float SolveDeffForTtk(float hpMax, float ttkSeconds, float hitsPerSecond)
        {
            if (ttkSeconds <= 0.0f || hitsPerSecond <= 0.0f)
            {
                return 0.0f;
            }
            return hpMax / (ttkSeconds * hitsPerSecond);
        }

        /// <summary>A1 验收：4 怪围攻（用实测围攻频率）存活时长是否落在 [10, 16] s。</summary>
        public static bool IsA1Pass(float hpMax, float deff)
        {
            float t = EstimateTtk(hpMax, deff, SwarmFreqMeasured);
            return t >= A1MinSeconds && t <= A1MaxSeconds;
        }

        /// <summary>A2 验收：单挑（用实测单挑频率）存活时长是否落在 [35, 60] s。</summary>
        public static bool IsA2Pass(float hpMax, float deff)
        {
            float t = EstimateTtk(hpMax, deff, SoloFreqMeasured);
            return t >= A2MinSeconds && t <= A2MaxSeconds;
        }

        /// <summary>
        /// 反解该区的 atk_mult：已知目标 d_eff、玩家 def、敌人基础攻击与区域 armor_add，
        /// 求需要多大的 atk_mult 才能打出这个 d_eff。
        ///
        /// 注意：这是**辅助工具**，不是真源。armor_add 与等级差的具体接法在 Unity 侧
        /// 重建后可能变化，逐区 atk_mult 必须在 Unity headless 测试台上实测复核。
        /// </summary>
        public static float SolveAtkMultForDeff(float targetDeff, float enemyBaseAtk, float playerDef, float k = DefSoftcapK)
        {
            if (enemyBaseAtk <= 0.0f)
            {
                return 0.0f;
            }
            float keep = 1.0f - MitigationOf(playerDef, k);
            if (keep <= 0.0f)
            {
                return 0.0f;
            }
            return targetDeff / (enemyBaseAtk * keep);
        }
    }
}
