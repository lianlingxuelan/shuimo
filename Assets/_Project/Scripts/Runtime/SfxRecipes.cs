// -----------------------------------------------------------------------------
// SfxRecipes.cs —— 13 张音效配方（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译、未经实听**。
// 下面每一个 DSP 数字都是「起调值」，不是定论 —— 它们存在的意义就是被本地
// 实听之后改掉。
//
// 【本文件与 SfxSynth 的分工】
// SfxSynth 是数学，本文件是**听感**。所有「这个声音是什么」的决定都在这里，
// 以就近的 private const 形式出现。为什么不塞进 AudioConfig：那张表存在的
// 意义是「一屏横向对比 13 个音效」，把这里约 120 个 DSP 内部数字搬过去，
// 它会退化成一份 500 行的流水账，恰好摧毁它想要的那个性质（架构 §7.1）。
// 房规不打折：这些数字在全仓同样只出现一次，只是「这一次」在这里。
//
// 【★ 音色三条硬规则（C-2 一票否决项的工程化落实）】
// PRD 要的是「听起来像木石水布金石，不像电子合成器」。这是听感判断，但可以
// 翻译成三条能在 code review 里逐条检查的硬规则。改配方前请先读这三条：
//
//   R-A 每个**撞击类**音效的第一层必须是 2~5 ms 的宽带噪声冲击。
//       没有瞬态 = 电子音，这是「物理撞击」与「合成器」最主要的分界。
//       本文件中带瞬态层的：EnemyHit / PlayerHurt / BossShockwave / PoiseBreak。
//       ※ BasicSlash / DodgeRoll 刻意没有瞬态 —— 它们是「破空 / 掠过」，
//         不是撞击，加瞬态反而会变成「挥剑砍在墙上」。
//         EnemyDeath 也刻意没有 —— 那是「溃散」不是「爆炸」。
//
//   R-B 纯正弦持续时间不得超过 200 ms，且必须带扫频或 LFO。
//       拖长的定频正弦 = 科幻能量音，一秒就暴露。本文件唯一超过 200 ms 的
//       正弦是 BossPhase 的 700 ms 上行滑音，但它**全程在扫频**，不是定频。
//
//   R-C 金石类音色一律用非谐波泛音比，不用整数倍。
//       整数倍泛音 = 管风琴 / 合成器；非谐波才是钟磬。本文件用的
//       [1.0, 2.76, 5.40, 8.93] 是管钟的实测泛音比，**不要顺手改成整数倍**。
//
// 【★ 随机源纪律】
// 全文件只使用 Render() 传进来的那个 System.Random。没有 UnityEngine.Random，
// 更没有内核的 SkillRng / PCG32 —— 从内核随机流里多抽一个数，确定性指纹
// 2.5294x 当场漂移。调用方（AudioClipFactory）应当用
// AudioConfig.MakeSynthRandom(key) 造这条流：它是**固定种子**的，
// 因为调音是一个对比过程，被比较的两次之间只允许有一个变量 —— 如果每次
// 重进 Play 噪声都换一段，你根本判断不出刚才改的那个数字是不是改对了。
//
// 【本文件同样零 UnityEngine 依赖】
// 不是硬性要求（只有 SfxSynth 是），但顺手做到了，于是 13 张配方全都能在
// EditMode 测试里直接跑出 float[] 来断言峰值 / 长度 / 首尾连续性。
// 后续维护请保持这个性质，它很便宜。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 13 张程序化音效配方。<see cref="Render"/> 是唯一入口，
    /// 每个 <see cref="RecipeKind"/> 对应一个**同名**私有函数。
    /// </summary>
    public static class SfxRecipes
    {
        // =====================================================================
        // 全配方共用的少数几个数字
        // =====================================================================

        /// <summary>
        /// 收尾保护淡出长度（秒）。除环境衬底外每张配方都在归一化前加这一下。
        /// 理由见 SfxSynth.ApplyTailFade 的注释：合成波形的最后一个样本通常不是 0，
        /// 播完归零的那个阶跃就是一声轻微的「哒」。
        /// </summary>
        private const float TailGuardSec = 0.002f;

        /// <summary>瞬态层的默认起手时长（秒）。半毫秒，听感上等同于「瞬间」，但不产生阶跃。</summary>
        private const float TransientAtkSec = 0.0005f;

        /// <summary>瞬态噪声源末端的微淡出（秒）。防止噪声突然断掉时留下阶跃。</summary>
        private const float TransientTaperSec = 0.0005f;

        /// <summary>定频正弦层的默认起手时长（秒）。配方表里没给 atk 的正弦层统一用它。</summary>
        private const float SineAtkSec = 0.001f;

        // =====================================================================
        // 入口
        // =====================================================================

        /// <summary>
        /// 按规格渲染出一段单声道 PCM。返回的数组长度为 <c>DurationSec × SampleRate</c>。
        ///
        /// 【为什么 default 分支是抛异常而不是返回静音】
        /// 漏掉一个 RecipeKind 分支的症状是「这个音效安静地没有声音」——
        /// 不报错、不崩溃，是本期最难排查的一类 bug。返回静音会把这个错误
        /// 一路藏到玩家耳朵里；抛异常则会被 AudioClipFactory 的 try/catch 抓住、
        /// 进失败黑名单、并在 Console 里留下一条指名道姓的警告。
        /// **宁可在预合成期吵一声，也不要在运行期安静。**
        /// </summary>
        /// <param name="spec">音效规格，来自 <see cref="AudioConfig.Table"/>。</param>
        /// <param name="rng">合成用随机流，不得为 null。应当来自 <see cref="AudioConfig.MakeSynthRandom"/>。</param>
        /// <returns>归一化后的单声道样本数组。</returns>
        public static float[] Render(SfxSpec spec, System.Random rng)
        {
            if (rng == null)
            {
                throw new ArgumentNullException("rng", AudioConfig.LogPrefix + " 合成随机流不得为 null。");
            }

            int n = SampleCount(spec);
            if (n <= 0)
            {
                throw new ArgumentException(
                    AudioConfig.LogPrefix + " 样本数非法（key=" + spec.Key + "，dur=" + spec.DurationSec +
                    "，rate=" + spec.SampleRate + "）。", "spec");
            }

            switch (spec.Kind)
            {
                case RecipeKind.EnemyHit: return EnemyHit(spec, rng);
                case RecipeKind.PlayerHurt: return PlayerHurt(spec, rng);
                case RecipeKind.EnemyDeath: return EnemyDeath(spec, rng);
                case RecipeKind.BossDeath: return BossDeath(spec, rng);
                case RecipeKind.BossPhase: return BossPhase(spec, rng);
                case RecipeKind.BossSummon: return BossSummon(spec, rng);
                case RecipeKind.BossShockwave: return BossShockwave(spec, rng);
                case RecipeKind.PoiseBreak: return PoiseBreak(spec, rng);
                case RecipeKind.BasicSlash: return BasicSlash(spec, rng);
                case RecipeKind.CircleBurst: return CircleBurst(spec, rng);
                case RecipeKind.BloodLotus: return BloodLotus(spec, rng);
                case RecipeKind.DodgeRoll: return DodgeRoll(spec, rng);
                case RecipeKind.WindLoop: return WindLoop(spec, rng);

                default:
                    throw new NotSupportedException(
                        AudioConfig.LogPrefix + " 配方未实现：" + spec.Kind + "（key=" + spec.Key + "）。");
            }
        }

        // =====================================================================
        // 共用小工具
        // =====================================================================

        /// <summary>规格对应的样本数。</summary>
        private static int SampleCount(SfxSpec spec)
        {
            return (int)(spec.DurationSec * spec.SampleRate);
        }

        /// <summary>秒 → 样本数。全文件只在这里做这个换算，避免各处写法不一致。</summary>
        private static int Samples(float sec, int fs)
        {
            return (int)(sec * fs);
        }

        /// <summary>
        /// 直接按时刻求值的 LFO（而不是相位累加）。
        ///
        /// 【为什么这里必须直接求值，而扫频那里必须相位累加】
        /// 两者的需求正好相反：
        ///   · 扫频的频率逐样本变化，只有对瞬时频率积分（= 相位累加）才是对的。
        ///   · LFO 的频率恒定，但**要求 t = 0 与 t = L 的相位逐位相同**（无缝循环
        ///     的必要条件之一）。相位累加会有微小的浮点漂移，264 600 个样本累积
        ///     下来足以在接缝处露馅；直接代入 t 求值则是精确的。
        /// 别把这两个用法互换。
        /// </summary>
        private static float Lfo(float freqHz, float t)
        {
            return (float)Math.Sin(SfxSynth.TwoPi * freqHz * t);
        }

        /// <summary>
        /// 每张非循环配方的统一收尾：收尾保护淡出 → 归一化。
        /// 环境衬底**不得**调用本方法，见 <see cref="WindLoop"/> 的注释。
        /// </summary>
        private static void Finish(float[] buf, SfxSpec spec)
        {
            SfxSynth.ApplyTailFade(buf, Samples(TailGuardSec, spec.SampleRate));
            SfxSynth.Normalize(buf, spec.NormMode, spec.NormTarget);
        }

        /// <summary>
        /// 在缓冲开头叠一层宽带噪声瞬态（R-A）。
        ///
        /// 【为什么噪声源末端要再加一段微淡出】
        /// 「3 ms 的噪声冲击」说的是噪声**源**只持续 3 ms，而包络在 3 ms 时通常
        /// 还有 0.5 左右。直接砍断就是一个阶跃 —— 本来是为了做出「撞击感」，
        /// 结果自己制造了一声咔哒。半毫秒的淡出解决它，听感上完全无差别。
        /// </summary>
        /// <param name="buf">目标缓冲，就地叠加。</param>
        /// <param name="fs">采样率。</param>
        /// <param name="burstSec">噪声源持续时长（秒）。</param>
        /// <param name="atkSec">包络起手（秒）。</param>
        /// <param name="tauSec">包络衰减时间常数（秒）。</param>
        /// <param name="gain">本层增益。</param>
        /// <param name="hpHz">高通截止（Hz）。传 0 表示不做高通（全带）。</param>
        /// <param name="rng">合成随机流。</param>
        private static void AddTransient(
            float[] buf, int fs, float burstSec, float atkSec, float tauSec, float gain, float hpHz, System.Random rng)
        {
            int burst = Samples(burstSec, fs);
            if (burst <= 0)
            {
                return;
            }
            if (burst > buf.Length)
            {
                burst = buf.Length;
            }

            int taper = Samples(TransientTaperSec, fs);
            if (taper < 1)
            {
                taper = 1;
            }
            if (taper > burst)
            {
                taper = burst;
            }

            float hpCoeff = hpHz > 0f ? SfxSynth.OnePoleCoeff(hpHz, fs) : 0f;
            float hpState = 0f;

            for (int i = 0; i < burst; i++)
            {
                float t = i / (float)fs;
                float x = SfxSynth.WhiteNoise(rng);
                if (hpHz > 0f)
                {
                    x = SfxSynth.OnePoleHP(ref hpState, x, hpCoeff);
                }

                float env = SfxSynth.ExpEnv(t, atkSec, tauSec);

                int remain = burst - i;
                if (remain <= taper)
                {
                    env *= remain / (float)taper;
                }

                buf[i] += x * env * gain;
            }
        }

        // =====================================================================
        // ① sfx_enemy_hit —— 敌人被命中（100 ms @ 44100）
        // =====================================================================
        //
        // 听感目标：清脆、短、有「打实了」的肉感。它是全场出现频率最高的音效
        // （每秒十几次），所以任何一点毛刺都会被放大成「吵」。

        /// <summary>瞬态：3 ms 宽带噪声冲击（R-A）。</summary>
        private const float HitTransientSec = 0.003f;

        /// <summary>瞬态衰减时间常数。</summary>
        private const float HitTransientTauSec = 0.004f;

        /// <summary>瞬态层增益。不是 1.0 —— 主体层才是主角，瞬态只负责「起头那一下」。</summary>
        private const float HitTransientGain = 0.80f;

        /// <summary>
        /// 主体带通中心频率。1.5–4 kHz 这一段的**几何**中心（√(1500×4000) ≈ 2450），
        /// 不是算术中心 2750 —— 因为人耳对频率的感知是对数的，几何中心才是听感上
        /// 「这段带宽的正中间」。
        /// </summary>
        private const float HitBodyCenterHz = 2450f;

        /// <summary>主体带通 Q。1.2 对应约 2 个八度的带宽，正好覆盖 1.5–4 kHz。</summary>
        private const float HitBodyQ = 1.2f;

        /// <summary>主体起手。</summary>
        private const float HitBodyAtkSec = 0.002f;

        /// <summary>主体衰减时间常数。</summary>
        private const float HitBodyTauSec = 0.018f;

        /// <summary>主体层增益。</summary>
        private const float HitBodyGain = 1.00f;

        /// <summary>
        /// 「肉感」层的正弦频率。180 Hz 这个值有一个额外用途：
        /// skill_basic_slash 全段高通在 600 Hz，两者在频域上完全不重叠，
        /// 于是「挥击 + 命中」同时发生时听成两个事件而不是糊成一声（PRD Q7）。
        /// 改这个数字之前请先看一眼 <see cref="SlashHpHz"/>。
        /// </summary>
        private const float HitMeatHz = 180f;

        /// <summary>肉感层起手。</summary>
        private const float HitMeatAtkSec = 0.001f;

        /// <summary>肉感层衰减时间常数。</summary>
        private const float HitMeatTauSec = 0.025f;

        /// <summary>肉感层增益。</summary>
        private const float HitMeatGain = 0.45f;

        private static float[] EnemyHit(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ① 瞬态（R-A）
            AddTransient(buf, fs, HitTransientSec, TransientAtkSec, HitTransientTauSec, HitTransientGain, 0f, rng);

            // ② 主体：带通噪声
            var svf = new SvfState();
            float f = SfxSynth.SvfCoeff(HitBodyCenterHz, fs);
            float q = 1f / HitBodyQ;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float bp = SfxSynth.SvfStep(ref svf, SfxSynth.WhiteNoise(rng), f, q);
                buf[i] += bp * SfxSynth.ExpEnv(t, HitBodyAtkSec, HitBodyTauSec) * HitBodyGain;
            }

            // ③ 肉感：低频正弦
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float s = SfxSynth.SweepPhase(ref phase, HitMeatHz, fs);
                buf[i] += s * SfxSynth.ExpEnv(t, HitMeatAtkSec, HitMeatTauSec) * HitMeatGain;
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ② sfx_player_hurt —— 玩家被命中（185 ms @ 22050）
        // =====================================================================
        //
        // 听感目标：钝、闷、往下掉。与敌人侧刻意做成两种材质 —— 玩家必须能在
        // 不看血条的情况下，只凭声音分辨「我挨打了」还是「我打中了」。

        /// <summary>瞬态：2 ms，比敌人侧短。</summary>
        private const float HurtTransientSec = 0.002f;

        /// <summary>瞬态衰减。3 ms，比敌人侧的 4 ms 更短 = 更钝。</summary>
        private const float HurtTransientTauSec = 0.003f;

        /// <summary>瞬态增益 0.50，只有敌人侧的六成 —— 「钝」就是瞬态弱、主体重。</summary>
        private const float HurtTransientGain = 0.50f;

        /// <summary>主体低通截止。900 Hz 以上全部切掉，这是「闷」的来源。</summary>
        private const float HurtBodyFcHz = 900f;

        /// <summary>主体起手。4 ms，明显慢于敌人侧的 2 ms。</summary>
        private const float HurtBodyAtkSec = 0.004f;

        /// <summary>主体衰减。45 ms，是敌人侧 18 ms 的 2.5 倍 = 更重、更拖。</summary>
        private const float HurtBodyTauSec = 0.045f;

        /// <summary>主体增益。</summary>
        private const float HurtBodyGain = 1.00f;

        /// <summary>下滑正弦起点。</summary>
        private const float HurtSweepF0Hz = 220f;

        /// <summary>下滑正弦终点。降一个八度，是「泄气」最直接的音高表达。</summary>
        private const float HurtSweepF1Hz = 110f;

        /// <summary>下滑扫频时长。</summary>
        private const float HurtSweepSec = 0.150f;

        /// <summary>下滑层起手。</summary>
        private const float HurtSweepAtkSec = 0.002f;

        /// <summary>下滑层衰减。</summary>
        private const float HurtSweepTauSec = 0.060f;

        /// <summary>下滑层增益。</summary>
        private const float HurtSweepGain = 0.60f;

        /// <summary>整体释放尾长度。40 ms 线性淡出，把三层一起收干净。</summary>
        private const float HurtReleaseSec = 0.040f;

        private static float[] PlayerHurt(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ① 瞬态（R-A）
            AddTransient(buf, fs, HurtTransientSec, TransientAtkSec, HurtTransientTauSec, HurtTransientGain, 0f, rng);

            // ② 主体：低通噪声。两级一阶级联 —— 单级一阶的 6 dB/oct 太缓，
            //    900 Hz 以上还剩太多高频，听起来是「沙」不是「闷」。
            float a = SfxSynth.OnePoleCoeff(HurtBodyFcHz, fs);
            float lp1 = 0f;
            float lp2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float x = SfxSynth.WhiteNoise(rng);
                float y = SfxSynth.OnePoleLP(ref lp1, x, a);
                y = SfxSynth.OnePoleLP(ref lp2, y, a);
                buf[i] += y * SfxSynth.ExpEnv(t, HurtBodyAtkSec, HurtBodyTauSec) * HurtBodyGain;
            }

            // ③ 下滑正弦：指数扫频，不是线性（线性扫下去前半段降得飞快、后半段几乎不动）
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float f = SfxSynth.ExpSweep(HurtSweepF0Hz, HurtSweepF1Hz, t, HurtSweepSec);
                float s = SfxSynth.SweepPhase(ref phase, f, fs);
                buf[i] += s * SfxSynth.ExpEnv(t, HurtSweepAtkSec, HurtSweepTauSec) * HurtSweepGain;
            }

            // ④ 整体释放尾
            SfxSynth.ApplyTailFade(buf, Samples(HurtReleaseSec, fs));

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ③ sfx_enemy_death —— 杂兵死亡（300 ms @ 22050）
        // =====================================================================
        //
        // 【★ 这张配方刻意没有瞬态层，别照着 R-A 给它补一个】
        // 它表达的是「溃散」不是「爆炸」。死亡发生在最后一次命中之后，而那一下
        // 命中音**自己已经带瞬态了** —— 再叠一个瞬态会听成「打了两下」。
        // 这里要的是命中音的余韵接着散开，所以是慢起手 + 平台 + 消失。
        //
        // 「散了」在 DSP 上的表达是**带宽收窄**：中心频率往下掉的同时 Q 往上抬，
        // 一团宽频噪声逐渐收成一根越来越细的线，最后消失。这是与
        // sfx_boss_shockwave（带宽张开）完全相反的一条曲线，两者可以对照着听。

        /// <summary>中心频率扫频起点。</summary>
        private const float DeathSweepF0Hz = 2200f;

        /// <summary>中心频率扫频终点。</summary>
        private const float DeathSweepF1Hz = 400f;

        /// <summary>中心频率扫频时长。</summary>
        private const float DeathSweepSec = 0.280f;

        /// <summary>Q 起点（宽）。</summary>
        private const float DeathQStart = 1.0f;

        /// <summary>Q 终点（窄）。Q 上升 = 带宽收窄 = 「散了」。</summary>
        private const float DeathQEnd = 4.0f;

        /// <summary>起手。</summary>
        private const float DeathAtkSec = 0.008f;

        /// <summary>平台时长。没有这 60 ms，听感是「打了一下」而不是「散开一片」。</summary>
        private const float DeathHoldSec = 0.060f;

        /// <summary>平台之后的衰减时间常数。约为剩余时长的 1/3，到 300 ms 时衰减到 5%。</summary>
        private const float DeathTauSec = 0.077f;

        private static float[] EnemyDeath(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            var svf = new SvfState();
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;

                float fc = SfxSynth.ExpSweep(DeathSweepF0Hz, DeathSweepF1Hz, t, DeathSweepSec);
                float bigQ = SfxSynth.Lerp(DeathQStart, DeathQEnd, t / DeathSweepSec);

                float f = SfxSynth.SvfCoeff(fc, fs);
                float q = 1f / bigQ;

                float bp = SfxSynth.SvfStep(ref svf, SfxSynth.WhiteNoise(rng), f, q);
                buf[i] = bp * SfxSynth.PlateauEnv(t, DeathAtkSec, DeathHoldSec, DeathTauSec);
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ④ sfx_boss_death —— BOSS 死亡（1150 ms @ 22050，豁免抢占）
        // =====================================================================
        //
        // 听感目标：一锤定音。全场最重的一声，也是唯一超过一秒的战斗音效。
        // 三层结构：低频冲击（身体倒下）+ 石磬泛音（仪式感）+ 混响垫（空间）。

        /// <summary>低频冲击扫频起点。</summary>
        private const float BdSweepF0Hz = 85f;

        /// <summary>低频冲击扫频终点。只降 25 Hz —— 再多就没有「沉住」的感觉了。</summary>
        private const float BdSweepF1Hz = 60f;

        /// <summary>低频冲击扫频时长。</summary>
        private const float BdSweepSec = 0.400f;

        /// <summary>低频冲击起手。</summary>
        private const float BdSweepAtkSec = 0.003f;

        /// <summary>低频冲击衰减。</summary>
        private const float BdSweepTauSec = 0.200f;

        /// <summary>低频冲击增益。</summary>
        private const float BdSweepGain = 1.00f;

        /// <summary>石磬基频。</summary>
        private const float BdBellF0Hz = 520f;

        /// <summary>石磬泛音比（R-C：管钟实测值，不许改成整数倍）。</summary>
        private static readonly float[] BdBellRatios = new float[] { 1.00f, 2.76f, 5.40f };

        /// <summary>石磬各泛音衰减（秒）。高次泛音衰减更快，这是所有敲击体的物理规律。</summary>
        private static readonly float[] BdBellDecays = new float[] { 0.700f, 0.400f, 0.240f };

        /// <summary>石磬层增益。</summary>
        private const float BdBellGain = 0.45f;

        /// <summary>混响垫低通截止。</summary>
        private const float BdPadFcHz = 1200f;

        /// <summary>混响垫衰减。</summary>
        private const float BdPadTauSec = 0.380f;

        /// <summary>混响垫增益。</summary>
        private const float BdPadGain = 0.30f;

        /// <summary>
        /// 梳状混响的四条延迟（秒）。37 / 53 / 71 / 97 都是**质数**毫秒 ——
        /// 互质的延迟长度不会产生公共周期，回声因此不会叠成一个可辨识的音高
        /// （那种「金属罐子」音染是廉价混响最典型的破绽）。
        /// </summary>
        private static readonly float[] BdReverbDelaysSec = new float[] { 0.037f, 0.053f, 0.071f, 0.097f };

        /// <summary>梳状混响反馈增益。</summary>
        private const float BdReverbG = 0.40f;

        private static float[] BossDeath(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ① 低频冲击
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float f = SfxSynth.ExpSweep(BdSweepF0Hz, BdSweepF1Hz, t, BdSweepSec);
                float s = SfxSynth.SweepPhase(ref phase, f, fs);
                buf[i] += s * SfxSynth.ExpEnv(t, BdSweepAtkSec, BdSweepTauSec) * BdSweepGain;
            }

            // ② 石磬泛音（R-C）
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                buf[i] += SfxSynth.InharmonicBell(BdBellRatios, BdBellDecays, BdBellF0Hz, t) * BdBellGain;
            }

            // ③ 混响垫：低通噪声
            float a = SfxSynth.OnePoleCoeff(BdPadFcHz, fs);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float y = SfxSynth.OnePoleLP(ref lp, SfxSynth.WhiteNoise(rng), a);
                buf[i] += y * SfxSynth.ExpEnv(t, SineAtkSec, BdPadTauSec) * BdPadGain;
            }

            // ④ 整段过梳状混响。★ 必须在三层都叠完之后做 —— 混响是空间属性，
            //    分层做混响等于把三层放进三个不同的房间。
            SfxSynth.CombReverb(buf, DelaySamples(BdReverbDelaysSec, fs), BdReverbG);

            Finish(buf, spec);
            return buf;
        }

        /// <summary>把一组以秒为单位的延迟换算成样本数。</summary>
        private static int[] DelaySamples(float[] secs, int fs)
        {
            var result = new int[secs.Length];
            for (int k = 0; k < secs.Length; k++)
            {
                result[k] = Samples(secs[k], fs);
            }
            return result;
        }

        // =====================================================================
        // ⑤ sfx_boss_phase —— BOSS 阶段转换（850 ms @ 22050）
        // =====================================================================
        //
        // 听感目标：压迫感。它不是一次撞击，是一次**预告** ——「更难的部分要来了」。
        //
        // 【★ 这是全表最接近「科幻」的一条，也是 C-2 最可能的元凶】
        // 700 ms 的上行滑音天生带能量武器味。它靠两件事把自己拉回来：
        //   ① 全程在扫频，不是定频（R-B 的唯一豁免依据）；
        //   ② 520 ms 处那一记非谐波钟击 —— 钟一响，前面的滑音就被重新解释成
        //      「钟声之前的蓄势」而不是「充能」。
        // 若实听仍觉得科幻，退路是把 ① 的正弦换成滤波噪声扫频。

        /// <summary>上行滑音起点。</summary>
        private const float BpSweepF0Hz = 70f;

        /// <summary>上行滑音终点。</summary>
        private const float BpSweepF1Hz = 190f;

        /// <summary>上行滑音时长。</summary>
        private const float BpSweepSec = 0.700f;

        /// <summary>上行滑音起始振幅。</summary>
        private const float BpAmpStart = 0.20f;

        /// <summary>
        /// 上行滑音终止振幅。振幅按 t² 而不是线性爬升 —— 缓入才是「压迫感」，
        /// 线性爬升听起来像「音量旋钮被匀速拧大」，那是机械动作不是威胁。
        /// </summary>
        private const float BpAmpEnd = 0.80f;

        /// <summary>钟击进入时刻。★ 它迟到 520 ms 才进来，这个「等待」本身就是压迫感的一部分。</summary>
        private const float BpBellStartSec = 0.520f;

        /// <summary>钟击基频。</summary>
        private const float BpBellF0Hz = 880f;

        /// <summary>钟击泛音比（R-C：四阶管钟实测值）。</summary>
        private static readonly float[] BpBellRatios = new float[] { 1.00f, 2.76f, 5.40f, 8.93f };

        /// <summary>钟击衰减（秒）。只给一个值，四个泛音共用 —— InharmonicBell 支持这种写法。</summary>
        private static readonly float[] BpBellDecays = new float[] { 0.330f };

        /// <summary>钟击增益。</summary>
        private const float BpBellGain = 0.50f;

        /// <summary>噪声垫低通截止。</summary>
        private const float BpPadFcHz = 500f;

        /// <summary>噪声垫增益。</summary>
        private const float BpPadGain = 0.25f;

        /// <summary>
        /// 收尾释放时长。架构文档没有给这一层 —— 但滑音层在 700 ms 后振幅停在 0.8，
        /// 到 850 ms 直接截断就是一个大阶跃。这 100 ms 指数收尾是必需的补充。
        /// </summary>
        private const float BpReleaseSec = 0.100f;

        private static float[] BossPhase(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ① 上行滑音，振幅按 t² 缓入
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float f = SfxSynth.ExpSweep(BpSweepF0Hz, BpSweepF1Hz, t, BpSweepSec);
                float s = SfxSynth.SweepPhase(ref phase, f, fs);

                float k = SfxSynth.Clamp01(t / BpSweepSec);
                float amp = SfxSynth.Lerp(BpAmpStart, BpAmpEnd, k * k);
                buf[i] += s * amp;
            }

            // ② 钟击：t = 520 ms 才进来（R-C）
            int bellStart = Samples(BpBellStartSec, fs);
            for (int i = bellStart; i < n; i++)
            {
                float tb = (i - bellStart) / (float)fs;
                buf[i] += SfxSynth.InharmonicBell(BpBellRatios, BpBellDecays, BpBellF0Hz, tb) * BpBellGain;
            }

            // ③ 低通噪声垫，与滑音同步渐强
            float a = SfxSynth.OnePoleCoeff(BpPadFcHz, fs);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float y = SfxSynth.OnePoleLP(ref lp, SfxSynth.WhiteNoise(rng), a);
                float k = SfxSynth.Clamp01(t / BpSweepSec);
                buf[i] += y * k * BpPadGain;
            }

            // ④ 收尾释放：τ 取剩余长度的 1/3，到末尾衰减到约 5%
            int releaseSamples = Samples(BpReleaseSec, fs);
            SfxSynth.ApplyExpFadeFrom(buf, n - releaseSamples, releaseSamples / 3f);

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑥ sfx_boss_summon —— BOSS 召唤（500 ms @ 22050）
        // =====================================================================
        //
        // 听感目标：几粒清亮的东西被抛进空间里。
        //
        // 【★ 「全程零低频」是它与 sfx_boss_phase 唯一的区分手段，不是装饰】
        // 两者都是 BOSS 的非攻击事件、都用非谐波钟、时长量级也接近。玩家要在
        // 混战中瞬间分辨「他要变强了」和「他叫人了」，靠的只能是频谱位置：
        // phase 全在低频往上爬，summon 全在高频。所以最后那道 800 Hz 高通
        // **必须在混响之后**执行 —— 混响会把能量往低频糊，先高通再混响等于白做。

        /// <summary>五颗颗粒的起始时刻（秒）。间隔逐渐拉大 = 「撒出去」而不是「敲鼓点」。</summary>
        private static readonly float[] BsGrainStartSec = new float[] { 0.000f, 0.060f, 0.130f, 0.210f, 0.300f };

        /// <summary>五颗颗粒的基频（Hz）。刻意不单调递增，避免听成音阶。</summary>
        private static readonly float[] BsGrainF0Hz = new float[] { 1560f, 1840f, 2100f, 1720f, 1980f };

        /// <summary>颗粒泛音比（R-C）。只用两阶 —— 颗粒很短，第三阶来不及被听见。</summary>
        private static readonly float[] BsGrainRatios = new float[] { 1.00f, 2.76f };

        /// <summary>颗粒衰减（秒）。</summary>
        private static readonly float[] BsGrainDecays = new float[] { 0.120f };

        /// <summary>颗粒增益。</summary>
        private const float BsGrainGain = 0.50f;

        /// <summary>
        /// 颗粒失谐幅度（±2%）。这是本配方唯一用到随机流的地方。
        /// 五颗完全准确的钟听起来是「电子提示音」，±2% 的失谐让它变成「五个物件」。
        /// </summary>
        private const float BsDetune = 0.02f;

        /// <summary>空间回响延迟（秒）。同样取互质毫秒值，理由见 <see cref="BdReverbDelaysSec"/>。</summary>
        private static readonly float[] BsReverbDelaysSec = new float[] { 0.023f, 0.031f, 0.043f };

        /// <summary>空间回响反馈增益。</summary>
        private const float BsReverbG = 0.35f;

        /// <summary>最终高通截止。见本节顶部注释：这道高通是它的身份证。</summary>
        private const float BsHighpassHz = 800f;

        private static float[] BossSummon(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ①② 五颗颗粒
            for (int g = 0; g < BsGrainStartSec.Length; g++)
            {
                int start = Samples(BsGrainStartSec[g], fs);
                if (start >= n)
                {
                    continue;
                }

                // 失谐：[-BsDetune, +BsDetune] 的相对偏移
                float detune = 1f + (float)((rng.NextDouble() * 2.0 - 1.0) * BsDetune);
                float f0 = BsGrainF0Hz[g] * detune;

                for (int i = start; i < n; i++)
                {
                    float tb = (i - start) / (float)fs;
                    buf[i] += SfxSynth.InharmonicBell(BsGrainRatios, BsGrainDecays, f0, tb) * BsGrainGain;
                }
            }

            // ③ 空间回响
            SfxSynth.CombReverb(buf, DelaySamples(BsReverbDelaysSec, fs), BsReverbG);

            // ④ ★ 最后一道：全程零低频。顺序不可与 ③ 交换，理由见本节顶部。
            float hpCoeff = SfxSynth.OnePoleCoeff(BsHighpassHz, fs);
            float hpState = 0f;
            for (int i = 0; i < n; i++)
            {
                buf[i] = SfxSynth.OnePoleHP(ref hpState, buf[i], hpCoeff);
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑦ sfx_boss_shockwave —— BOSS 冲击波（650 ms @ 22050，豁免抢占）
        // =====================================================================
        //
        // 听感目标：「你该躲了」。这是全期唯一一个承担**玩法信号**职责的音效 ——
        // 它被 voice 抢占豁免保护，就是因为漏听它等于挨一下。
        //
        // 【与 sfx_enemy_death 完全相反的一条曲线】
        // enemy_death 是带宽收窄（散了），这里是带宽张开（推过来）。两条曲线放在
        // 一起听能立刻分辨，这是有意的设计对称，不要单独改其中一条。

        /// <summary>硬瞬态时长。4 ms，全表最长的瞬态 ——「瞬态必须够硬」。</summary>
        private const float BwTransientSec = 0.004f;

        /// <summary>硬瞬态衰减。</summary>
        private const float BwTransientTauSec = 0.005f;

        /// <summary>硬瞬态增益。</summary>
        private const float BwTransientGain = 0.90f;

        /// <summary>低频推力扫频起点。</summary>
        private const float BwLowF0Hz = 90f;

        /// <summary>低频推力扫频终点。45 Hz 已经在「听到」与「感到」的边界上。</summary>
        private const float BwLowF1Hz = 45f;

        /// <summary>
        /// 低频推力扫频时长。架构文档只给了 90→45 Hz 没给时长，这里取 250 ms ——
        /// 比包络的 τ=110 ms 长一倍多，保证「还在往下走的时候声音已经在退」，
        /// 那是物理冲击波远离的听感。
        /// </summary>
        private const float BwLowSweepSec = 0.250f;

        /// <summary>低频推力起手。</summary>
        private const float BwLowAtkSec = 0.001f;

        /// <summary>低频推力衰减。</summary>
        private const float BwLowTauSec = 0.110f;

        /// <summary>低频推力增益。</summary>
        private const float BwLowGain = 1.00f;

        /// <summary>带通中心扫频起点。</summary>
        private const float BwBandF0Hz = 120f;

        /// <summary>
        /// 带通中心扫频终点。
        /// ⚠️ 在 22050 Hz 采样率下，SVF 的稳定性钳制会把实际上端压到约 3.7 kHz
        /// （见 SfxSynth.SvfClamp 的注释）。听感上「带宽张开」主要由 Q 的下降驱动，
        /// 影响有限；若实听觉得张不开，把本音效的 SampleRate 改成 44100 即可。
        /// </summary>
        private const float BwBandF1Hz = 5000f;

        /// <summary>带通中心扫频时长。</summary>
        private const float BwBandSweepSec = 0.400f;

        /// <summary>带通 Q 起点（极窄）。</summary>
        private const float BwQStart = 6.0f;

        /// <summary>带通 Q 终点（极宽）。Q 下降 = 带宽张开。</summary>
        private const float BwQEnd = 0.8f;

        /// <summary>带通层起手。架构文档未给，取 3 ms —— 前 4 ms 由硬瞬态覆盖，听不出来。</summary>
        private const float BwBandAtkSec = 0.003f;

        /// <summary>带通层增益。架构文档未给，取 1.0（它是主体层）。</summary>
        private const float BwBandGain = 1.00f;

        /// <summary>整体指数淡出起点。</summary>
        private const float BwFadeStartSec = 0.400f;

        private static float[] BossShockwave(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ① 硬瞬态（R-A）
            AddTransient(buf, fs, BwTransientSec, TransientAtkSec, BwTransientTauSec, BwTransientGain, 0f, rng);

            // ② 低频推力
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float f = SfxSynth.ExpSweep(BwLowF0Hz, BwLowF1Hz, t, BwLowSweepSec);
                float s = SfxSynth.SweepPhase(ref phase, f, fs);
                buf[i] += s * SfxSynth.ExpEnv(t, BwLowAtkSec, BwLowTauSec) * BwLowGain;
            }

            // ③ 带通中心上扫 + Q 下降（带宽张开）
            var svf = new SvfState();
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;

                float fc = SfxSynth.ExpSweep(BwBandF0Hz, BwBandF1Hz, t, BwBandSweepSec);
                float bigQ = SfxSynth.Lerp(BwQStart, BwQEnd, t / BwBandSweepSec);

                float f = SfxSynth.SvfCoeff(fc, fs);
                float q = 1f / bigQ;

                float bp = SfxSynth.SvfStep(ref svf, SfxSynth.WhiteNoise(rng), f, q);

                // 只做一段起手斜坡，不做衰减 —— 衰减由 ④ 统一负责，
                // 两处都做会得到一条比设计更陡的曲线。
                float ramp = t < BwBandAtkSec ? t / BwBandAtkSec : 1f;
                buf[i] += bp * ramp * BwBandGain;
            }

            // ④ 400 → 650 ms 指数淡出
            int fadeStart = Samples(BwFadeStartSec, fs);
            int fadeLen = n - fadeStart;
            if (fadeLen > 0)
            {
                SfxSynth.ApplyExpFadeFrom(buf, fadeStart, fadeLen / 3f);
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑧ sfx_poise_break —— 韧性打破（220 ms @ 44100）
        // =====================================================================
        //
        // 听感目标：脆。像一块薄石片裂开，而不是像什么东西爆炸。
        // 44100 采样率在这里不是奢侈：裂纹层最高一根在 6.3 kHz，
        // 22050 的奈奎斯特（11025 Hz）虽然放得下，但抗混叠余量太小，会发糊。

        /// <summary>主瞬态时长。</summary>
        private const float PbTransientSec = 0.002f;

        /// <summary>主瞬态衰减。</summary>
        private const float PbTransientTauSec = 0.012f;

        /// <summary>主瞬态增益。</summary>
        private const float PbTransientGain = 1.00f;

        /// <summary>主瞬态高通。把 2.5 kHz 以下全切掉，「咔」才不会变成「咚」。</summary>
        private const float PbTransientHpHz = 2500f;

        /// <summary>裂纹层基频（3.1 kHz）。</summary>
        private const float PbCrackF0Hz = 3100f;

        /// <summary>
        /// 裂纹层泛音比（R-C）。对应 3.1 / 4.7 / 6.3 kHz 三根：
        /// 4700 / 3100 = 1.5161，6300 / 3100 = 2.0323。
        /// 注意 2.0323 **刻意不是** 2.0 —— 差这 1.6% 就是「石片」与「电子提示音」
        /// 的分界。整数倍会立刻听成谐波，那是合成器的声音。
        /// </summary>
        private static readonly float[] PbCrackRatios = new float[] { 1.0000f, 1.5161f, 2.0323f };

        /// <summary>裂纹层衰减（秒）。</summary>
        private static readonly float[] PbCrackDecays = new float[] { 0.030f };

        /// <summary>裂纹层增益。</summary>
        private const float PbCrackGain = 0.40f;

        /// <summary>body 正弦频率。</summary>
        private const float PbBodyHz = 400f;

        /// <summary>body 衰减。</summary>
        private const float PbBodyTauSec = 0.045f;

        /// <summary>body 增益。很低 —— 它只是给「脆」垫一点点分量，多了就不脆了。</summary>
        private const float PbBodyGain = 0.25f;

        /// <summary>余韵噪声环中心频率。</summary>
        private const float PbRingCenterHz = 4700f;

        /// <summary>余韵噪声环 Q。</summary>
        private const float PbRingQ = 3.0f;

        /// <summary>余韵噪声环增益。极低电平，它只负责「碎屑还在落」这个暗示。</summary>
        private const float PbRingGain = 0.08f;

        private static float[] PoiseBreak(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ① 主瞬态（R-A），带 2.5 kHz 高通
            AddTransient(buf, fs, PbTransientSec, TransientAtkSec, PbTransientTauSec,
                         PbTransientGain, PbTransientHpHz, rng);

            // ② 裂纹：三根失谐正弦（R-C）
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                buf[i] += SfxSynth.InharmonicBell(PbCrackRatios, PbCrackDecays, PbCrackF0Hz, t) * PbCrackGain;
            }

            // ③ body
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float s = SfxSynth.SweepPhase(ref phase, PbBodyHz, fs);
                buf[i] += s * SfxSynth.ExpEnv(t, SineAtkSec, PbBodyTauSec) * PbBodyGain;
            }

            // ④ 余韵：带通噪声环，线性淡出到全长末尾
            var svf = new SvfState();
            float f = SfxSynth.SvfCoeff(PbRingCenterHz, fs);
            float q = 1f / PbRingQ;
            for (int i = 0; i < n; i++)
            {
                float bp = SfxSynth.SvfStep(ref svf, SfxSynth.WhiteNoise(rng), f, q);
                float fade = 1f - i / (float)n;
                buf[i] += bp * fade * PbRingGain;
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑨ skill_basic_slash —— 水剑斩（150 ms @ 44100）
        // =====================================================================
        //
        // 听感目标：薄。它每秒要响好几次，还经常与 sfx_enemy_hit 同时发生。
        //
        // 【★ 它的归一化目标是全场最低的 0.72，这是刻意的】
        // 普攻音效抢戏是打击感设计里最常见的错误 —— 挥击本身不该比命中更响，
        // 否则玩家分不清「我挥了」和「我打中了」，而后者才是需要被听见的信息。
        //
        // 【★ 全段高通 600 Hz：这是频谱分离，不是单纯降音量】
        // sfx_enemy_hit 的「肉感」层在 180 Hz，本音效 600 Hz 以下全空。
        // 两者在频域上不重叠，即使时域完全重合也不会糊成一声，而是听成
        // 「高频破空 + 低频闷响」两个事件。改这两个数字之一时请一起看另一个。
        //
        // 刻意没有瞬态层：这是破空不是撞击（见文件头 R-A 的例外说明）。

        /// <summary>带通中心扫频起点。</summary>
        private const float SlashF0Hz = 4000f;

        /// <summary>带通中心扫频终点。</summary>
        private const float SlashF1Hz = 1000f;

        /// <summary>带通中心扫频时长。110 ms，比全长 150 ms 短 —— 尾巴留给衰减。</summary>
        private const float SlashSweepSec = 0.110f;

        /// <summary>带通 Q。2.5 偏窄，「薄」就是窄。</summary>
        private const float SlashQ = 2.5f;

        /// <summary>起手。</summary>
        private const float SlashAtkSec = 0.005f;

        /// <summary>衰减。</summary>
        private const float SlashTauSec = 0.035f;

        /// <summary>全段高通截止。见本节顶部注释。</summary>
        private const float SlashHpHz = 600f;

        private static float[] BasicSlash(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ①② 带通噪声 + 包络
            var svf = new SvfState();
            float q = 1f / SlashQ;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float fc = SfxSynth.ExpSweep(SlashF0Hz, SlashF1Hz, t, SlashSweepSec);
                float f = SfxSynth.SvfCoeff(fc, fs);
                float bp = SfxSynth.SvfStep(ref svf, SfxSynth.WhiteNoise(rng), f, q);
                buf[i] = bp * SfxSynth.ExpEnv(t, SlashAtkSec, SlashTauSec);
            }

            // ③ ★ 整段高通，必须是最后一步。带通扫到 1000 Hz 时裙边已经探进
            //    600 Hz 以下，先高通再扫等于没高通。
            float hpCoeff = SfxSynth.OnePoleCoeff(SlashHpHz, fs);
            float hpState = 0f;
            for (int i = 0; i < n; i++)
            {
                buf[i] = SfxSynth.OnePoleHP(ref hpState, buf[i], hpCoeff);
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑩ skill_circle_burst —— 法阵冲击（425 ms @ 22050）
        // =====================================================================
        //
        // 听感目标：一圈东西「呼」地推开。中心频率固定、只有带宽在变 ——
        // 这是它与 sfx_boss_shockwave（中心频率也在扫）的区别：
        // 法阵是原地扩张，冲击波是朝你推过来。

        /// <summary>带通中心频率，固定不动。</summary>
        private const float CbCenterHz = 700f;

        /// <summary>Q 起点（极窄）。</summary>
        private const float CbQStart = 5.0f;

        /// <summary>Q 终点（宽）。</summary>
        private const float CbQEnd = 0.6f;

        /// <summary>Q 扫动时长。180 ms 完成张开，之后维持。</summary>
        private const float CbQSweepSec = 0.180f;

        /// <summary>起手。6 ms，与 blood_lotus 的 25 ms 形成刻意对比（快 vs 粘）。</summary>
        private const float CbAtkSec = 0.006f;

        /// <summary>平台时长。</summary>
        private const float CbHoldSec = 0.080f;

        /// <summary>平台后衰减。</summary>
        private const float CbTauSec = 0.110f;

        /// <summary>body 正弦频率。</summary>
        private const float CbBodyHz = 150f;

        /// <summary>body 衰减。</summary>
        private const float CbBodyTauSec = 0.090f;

        /// <summary>body 增益。</summary>
        private const float CbBodyGain = 0.50f;

        private static float[] CircleBurst(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ①② 带通噪声，中心固定、Q 快速下降
            var svf = new SvfState();
            float f = SfxSynth.SvfCoeff(CbCenterHz, fs);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float bigQ = SfxSynth.Lerp(CbQStart, CbQEnd, t / CbQSweepSec);
                float q = 1f / bigQ;
                float bp = SfxSynth.SvfStep(ref svf, SfxSynth.WhiteNoise(rng), f, q);
                buf[i] = bp * SfxSynth.PlateauEnv(t, CbAtkSec, CbHoldSec, CbTauSec);
            }

            // ③ body 正弦
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float s = SfxSynth.SweepPhase(ref phase, CbBodyHz, fs);
                buf[i] += s * SfxSynth.ExpEnv(t, CbAtkSec, CbBodyTauSec) * CbBodyGain;
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑪ skill_blood_lotus —— 血莲侵蚀（600 ms @ 22050）
        // =====================================================================
        //
        // 听感目标：粘稠、不祥。全表唯一一个「慢起手」的音效。
        //
        // 【90 Hz 与 128 Hz 这一对失谐是它的核心，不要「调准」】
        // 128 / 90 = 1.422，约等于增四度（三全音），西方音乐传统里的
        // 「魔鬼音程」。两个频率差 38 Hz 还会产生每秒 38 次的拍频，
        // 那就是「颤」的物理来源之一。把它们调成八度或纯五度，这个音效
        // 立刻变成「充能完毕」的正面音。

        /// <summary>低通噪声截止。</summary>
        private const float BlNoiseFcHz = 350f;

        /// <summary>低频正弦 A。</summary>
        private const float BlSineAHz = 90f;

        /// <summary>低频正弦 B。与 A 构成三全音，见本节顶部注释。</summary>
        private const float BlSineBHz = 128f;

        /// <summary>噪声层增益。架构文档未给三层的相对增益，这里按「噪声打底、双音在上」分配。</summary>
        private const float BlNoiseGain = 1.00f;

        /// <summary>正弦 A 增益。</summary>
        private const float BlSineAGain = 0.55f;

        /// <summary>正弦 B 增益。比 A 略低，让 A 是根音、B 是「不对劲的那一根」。</summary>
        private const float BlSineBGain = 0.45f;

        /// <summary>颤动 LFO 频率。6 Hz 落在「不安」的心理声学区间（4~8 Hz 最令人焦躁）。</summary>
        private const float BlLfoHz = 6f;

        /// <summary>颤动 LFO 基线。</summary>
        private const float BlLfoBase = 0.72f;

        /// <summary>颤动 LFO 深度。0.72 ± 0.28 —— 不到 0，所以是「颤」不是「断续」。</summary>
        private const float BlLfoDepth = 0.28f;

        /// <summary>起手。★ 25 ms 是全表最慢的起手 = 粘稠。与 circle_burst 的 6 ms 是刻意对照。</summary>
        private const float BlAtkSec = 0.025f;

        /// <summary>衰减。</summary>
        private const float BlTauSec = 0.200f;

        /// <summary>高频薄雾中心（3–5 kHz 的几何中心）。</summary>
        private const float BlMistCenterHz = 3873f;

        /// <summary>高频薄雾 Q。</summary>
        private const float BlMistQ = 1.5f;

        /// <summary>高频薄雾增益。极低 —— 它只是让声音「不干净」，不该被单独听见。</summary>
        private const float BlMistGain = 0.08f;

        private static float[] BloodLotus(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            // ①②③ 低通噪声 + 两根失谐正弦，整体乘 LFO 再乘包络
            float a = SfxSynth.OnePoleCoeff(BlNoiseFcHz, fs);
            float lp = 0f;
            double phaseA = 0.0;
            double phaseB = 0.0;

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;

                float noise = SfxSynth.OnePoleLP(ref lp, SfxSynth.WhiteNoise(rng), a) * BlNoiseGain;
                float sineA = SfxSynth.SweepPhase(ref phaseA, BlSineAHz, fs) * BlSineAGain;
                float sineB = SfxSynth.SweepPhase(ref phaseB, BlSineBHz, fs) * BlSineBGain;

                float lfo = BlLfoBase + BlLfoDepth * Lfo(BlLfoHz, t);
                float env = SfxSynth.ExpEnv(t, BlAtkSec, BlTauSec);

                buf[i] = (noise + sineA + sineB) * lfo * env;
            }

            // ④ 高频薄雾。架构文档没给它包络 —— 但不给包络就会在 t=0 处硬起、
            //    在末尾硬断，两头各一声咔哒。这里复用主层的包络，最省事也最自然。
            var svf = new SvfState();
            float f = SfxSynth.SvfCoeff(BlMistCenterHz, fs);
            float q = 1f / BlMistQ;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;
                float bp = SfxSynth.SvfStep(ref svf, SfxSynth.WhiteNoise(rng), f, q);
                buf[i] += bp * SfxSynth.ExpEnv(t, BlAtkSec, BlTauSec) * BlMistGain;
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑫ dodge_roll —— 踏雪闪避（250 ms @ 22050）
        // =====================================================================
        //
        // 【★ 注意这个 key 没有 skill_ 前缀，它就叫 dodge_roll】
        // 这是内核既成事实（SkillConfig.SKILL_DODGE_ROLL = "dodge_roll"），
        // 不要「顺手统一」。本文件不接触字符串，AudioConfig 那边引的是常量，
        // 所以这条风险在编译期已经被消掉了 —— 这条注释只是为了让你别去改内核。
        //
        // 听感目标：一阵风从耳边过去。归一化目标 0.75 也是刻意压低的 ——
        // 它的作用是确认「我躲了」，不是抢戏。
        //
        // 刻意没有瞬态层：掠过不是撞击（见文件头 R-A 的例外说明）。

        /// <summary>高通截止。1800 Hz 以下全切 —— 风声没有低频，有低频就变成「摔了一跤」。</summary>
        private const float DrHpHz = 1800f;

        /// <summary>
        /// 掠过窗的峰值位置（占全长比例）。
        /// 40% 而不是 50%：真实的掠过声总是「来得快、去得慢」（尾迹）。
        /// </summary>
        private const float DrWindowPeak = 0.40f;

        /// <summary>带通中心：起点。</summary>
        private const float DrCenterAHz = 2500f;

        /// <summary>带通中心：峰值处（最亮的一刻，与振幅峰值同步）。</summary>
        private const float DrCenterBHz = 4500f;

        /// <summary>带通中心：终点。落回比起点更低，是「远离」的听感。</summary>
        private const float DrCenterCHz = 2000f;

        /// <summary>带通 Q。</summary>
        private const float DrQ = 1.2f;

        private static float[] DodgeRoll(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int n = SampleCount(spec);
            var buf = new float[n];

            float total = spec.DurationSec;
            float peakSec = DrWindowPeak * total;

            var svf = new SvfState();
            float q = 1f / DrQ;
            float hpCoeff = SfxSynth.OnePoleCoeff(DrHpHz, fs);
            float hpState = 0f;

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)fs;

                // ① 高通噪声
                float x = SfxSynth.OnePoleHP(ref hpState, SfxSynth.WhiteNoise(rng), hpCoeff);

                // ③ 中心频率 2500 → 4500 → 2000，两段都用指数插值（听感上才是匀速）
                float fc;
                if (t < peakSec)
                {
                    fc = SfxSynth.ExpSweep(DrCenterAHz, DrCenterBHz, t, peakSec);
                }
                else
                {
                    fc = SfxSynth.ExpSweep(DrCenterBHz, DrCenterCHz, t - peakSec, total - peakSec);
                }

                float f = SfxSynth.SvfCoeff(fc, fs);
                float bp = SfxSynth.SvfStep(ref svf, x, f, q);

                // ② 掠过窗
                buf[i] = bp * SfxSynth.RaisedCosineWindow(t, total, DrWindowPeak);
            }

            Finish(buf, spec);
            return buf;
        }

        // =====================================================================
        // ⑬ amb_wind_loop —— 环境衬底（12.0 s @ 22050，RMS 归一化，无缝循环）
        // =====================================================================
        //
        // 【★★ 无缝循环有两个条件，缺一不可。这是本期最容易做对 90% 却栽在
        //      最后一步的地方。】
        //
        //   条件一（在本函数里）：所有 LFO 周期必须整除循环总长。
        //     f₁ = 1/L、f₂ = 2/L、f₃ = 3/L，于是 t = L 时三个 LFO 的相位与 t = 0
        //     **逐位相同**，包络层面不存在接缝。注意这三个频率是从 spec.DurationSec
        //     **算出来**的，不是写死的 0.0833 / 0.1667 / 0.25 —— 这样把循环长度
        //     从 12 s 改成 8 s 时，无缝性自动保持。写死就会在改长度那天悄悄失效。
        //
        //   条件二（在 SfxSynth.CrossfadeLoop 里）：噪声本身必须**等功率**交叉淡化。
        //     LFO 相位对齐只解决了包络的连续性，底层噪声在 t=L 和 t=0 是两段完全
        //     无关的随机序列，直接首尾相接会有一个阶跃 → 一声咔哒。
        //     而交叉淡化必须是 sin/cos 而不是线性，否则每 12 秒一次 3 dB 音量凹陷
        //     （听起来像「喘一口气」），完整推导见那个函数的注释。
        //
        // 【★ 本配方是全文件唯一不调用 Finish() 的】
        // Finish 会在末尾加一段淡出 —— 那正好会把刚做好的无缝循环毁掉，
        // 变成每 12 秒一次的「音量掉下去再回来」。这不是疏漏，是必须的例外。
        //
        // 【为什么用 RMS 归一化而不是峰值】
        // 噪声的峰值由极少数离群样本决定，与感知响度几乎无关。一段风声按峰值
        // 归一化到 0.89，听起来会响得离谱。详见 NormalizeMode 的注释。

        /// <summary>主层低通中心截止。</summary>
        private const float WlMainFcBaseHz = 500f;

        /// <summary>主层低通截止摆幅。500 ± 200 → 300~700 Hz。</summary>
        private const float WlMainFcDepthHz = 200f;

        /// <summary>主层振幅基线。</summary>
        private const float WlMainAmpBase = 0.775f;

        /// <summary>主层振幅摆幅。0.775 ± 0.225 → 0.55~1.0。</summary>
        private const float WlMainAmpDepth = 0.225f;

        /// <summary>主层 Q。低通不需要谐振，取 0.9（略低于 1）避免截止点凸起。</summary>
        private const float WlMainQ = 0.9f;

        /// <summary>副层带通中心（800–1600 Hz 的几何中心 √(800×1600) ≈ 1131）。</summary>
        private const float WlSubCenterHz = 1131f;

        /// <summary>副层带通 Q。</summary>
        private const float WlSubQ = 1.2f;

        /// <summary>副层增益。</summary>
        private const float WlSubGain = 0.18f;

        /// <summary>副层振幅基线。</summary>
        private const float WlSubAmpBase = 0.5f;

        /// <summary>副层振幅摆幅。0.5 ± 0.5 → 0~1，副层会完全消失又回来。</summary>
        private const float WlSubAmpDepth = 0.5f;

        /// <summary>LFO₁（主层截止频率）在一个循环里的周期数。</summary>
        private const float WlLfo1Cycles = 1f;

        /// <summary>LFO₂（主层振幅）在一个循环里的周期数。</summary>
        private const float WlLfo2Cycles = 2f;

        /// <summary>
        /// LFO₃（副层振幅）在一个循环里的周期数。
        /// 取 3 而不是 1 或 2，是为了让副层有一点不同的呼吸节奏 ——
        /// 三层同起同落会听成「整体在脉动」，那反而比没有环境音更引人注意。
        /// </summary>
        private const float WlLfo3Cycles = 3f;

        private static float[] WindLoop(SfxSpec spec, System.Random rng)
        {
            int fs = spec.SampleRate;
            int loopLen = SampleCount(spec);
            int crossfade = Samples(AudioConfig.AmbienceCrossfadeSec, fs);

            // 多合成 crossfade 个样本，它们是交叉淡化的「尾部素材」。
            int total = loopLen + crossfade;
            var src = new float[total];

            // 条件一：三条 LFO 频率都是 1/L 的整数倍，由循环长度算出而非写死。
            float lfo1Hz = WlLfo1Cycles / spec.DurationSec;
            float lfo2Hz = WlLfo2Cycles / spec.DurationSec;
            float lfo3Hz = WlLfo3Cycles / spec.DurationSec;

            var svfMain = new SvfState();
            var svfSub = new SvfState();
            float qMain = 1f / WlMainQ;
            float qSub = 1f / WlSubQ;
            float fSub = SfxSynth.SvfCoeff(WlSubCenterHz, fs);

            for (int i = 0; i < total; i++)
            {
                float t = i / (float)fs;

                // 主层：白噪声 → SVF 低通（截止频率被 LFO₁ 调制），振幅被 LFO₂ 调制
                float fcMain = WlMainFcBaseHz + WlMainFcDepthHz * Lfo(lfo1Hz, t);
                float fMain = SfxSynth.SvfCoeff(fcMain, fs);
                SfxSynth.SvfStep(ref svfMain, SfxSynth.WhiteNoise(rng), fMain, qMain);
                float main = svfMain.Low * (WlMainAmpBase + WlMainAmpDepth * Lfo(lfo2Hz, t));

                // 副层：白噪声 → SVF 带通，振幅被 LFO₃ 调制
                float band = SfxSynth.SvfStep(ref svfSub, SfxSynth.WhiteNoise(rng), fSub, qSub);
                float sub = band * WlSubGain * (WlSubAmpBase + WlSubAmpDepth * Lfo(lfo3Hz, t));

                src[i] = main + sub;
            }

            // 条件二：等功率交叉淡化，输出长度收回到 loopLen。
            float[] buf = SfxSynth.CrossfadeLoop(src, loopLen, crossfade);

            // ★ 这里直接 Normalize，**不走 Finish** —— 见本节顶部说明。
            SfxSynth.Normalize(buf, spec.NormMode, spec.NormTarget);
            return buf;
        }
    }
}
