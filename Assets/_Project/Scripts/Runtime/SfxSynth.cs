// -----------------------------------------------------------------------------
// SfxSynth.cs —— 纯 DSP 原语库（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译、未经实听**。
//
// 【★ 本文件刻意零 UnityEngine 依赖，这是硬约束不是风格偏好】
// 全文件不出现 using UnityEngine，因此：
//   · 它可以在 EditMode 测试里**脱离引擎直接跑** —— 这是本期唯一能在没有耳朵
//     的情况下自动验证的部分（断言「归一化后峰值 ≤ 目标值」、「循环首尾样本差
//     < 阈值」这类命题，全都不需要 AudioClip、不需要 AudioSource、不需要播放）。
//   · 代价是不能用 Mathf。所以本文件自带 Clamp / Clamp01 / Lerp 三个小工具，
//     它们与 Mathf 的同名函数语义一致，**不是重复造轮子，是解耦的必要成本**。
// 唯一被引用的外部类型是 NormalizeMode 枚举（定义在 AudioConfig.cs）——
// 枚举本身没有任何 Unity 依赖，跨文件引用不会把引擎拖进来。
//
// 【为什么原语库要单独成文件，而不是揉进 SfxRecipes】
// 13 张配方是「听感描述」，原语是「数学」。两者的修改频率差一个量级：调音时
// 每天要改配方里的数字，而原语一旦写对就再也不动。混在一起的直接后果是，
// 每次调音的 diff 里都混着 DSP 代码，review 的人分不清哪些改动是有风险的。
//
// 【★ 本文件里有两个「看起来可以优化、实际不能动」的地方，都写了注释】
//   ① CombReverb 必须逐条延迟线**串行**跑完整个 buffer，不能把四条延迟合到
//      同一个 i 循环里 —— 后者在数学上是发散的，见该函数注释。
//   ② SvfStep 每样本都做一次稳定性钳制（含一次 sqrt），不能提到循环外 ——
//      因为 f 和 q 本来就是逐样本调制的，这正是选 SVF 而不是一阶滤波的理由。
// 这两处都属于「下一个人会顺手优化掉」的类型，删注释前请先看懂注释。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 状态变量滤波器（Chamberlin SVF）的状态。
    ///
    /// 【为什么本期主力是 SVF 而不是一阶滤波】
    /// 13 张配方里有 6 张需要「带宽随时间张开 / 收窄」或「中心频率逐样本扫动」。
    /// 一阶滤波器做不到这一点（它只有一个可调参数且没有 Q），SVF 是最便宜的
    /// 选择：每样本 3 次乘加，同时给出低通 / 带通 / 高通三路输出。
    /// </summary>
    public struct SvfState
    {
        /// <summary>低通输出（同时也是内部积分器状态）。</summary>
        public float Low;

        /// <summary>带通输出（同时也是内部积分器状态）。</summary>
        public float Band;

        /// <summary>高通输出。纯输出，不参与状态递推。</summary>
        public float High;
    }

    /// <summary>
    /// 纯 DSP 原语库。全部是纯函数或 <c>ref</c> 状态推进，无任何全局可变状态，
    /// 因此天然可重入、可并行、可在测试里逐个断言。
    /// </summary>
    public static class SfxSynth
    {
        /// <summary>2π。写成 const 免得每个函数里重复算。</summary>
        public const double TwoPi = 6.283185307179586476925286766559;

        /// <summary>π。</summary>
        public const double Pi = 3.141592653589793238462643383279;

        // =====================================================================
        // 小工具（因为不能用 Mathf，见文件头）
        // =====================================================================

        /// <summary>把 v 钳制到 [lo, hi]。</summary>
        /// <param name="v">输入值。</param>
        /// <param name="lo">下界。</param>
        /// <param name="hi">上界。</param>
        /// <returns>钳制后的值。</returns>
        public static float Clamp(float v, float lo, float hi)
        {
            if (v < lo)
            {
                return lo;
            }
            if (v > hi)
            {
                return hi;
            }
            return v;
        }

        /// <summary>把 v 钳制到 [0, 1]。</summary>
        /// <param name="v">输入值。</param>
        /// <returns>钳制后的值。</returns>
        public static float Clamp01(float v)
        {
            return Clamp(v, 0f, 1f);
        }

        /// <summary>线性插值，t 会先被钳到 [0,1]。</summary>
        /// <param name="a">t=0 时的值。</param>
        /// <param name="b">t=1 时的值。</param>
        /// <param name="t">插值系数。</param>
        /// <returns>插值结果。</returns>
        public static float Lerp(float a, float b, float t)
        {
            float k = Clamp01(t);
            return a + (b - a) * k;
        }

        // =====================================================================
        // 噪声源
        // =====================================================================

        /// <summary>
        /// 白噪声，输出范围 [-1, 1)。
        ///
        /// 【★ 随机源纪律：只能用传进来的这个 System.Random】
        /// 严禁 UnityEngine.Random（进程级全局共享状态 —— 将来任何人为了复现某个
        /// 场景写下 Random.InitState(seed)，音频抽掉的几十个随机数就会**静默地**
        /// 改变其他所有表现层随机的序列）；更严禁碰内核的 SkillRng / PCG32
        /// （从内核流里多抽一个数，后续所有战斗随机全部错位，确定性指纹当场作废）。
        /// 参数传入的实例是**物理隔离**的，这比「我检查过了没人乱用」强得多。
        /// </summary>
        /// <param name="rng">调用方持有的随机流，不得为 null。</param>
        /// <returns>[-1, 1) 的白噪声样本。</returns>
        public static float WhiteNoise(System.Random rng)
        {
            return (float)(2.0 * rng.NextDouble() - 1.0);
        }

        // =====================================================================
        // 一阶滤波
        // =====================================================================

        /// <summary>
        /// 一阶滤波器的系数：a = 1 - exp(-2π·fc/fs)。
        ///
        /// 【为什么不用更简单的 a = fc/(fc+fs) 之类的近似】
        /// 那些近似在 fc 接近 fs/4 时截止频率会明显偏低，而本期有 fc = 2500 Hz
        /// 配 fs = 22050 的用法（poise_break 的瞬态高通）。指数式是精确解，
        /// 每张配方只算一次（在循环外），成本可以忽略。
        /// </summary>
        /// <param name="fc">截止频率（Hz）。会被钳到 [1, fs/2) 的开区间内。</param>
        /// <param name="fs">采样率（Hz）。</param>
        /// <returns>滤波系数 a，落在 (0, 1)。</returns>
        public static float OnePoleCoeff(float fc, int fs)
        {
            if (fs <= 0)
            {
                return 1f;
            }

            float nyquist = fs * 0.5f;
            float f = Clamp(fc, 1f, nyquist * 0.99f);
            double a = 1.0 - Math.Exp(-TwoPi * f / fs);
            return Clamp((float)a, 1e-6f, 1f);
        }

        /// <summary>
        /// 一阶低通：推进一步并返回低通输出。
        /// </summary>
        /// <param name="y">滤波器状态，由调用方持有（每层一个）。</param>
        /// <param name="x">当前输入样本。</param>
        /// <param name="a">由 <see cref="OnePoleCoeff"/> 算出的系数。</param>
        /// <returns>低通输出。</returns>
        public static float OnePoleLP(ref float y, float x, float a)
        {
            y += a * (x - y);
            return y;
        }

        /// <summary>
        /// 一阶高通：内部跑一个低通，输出 x 减去低通分量。
        ///
        /// 【注意状态字段的含义】
        /// 传进来的 <paramref name="lpState"/> 是**低通**的状态，不是高通的输出。
        /// 复用同一个 float 做两种滤波时把状态搞混，症状是滤波器行为完全不对
        /// 却不报错 —— 所以参数名叫 lpState 而不是 y。
        /// </summary>
        /// <param name="lpState">内部低通的状态，由调用方持有。</param>
        /// <param name="x">当前输入样本。</param>
        /// <param name="a">由 <see cref="OnePoleCoeff"/> 算出的系数。</param>
        /// <returns>高通输出。</returns>
        public static float OnePoleHP(ref float lpState, float x, float a)
        {
            lpState += a * (x - lpState);
            return x - lpState;
        }

        // =====================================================================
        // 状态变量滤波器（SVF）
        // =====================================================================

        /// <summary>
        /// SVF 的频率系数：f = 2·sin(π·fc/fs)。
        /// </summary>
        /// <param name="fc">中心 / 截止频率（Hz）。</param>
        /// <param name="fs">采样率（Hz）。</param>
        /// <returns>频率系数 f。真正的稳定性钳制在 <see cref="SvfClamp"/> 里做。</returns>
        public static float SvfCoeff(float fc, int fs)
        {
            if (fs <= 0)
            {
                return 0f;
            }

            float nyquist = fs * 0.5f;
            float f = Clamp(fc, 1f, nyquist * 0.98f);
            return (float)(2.0 * Math.Sin(Pi * f / fs));
        }

        /// <summary>
        /// ★ SVF 稳定性钳制。这是本文件里最容易被误删的一段，先看懂再动。
        ///
        /// 【为什么必须钳，以及为什么必须 f 和 q 一起钳】
        /// Chamberlin SVF 的状态转移矩阵是
        ///     l[n] = l[n-1] + f·b[n-1]
        ///     b[n] = -f·l[n-1] + (1 - f·q - f²)·b[n-1] + f·x
        /// 其行列式为 1 - f·q，迹为 2 - f·q - f²。两个特征值都落在单位圆内
        /// （即滤波器不发散）的充要条件是：
        ///     0 &lt; f·q &lt; 2   且   f² + 2·f·q &lt; 4
        ///
        /// 这两条是**耦合**的 —— 单独钳 f 或单独钳 q 都不够，也都会误伤：
        ///   · sfx_boss_shockwave 扫到 5000 Hz @ 22050 且 Q 降到 0.8（q=1.25）时
        ///     f² + 2fq = 1.71 + 3.27 = 4.98 &gt; 4 → **发散**，输出 NaN / 爆音。
        ///   · 而 skill_circle_burst 的 Q 也降到 0.6（q=1.67，比上面还低），
        ///     但它的中心频率固定在 700 Hz，f 只有 0.199，
        ///     f² + 2fq = 0.04 + 0.66 = 0.70 ≪ 4 → **完全稳定**。
        ///     如果图省事「统一钳 q ≤ 1.4」，就会白白改掉 circle_burst 的音色。
        ///
        /// 所以这里的做法是：q 只保一个极小下界（避免除零式的自激），
        /// 然后**按当前的 q 反解出允许的 f 上限**，只在真的越界时才压 f。
        /// 取 3.6 而不是 4.0 是留 10% 余量，浮点误差与 pitch 变化都吃在这里。
        ///
        /// 【副作用，必须知情】
        /// boss_shockwave 在 22050 Hz 下扫频上端会被压到约 3.7 kHz 而到不了
        /// 名义上的 5 kHz。听感上「带宽张开」主要由 Q 的下降驱动，影响有限；
        /// 若本地实听觉得张不开，把该音效的 SampleRate 改成 44100 即可
        /// （届时 f 只有 0.71，5 kHz 可完整到达），一个数字。
        /// </summary>
        /// <param name="f">频率系数，可能被就地压低。</param>
        /// <param name="q">阻尼系数（= 1/Q），可能被就地抬高到下界。</param>
        public static void SvfClamp(ref float f, ref float q)
        {
            if (q < 0.02f)
            {
                q = 0.02f;
            }

            // 由 f² + 2·f·q ≤ 3.6 解出 f ≤ -q + sqrt(q² + 3.6)。
            float fMax = (float)(-q + Math.Sqrt(q * (double)q + 3.6));
            if (fMax < 0.001f)
            {
                fMax = 0.001f;
            }

            if (f > fMax)
            {
                f = fMax;
            }
            if (f < 0f)
            {
                f = 0f;
            }
        }

        /// <summary>
        /// SVF 推进一步。三路输出全部写回 <paramref name="s"/>，
        /// 返回值是**带通**输出（13 张配方里用得最多的那一路）。
        /// 需要低通 / 高通请读 <see cref="SvfState.Low"/> / <see cref="SvfState.High"/>。
        ///
        /// 【★ 三行递推的顺序不能交换】
        /// low 用的是**上一样本**的 band，high 用的是刚更新的 low 和上一样本的 band，
        /// band 用的是刚算出的 high。换任意两行的顺序都会得到一个不同的（且通常
        /// 不稳定的）滤波器，而它不会报错，只是听起来「不对」。
        ///
        /// 【为什么稳定性钳制放在每样本里而不是提到循环外】
        /// 因为 f 和 q 本来就是**逐样本调制**的 —— 这正是本期选 SVF 而不是一阶
        /// 滤波的全部理由（6 张配方要扫中心频率或扫带宽）。提到循环外就等于假设
        /// 它们不变，那假设一旦被后来的人破坏，症状是整段输出变成 NaN。
        /// 每样本一次 sqrt，在预合成期（一次性、约 39 万样本）完全不值得心疼。
        /// </summary>
        /// <param name="s">滤波器状态，由调用方持有。</param>
        /// <param name="x">当前输入样本。</param>
        /// <param name="f">频率系数，来自 <see cref="SvfCoeff"/>。</param>
        /// <param name="q">阻尼系数，等于 1/Q。</param>
        /// <returns>带通输出。</returns>
        public static float SvfStep(ref SvfState s, float x, float f, float q)
        {
            float ff = f;
            float qq = q;
            SvfClamp(ref ff, ref qq);

            s.Low += ff * s.Band;
            s.High = x - s.Low - qq * s.Band;
            s.Band += ff * s.High;

            return s.Band;
        }

        // =====================================================================
        // 包络
        // =====================================================================

        /// <summary>
        /// 「快起手 + 指数衰减」包络：t &lt; atk 时线性爬到 1，之后 exp(-(t-atk)/tau)。
        /// τ 后信号衰减到 36.8%。
        /// </summary>
        /// <param name="t">当前时刻（秒）。</param>
        /// <param name="atk">起手时长（秒）。传 0 或负数视为瞬时起手。</param>
        /// <param name="tau">衰减时间常数（秒）。</param>
        /// <returns>包络值 [0, 1]。</returns>
        public static float ExpEnv(float t, float atk, float tau)
        {
            if (t <= 0f)
            {
                return 0f;
            }
            if (atk > 0f && t < atk)
            {
                return t / atk;
            }
            if (tau <= 0f)
            {
                return 0f;
            }
            return (float)Math.Exp(-(t - (atk > 0f ? atk : 0f)) / tau);
        }

        /// <summary>
        /// 「起手 + 平台 + 指数衰减」包络。
        ///
        /// 【为什么需要平台段，不能用 ExpEnv 凑】
        /// sfx_enemy_death 的「溃散」与 skill_circle_burst 的「冲击」都要求声音
        /// **先撑住一段再散掉**。纯指数衰减一起手就开始掉，听感是「打了一下」
        /// 而不是「炸开一片」—— 这个差别在 60~80 ms 的平台上，凑不出来。
        /// </summary>
        /// <param name="t">当前时刻（秒）。</param>
        /// <param name="atk">起手时长（秒）。</param>
        /// <param name="hold">平台时长（秒）。</param>
        /// <param name="tau">平台之后的衰减时间常数（秒）。</param>
        /// <returns>包络值 [0, 1]。</returns>
        public static float PlateauEnv(float t, float atk, float hold, float tau)
        {
            if (t <= 0f)
            {
                return 0f;
            }
            if (atk > 0f && t < atk)
            {
                return t / atk;
            }

            float a = atk > 0f ? atk : 0f;
            float h = hold > 0f ? hold : 0f;
            if (t < a + h)
            {
                return 1f;
            }
            if (tau <= 0f)
            {
                return 0f;
            }
            return (float)Math.Exp(-(t - a - h) / tau);
        }

        /// <summary>
        /// 「掠过」窗：一条峰值位置可指定的升余弦窗，两端为 0，峰值处为 1。
        ///
        /// 【为什么闪避音要用它而不是 ExpEnv】
        /// 闪避是「一阵风从耳边过去」，它有**来**也有**去**。ExpEnv 是一巴掌打上
        /// 然后衰减，那是撞击的形状。峰值放在 40% 而不是 50%，是因为真实的掠过
        /// 声总是「来得快、去得慢」（多普勒 + 尾迹）。
        /// </summary>
        /// <param name="t">当前时刻（秒）。</param>
        /// <param name="total">窗总长（秒）。</param>
        /// <param name="peakAt">峰值位置，占总长的比例，取值 (0,1)。</param>
        /// <returns>窗值 [0, 1]。</returns>
        public static float RaisedCosineWindow(float t, float total, float peakAt)
        {
            if (total <= 0f || t <= 0f || t >= total)
            {
                return 0f;
            }

            float p = Clamp(peakAt, 0.01f, 0.99f) * total;
            if (t < p)
            {
                return (float)(0.5 * (1.0 - Math.Cos(Pi * t / p)));
            }
            return (float)(0.5 * (1.0 + Math.Cos(Pi * (t - p) / (total - p))));
        }

        // =====================================================================
        // 振荡与扫频
        // =====================================================================

        /// <summary>
        /// 相位累加式正弦振荡器：相位推进 2π·f/fs 后返回 sin(phase)。
        ///
        /// 【★ 为什么相位是 double 而不是 float】
        /// 环境衬底有 264 600 个样本。float 只有 24 位有效位，累加到几万弧度之后
        /// 每一步的增量会被舍入吃掉一大半，听感是「越到后面音高越不准、还发抖」。
        /// double 有 53 位，同样的累加量误差可以忽略。**不要为了省 4 字节把它
        /// 改成 float** —— 这个 bug 只在长音效上出现，短音效测不出来。
        ///
        /// 【为什么用相位累加而不是直接 sin(2π·f·t)】
        /// 因为 f 是逐样本变化的（扫频）。直接代入 t 算出来的是「频率一直是当前
        /// 这个 f」的波形，相邻样本之间会跳相位 → 扫频听起来带颗粒感甚至爆音。
        /// 相位累加才是对瞬时频率做积分，是唯一正确的做法。
        /// </summary>
        /// <param name="phase">相位状态（弧度），由调用方持有。</param>
        /// <param name="f">当前瞬时频率（Hz）。</param>
        /// <param name="fs">采样率（Hz）。</param>
        /// <returns>正弦输出 [-1, 1]。</returns>
        public static float SweepPhase(ref double phase, float f, int fs)
        {
            if (fs <= 0)
            {
                return 0f;
            }

            phase += TwoPi * f / fs;
            if (phase > TwoPi)
            {
                // 只在超过一圈时回卷，避免相位无限增长导致 double 也开始丢精度。
                phase -= TwoPi;
            }
            return (float)Math.Sin(phase);
        }

        /// <summary>
        /// 指数扫频的瞬时频率：f0·(f1/f0)^(t/T)。
        ///
        /// 【★ 为什么是指数而不是线性】
        /// 人耳对频率的感知是对数的。220 → 110 Hz 线性扫下去，前半段听起来降得
        /// 飞快、后半段几乎不动；指数扫频才是「匀速下滑」的听感。这一条直接决定
        /// sfx_player_hurt / sfx_boss_phase / skill_basic_slash 三个扫频音
        /// 听起来是自然还是别扭。
        /// </summary>
        /// <param name="f0">起始频率（Hz），必须 &gt; 0。</param>
        /// <param name="f1">终止频率（Hz），必须 &gt; 0。</param>
        /// <param name="t">当前时刻（秒）。会被钳到 [0, T]。</param>
        /// <param name="total">扫频总时长（秒）。</param>
        /// <returns>瞬时频率（Hz）。</returns>
        public static float ExpSweep(float f0, float f1, float t, float total)
        {
            if (f0 <= 0f || f1 <= 0f)
            {
                return f0 > 0f ? f0 : 1f;
            }
            if (total <= 0f)
            {
                return f1;
            }

            float k = Clamp01(t / total);
            return (float)(f0 * Math.Pow(f1 / (double)f0, k));
        }

        /// <summary>
        /// 非谐波钟：Σ sin(2π·f0·rᵢ·t)·exp(-t/τᵢ)。
        ///
        /// 【★ 为什么金石类音色一律用非谐波泛音比（R-C 硬规则）】
        /// 整数倍泛音（1, 2, 3, 4…）听起来是管风琴 / 合成器；真实的钟磬因为是
        /// 三维振动体，泛音比是非整数的。[1.0, 2.76, 5.40, 8.93] 是管钟的实测
        /// 泛音比，不是随手编的数 —— **不要「顺手」把它改成整数倍**。
        ///
        /// 【为什么各泛音的衰减时间不同】
        /// 高次泛音衰减快是所有敲击体的物理规律（内耗随频率上升）。所有泛音用
        /// 同一个 τ 会得到一种「电子风铃」的假感，这是最容易暴露合成痕迹的地方。
        ///
        /// 【为什么它只用在 BOSS 事件上】
        /// 每个泛音一次 sin + 一次 exp，4 个泛音就是 8 次超越函数 / 样本，是全部
        /// 原语里最贵的。BOSS 事件一局只响几次、且都在预合成期算完，运行期零成本；
        /// 用在每秒响十几次的 sfx_enemy_hit 上则预合成时间显著上升而收益很小
        /// （100 ms 的音效听不出泛音结构）。成本与出场频率恰好成反比。
        /// </summary>
        /// <param name="ratios">泛音比数组，不得为 null 或空。</param>
        /// <param name="decays">
        /// 各泛音的衰减时间常数（秒）。允许长度为 1 —— 此时全部泛音共用 decays[0]。
        /// 长度既不等于 1 也不等于 ratios.Length 时，按下标取，越界的用最后一个。
        /// </param>
        /// <param name="f0">基频（Hz）。</param>
        /// <param name="t">自敲击起算的时刻（秒）。</param>
        /// <returns>叠加后的样本值（未归一化，量级约等于泛音个数）。</returns>
        public static float InharmonicBell(float[] ratios, float[] decays, float f0, float t)
        {
            if (ratios == null || ratios.Length == 0 || decays == null || decays.Length == 0)
            {
                return 0f;
            }
            if (t < 0f)
            {
                return 0f;
            }

            double sum = 0.0;
            for (int i = 0; i < ratios.Length; i++)
            {
                int di = i < decays.Length ? i : decays.Length - 1;
                float tau = decays[di];
                if (tau <= 0f)
                {
                    continue;
                }

                double amp = Math.Exp(-t / (double)tau);
                sum += Math.Sin(TwoPi * f0 * ratios[i] * t) * amp;
            }
            return (float)sum;
        }

        // =====================================================================
        // 混响
        // =====================================================================

        /// <summary>
        /// 就地反馈梳状混响：对每条延迟线执行 buf[i] += g·buf[i-d]（升序遍历）。
        ///
        /// 【★★ 为什么必须「一条延迟线跑完整个 buffer，再跑下一条」】
        /// 这是本文件里唯一一处会被优化成 bug 的地方，务必看完再动手。
        ///
        /// 看起来更「高效」的写法是把四条延迟合到同一个 i 循环里：
        ///     for i: buf[i] += g*buf[i-d0] + g*buf[i-d1] + g*buf[i-d2] + g*buf[i-d3]
        /// 这等价于一个传递函数为 1 / (1 - g·(z^-d0 + z^-d1 + z^-d2 + z^-d3)) 的
        /// 递归滤波器，它在直流处的环路增益是 4 × 0.40 = **1.6 &gt; 1** ——
        /// 数学上直接发散。症状是 buffer 后半段指数爆炸成几百万，归一化之后
        /// 前面全部内容被压成 0，听起来是「一声闷响之后完全没了」。
        ///
        /// 而串行写法是四个**各自独立稳定**的梳状滤波器级联（每个的环路增益都是
        /// 0.40 &lt; 1），既是架构文档「升序遍历」的本意，也是唯一不发散的读法。
        ///
        /// 【为什么就地做而不是开临时 buffer】
        /// 反馈梳状本来就要读自己刚写出去的历史样本，就地是它的定义而不是优化。
        /// </summary>
        /// <param name="buf">待处理的样本缓冲，就地修改。</param>
        /// <param name="delaySamples">各条延迟线的长度（**样本数**，不是毫秒）。</param>
        /// <param name="g">反馈增益。会被钳到 [0, 0.9]，超过 1 必定发散。</param>
        public static void CombReverb(float[] buf, int[] delaySamples, float g)
        {
            if (buf == null || buf.Length == 0 || delaySamples == null)
            {
                return;
            }

            float gain = Clamp(g, 0f, 0.9f);
            for (int k = 0; k < delaySamples.Length; k++)
            {
                int d = delaySamples[k];
                if (d <= 0 || d >= buf.Length)
                {
                    continue;
                }

                for (int i = d; i < buf.Length; i++)
                {
                    buf[i] += gain * buf[i - d];
                }
            }
        }

        // =====================================================================
        // 测量与归一化
        // =====================================================================

        /// <summary>取缓冲的峰值 max|x|。</summary>
        /// <param name="buf">样本缓冲。</param>
        /// <returns>峰值；空缓冲返回 0。</returns>
        public static float Peak(float[] buf)
        {
            if (buf == null || buf.Length == 0)
            {
                return 0f;
            }

            float p = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > p)
                {
                    p = a;
                }
            }
            return p;
        }

        /// <summary>
        /// 取缓冲的均方根。
        /// 累加用 double —— 26 万个样本的平方和用 float 累加会明显偏小。
        /// </summary>
        /// <param name="buf">样本缓冲。</param>
        /// <returns>RMS；空缓冲返回 0。</returns>
        public static float Rms(float[] buf)
        {
            if (buf == null || buf.Length == 0)
            {
                return 0f;
            }

            double sum = 0.0;
            for (int i = 0; i < buf.Length; i++)
            {
                sum += buf[i] * (double)buf[i];
            }
            return (float)Math.Sqrt(sum / buf.Length);
        }

        /// <summary>
        /// 把缓冲里的 NaN / ±Infinity 就地换成 0。
        ///
        /// 【为什么值得花一趟遍历】
        /// 只要有**一个** NaN，Peak 与 Rms 的结果就都是 NaN，缩放系数是 NaN，
        /// 整个 buffer 变成 NaN。而一个全 NaN 的 AudioClip 在不同平台上的行为是
        /// 未定义的 —— 可能静音，也可能是一声全功率的爆音直接怼进耳机。
        /// 这一趟遍历买的是「最坏情况只是这个音效没声音」，很划算。
        /// </summary>
        /// <param name="buf">样本缓冲，就地修改。</param>
        public static void Sanitize(float[] buf)
        {
            if (buf == null)
            {
                return;
            }

            for (int i = 0; i < buf.Length; i++)
            {
                float v = buf[i];
                if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    buf[i] = 0f;
                }
            }
        }

        /// <summary>
        /// 归一化。
        ///
        /// 【为什么每张配方都必须归一化 —— 这不是洁癖】
        /// 手写 DSP 出来的峰值是完全不可预测的：三层叠加的 boss_death 可能到 2.7，
        /// dodge_roll 可能只有 0.15。不归一化的话，AudioConfig 里的 13 个 Gain 就
        /// 变成「13 个互相不可比的未知数」，调音时改一个数字的实际效果无法预期 ——
        /// 而「能一眼横向对比」正是那张常量表存在的全部意义。
        /// 归一化之后，Gain 才真正表示「这个音效相对其他音效有多响」。
        /// </summary>
        /// <param name="buf">样本缓冲，就地修改。</param>
        /// <param name="mode">归一化模式。</param>
        /// <param name="target">目标值（Peak 模式为峰值，Rms 模式为均方根）。</param>
        public static void Normalize(float[] buf, NormalizeMode mode, float target)
        {
            if (buf == null || buf.Length == 0 || target <= 0f)
            {
                return;
            }

            Sanitize(buf);

            float measured = mode == NormalizeMode.Rms ? Rms(buf) : Peak(buf);
            if (measured < 1e-9f)
            {
                // 全静音缓冲。除下去会得到 Infinity，什么都不做才是对的。
                return;
            }

            float scale = target / measured;
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] *= scale;
            }

            if (mode == NormalizeMode.Rms)
            {
                // RMS 归一化只保证「平均能量」达标，噪声的离群峰值完全可能冲到
                // 1.0 以上。软削波在这里不是可选项，是 RMS 模式的组成部分。
                SoftClipBuffer(buf);
            }
        }

        /// <summary>
        /// 软削波：阈值以下完全线性（不染色），以上用 tanh 压缩，渐近到 1.0。
        ///
        /// 【为什么不用硬钳制 Clamp(-1, 1)】
        /// 硬钳制会把波形削平成一段直线，产生大量高次谐波 —— 那正是人耳听到的
        /// 「破音」。软削波把同样的能量弯下去而不是切掉，听感是「压住了」
        /// 而不是「炸了」。
        /// </summary>
        /// <param name="x">输入样本。</param>
        /// <returns>削波后的样本，绝对值恒 &lt; 1。</returns>
        public static float SoftClip(float x)
        {
            const float threshold = 0.70f;

            float a = x < 0f ? -x : x;
            if (a <= threshold)
            {
                return x;
            }

            float sign = x < 0f ? -1f : 1f;
            float over = (a - threshold) / (1f - threshold);
            float shaped = threshold + (1f - threshold) * (float)Math.Tanh(over);
            return sign * shaped;
        }

        /// <summary>对整个缓冲逐样本软削波。</summary>
        /// <param name="buf">样本缓冲，就地修改。</param>
        public static void SoftClipBuffer(float[] buf)
        {
            if (buf == null)
            {
                return;
            }

            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = SoftClip(buf[i]);
            }
        }

        // =====================================================================
        // 淡化
        // =====================================================================

        /// <summary>
        /// 在缓冲末尾加一段线性淡出。
        ///
        /// 【为什么每个非循环音效都要收这一下】
        /// 合成出来的波形在最后一个样本上通常不是 0。AudioSource 播完之后信号
        /// 直接归零，这个阶跃就是一声轻微的「哒」。它单独听几乎察觉不到，
        /// 但围攻时每秒响十几次，会累积成一种说不清的毛刺感 —— 而 A-10 明令
        /// 「不出现爆音」。2 ms 的淡出成本为零，收益是彻底消除这一类。
        /// </summary>
        /// <param name="buf">样本缓冲，就地修改。</param>
        /// <param name="fadeSamples">淡出长度（样本数）。超过缓冲长度时按缓冲长度处理。</param>
        public static void ApplyTailFade(float[] buf, int fadeSamples)
        {
            if (buf == null || buf.Length == 0 || fadeSamples <= 0)
            {
                return;
            }

            int n = fadeSamples > buf.Length ? buf.Length : fadeSamples;
            int start = buf.Length - n;
            for (int i = 0; i < n; i++)
            {
                float w = 1f - (i + 1) / (float)n;
                buf[start + i] *= w;
            }
        }

        /// <summary>
        /// 从指定样本处开始施加指数淡出（之前的样本不受影响）。
        /// 用于「前半段保持、后半段散掉」这种两段式收尾。
        /// </summary>
        /// <param name="buf">样本缓冲，就地修改。</param>
        /// <param name="fromIndex">起始样本下标。小于 0 视为 0。</param>
        /// <param name="tauSamples">衰减时间常数（**样本数**）。必须 &gt; 0。</param>
        public static void ApplyExpFadeFrom(float[] buf, int fromIndex, float tauSamples)
        {
            if (buf == null || buf.Length == 0 || tauSamples <= 0f)
            {
                return;
            }

            int start = fromIndex < 0 ? 0 : fromIndex;
            for (int i = start; i < buf.Length; i++)
            {
                buf[i] *= (float)Math.Exp(-(i - start) / (double)tauSamples);
            }
        }

        // =====================================================================
        // 无缝循环
        // =====================================================================

        /// <summary>
        /// ★ 等功率交叉淡化，把一段「L + xf」的素材做成长度为 L 的无缝循环。
        ///
        /// 输入必须是连续合成出来的 L + xf 个样本；输出取前 L 个，其中前 xf 个
        /// 是「尾部素材」淡出、「头部素材」淡入的混合。这样 out[L-1] 与 out[0]
        /// 在原始素材里本来就是相邻的两个样本，接缝处不存在阶跃。
        ///
        /// 【★★ 为什么必须是等功率（sin/cos）而不是线性（w / 1-w）】
        /// 这是噪声信号特有的陷阱，也是本期「最容易做对了 90% 却栽在最后一步」
        /// 的地方。两段**互不相关**的噪声做线性淡化时，中点处两路各占 0.5，
        /// 但不相关信号的功率是平方相加：
        ///     线性中点功率 = 0.5² + 0.5² = 0.50   → 比两端低 3 dB
        ///     等功率中点   = sin²(π/4) + cos²(π/4) = 1.0  → 恒定
        /// 线性淡化会在每个循环接缝处留下一个 3 dB 的**音量凹陷** —— 听起来就是
        /// 每 12 秒「喘一口气」。它比阶跃咔哒更隐蔽，但验收要求「听 60 秒听不出
        /// 接缝」，5 次凹陷一定会被察觉。
        ///
        /// 【它只解决了一半问题，另一半在配方里】
        /// 交叉淡化管的是**噪声本身**的连续性；LFO 包络的连续性必须靠「所有 LFO
        /// 周期整除循环总长」来保证，那件事在 WindLoop 配方里做。两个条件缺一不可。
        /// </summary>
        /// <param name="src">连续合成的素材，长度必须 ≥ L + xf。</param>
        /// <param name="loopSamples">目标循环长度 L（样本数）。</param>
        /// <param name="crossfadeSamples">交叉淡化长度 xf（样本数）。</param>
        /// <returns>长度为 L 的无缝循环缓冲；参数非法时返回长度为 L 的尽力而为拷贝。</returns>
        public static float[] CrossfadeLoop(float[] src, int loopSamples, int crossfadeSamples)
        {
            if (src == null || loopSamples <= 0)
            {
                return new float[loopSamples > 0 ? loopSamples : 0];
            }

            int l = loopSamples > src.Length ? src.Length : loopSamples;
            var dst = new float[l];

            int xf = crossfadeSamples;
            if (xf < 0)
            {
                xf = 0;
            }
            if (xf > l)
            {
                xf = l;
            }
            // 尾部素材不够长就退化成直接截断：宁可有接缝，也不能越界。
            if (l + xf > src.Length)
            {
                xf = src.Length - l;
                if (xf < 0)
                {
                    xf = 0;
                }
            }

            for (int i = 0; i < xf; i++)
            {
                double w = i / (double)xf;
                double fadeIn = Math.Sin(Pi * w * 0.5);
                double fadeOut = Math.Cos(Pi * w * 0.5);
                dst[i] = (float)(src[i] * fadeIn + src[l + i] * fadeOut);
            }

            for (int i = xf; i < l; i++)
            {
                dst[i] = src[i];
            }

            return dst;
        }
    }
}
