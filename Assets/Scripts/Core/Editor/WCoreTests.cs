// -----------------------------------------------------------------------------
// WCoreTests.cs —— T0 核心层回归测试（NUnit 风格）
//
// ★★★ 本环境无 dotnet / 无 NUnit，此文件**未经编译验证**，是源码交付。 ★★★
//     请在本地执行下列任一方式验证：
//       · dotnet test（把 Core/*.cs 挂进一个 net6.0+ 的 NUnit 测试工程）
//       · Unity 打开工程 → Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
//     所有断言的**数值**已由同目录 wcore_selfcheck.py 在纯 Python 侧独立复现并
//     全部通过（57/57），因此可以确信是 C# 语法层面的问题、而非算法跑偏。
//
// 【对拍基线】godot 侧 .qa_logs/qa_t01_foundation.log（r2_t01）
//     1 源 1.700 / 2 源 3.350 / 3 源 4.300 / 4 源 4.300 / 8 源 4.300（次/s，20s 窗口）
//     围攻 / 单挑倍率 = 2.53x
//     C8 软上限：强灌 200 来源不 tick → 裁剪后 32 条
//
// 【本文件同样不得引用 UnityEngine】—— 保持 Core 层可脱离引擎独立测试。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Xianxia.Core.Tests
{
    /// <summary>W-CORE 承伤模型的频率对拍与闸门语义测试。</summary>
    [TestFixture]
    public class WCoreTests
    {
        /// <summary>对拍窗口时长（秒）。与 Godot QA 脚本一致。</summary>
        private const float WindowSeconds = 20.0f;

        /// <summary>频率断言容差（次/s）。</summary>
        private const float FreqTolerance = 0.06f;

        /// <summary>倍率断言容差。</summary>
        private const float RatioTolerance = 0.05f;

        private const float Step = FixedStepAccumulator.FixedStep;

        // ---------------------------------------------------------------------
        // 模拟器
        // ---------------------------------------------------------------------

        /// <summary>
        /// 锁步模拟：按固定步长推进，每步先 tick 闸门、再让 n 个来源依次尝试攻击。
        /// 等价于 Godot 60Hz headless 测试台。
        /// </summary>
        private static int SimulateHits(int sourceCount, float duration = WindowSeconds)
        {
            WCoreState w = NewSandbagState();
            int hits = 0;
            int totalSteps = (int)Math.Round(duration / Step);

            for (int s = 0; s < totalSteps; s++)
            {
                w.Tick(Step);
                for (int sid = 0; sid < sourceCount; sid++)
                {
                    if (w.TakeDamageFrom(sid, 1.0f))
                    {
                        hits++;
                    }
                }
            }
            return hits;
        }

        /// <summary>锁步模拟的频率（次/s）。</summary>
        private static float SimulateFreq(int sourceCount, float duration = WindowSeconds)
        {
            return SimulateHits(sourceCount, duration) / duration;
        }

        /// <summary>
        /// 真实帧率驱动：deltaTime = 1/fps 喂累加器，逻辑仍落在 1/60 网格上。
        /// 这是 Unity 侧的实际接法。
        /// </summary>
        private static float SimulateFreqAtFps(int sourceCount, int fps, float duration = WindowSeconds)
        {
            WCoreState w = NewSandbagState();
            FixedStepAccumulator acc = new FixedStepAccumulator();
            int hits = 0;

            Action<float> onStep = delegate (float dt)
            {
                w.Tick(dt);
                for (int sid = 0; sid < sourceCount; sid++)
                {
                    if (w.TakeDamageFrom(sid, 1.0f))
                    {
                        hits++;
                    }
                }
            };

            int frames = (int)Math.Round(duration * fps);
            float frameDt = 1.0f / fps;
            for (int f = 0; f < frames; f++)
            {
                acc.Advance(frameDt, onStep);
            }
            return hits / duration;
        }

        /// <summary>造一个「打不死的沙包」：只数命中次数，不关心死亡分支。</summary>
        private static WCoreState NewSandbagState()
        {
            WCoreState w = new WCoreState();
            w.HpMax = 1e9f;
            w.Hp = 1e9f;
            w.DodgeEnabled = false;   // 对拍必须关闭，否则随机闪避污染纯承伤频率
            return w;
        }

        // ---------------------------------------------------------------------
        // T1 频率表
        // ---------------------------------------------------------------------

        /// <summary>1/2/3/4/8 源承伤频率逐一对拍 Godot r2_t01 实测值。</summary>
        [TestCase(1, 1.700f, TestName = "W-CORE 单挑 1 源 = 1.700 次每秒")]
        [TestCase(2, 3.350f, TestName = "W-CORE 2 源 = 3.350 次每秒")]
        [TestCase(3, 4.300f, TestName = "W-CORE 围攻 3 源 = 4.300 次每秒")]
        [TestCase(4, 4.300f, TestName = "W-CORE 围攻 4 源 = 4.300 次每秒（已达天花板）")]
        [TestCase(8, 4.300f, TestName = "W-CORE 围攻 8 源 = 4.300 次每秒（不再增长）")]
        public void 承伤频率对拍Godot基线(int sourceCount, float expected)
        {
            float freq = SimulateFreq(sourceCount);
            Assert.That(freq, Is.EqualTo(expected).Within(FreqTolerance),
                string.Format("{0} 源实测 {1:F4} 次/s，基线 {2:F3}", sourceCount, freq, expected));
        }

        /// <summary>命中次数的绝对值也要对上（34 / 67 / 86 次 / 20s）。</summary>
        [TestCase(1, 34)]
        [TestCase(2, 67)]
        [TestCase(3, 86)]
        [TestCase(4, 86)]
        [TestCase(8, 86)]
        public void 命中次数对拍Godot基线(int sourceCount, int expectedHits)
        {
            Assert.That(SimulateHits(sourceCount), Is.EqualTo(expectedHits));
        }

        // ---------------------------------------------------------------------
        // T2 围攻倍率
        // ---------------------------------------------------------------------

        /// <summary>
        /// 围攻 / 单挑承伤倍率 = 2.53x。
        /// 旧模型（命中即全局 iframe）恒为 1.00x —— 那正是「围攻无威胁」的根因。
        /// </summary>
        [Test]
        public void 围攻单挑倍率等于2点53倍()
        {
            float solo = SimulateFreq(1);
            float swarm = SimulateFreq(3);
            float ratio = swarm / solo;

            Assert.That(ratio, Is.EqualTo(2.53f).Within(RatioTolerance),
                string.Format("实测 {0:F4}x（单挑 {1:F3} / 围攻 {2:F3}）", ratio, solo, swarm));
            Assert.That(ratio, Is.GreaterThan(2.0f), "倍率退回 1.00x 说明又变成了全局无敌帧模型");
        }

        // ---------------------------------------------------------------------
        // T3 帧量化 / 帧率无关性
        // ---------------------------------------------------------------------

        /// <summary>
        /// 固定步长常量必须是 1/60，不能写字面量 0.01667。
        /// 13 × 0.01667 = 0.21671 &gt; 0.2167（闸门第 13 步就开，围攻飙到 4.65）
        /// 13 × (1/60)  = 0.216667 &lt; 0.2167（必须等到第 14 步，围攻 4.30 = 基线）
        /// </summary>
        [Test]
        public void 固定步长常量必须是六十分之一()
        {
            Assert.That(FixedStepAccumulator.FixedStep, Is.EqualTo(1.0f / 60.0f),
                "写成 0.01667f 会让围攻频率跳到 4.65 次/s，倍率 2.74x，手感验收全线崩盘");

            // 反例佐证：13 步在两种写法下的累积值分别落在闸门阈值两侧。
            Assert.That(13 * 0.01667f, Is.GreaterThan(WCoreState.GlobalHitGap));
            Assert.That(13 * (1.0f / 60.0f), Is.LessThan(WCoreState.GlobalHitGap));
            Assert.That(14 * (1.0f / 60.0f), Is.GreaterThan(WCoreState.GlobalHitGap));
        }

        /// <summary>攻击尝试锁在 1/60 网格后，任意引擎帧率下频率一致。</summary>
        [TestCase(30)]
        [TestCase(60)]
        [TestCase(90)]
        [TestCase(144)]
        [TestCase(240)]
        public void 承伤频率与引擎帧率无关(int fps)
        {
            float freq = SimulateFreqAtFps(3, fps);
            Assert.That(freq, Is.EqualTo(4.300f).Within(FreqTolerance),
                string.Format("{0} fps 下实测 {1:F4} 次/s", fps, freq));
        }

        /// <summary>长卡顿后不应一口气补完全部步数（死亡螺旋防护）。</summary>
        [Test]
        public void 累加器限制单次追赶步数()
        {
            FixedStepAccumulator acc = new FixedStepAccumulator();
            int steps = acc.Advance(5.0f, null);   // 5 秒卡顿 = 300 步的量
            Assert.That(steps, Is.EqualTo(FixedStepAccumulator.MaxStepsPerAdvance));
            Assert.That(acc.Remainder, Is.EqualTo(0.0f), "超额余量应被丢弃而非留到下一帧继续补");
        }

        // ---------------------------------------------------------------------
        // T5 C8 软上限
        // ---------------------------------------------------------------------

        /// <summary>强灌 200 来源不 tick，冷却表必须被裁到 32 条。</summary>
        [Test]
        public void C8软上限压测裁剪到32条()
        {
            WCoreState w = NewSandbagState();
            for (int sid = 0; sid < 200; sid++)
            {
                // 走 QA 后门：正常路径下全局闸门会拦掉第 2 次之后的写入，
                // 冷却表涨不到 32 条以上，裁剪分支就测不到。
                w.QaForceArmGates(sid);
            }
            Assert.That(w.SourceCdCount, Is.EqualTo(WCoreState.HitCdMapSoftCap));
        }

        /// <summary>冷却值全部相等时，裁剪结果必须确定（按 sourceId 升序保留前 32）。</summary>
        [Test]
        public void 软上限裁剪结果确定()
        {
            WCoreState a = NewSandbagState();
            WCoreState b = NewSandbagState();
            for (int sid = 0; sid < 200; sid++)
            {
                a.QaForceArmGates(sid);
            }
            for (int sid = 199; sid >= 0; sid--)   // 反序灌入
            {
                b.QaForceArmGates(sid);
            }

            for (int sid = 0; sid < 32; sid++)
            {
                Assert.That(a.SourceCdRemaining(sid), Is.GreaterThan(0.0f), "a 应保留 sid=" + sid);
                Assert.That(b.SourceCdRemaining(sid), Is.GreaterThan(0.0f), "b 应保留 sid=" + sid);
            }
            Assert.That(a.SourceCdRemaining(32), Is.EqualTo(0.0f));
            Assert.That(b.SourceCdRemaining(32), Is.EqualTo(0.0f));
        }

        // ---------------------------------------------------------------------
        // T6 闸门语义
        // ---------------------------------------------------------------------

        /// <summary>同一逻辑步内，第 2 个来源必被全局最小间隔拦下。</summary>
        [Test]
        public void 全局闸门拦下同步内第二次命中()
        {
            WCoreState w = NewSandbagState();
            Assert.That(w.TakeDamageFrom(1, 10.0f), Is.True);
            Assert.That(w.TakeDamageFrom(2, 10.0f), Is.False);
        }

        /// <summary>全局闸门 14 步后开启；此时同一来源仍被自身 0.6s 冷却挡住，别的来源可以命中。</summary>
        [Test]
        public void 每来源冷却与全局闸门相互独立()
        {
            WCoreState w = NewSandbagState();
            w.TakeDamageFrom(1, 10.0f);

            for (int i = 0; i < 14; i++)
            {
                w.Tick(Step);
            }
            Assert.That(w.GlobalGapRemaining, Is.EqualTo(0.0f), "14 步后全局闸门应已开启");
            Assert.That(w.TakeDamageFrom(1, 10.0f), Is.False, "来源 1 仍在自身 0.6s 冷却中");
            Assert.That(w.TakeDamageFrom(2, 10.0f), Is.True, "来源 2 应可命中");
        }

        /// <summary>冷却归零即移除，避免冷却表随本局见过的敌人总数单调增长。</summary>
        [Test]
        public void 冷却归零即移除条目()
        {
            WCoreState w = NewSandbagState();
            w.TakeDamageFrom(7, 1.0f);
            Assert.That(w.SourceCdCount, Is.EqualTo(1));

            for (int i = 0; i < 36; i++)   // 0.6s = 36 步
            {
                w.Tick(Step);
            }
            Assert.That(w.SourceCdCount, Is.EqualTo(0), "应移除而非留一条值为 0 的垃圾");
        }

        /// <summary>闪避残留的短无敌对所有来源一票否决。</summary>
        [Test]
        public void 闪避无敌期间全部来源被拦()
        {
            WCoreState w = NewSandbagState();
            w.Iframe = WCoreState.DodgeIframe;
            Assert.That(w.TakeDamageFrom(3, 10.0f), Is.False);
            Assert.That(w.TakeDamageFrom(4, 10.0f), Is.False);
        }

        /// <summary>Reset 清空两层闸门与无敌（切区必调，防实例 id 复用导致「新怪打不动我」）。</summary>
        [Test]
        public void Reset清空全部闸门()
        {
            WCoreState w = NewSandbagState();
            w.TakeDamageFrom(1, 1.0f);
            w.Iframe = 0.2f;

            w.Reset();

            Assert.That(w.SourceCdCount, Is.EqualTo(0));
            Assert.That(w.GlobalGapRemaining, Is.EqualTo(0.0f));
            Assert.That(w.Iframe, Is.EqualTo(0.0f));
            Assert.That(w.TakeDamageFrom(1, 1.0f), Is.True, "Reset 后旧来源应能立刻命中");
        }

        // ---------------------------------------------------------------------
        // T7 闪避 / 减伤 / 死亡
        // ---------------------------------------------------------------------

        /// <summary>闪避默认关闭 —— 对拍基线依赖这一点。</summary>
        [Test]
        public void 闪避默认关闭()
        {
            Assert.That(new WCoreState().DodgeEnabled, Is.False);
        }

        /// <summary>闪避成功：返回 true、不扣血、进入 0.25s 短无敌，且同样占用两层闸门。</summary>
        [Test]
        public void 闪避成功不扣血且进入短无敌()
        {
            WCoreState w = new WCoreState();
            w.HpMax = 1000.0f;
            w.Hp = 1000.0f;
            w.DodgeEnabled = true;
            w.DodgeRoll = delegate { return true; };

            Assert.That(w.TakeDamageFrom(1, 999.0f), Is.True, "闪避也算「本次生效」，调用方要放闪避特效");
            Assert.That(w.Hp, Is.EqualTo(1000.0f));
            Assert.That(w.Iframe, Is.EqualTo(WCoreState.DodgeIframe).Within(1e-6f));
            Assert.That(w.SourceCdRemaining(1), Is.GreaterThan(0.0f),
                "闪避必须落闸，否则闪避成功反而变成一次免费的重置机会");
        }

        /// <summary>减伤过滤器接入后按 A2 软上限除法模型扣血。</summary>
        [Test]
        public void 减伤过滤器生效()
        {
            WCoreState w = new WCoreState();
            w.HpMax = 1000.0f;
            w.Hp = 1000.0f;
            w.DamageFilter = delegate (float raw) { return Difficulty.DamageTaken(raw, 100.0f); };

            w.TakeDamageFrom(1, 100.0f);   // def=100=K ⇒ 减半
            Assert.That(w.Hp, Is.EqualTo(950.0f).Within(1e-3f));
        }

        /// <summary>血量归零触发 Died 且钳到 0（是否真的判死由上层决定）。</summary>
        [Test]
        public void 血量归零触发死亡事件()
        {
            WCoreState w = new WCoreState();
            w.HpMax = 10.0f;
            w.Hp = 10.0f;
            int fired = 0;
            w.Died += delegate { fired++; };

            w.TakeDamageFrom(1, 999.0f);

            Assert.That(w.Hp, Is.EqualTo(0.0f));
            Assert.That(fired, Is.EqualTo(1));
        }

        /// <summary>无源伤害（陷阱 / 脚本）走 0 号槽位，同样受闸门约束。</summary>
        [Test]
        public void 无源伤害走零号槽位()
        {
            WCoreState w = NewSandbagState();
            Assert.That(w.TakeDamage(1.0f), Is.True);
            Assert.That(w.SourceCdRemaining(0), Is.GreaterThan(0.0f));
            Assert.That(w.TakeDamage(1.0f), Is.False, "无源伤害不得绕过天花板");
        }

        // ---------------------------------------------------------------------
        // 常量守卫
        // ---------------------------------------------------------------------

        /// <summary>手感常量是移植的单一事实源，改动必须走评审。</summary>
        [Test]
        public void 手感常量与Godot逐值一致()
        {
            Assert.That(WCoreState.PerSourceHitCd, Is.EqualTo(0.6f));
            Assert.That(WCoreState.GlobalHitGap, Is.EqualTo(0.2167f));
            Assert.That(WCoreState.HitCdMapSoftCap, Is.EqualTo(32));
            Assert.That(WCoreState.DodgeIframe, Is.EqualTo(0.25f));
        }
    }

    /// <summary>模型 B 难度靶子测试。</summary>
    [TestFixture]
    public class DifficultyTests
    {
        /// <summary>d_eff = hp_max / 65。H=300 → 4.62（文档校验样本）。</summary>
        [Test]
        public void 模型B靶子公式()
        {
            Assert.That(Difficulty.ComputeDeff(300.0f), Is.EqualTo(4.62f).Within(0.01f));
        }

        /// <summary>H=300 时 A1 = 15.1s、A2 = 38.2s，且分别落在 [10,16] / [35,60]。</summary>
        [Test]
        public void 模型B靶子满足A1A2窗口()
        {
            const float h = 300.0f;
            float deff = Difficulty.ComputeDeff(h);

            float a1 = Difficulty.EstimateTtk(h, deff, Difficulty.SwarmFreqMeasured);
            float a2 = Difficulty.EstimateTtk(h, deff, Difficulty.SoloFreqMeasured);

            Assert.That(a1, Is.EqualTo(15.1f).Within(0.1f));
            Assert.That(a2, Is.EqualTo(38.2f).Within(0.1f));
            Assert.That(Difficulty.IsA1Pass(h, deff), Is.True);
            Assert.That(Difficulty.IsA2Pass(h, deff), Is.True);
        }

        /// <summary>靶子必须落在 H/73.8 ~ H/58.3 的交集区间内。</summary>
        [TestCase(120.0f)]
        [TestCase(300.0f)]
        [TestCase(2400.0f)]
        public void 靶子落在有效区间内(float hpMax)
        {
            float deff = Difficulty.ComputeDeff(hpMax);
            Assert.That(Difficulty.IsDeffInTarget(deff, hpMax), Is.True);
            Assert.That(deff, Is.GreaterThanOrEqualTo(Difficulty.DeffLowerBound(hpMax)));
            Assert.That(deff, Is.LessThanOrEqualTo(Difficulty.DeffUpperBound(hpMax)));
        }

        /// <summary>A2 软上限除法模型：单调、恒 &lt; 1、def=K 时正好减半。</summary>
        [Test]
        public void 减伤模型无免疫悬崖()
        {
            Assert.That(Difficulty.MitigationOf(0.0f), Is.EqualTo(0.0f));
            Assert.That(Difficulty.MitigationOf(100.0f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(Difficulty.MitigationOf(1e9f), Is.LessThan(1.0f), "再厚的甲也不能免疫");
            Assert.That(Difficulty.MitigationOf(-50.0f), Is.EqualTo(0.0f), "负防御按 0 处理");

            float prev = -1.0f;
            for (float d = 0.0f; d <= 1000.0f; d += 25.0f)
            {
                float m = Difficulty.MitigationOf(d);
                Assert.That(m, Is.GreaterThan(prev), "减伤必须严格单调递增");
                prev = m;
            }
        }

        /// <summary>承伤下限 1.0：再厚的甲也至少掉 1 点。</summary>
        [Test]
        public void 承伤下限一点()
        {
            Assert.That(Difficulty.DamageTaken(1.0f, 1e6f), Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(Difficulty.DamageTaken(100.0f, 100.0f), Is.EqualTo(50.0f).Within(1e-3f));
        }

        /// <summary>反解：SolveDeffForTtk 与 EstimateTtk 互为逆运算。</summary>
        [Test]
        public void TTK正反解自洽()
        {
            const float h = 450.0f;
            float deff = Difficulty.SolveDeffForTtk(h, 40.0f, Difficulty.SoloFreqMeasured);
            Assert.That(Difficulty.EstimateTtk(h, deff, Difficulty.SoloFreqMeasured),
                Is.EqualTo(40.0f).Within(1e-3f));
        }

        /// <summary>反解 atk_mult：代回去应还原目标 d_eff。</summary>
        [Test]
        public void 反解atkMult自洽()
        {
            const float targetDeff = 4.62f;
            const float baseAtk = 20.0f;
            const float playerDef = 100.0f;

            float mult = Difficulty.SolveAtkMultForDeff(targetDeff, baseAtk, playerDef);
            float actual = Difficulty.DamageTaken(baseAtk * mult, playerDef);
            Assert.That(actual, Is.EqualTo(targetDeff).Within(1e-3f));
        }

        /// <summary>实测频率常量与 Godot 基线一致。</summary>
        [Test]
        public void 频率常量与基线一致()
        {
            Assert.That(Difficulty.SoloFreqMeasured, Is.EqualTo(1.700f));
            Assert.That(Difficulty.SwarmFreqMeasured, Is.EqualTo(4.300f));
            Assert.That(Difficulty.SwarmFreqMeasured / Difficulty.SoloFreqMeasured,
                Is.EqualTo(2.53f).Within(0.01f));
        }
    }

    /// <summary>PCG32 确定性测试。</summary>
    [TestFixture]
    public class PCG32Tests
    {
        /// <summary>
        /// 参考向量：seed=20260730、默认 inc 的前 8 个 uint32。
        /// 这组数字由 wcore_selfcheck.py 的独立 Python 实现产出并已交叉验证。
        /// 一旦这条断言挂了，说明 RNG 序列漂移 —— 全部依赖随机的手感数值同步作废。
        /// </summary>
        private static readonly uint[] Expected =
        {
            2674124880u, 642922184u, 2695042333u, 1167291617u,
            2978475972u, 3047763484u, 2354280111u, 902273261u
        };

        [Test]
        public void 默认种子参考向量匹配()
        {
            PCG32 r = new PCG32(PCG32.DefaultSeed);
            for (int i = 0; i < Expected.Length; i++)
            {
                Assert.That(r.NextUInt(), Is.EqualTo(Expected[i]), "第 " + i + " 个输出不匹配");
            }
        }

        [Test]
        public void 同种子重放序列一致()
        {
            PCG32 a = new PCG32(PCG32.DefaultSeed);
            PCG32 b = new PCG32(PCG32.DefaultSeed);
            for (int i = 0; i < 256; i++)
            {
                Assert.That(b.NextUInt(), Is.EqualTo(a.NextUInt()));
            }
        }

        [Test]
        public void 不同种子序列不同()
        {
            PCG32 a = new PCG32(PCG32.DefaultSeed);
            PCG32 b = new PCG32(PCG32.DefaultSeed + 1);
            bool anyDiff = false;
            for (int i = 0; i < 16; i++)
            {
                if (a.NextUInt() != b.NextUInt())
                {
                    anyDiff = true;
                }
            }
            Assert.That(anyDiff, Is.True);
        }

        /// <summary>NextFloat 必须严格 &lt; 1，否则 rate=1.0 的概率判定会偶发失效。</summary>
        [Test]
        public void NextFloat落在半开区间()
        {
            PCG32 r = new PCG32(PCG32.DefaultSeed);
            double sum = 0.0;
            const int n = 20000;
            for (int i = 0; i < n; i++)
            {
                float f = r.NextFloat();
                Assert.That(f, Is.GreaterThanOrEqualTo(0.0f));
                Assert.That(f, Is.LessThan(1.0f));
                sum += f;
            }
            Assert.That(sum / n, Is.EqualTo(0.5).Within(0.02), "均值应接近 0.5");
        }

        [Test]
        public void NextRangeInt为闭区间()
        {
            PCG32 r = new PCG32(PCG32.DefaultSeed);
            bool sawMin = false;
            bool sawMax = false;
            for (int i = 0; i < 20000; i++)
            {
                int v = r.NextRangeInt(1, 6);
                Assert.That(v, Is.InRange(1, 6));
                if (v == 1) sawMin = true;
                if (v == 6) sawMax = true;
            }
            Assert.That(sawMin && sawMax, Is.True, "闭区间两端都应能取到");
        }

        [Test]
        public void 退化区间返回下界且不消耗随机流()
        {
            PCG32 r = new PCG32(PCG32.DefaultSeed);
            ulong before = r.State;
            Assert.That(r.NextRangeInt(5, 5), Is.EqualTo(5));
            Assert.That(r.NextRange(2.0f, 2.0f), Is.EqualTo(2.0f));
            Assert.That(r.Chance(0.0f), Is.False);
            Assert.That(r.Chance(1.0f), Is.True);
            Assert.That(r.State, Is.EqualTo(before), "退化分支不应推进状态");
        }

        [Test]
        public void 状态可存档恢复()
        {
            PCG32 r = new PCG32(PCG32.DefaultSeed);
            for (int i = 0; i < 37; i++)
            {
                r.NextUInt();
            }
            ulong savedState = r.State;
            ulong savedInc = r.Increment;
            uint next = r.NextUInt();

            PCG32 restored = new PCG32(0UL);
            restored.Increment = savedInc;
            restored.State = savedState;
            Assert.That(restored.NextUInt(), Is.EqualTo(next));
        }

        [Test]
        public void Fork产生独立子流()
        {
            PCG32 root = new PCG32(PCG32.DefaultSeed);
            PCG32 terrain = root.Fork(1UL);
            PCG32 drops = root.Fork(2UL);

            bool anyDiff = false;
            for (int i = 0; i < 16; i++)
            {
                if (terrain.NextUInt() != drops.NextUInt())
                {
                    anyDiff = true;
                }
            }
            Assert.That(anyDiff, Is.True, "不同 streamId 的子流不应同步");
        }
    }

    /// <summary>区域种子派生测试。</summary>
    [TestFixture]
    public class ZoneSeedTests
    {
        /// <summary>
        /// Godot String::hash() 是 djb2（不是 FNV-1a）。参考值由 Python 侧独立算出。
        /// 用错散列 ⇒ 同一 zone_id 得到不同种子 ⇒ Unity 地图与 Godot 原型对不上。
        /// </summary>
        [TestCase("zone_fangshi", 1943691360u)]
        [TestCase("zone_youhuang", 3189750928u)]
        [TestCase("zone_chiyan", 4244601628u)]
        [TestCase("zone_hantan", 137184090u)]
        [TestCase("zone_jianzhong", 806501800u)]
        [TestCase("zone_moyuan", 349860345u)]
        public void Djb2对拍Godot散列(string zoneId, uint expected)
        {
            Assert.That(GodotHash.Djb2(zoneId), Is.EqualTo(expected));
        }

        [Test]
        public void Djb2与Fnv1a不是同一个函数()
        {
            Assert.That(GodotHash.Djb2("zone_youhuang"),
                Is.Not.EqualTo(GodotHash.Fnv1a32("zone_youhuang")));
            Assert.That(GodotHash.Fnv1a32("zone_youhuang"), Is.EqualTo(4282403924u));
        }

        [Test]
        public void 安全区种子与访问次数无关()
        {
            long s0 = ZoneSeed.Derive("zone_fangshi", true, 0);
            long s9 = ZoneSeed.Derive("zone_fangshi", true, 9);
            Assert.That(s0, Is.EqualTo(s9));
            Assert.That(s0, Is.EqualTo((long)GodotHash.Djb2("zone_fangshi")));
        }

        [Test]
        public void 战斗区首访种子等于散列()
        {
            Assert.That(ZoneSeed.Derive("zone_youhuang", false, 0),
                Is.EqualTo((long)GodotHash.Djb2("zone_youhuang")));
        }

        /// <summary>visits ≥ 2 时 visits×ZONE_SEED_MIX 会溢出 32 位，必须用 64 位承接。</summary>
        [Test]
        public void 高访问次数种子超出32位()
        {
            long s3 = ZoneSeed.Derive("zone_youhuang", false, 3);
            Assert.That(s3, Is.EqualTo(5984866691L));
            Assert.That(s3, Is.GreaterThan(uint.MaxValue));
        }

        [Test]
        public void 连续访问种子互不相同()
        {
            HashSet<long> seen = new HashSet<long>();
            for (int v = 0; v < 16; v++)
            {
                Assert.That(seen.Add(ZoneSeed.Derive("zone_chiyan", false, v)), Is.True,
                    "第 " + v + " 次访问的种子与之前重复");
            }
        }

        [Test]
        public void 访问计数器发放与预览()
        {
            ZoneSeedTracker t = new ZoneSeedTracker();
            Assert.That(t.VisitsOf("zone_chiyan"), Is.EqualTo(0));

            long peek = t.PeekSeed("zone_chiyan", false);
            long taken = t.NextSeed("zone_chiyan", false);
            Assert.That(taken, Is.EqualTo(peek), "Peek 应等于下一次 Next 的结果");
            Assert.That(t.VisitsOf("zone_chiyan"), Is.EqualTo(1), "Next 必须推进计数");
            Assert.That(t.PeekSeed("zone_chiyan", false), Is.Not.EqualTo(taken));

            t.SetVisits("zone_chiyan", 5);
            Assert.That(t.PeekSeed("zone_chiyan", false),
                Is.EqualTo(ZoneSeed.Derive("zone_chiyan", false, 5)), "读档恢复计数");

            t.Clear();
            Assert.That(t.VisitsOf("zone_chiyan"), Is.EqualTo(0));
        }
    }

    /// <summary>
    /// zones.json 数据契约测试。
    /// 需要工作目录能定位到 Assets/Data/zones.json；CI 里请用 ZonesJsonPath 环境变量覆盖。
    /// </summary>
    [TestFixture]
    public class ZoneLoaderTests
    {
        private ZoneDatabase _db;

        [OneTimeSetUp]
        public void LoadOnce()
        {
            _db = ZoneLoader.LoadFromFile(ResolveZonesPath());
        }

        /// <summary>从环境变量或逐级上溯定位 zones.json，兼容 dotnet test 与 Unity 两种工作目录。</summary>
        private static string ResolveZonesPath()
        {
            string fromEnv = Environment.GetEnvironmentVariable("ZonesJsonPath");
            if (!string.IsNullOrEmpty(fromEnv) && System.IO.File.Exists(fromEnv))
            {
                return fromEnv;
            }

            System.IO.DirectoryInfo dir = new System.IO.DirectoryInfo(Environment.CurrentDirectory);
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, ZoneLoader.DefaultRelativePath);
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new System.IO.FileNotFoundException(
                "定位不到 " + ZoneLoader.DefaultRelativePath + "，请设置 ZonesJsonPath 环境变量");
        }

        [Test]
        public void 加载六个区域()
        {
            Assert.That(_db.Zones.Count, Is.EqualTo(6));
            Assert.That(_db.SchemaVersion, Is.EqualTo(1));
        }

        [Test]
        public void 主城唯一且为安全区()
        {
            Assert.That(_db.HubZoneId(), Is.EqualTo("zone_fangshi"));
            Assert.That(_db.IsSafe("zone_fangshi"), Is.True);
        }

        [Test]
        public void 按order升序排列()
        {
            for (int i = 1; i < _db.Zones.Count; i++)
            {
                Assert.That(_db.Zones[i].Order, Is.GreaterThanOrEqualTo(_db.Zones[i - 1].Order));
            }
        }

        [Test]
        public void 嵌套字段解析正确()
        {
            ZoneData z = _db.Get("zone_youhuang");
            Assert.That(z, Is.Not.Null);
            Assert.That(z.DisplayName, Is.EqualTo("幽篁竹海"), "中文字段必须按 UTF-8 正确读入");
            Assert.That(z.Tier, Is.EqualTo(1));
            Assert.That(z.BaseLevel, Is.EqualTo(3));
            Assert.That(z.IsSafe, Is.False);

            Assert.That(z.Theme.Algo, Is.EqualTo("forest"));
            Assert.That(z.Theme.Width, Is.EqualTo(120));
            Assert.That(z.Theme.Height, Is.EqualTo(80));
            Assert.That(z.Theme.Palette.Ground2, Is.EqualTo("#4d6349"),
                "ground2 ⇒ 命名策略不能拆成 ground_2");
            Assert.That(z.Theme.Decor.Kind, Is.EqualTo("bamboo"));
            Assert.That(z.Theme.Operators.Count, Is.EqualTo(3));
            Assert.That(z.Theme.Operators[0].Op, Is.EqualTo("vein"));
            Assert.That(z.Theme.Operators[0].Target, Is.EqualTo("water"));
            Assert.That(z.Theme.Operators[0].Len, Is.EqualTo(new[] { 22, 48 }));

            Assert.That(z.Enemies.Count, Is.EqualTo(6));
            Assert.That(z.Enemies.Kinds, Is.EquivalentTo(new[] { "blood", "witch" }));
            Assert.That(z.Enemies.ArmorAdd, Is.EqualTo(0));

            Assert.That(z.Boss.BossId, Is.EqualTo("boss_zhuxiaowang"));
            Assert.That(z.Boss.HpMult, Is.EqualTo(10.0f).Within(1e-6f));
            Assert.That(z.Boss.DropExtra.Materials["mat_bamboo_marrow"], Is.EqualTo(2));
            Assert.That(z.Boss.DropExtra.EquipmentQualityCap, Is.True);

            Assert.That(z.ExclusiveMaterial, Is.EqualTo("mat_bamboo_marrow"));
            Assert.That(z.Npcs, Is.Empty);
        }

        [Test]
        public void 解锁条件解析正确()
        {
            Assert.That(_db.Get("zone_chiyan").Unlock.KillsIn["zone_youhuang"], Is.EqualTo(25));
            Assert.That(_db.Get("zone_hantan").Unlock.BossCleared, Is.EquivalentTo(new[] { "zone_chiyan" }));
            Assert.That(_db.Get("zone_fangshi").Unlock.Level, Is.EqualTo(-1), "-1 = 不校验");
        }

        [Test]
        public void NPC与任务链解析正确()
        {
            ZoneData hub = _db.Get("zone_fangshi");
            Assert.That(hub.Npcs.Count, Is.EqualTo(5));

            ZoneNpc elder = hub.Npcs.Find(delegate (ZoneNpc n) { return n.NpcId == "npc_xuanzhen"; });
            Assert.That(elder, Is.Not.Null);
            Assert.That(elder.DisplayName, Is.EqualTo("玄真子"));
            Assert.That(elder.TileX, Is.EqualTo(58));
            Assert.That(elder.TileY, Is.EqualTo(40));
            Assert.That(elder.QuestChain.Count, Is.EqualTo(2));
            Assert.That(elder.QuestChain[0].QuestId, Is.EqualTo("quest_01"));
            Assert.That(elder.QuestChain[0].TurnIn, Is.EqualTo("dlg_xuanzhen_complete"));
        }

        [Test]
        public void 战斗区全部配了BOSS()
        {
            foreach (ZoneData z in _db.Zones)
            {
                if (!z.IsSafe)
                {
                    Assert.That(z.Boss, Is.Not.Null, z.ZoneId + " 缺 boss");
                }
            }
        }

        [Test]
        public void 未知区域返回空()
        {
            Assert.That(_db.Get("zone_不存在"), Is.Null);
            Assert.That(_db.Get(null), Is.Null);
            Assert.That(_db.IsSafe("zone_不存在"), Is.False);
        }

        /// <summary>zone_id 是存档 key，重复必须在加载期炸掉。</summary>
        [Test]
        public void 重复ZoneId加载失败()
        {
            const string bad = @"{""_version"":1,""zones"":[
                {""zone_id"":""a"",""is_hub"":true,""is_safe"":true,
                 ""theme"":{""size"":[10,10]},""enemies"":{""count"":0}},
                {""zone_id"":""a"",""is_safe"":true,
                 ""theme"":{""size"":[10,10]},""enemies"":{""count"":0}}]}";
            Assert.Throws<System.IO.InvalidDataException>(delegate { ZoneLoader.LoadFromJson(bad); });
        }

        [Test]
        public void 缺少主城加载失败()
        {
            const string bad = @"{""_version"":1,""zones"":[
                {""zone_id"":""a"",""is_safe"":true,
                 ""theme"":{""size"":[10,10]},""enemies"":{""count"":0}}]}";
            Assert.Throws<System.IO.InvalidDataException>(delegate { ZoneLoader.LoadFromJson(bad); });
        }
    }
}
