// -----------------------------------------------------------------------------
// CombatKernelTests.cs —— T1 战斗内核的 NUnit 验收套件
//
// 【与 t1_selfcheck.py 的关系】
// 本文件与同目录的 t1_selfcheck.py 是**同一套断言的两种宿主**：
//   · t1_selfcheck.py  —— 纯 Python 镜像，可在无 dotnet 的环境离线跑（已实测 63/63 PASS）；
//   · CombatKernelTests.cs —— 直接吃真实内核类型，在 Unity Test Runner / dotnet test 里跑。
// 两边用例编号一一对应（T1_01 ~ T1_14 + Extra）。Python 那份负责证明「算法是对的」，
// 这一份负责证明「C# 实现与算法一致」。任何一边改了数值基线，另一边必须同步改。
//
// 【本文件当前的验证状态】
// 编写环境没有 dotnet/Unity，**本文件未经编译**。数值基线全部来自 t1_selfcheck.py
// 的实测输出，不是估算值。首次在本地跑之前请预期可能存在签名笔误。
//
// 【float32 与 float64 的差异如何处理】
// Python 用 double，C# 内核用 float。闸门临界点 13×(1/60)=0.2166667 距 GlobalHitGap
// 0.2167 尚有 3.3e-5 的余量，而 float32 在该量级的分辨率约 1.5e-8，相差三个数量级，
// 因此两边的放行/拦截判定必然一致——这正是「绝不写 0.01667f 字面量」的意义所在
// （13×0.01667=0.21671 会翻越 0.2167，围攻频率从 4.300 跳到 4.65）。
// 几何/数值断言统一用 float 量级的容差，不照抄 Python 的 rel_tol=1e-6。
//
// 【禁止事项】不得引用 UnityEngine。本文件位于 Combat/ 下（非 Unity/ 子目录），
// 受 CI 的 grep 守卫约束，只允许 BCL + NUnit。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Xianxia.Core;

namespace Xianxia.Combat.Tests
{
    /// <summary>
    /// 测试用事件记录器。把内核抛出的全部事件按「步号」记下来供断言检查。
    /// </summary>
    internal sealed class RecorderEvents : ICombatEvents
    {
        /// <summary>挂上 Encounter 时步号取它的 StepCount。</summary>
        public Encounter Enc;

        /// <summary>
        /// 未挂 Encounter（例如单独驱动 BossController）时由调用方 <see cref="Tick"/> 自报步号。
        /// 否则事件只能记成 0，时间轴就塌成一个点。
        /// </summary>
        public int ManualStep;

        public readonly List<int> HitSteps = new List<int>(256);
        public readonly List<int> HitSources = new List<int>(256);
        public readonly List<float> HitDamages = new List<float>(256);
        public int Blocked;

        public readonly List<int> DeathIds = new List<int>(8);
        public readonly List<BossPhase> Phases = new List<BossPhase>(4);
        public readonly List<int> PhaseSteps = new List<int>(4);
        public readonly List<int> SummonSteps = new List<int>(8);
        public readonly List<int> SummonCounts = new List<int>(8);
        public readonly List<int> ShockSteps = new List<int>(8);
        public readonly List<float> ShockRadii = new List<float>(8);
        public readonly List<int> KnockbackIds = new List<int>(8);

        public RecorderEvents() { }

        public RecorderEvents(Encounter enc)
        {
            Enc = enc;
        }

        /// <summary>手动驱动模式下推进一个步号（与 Encounter.StepCount 一样从 1 起）。</summary>
        public int Tick()
        {
            ManualStep += 1;
            return ManualStep;
        }

        private int Step()
        {
            return Enc != null ? Enc.StepCount : ManualStep;
        }

        public void OnHit(Combatant attacker, Combatant defender, float dmg, bool applied)
        {
            if (applied)
            {
                HitSteps.Add(Step());
                HitSources.Add(attacker != null ? attacker.Id : -1);
                HitDamages.Add(dmg);
            }
            else
            {
                Blocked += 1;
            }
        }

        public void OnEnemyDeath(Combatant e)
        {
            DeathIds.Add(e != null ? e.Id : -1);
        }

        public void OnBossPhase(BossPhase phase)
        {
            Phases.Add(phase);
            PhaseSteps.Add(Step());
        }

        public void OnSummon(int n)
        {
            SummonSteps.Add(Step());
            SummonCounts.Add(n);
        }

        public void OnShockwave(float radius, Vec2 center)
        {
            ShockSteps.Add(Step());
            ShockRadii.Add(radius);
        }

        public void OnKnockback(Combatant e, Vec2 impulse)
        {
            KnockbackIds.Add(e != null ? e.Id : -1);
        }
    }

    /// <summary>T1 战斗内核验收套件。</summary>
    [TestFixture]
    public class CombatKernelTests
    {
        // --- 容差 ---

        /// <summary>频率容差（次/秒）。</summary>
        private const float FreqTol = 0.02f;

        /// <summary>倍率容差。</summary>
        private const float MultTol = 0.03f;

        /// <summary>一般数值容差（float32 量级）。</summary>
        private const float Eps = 1e-4f;

        /// <summary>玩家血量设成天文数字，保证 20 秒内不会死、只测闸门。</summary>
        private const float PlayerHugeHp = 1e9f;

        private static readonly float FixedStep = FixedStepAccumulator.FixedStep;

        // ---------------------------------------------------------------------
        // 场景构建工具
        // ---------------------------------------------------------------------

        /// <summary>
        /// 构建「n 只敌人钉在玩家接触范围内」的场景。
        ///
        /// 【为什么把速度设成 0】
        /// 本组用例要测的是**两层闸门的量化行为**，不是走位。留着移动速度，敌人会在
        /// 突进相位以 3.2 倍速冲过玩家再折返，「接触与否」就变成位移噪声的函数，
        /// 4.300 次/s 这条基线根本没法复现。速度归零 ⇒ 恒定接触 ⇒ 只剩闸门在起作用，
        /// 这正是能与 T0 的 simulate_lockstep 逐位对齐的前提。
        /// </summary>
        private static Encounter BuildPinnedEncounter(int nEnemies, out RecorderEvents rec, out Combatant player, float contactDamage = 1.0f)
        {
            Encounter enc = new Encounter();
            rec = new RecorderEvents(enc);
            enc.Events = rec;

            player = Combatant.CreatePlayer(0, PlayerHugeHp, Vec2.Zero);
            enc.SetPlayer(player);

            for (int i = 0; i < nEnemies; i++)
            {
                float deg = 360.0f * i / Math.Max(1, nEnemies);
                Vec2 pos = Vec2.Right.RotatedDeg(deg) * 20.0f;   // 20 < TOUCH_RANGE(44)
                Combatant e = Combatant.CreateEnemy(i + 1, pos, 1e9f, contactDamage, 0.0f);
                e.AI.SpawnPos = pos;
                enc.Add(e);
            }

            return enc;
        }

        /// <summary>按固定步直接推进（等价于 60Hz headless 测试台）。</summary>
        private static int RunLockstep(Encounter enc, float duration = 20.0f)
        {
            int steps = (int)Math.Round(duration / FixedStep);
            for (int i = 0; i < steps; i++)
            {
                enc.StepFixed(FixedStep);
            }
            return steps;
        }

        /// <summary>真实帧率驱动：deltaTime = 1/fps 喂累加器，逻辑仍落在 1/60 网格。</summary>
        private static int RunAtFps(Encounter enc, float fps, float duration = 20.0f)
        {
            CombatScheduler sched = new CombatScheduler(enc);
            int frames = (int)Math.Round(duration * fps);
            float dt = 1.0f / fps;
            for (int i = 0; i < frames; i++)
            {
                sched.Tick(dt);
            }
            return sched.TotalSteps;
        }

        /// <summary>跑 n 源围攻，返回每秒命中次数。</summary>
        private static float MeasureFrequency(int nSources, float duration = 20.0f)
        {
            RecorderEvents rec;
            Combatant player;
            Encounter enc = BuildPinnedEncounter(nSources, out rec, out player);
            RunLockstep(enc, duration);
            return rec.HitSteps.Count / duration;
        }

        /// <summary>
        /// T0 侧参照序列：直接用 <see cref="WCoreState"/> 跑同样的锁步。
        /// 返回 (步号, 源号) 序列，用于证明 T1 的承伤确实路由进了同一个 W-CORE。
        /// </summary>
        private static List<KeyValuePair<int, int>> T0ReferenceHitSteps(int nSources, float duration = 20.0f)
        {
            WCoreState w = new WCoreState();
            w.HpMax = PlayerHugeHp;
            w.Hp = PlayerHugeHp;

            List<KeyValuePair<int, int>> outList = new List<KeyValuePair<int, int>>(256);
            int steps = (int)Math.Round(duration / FixedStep);
            for (int i = 0; i < steps; i++)
            {
                w.Tick(FixedStep);
                for (int sid = 0; sid < nSources; sid++)
                {
                    if (w.TakeDamageFrom(sid, 1.0f))
                    {
                        outList.Add(new KeyValuePair<int, int>(i, sid));
                    }
                }
            }
            return outList;
        }

        /// <summary>造一只用于 BOSS 用例的首领（不经 zones.json，参数直给）。</summary>
        private static Combatant MakeBoss(out BossController boss, float hpMax = 1000.0f, float speed = 80.0f, float contact = 5.0f)
        {
            Combatant c = new Combatant();
            c.Id = 99;
            c.Faction = Faction.Enemy;
            c.Kind = CombatConfig.KIND_BLOOD;
            c.DisplayName = "测试首领";
            c.Level = 10;
            c.IsBoss = true;
            c.HpMax = hpMax;
            c.Hp = hpMax;
            c.ContactDamage = contact;
            c.PoiseMax = 60.0f * CombatConfig.BOSS_POISE_MULT;
            c.Poise = c.PoiseMax;

            boss = new BossController(c);
            boss.SpeedBase = speed;
            c.AI = boss;
            return c;
        }

        // =====================================================================
        // T1-01 Vec2 几何（对拍 Godot Vector2）
        // =====================================================================

        [Test]
        public void T1_01_Vec2Geometry()
        {
            Assert.That(new Vec2(0, 0).DistanceTo(new Vec2(3, 4)), Is.EqualTo(5.0f).Within(Eps), "T1-01a 距离");
            Assert.That(new Vec2(1, 2).Dot(new Vec2(3, 4)), Is.EqualTo(11.0f).Within(Eps), "T1-01b 点积");
            Assert.That(Vec2.Right.AngleDegBetween(Vec2.Up), Is.EqualTo(90.0f).Within(1e-3f), "T1-01c 夹角 90°");

            // 共线时反余弦的定义域会被浮点误差顶出 [-1,1]，必须夹取，否则出 NaN。
            Assert.That(Vec2.Right.AngleDegBetween(Vec2.Right), Is.EqualTo(0.0f).Within(1e-3f), "T1-01d 共线 0°");
            Assert.That(Vec2.Right.AngleDegBetween(-Vec2.Right), Is.EqualTo(180.0f).Within(1e-3f), "T1-01d 反向 180°");

            // TOUCH_RANGE 是「<=」判定：44 命中，44.001 落空。
            Assert.That(44.0f <= CombatConfig.TOUCH_RANGE, Is.True, "T1-01e 边界命中");
            Assert.That(44.001f <= CombatConfig.TOUCH_RANGE, Is.False, "T1-01e 越界落空");

            Vec2 r = Vec2.Right.RotatedDeg(90.0f);
            Assert.That(r.X, Is.EqualTo(0.0f).Within(1e-3f), "T1-01f 旋转 X");
            Assert.That(r.Y, Is.EqualTo(1.0f).Within(1e-3f), "T1-01f 旋转 Y");

            // 零向量退化：不得产出 NaN，否则一个死怪能把整场战斗的位置全污染成 NaN。
            Assert.That(Vec2.Zero.Normalized().IsZero(), Is.True, "T1-01g 零向量归一化");
            Assert.That(Vec2.Zero.DirectionTo(Vec2.Zero).IsZero(), Is.True, "T1-01g 零向量方向");

            // 击退初速由「摩擦减速走完 48px」反解：v0 = sqrt(2·a·s)
            float v0 = Combatant.KnockbackImpulse(Vec2.Right, CombatConfig.POISE_BREAK_KNOCKBACK_DIST).Length();
            float expect = (float)Math.Sqrt(2.0 * CombatConfig.KNOCKBACK_FRICTION * CombatConfig.POISE_BREAK_KNOCKBACK_DIST);
            Assert.That(v0, Is.EqualTo(expect).Within(1e-2f), "T1-01h 击退初速 366.61 px/s");
        }

        // =====================================================================
        // T1-02 / T1-03 / T1-04 频率与倍率（内核全链路）
        // =====================================================================

        [Test]
        public void T1_02_SoloFrequency()
        {
            float f = MeasureFrequency(1);
            Assert.That(f, Is.EqualTo(1.700f).Within(FreqTol),
                "T1-02 单挑频率应为 1.700 次/s，实测 " + f.ToString("F3"));
        }

        [Test]
        public void T1_03_SwarmFrequency()
        {
            float f = MeasureFrequency(4);
            Assert.That(f, Is.EqualTo(4.300f).Within(FreqTol),
                "T1-03 围攻频率应为 4.300 次/s，实测 " + f.ToString("F3"));
        }

        [Test]
        public void T1_03b_FrequencyMonotoneSaturation()
        {
            // 频率随源数单调不降，并在 3 源处饱和到 4.300（全局闸门 0.2167s 的硬顶）。
            float f1 = MeasureFrequency(1);
            float f2 = MeasureFrequency(2);
            float f3 = MeasureFrequency(3);
            float f4 = MeasureFrequency(4);
            float f8 = MeasureFrequency(8);

            Assert.That(f1, Is.EqualTo(1.700f).Within(FreqTol), "1 源");
            Assert.That(f2, Is.EqualTo(3.350f).Within(FreqTol), "2 源");
            Assert.That(f3, Is.EqualTo(4.300f).Within(FreqTol), "3 源");
            Assert.That(f4, Is.EqualTo(4.300f).Within(FreqTol), "4 源");
            Assert.That(f8, Is.EqualTo(4.300f).Within(FreqTol), "8 源（饱和后再多也不涨）");

            Assert.That(f2, Is.GreaterThanOrEqualTo(f1 - Eps), "单调");
            Assert.That(f3, Is.GreaterThanOrEqualTo(f2 - Eps), "单调");
            Assert.That(f8, Is.LessThanOrEqualTo(f4 + Eps), "饱和");
        }

        [Test]
        public void T1_04_SwarmRatio()
        {
            float solo = MeasureFrequency(1);
            float swarm = MeasureFrequency(4);
            float ratio = swarm / solo;
            Assert.That(ratio, Is.EqualTo(2.53f).Within(MultTol),
                "T1-04 围攻/单挑倍率应为 2.53x，实测 " + ratio.ToString("F4"));
        }

        // =====================================================================
        // T1-05 帧率无关性
        // =====================================================================

        [Test]
        public void T1_05_FrameRateIndependence()
        {
            // 30 / 60 / 144 / 240Hz 喂 Tick，逻辑步恒为 1/60，频率必须一致。
            float[] fpsList = { 30.0f, 60.0f, 144.0f, 240.0f };
            foreach (float fps in fpsList)
            {
                foreach (int n in new[] { 1, 4 })
                {
                    RecorderEvents rec;
                    Combatant player;
                    Encounter enc = BuildPinnedEncounter(n, out rec, out player);
                    RunAtFps(enc, fps, 20.0f);
                    float f = rec.HitSteps.Count / 20.0f;
                    float expect = n == 1 ? 1.700f : 4.300f;
                    Assert.That(f, Is.EqualTo(expect).Within(FreqTol),
                        string.Format("T1-05 {0}Hz/{1}源 频率应为 {2:F3}，实测 {3:F3}", fps, n, expect, f));
                }
            }
        }

        // =====================================================================
        // T1-06 减法承伤（敌人侧）与除法软上限（玩家侧）
        // =====================================================================

        [Test]
        public void T1_06_SubtractiveMitigation()
        {
            // real = max(1, raw − max(0, armor − breakDef))
            Assert.That(DamageResolver.PreviewSubtractive(4, 10, 0), Is.EqualTo(1.0f).Within(Eps), "T1-06a 地板值 1");
            Assert.That(DamageResolver.PreviewSubtractive(20, 10, 0), Is.EqualTo(10.0f).Within(Eps), "T1-06a 常规");
            Assert.That(DamageResolver.PreviewSubtractive(100, 5, 0), Is.EqualTo(95.0f).Within(Eps), "T1-06a 高伤");
            Assert.That(DamageResolver.PreviewSubtractive(20, 10, 4), Is.EqualTo(14.0f).Within(Eps), "T1-06a 破防 4");
            Assert.That(DamageResolver.PreviewSubtractive(20, 10, 30), Is.EqualTo(20.0f).Within(Eps), "T1-06a 破防夹取到 0");

            // 连续扣血
            Combatant e = Combatant.CreateEnemy(1, Vec2.Zero, 100.0f, 1.0f, 0.0f);
            e.Armor = 10.0f;
            e.ApplyEnemyDamage(20.0f);
            Assert.That(e.Hp, Is.EqualTo(90.0f).Within(Eps), "T1-06b 第一下扣 10");
            e.ApplyEnemyDamage(4.0f);
            Assert.That(e.Hp, Is.EqualTo(89.0f).Within(Eps), "T1-06b 第二下走地板扣 1");

            // 血量夹到 0 不为负
            e.ApplyEnemyDamage(1e6f);
            Assert.That(e.Hp, Is.EqualTo(0.0f).Within(Eps), "T1-06c 不为负");
            Assert.That(e.IsAlive, Is.False, "T1-06c 已死");

            // 玩家侧刻意不同构：走 W-CORE 的除法软上限 keep = 1 − def/(def+100)
            float keep = 1.0f - Difficulty.MitigationOf(50.0f);
            Assert.That(keep, Is.EqualTo(2.0f / 3.0f).Within(1e-5f), "T1-06d def=50 ⇒ keep=2/3");
        }

        // =====================================================================
        // T1-07 AI 状态转移
        // =====================================================================

        private static AIState ProbeState(float dist, float strikeCd = 0.0f)
        {
            Combatant e = Combatant.CreateEnemy(1, new Vec2(dist, 0.0f), 100.0f, 1.0f, 70.0f);
            e.AI.SpawnPos = new Vec2(dist, 0.0f);
            e.AI.StrikeCd = strikeCd;
            e.AI.Update(FixedStep, Vec2.Zero, Vec2.Zero);
            return e.AI.State;
        }

        [Test]
        public void T1_07_AiStateTransitions()
        {
            // 脱战 > STRIKE 冷却 > 进入 STRIKE 距离 > 否则 CHASE
            Assert.That(ProbeState(600.0f), Is.EqualTo(AIState.PATROL), "T1-07a 远超牵引");
            Assert.That(ProbeState(480.1f), Is.EqualTo(AIState.PATROL), "T1-07a 刚出牵引");
            Assert.That(ProbeState(480.0f), Is.EqualTo(AIState.CHASE), "T1-07a 牵引边界内（> 才脱战）");
            Assert.That(ProbeState(300.0f), Is.EqualTo(AIState.CHASE), "T1-07a 中距");
            Assert.That(ProbeState(160.1f), Is.EqualTo(AIState.CHASE), "T1-07a 刚出突进距离");
            Assert.That(ProbeState(160.0f), Is.EqualTo(AIState.STRIKE), "T1-07a 突进边界（<= 即进）");
            Assert.That(ProbeState(50.0f), Is.EqualTo(AIState.STRIKE), "T1-07a 贴脸");

            Assert.That(ProbeState(50.0f, 2.0f), Is.EqualTo(AIState.CHASE), "T1-07b 冷却中贴脸也只 CHASE");

            // PATROL：远离锚点时以 30% 速度回锚
            Combatant far = Combatant.CreateEnemy(1, new Vec2(1000.0f, 0.0f), 100.0f, 1.0f, 70.0f);
            far.AI.SpawnPos = new Vec2(1400.0f, 0.0f);      // 距锚点 400 ≥ 20
            far.AI.Update(FixedStep, Vec2.Zero, Vec2.Zero);
            Assert.That(far.AI.State, Is.EqualTo(AIState.PATROL), "T1-07c 应处于 PATROL");
            Assert.That(far.AI.PollCommand().DesiredVelocity.Length(),
                Is.EqualTo(70.0f * CombatConfig.AI_PATROL_SPEED_MULT).Within(1e-3f),
                "T1-07c 回锚速度 = 基础 ×0.3");

            // 到 20 像素内即停，否则会在锚点上抖动
            Combatant near = Combatant.CreateEnemy(2, new Vec2(1000.0f, 0.0f), 100.0f, 1.0f, 70.0f);
            near.AI.SpawnPos = new Vec2(1005.0f, 0.0f);     // 距锚点 5 < 20
            near.AI.Update(FixedStep, Vec2.Zero, Vec2.Zero);
            Assert.That(near.AI.PollCommand().DesiredVelocity.Length(), Is.EqualTo(0.0f).Within(Eps),
                "T1-07c 锚点 20px 内停住");
        }

        // =====================================================================
        // T1-08 STRIKE 时序：windup 0.35 → dash 0.25 → cd 3.5
        // =====================================================================

        [Test]
        public void T1_08_StrikeTiming()
        {
            Combatant e = Combatant.CreateEnemy(1, Vec2.Zero, 100.0f, 10.0f, 100.0f);
            e.AI.SpawnPos = Vec2.Zero;
            Vec2 target = new Vec2(100.0f, 0.0f);   // dist=100 ≤ 160 ⇒ 直接进 STRIKE

            int windupSteps = 0;
            int dashSteps = 0;
            bool started = false;
            bool ended = false;
            float firstDashSpeed = -1.0f;
            bool allDashMult15 = true;
            bool allDashSpeedFull = true;

            for (int i = 0; i < 400; i++)
            {
                e.AI.Update(FixedStep, target, Vec2.Zero);
                AICommand cmd = e.AI.PollCommand();

                if (cmd.StrikeStarted)
                {
                    started = true;
                }
                if (e.AI.StrikePhase == EnemyAI.STRIKE_PHASE_WINDUP)
                {
                    windupSteps += 1;
                }
                if (cmd.Dashing)
                {
                    dashSteps += 1;
                    if (firstDashSpeed < 0.0f)
                    {
                        firstDashSpeed = cmd.DesiredVelocity.Length();
                    }
                    if (Math.Abs(cmd.DamageMult - CombatConfig.AI_STRIKE_DMG_MULT) > Eps)
                    {
                        allDashMult15 = false;
                    }
                    if (Math.Abs(cmd.DesiredVelocity.Length() - 100.0f * CombatConfig.AI_STRIKE_SPEED_MULT) > 1e-2f)
                    {
                        allDashSpeedFull = false;
                    }
                }
                if (cmd.StrikeEnded)
                {
                    ended = true;
                    break;
                }
            }

            Assert.That(started, Is.True, "T1-08a 进入 STRIKE 应抛 StrikeStarted");
            Assert.That(ended, Is.True, "T1-08 突进应在 400 步内结束");

            // 【为什么容差是「±1 逻辑步」而不是精确相等】
            // 计时器用 `timer -= dt` 逐步递减，0.35 与 0.25 都不是 1/60 的精确二进制倍数，
            // 递减 21 次后残差是个极小正数而非 0，于是相位多挂一步才翻。
            // 实测：windup 22 步(0.36667s)、dash 16 步(0.26667s)，各比标称多一步。
            // 这一步的偏差在 60Hz 下是 16.7ms，属原型手感范围内，不做"修正"以免改动手感基线；
            // 但**必须保证它不累加**——见 T1-10/T1-11 对 BOSS 周期的进位式复位断言。
            float windupT = windupSteps * FixedStep;
            float dashT = dashSteps * FixedStep;
            Assert.That(Math.Abs(windupT - CombatConfig.AI_STRIKE_WINDUP), Is.LessThanOrEqualTo(FixedStep + 1e-6f),
                "T1-08b windup 应 ≈0.35s（±1 逻辑步），实测 " + windupSteps + " 步");
            Assert.That(Math.Abs(dashT - CombatConfig.AI_STRIKE_DASH), Is.LessThanOrEqualTo(FixedStep + 1e-6f),
                "T1-08c dash 应 ≈0.25s（±1 逻辑步），实测 " + dashSteps + " 步");

            Assert.That(firstDashSpeed, Is.EqualTo(100.0f * CombatConfig.AI_STRIKE_SPEED_MULT).Within(1e-2f),
                "T1-08d 突进速度 = 基础 ×3.2");
            Assert.That(allDashSpeedFull, Is.True, "T1-08d 突进期每一步都是满速");

            // D-2：突进最后一步会先把相位重置为 IDLE 再返回突进速度。若倍率去读相位，
            // 这一步就会「以 3.2 倍速冲过去却只结算 1.0 倍伤害」——而这一步恰恰最容易撞上玩家。
            Assert.That(allDashMult15, Is.True, "T1-08e 突进期伤害倍率恒为 1.5（含最后一步 · D-2）");

            Assert.That(e.AI.StrikeCd, Is.EqualTo(CombatConfig.AI_STRIKE_CD).Within(Eps), "T1-08f 落 CD 3.5s");
            Assert.That(e.AI.State, Is.EqualTo(AIState.CHASE), "T1-08f 回到 CHASE");

            // 蓄力期零位移 —— 这是玩家的反应窗口，动了就等于没有窗口。
            // 同时校验 D-3：蓄力最后一步已把相位翻成 DASH，但返回的仍是零速度，
            // 此时 Dashing 必须还是 false，否则 1.5 倍伤害会在敌人没动的时候就先亮。
            Combatant e2 = Combatant.CreateEnemy(2, Vec2.Zero, 100.0f, 10.0f, 100.0f);
            float moved = 0.0f;
            for (int i = 0; i < 10; i++)
            {
                e2.AI.Update(FixedStep, target, Vec2.Zero);
                AICommand c2 = e2.AI.PollCommand();
                moved += c2.DesiredVelocity.Length();
                Assert.That(c2.Dashing, Is.False, "T1-08g 蓄力期不得点亮 Dashing（D-3）");
            }
            Assert.That(moved, Is.EqualTo(0.0f).Within(Eps), "T1-08g 蓄力期零位移");
        }

        // =====================================================================
        // T1-09 BOSS 阶段阈值
        // =====================================================================

        [Test]
        public void T1_09_BossPhaseThresholds()
        {
            BossController boss;
            Combatant c = MakeBoss(out boss);
            RecorderEvents rec = new RecorderEvents();
            boss.Events = rec;
            Vec2 far = new Vec2(10000.0f, 0.0f);    // 拉远，避免 AI 干扰

            float[] ratios = { 0.70f, 0.66f, 0.65f, 0.50f, 0.31f, 0.30f, 0.10f };
            BossPhase[] expects =
            {
                BossPhase.P1, BossPhase.P1, BossPhase.P2, BossPhase.P2,
                BossPhase.P2, BossPhase.P3, BossPhase.P3
            };

            for (int i = 0; i < ratios.Length; i++)
            {
                c.Hp = c.HpMax * ratios[i];
                rec.Tick();
                boss.Update(FixedStep, far, Vec2.Zero);
                Assert.That(boss.Phase, Is.EqualTo(expects[i]),
                    string.Format("T1-09a hp={0:P0} 应为 {1}", ratios[i], expects[i]));
            }

            Assert.That(rec.Phases.Count, Is.EqualTo(2), "T1-09b 阶段事件共 2 次");
            Assert.That(rec.Phases[0], Is.EqualTo(BossPhase.P2), "T1-09b 第一次是 P2");
            Assert.That(rec.Phases[1], Is.EqualTo(BossPhase.P3), "T1-09b 第二次是 P3");

            // 只降不升：回满血不该把阶段退回去，否则读盘/吸血能让 BOSS 反复播放入场横幅。
            c.Hp = c.HpMax;
            rec.Tick();
            boss.Update(FixedStep, far, Vec2.Zero);
            Assert.That(boss.Phase, Is.EqualTo(BossPhase.P3), "T1-09c 回满血不回退");
            Assert.That(rec.Phases.Count, Is.EqualTo(2), "T1-09c 不再多抛事件");

            // 一击跨两阈值：只抛 P3，不在同一帧闪两条横幅
            BossController boss2;
            Combatant c2 = MakeBoss(out boss2);
            RecorderEvents rec2 = new RecorderEvents();
            boss2.Events = rec2;
            c2.Hp = c2.HpMax * 0.20f;
            rec2.Tick();
            boss2.Update(FixedStep, far, Vec2.Zero);
            Assert.That(boss2.Phase, Is.EqualTo(BossPhase.P3), "T1-09d 直接进 P3");
            Assert.That(rec2.Phases.Count, Is.EqualTo(1), "T1-09d 只抛一次事件");
            Assert.That(rec2.Phases[0], Is.EqualTo(BossPhase.P3), "T1-09d 抛的是 P3");
        }

        // =====================================================================
        // T1-10 BOSS P2：速度×1.25，每 8.0s 召唤 2 只
        // =====================================================================

        [Test]
        public void T1_10_BossPhase2Summon()
        {
            BossController boss;
            Combatant c = MakeBoss(out boss, 1000.0f, 80.0f);
            RecorderEvents rec = new RecorderEvents();
            boss.Events = rec;
            boss.Spawn = req => Combatant.CreateEnemy(1000 + req.Index, req.Position, 10.0f, 1.0f, 60.0f);
            Vec2 far = new Vec2(10000.0f, 0.0f);

            c.Hp = c.HpMax * 0.60f;     // 进 P2
            rec.Tick();
            boss.Update(FixedStep, far, Vec2.Zero);

            Assert.That(boss.SpeedMult, Is.EqualTo(CombatConfig.BOSS_P2_SPEED_MULT).Within(Eps), "T1-10a 速度倍率 1.25");
            Assert.That(boss.AtkMult, Is.EqualTo(CombatConfig.BOSS_P2_ATK_MULT).Within(Eps), "T1-10a 攻击倍率 1.00");
            Assert.That(boss.EffectiveSpeed, Is.EqualTo(80.0f * 1.25f).Within(1e-3f), "T1-10a 有效速度 100");

            int steps = (int)Math.Round(24.5f / FixedStep);
            for (int i = 0; i < steps; i++)
            {
                rec.Tick();
                boss.Update(FixedStep, far, Vec2.Zero);
            }

            Assert.That(rec.SummonSteps.Count, Is.EqualTo(3), "T1-10b 24.5s 内应召唤 3 次");

            // D-4：周期用**进位式复位**（cd += 周期），不是 `cd = 周期`。
            // 后者每次都会把上一周期的浮点残差抹掉重来，误差按次数线性累加，
            // 跑满一场 BOSS 战能漂出小半个周期；进位式让残差自我抵消，间隔恒定。
            for (int i = 0; i < rec.SummonSteps.Count; i++)
            {
                float t = rec.SummonSteps[i] * FixedStep;
                float expect = CombatConfig.BOSS_P2_SUMMON_CD * (i + 1);
                Assert.That(Math.Abs(t - expect), Is.LessThanOrEqualTo(FixedStep + 1e-6f),
                    string.Format("T1-10b 第 {0} 次召唤应在 {1:F1}s（±1 步），实测 {2:F3}s", i + 1, expect, t));
            }
            for (int i = 1; i < rec.SummonSteps.Count; i++)
            {
                float gap = (rec.SummonSteps[i] - rec.SummonSteps[i - 1]) * FixedStep;
                Assert.That(gap, Is.EqualTo(CombatConfig.BOSS_P2_SUMMON_CD).Within(1e-3f),
                    "T1-10b 相邻间隔必须恒为 8.0s（误差不累加 · D-4）");
            }

            foreach (int n in rec.SummonCounts)
            {
                Assert.That(n, Is.EqualTo(CombatConfig.BOSS_P2_SUMMON_N), "T1-10c 每次召唤 2 只");
            }
            Assert.That(boss.PendingSpawns.Count, Is.EqualTo(6), "T1-10c 6 只召唤物已入队");
        }

        // =====================================================================
        // T1-11 BOSS P3：攻击×1.40 / 速度×1.40，每 6.0s 冲击波 r=200
        // =====================================================================

        [Test]
        public void T1_11_BossPhase3Shockwave()
        {
            BossController boss;
            Combatant c = MakeBoss(out boss, 1000.0f, 80.0f);
            RecorderEvents rec = new RecorderEvents();
            boss.Events = rec;
            Vec2 far = new Vec2(10000.0f, 0.0f);

            c.Hp = c.HpMax * 0.25f;     // 直接进 P3
            rec.Tick();
            boss.Update(FixedStep, far, Vec2.Zero);

            Assert.That(boss.AtkMult, Is.EqualTo(CombatConfig.BOSS_P3_ATK_MULT).Within(Eps), "T1-11a 攻击倍率 1.40");
            Assert.That(boss.SpeedMult, Is.EqualTo(CombatConfig.BOSS_P3_SPEED_MULT).Within(Eps), "T1-11a 速度倍率 1.40");
            Assert.That(boss.EffectiveSpeed, Is.EqualTo(80.0f * 1.40f).Within(1e-3f), "T1-11a 有效速度 112");

            int steps = (int)Math.Round(18.5f / FixedStep);
            for (int i = 0; i < steps; i++)
            {
                rec.Tick();
                boss.Update(FixedStep, far, Vec2.Zero);
                Vec2 center;
                float radius;
                boss.ConsumeShockwave(out center, out radius);   // 排空，模拟 Encounter 的消费
            }

            Assert.That(rec.ShockSteps.Count, Is.EqualTo(3), "T1-11b 18.5s 内应放 3 次冲击波");
            for (int i = 1; i < rec.ShockSteps.Count; i++)
            {
                float gap = (rec.ShockSteps[i] - rec.ShockSteps[i - 1]) * FixedStep;
                Assert.That(gap, Is.EqualTo(CombatConfig.BOSS_P3_SHOCK_CD).Within(1e-3f),
                    "T1-11b 相邻间隔必须恒为 6.0s（误差不累加 · D-4）");
            }
            foreach (float r in rec.ShockRadii)
            {
                Assert.That(r, Is.EqualTo(CombatConfig.BOSS_P3_SHOCK_RADIUS).Within(Eps), "T1-11c 半径 200");
            }

            // 冲击波命中判定是纯几何，不依赖任何碰撞体
            BossController src;
            Combatant srcC = MakeBoss(out src, 1000.0f, 80.0f, 20.0f);
            RecorderEvents rec2 = new RecorderEvents();

            Combatant player = Combatant.CreatePlayer(0, PlayerHugeHp, new Vec2(150.0f, 0.0f));
            bool hitIn = DamageResolver.ResolveShockwave(srcC, player, Vec2.Zero, CombatConfig.BOSS_P3_SHOCK_RADIUS, rec2);
            Assert.That(hitIn, Is.True, "T1-11d 150 < 200 应命中");

            player.Position = new Vec2(250.0f, 0.0f);
            player.WCore.Reset();
            bool hitOut = DamageResolver.ResolveShockwave(srcC, player, Vec2.Zero, CombatConfig.BOSS_P3_SHOCK_RADIUS, rec2);
            Assert.That(hitOut, Is.False, "T1-11d 250 > 200 应落空");
        }

        // =====================================================================
        // T1-12 分级缩放 / 精英 / 词缀
        // =====================================================================

        [Test]
        public void T1_12_Scaling()
        {
            Assert.That(DifficultyBridge.HpScale(1), Is.EqualTo(1.00f).Within(Eps), "T1-12a hp_scale(1)");
            Assert.That(DifficultyBridge.HpScale(5), Is.EqualTo(1.72f).Within(Eps), "T1-12a hp_scale(5)");
            Assert.That(DifficultyBridge.HpScale(10), Is.EqualTo(2.62f).Within(Eps), "T1-12a hp_scale(10)");

            Assert.That(DifficultyBridge.DmgScale(1), Is.EqualTo(1.00f).Within(Eps), "T1-12b dmg_scale(1)");
            Assert.That(DifficultyBridge.DmgScale(5), Is.EqualTo(1.56f).Within(Eps), "T1-12b dmg_scale(5)");
            Assert.That(DifficultyBridge.DmgScale(10), Is.EqualTo(2.26f).Within(Eps), "T1-12b dmg_scale(10)");

            Assert.That(DifficultyBridge.ClampLevel(0), Is.EqualTo(CombatConfig.ENEMY_LEVEL_MIN), "T1-12c 下夹");
            Assert.That(DifficultyBridge.ClampLevel(999), Is.EqualTo(CombatConfig.ENEMY_LEVEL_MAX), "T1-12c 上夹");
            Assert.That(DifficultyBridge.ClampLevel(7), Is.EqualTo(7), "T1-12c 区间内不动");

            // normal witch lv5: hp = 22 × 1.72 = 37.84
            DifficultyBridge bridge = new DifficultyBridge();
            bridge.PlayerDef = 0.0f;
            ZoneEnemies cfg = new ZoneEnemies
            {
                Count = 4,
                Kinds = new List<string> { CombatConfig.KIND_WITCH },
                HpMult = 1.0f,
                AtkMult = 1.0f,
                ArmorAdd = 0,
                EliteRate = 0.0f,
                AffixRate = 0.0f
            };
            EnemyBaseStats witch = EnemyBaseTable.Get(CombatConfig.KIND_WITCH);
            Combatant e = bridge.BuildEnemy(cfg, witch, 5, null);

            Assert.That(e.HpMax, Is.EqualTo(37.84f).Within(1e-2f), "T1-12d witch lv5 hp");
            Assert.That(e.MoveSpeed, Is.EqualTo(witch.Speed).Within(Eps), "T1-12e speed 透传");
            Assert.That(e.Armor, Is.EqualTo(witch.Armor).Within(Eps), "T1-12e armor 透传");
            Assert.That(e.PoiseMax, Is.EqualTo(witch.PoiseMax).Within(Eps), "T1-12e poise 透传");

            // 精英：HP×2.6 / ATK×1.5 / EXP×2.0
            float baseHp = e.HpMax;
            float baseAtk = e.ContactDamage;
            float baseExp = e.ExpValue;
            Combatant elite = bridge.BuildEnemy(cfg, witch, 5, null);
            elite.IsElite = true;
            elite.HpMax = elite.HpMax * CombatConfig.ELITE_HP_MULT;
            elite.Hp = elite.HpMax;
            elite.ContactDamage = elite.ContactDamage * CombatConfig.ELITE_ATK_MULT;
            elite.ExpValue = elite.ExpValue * CombatConfig.ELITE_EXP_MULT;
            Assert.That(elite.HpMax, Is.EqualTo(baseHp * 2.6f).Within(1e-2f), "T1-12f 精英 HP×2.6");
            Assert.That(elite.ContactDamage, Is.EqualTo(baseAtk * 1.5f).Within(1e-3f), "T1-12f 精英 ATK×1.5");
            Assert.That(elite.ExpValue, Is.EqualTo(baseExp * 2.0f).Within(1e-3f), "T1-12f 精英 EXP×2.0");

            // 词缀 swift / ironhide / blaze
            Combatant sw = bridge.BuildEnemy(cfg, witch, 5, null);
            float speed0 = sw.MoveSpeed;
            DifficultyBridge.ApplyAffix(sw, AffixKind.Swift);
            Assert.That(sw.MoveSpeed, Is.EqualTo(speed0 * CombatConfig.AFFIX_SWIFT_SPEED_MULT).Within(1e-2f),
                "T1-12g swift ×1.35");
            // 速度只有一个真源：MoveSpeed 是 AI.SpeedBase 的代理，词缀必须落在它上面，
            // 否则 AI 用旧速度跑、UI 显示新速度，两边永远对不上。
            Assert.That(sw.AI.SpeedBase, Is.EqualTo(sw.MoveSpeed).Within(Eps), "T1-12h swift 落在 AI.SpeedBase");

            Combatant ih = bridge.BuildEnemy(cfg, witch, 5, null);
            float armor0 = ih.Armor;
            DifficultyBridge.ApplyAffix(ih, AffixKind.Ironhide);
            Assert.That(ih.Armor, Is.EqualTo(armor0 + CombatConfig.AFFIX_IRONHIDE_ARMOR_ADD).Within(Eps),
                "T1-12g ironhide +12");

            Combatant bz = bridge.BuildEnemy(cfg, witch, 5, null);
            float dmg0 = bz.ContactDamage;
            DifficultyBridge.ApplyAffix(bz, AffixKind.Blaze);
            Assert.That(bz.ContactDamage, Is.EqualTo(dmg0 * CombatConfig.AFFIX_BLAZE_DMG_MULT).Within(1e-4f),
                "T1-12g blaze ×1.25");

            // BOSS：lv = base + offset，hp = normal × hp_mult，韧性 ×3.0，经验 ×3.0
            ZoneBoss bossCfg = new ZoneBoss
            {
                BossId = "test_boss",
                DisplayName = "测试首领",
                Kind = CombatConfig.KIND_WITCH,
                LevelOffset = 3,
                HpMult = 10.0f,
                AtkMult = 1.6f
            };
            Combatant boss = bridge.BuildBoss(bossCfg, cfg, 2, null);
            Assert.That(boss.Level, Is.EqualTo(5), "T1-12i BOSS lv = 2+3");
            Assert.That(boss.HpMax, Is.EqualTo(22.0f * 1.72f * 10.0f).Within(1e-1f), "T1-12i BOSS hp = 378.4");
            Assert.That(boss.PoiseMax, Is.EqualTo(witch.PoiseMax * CombatConfig.BOSS_POISE_MULT).Within(Eps),
                "T1-12i BOSS 韧性 ×3.0");
            Assert.That(boss.IsBoss, Is.True, "T1-12i IsBoss");
            Assert.That(boss.AI, Is.InstanceOf<BossController>(), "T1-12i BOSS 应挂 BossController");
        }

        // =====================================================================
        // T1-13 模型 B 反调：d_eff ≈ **玩家** hp_max/65（U1 修复后口径）
        //
        // 靶子分子是玩家血上限，不是敌人的。A1/A2 两条验收窗口量的是「玩家还能活
        // 多少秒」，分子只可能是玩家的血。用敌人血会让 d_eff 掉到 1 以下被
        // Difficulty.DamageFloor 顶平，单挑 150+ 秒不死。详见 DifficultyBridge 文件头。
        // =====================================================================

        [Test]
        public void T1_13_ModelBSolve()
        {
            string[] kinds =
            {
                CombatConfig.KIND_BLOOD, CombatConfig.KIND_WITCH,
                CombatConfig.KIND_SWORD, CombatConfig.KIND_ALCHEMY
            };
            float[] defs = { 0.0f, 25.0f, 50.0f, 120.0f };
            int[] levels = { 1, 3, 5, 8, 12 };

            ZoneEnemies cfg = new ZoneEnemies
            {
                Count = 4,
                Kinds = new List<string>(kinds),
                HpMult = 1.0f,
                AtkMult = 1.0f,
                ArmorAdd = 0,
                EliteRate = 0.0f,
                AffixRate = 0.0f
            };

            // 80 组：4 防御 × 4 种族 × 5 等级，全部必须命中靶心 playerHp/65。
            // 靶心恒定、与敌人种族/等级/血量无关——「等级越高越难」由敌人血更厚、
            // 数量更多来体现，不由单次接触伤害体现。
            foreach (float def in defs)
            {
                DifficultyBridge bridge = new DifficultyBridge();
                bridge.PlayerDef = def;
                float keep = 1.0f - Difficulty.MitigationOf(def);
                float target = Difficulty.ComputeDeff(bridge.PlayerHpMax);

                foreach (string kind in kinds)
                {
                    EnemyBaseStats bs = EnemyBaseTable.Get(kind);
                    foreach (int lv in levels)
                    {
                        Combatant e = bridge.BuildEnemy(cfg, bs, lv, null);
                        float deff = e.ContactDamage * keep;

                        Assert.That(deff, Is.EqualTo(target).Within(Math.Max(1e-3f, target * 1e-3f)),
                            string.Format("T1-13a def={0} {1} lv{2} d_eff 应命中靶心 playerHp/65", def, kind, lv));
                        Assert.That(Difficulty.IsDeffInTarget(deff, bridge.PlayerHpMax), Is.True,
                            string.Format("T1-13a def={0} {1} lv{2} 应落在 [pHp/73.8, pHp/58.3]", def, kind, lv));
                        Assert.That(bridge.IsDeffInTarget(e), Is.True,
                            "T1-13a Bridge.IsDeffInTarget 应与 Difficulty 判定一致");
                    }
                }
            }

            // BOSS 的反调靶子与杂兵同口径取**玩家**血上限，再乘 boss.atk_mult 作为显式加成。
            // 若直接拿 BOSS 的巨额 hp_max 反调，d_eff 会被 hp_mult=10 整整放大十倍，
            // 玩家会被一次接触秒杀。
            DifficultyBridge b2 = new DifficultyBridge();
            b2.PlayerDef = 0.0f;
            ZoneBoss bossCfg = new ZoneBoss
            {
                BossId = "test_boss",
                DisplayName = "测试首领",
                Kind = CombatConfig.KIND_WITCH,
                LevelOffset = 3,
                HpMult = 10.0f,
                AtkMult = 1.6f
            };
            Combatant boss = b2.BuildBoss(bossCfg, cfg, 2, null);
            float expectDeff = Difficulty.ComputeDeff(b2.PlayerHpMax) * 1.6f;
            Assert.That(boss.ContactDamage, Is.EqualTo(expectDeff).Within(1e-2f),
                "T1-13d BOSS d_eff = (playerHp/65)×1.6，不被 hp_mult 放大");

            // T1-13e U1 回归闸：修复前 d_eff≈0.58 被 DamageFloor=1 顶平，单挑 150+ 秒不死。
            // 守住「靶心必须真的落在 A1/A2 双窗口内」，防止将来有人把靶子改回敌人血。
            DifficultyBridge b3 = new DifficultyBridge();
            float deffTarget = Difficulty.ComputeDeff(b3.PlayerHpMax);
            Assert.That(deffTarget, Is.GreaterThan(Difficulty.DamageFloor),
                "T1-13e 靶心 d_eff 必须高于伤害地板，否则承伤被顶平、难度曲线失效");
            Assert.That(Difficulty.IsA2Pass(b3.PlayerHpMax, deffTarget), Is.True,
                "T1-13e A2 单挑存活应落在 [35, 60] s");
            Assert.That(Difficulty.IsA1Pass(b3.PlayerHpMax, deffTarget), Is.True,
                "T1-13e A1 围攻存活应落在 [10, 16] s");
        }

        // =====================================================================
        // T1-14 端到端锁步：固定种子 + 4 怪围攻，对拍 T0 承伤序列
        // =====================================================================

        [Test]
        public void T1_14_EndToEndLockstep()
        {
            // (a) zone 种子由 djb2 确定性派生
            uint h = GodotHash.Djb2("zone_youhuang");
            long s1 = ZoneSeed.Derive("zone_youhuang", false, 1);
            long s2 = ZoneSeed.Derive("zone_youhuang", false, 1);
            Assert.That(s1, Is.EqualTo(s2), "T1-14a 同参数派生必须一致");
            Assert.That(h, Is.Not.EqualTo(0u), "T1-14a djb2 非零");

            ZoneEnemies cfg = new ZoneEnemies
            {
                Count = 4,
                Kinds = new List<string> { CombatConfig.KIND_BLOOD, CombatConfig.KIND_WITCH },
                HpMult = 1.0f,
                AtkMult = 1.0f,
                ArmorAdd = 0,
                EliteRate = 0.0f,
                AffixRate = 0.0f
            };

            // (b) 同种子两次生成的怪群完全一致
            List<string> g1 = BuildWave(cfg, new PCG32(PCG32.DefaultSeed));
            List<string> g2 = BuildWave(cfg, new PCG32(PCG32.DefaultSeed));
            Assert.That(g1, Is.EqualTo(g2), "T1-14b seed=20260730 两次生成必须逐位一致");

            // (c) 换 zone 种子长出不同怪群（证明种子确实生效，不是摆设）
            List<string> g3 = BuildWave(cfg, ZoneSeed.CreateRng("zone_youhuang", false, 1));
            Assert.That(g3, Is.Not.EqualTo(g1), "T1-14c 换种子应长出不同怪群");

            // (d) 4 怪围攻频率 4.300
            RecorderEvents rec;
            Combatant player;
            Encounter enc = BuildPinnedEncounter(4, out rec, out player);
            RunLockstep(enc, 20.0f);
            float freq = rec.HitSteps.Count / 20.0f;
            Assert.That(freq, Is.EqualTo(4.300f).Within(FreqTol),
                "T1-14d 端到端围攻频率应为 4.300，实测 " + freq.ToString("F3"));

            // (e) 承伤序列与 T0 W-CORE 逐位一致 —— 这条才是「T1 真的路由进了同一个 W-CORE」
            //     的证明。频率对得上但序列错位，说明中间有人偷偷缓存或重排了命中。
            List<KeyValuePair<int, int>> t0 = T0ReferenceHitSteps(4, 20.0f);
            Assert.That(rec.HitSteps.Count, Is.EqualTo(t0.Count),
                "T1-14e 命中次数应与 T0 参照一致");
            for (int i = 0; i < t0.Count; i++)
            {
                // Encounter.StepCount 从 1 起，T0 参照的步号从 0 起，故 -1 对齐。
                Assert.That(rec.HitSteps[i] - 1, Is.EqualTo(t0[i].Key),
                    string.Format("T1-14e 第 {0} 次命中的步号应一致", i));
                Assert.That(rec.HitSources[i] - 1, Is.EqualTo(t0[i].Value),
                    string.Format("T1-14e 第 {0} 次命中的来源应一致", i));
            }

            // (f) 端到端倍率 2.53x
            float solo = MeasureFrequency(1);
            float ratio = freq / solo;
            Assert.That(ratio, Is.EqualTo(2.53f).Within(MultTol),
                "T1-14f 端到端倍率应为 2.53x，实测 " + ratio.ToString("F4"));
        }

        /// <summary>造一波 4 只怪，返回可比较的特征串列表。</summary>
        private static List<string> BuildWave(ZoneEnemies cfg, PCG32 rng)
        {
            DifficultyBridge bridge = new DifficultyBridge();
            bridge.PlayerDef = 0.0f;
            List<string> outList = new List<string>(4);
            for (int i = 0; i < 4; i++)
            {
                Combatant e = bridge.BuildEnemy(cfg, 3, null, rng);
                outList.Add(string.Format("{0}/lv{1}/hp{2:F4}/dmg{3:F4}/e{4}/a{5}",
                    e.Kind, e.Level, e.HpMax, e.ContactDamage, e.IsElite ? 1 : 0, e.Affix));
            }
            return outList;
        }

        // =====================================================================
        // 附加 · 全链路健全性（霸体 / 击退 / 死亡 / 召唤入列）
        // =====================================================================

        [Test]
        public void Extra_PoiseKnockbackDeathAndSummon()
        {
            // (a) 韧性累积到 0 才破韧，不是每下都破；破韧后立刻回满
            Combatant e = Combatant.CreateEnemy(1, Vec2.Zero, 100.0f, 1.0f, 0.0f);
            e.PoiseMax = 30.0f;
            e.Poise = 30.0f;
            int breaks = 0;
            for (int i = 0; i < 3; i++)
            {
                if (e.DamagePoise(12.0f))
                {
                    breaks += 1;
                }
            }
            Assert.That(breaks, Is.EqualTo(1), "附加a 3 下 ×12 只应破韧 1 次");
            Assert.That(e.Poise, Is.EqualTo(30.0f).Within(Eps), "附加a 破韧后韧性回满");

            // (b) 破韧施加硬直 0.20s 且写入击退速度
            e.ApplyHitStun(CombatConfig.POISE_BREAK_HITSTUN);
            e.TakeKnockback(Combatant.KnockbackImpulse(Vec2.Right, CombatConfig.POISE_BREAK_KNOCKBACK_DIST));
            Assert.That(e.HitStunTimer, Is.EqualTo(CombatConfig.POISE_BREAK_HITSTUN).Within(Eps), "附加b 硬直 0.20s");
            Assert.That(e.KnockbackVel.Length(), Is.GreaterThan(300.0f), "附加b 击退初速已写入");

            // (c) 硬直期间击退位移照常推进 —— 否则「被打飞」会变成「原地僵住」，
            //     打击感直接消失。R2：只做速度脉冲，不做墙体解算。
            float moved = 0.0f;
            for (int i = 0; i < 12; i++)
            {
                Vec2 before = e.Position;
                e.IntegrateKnockback(FixedStep);
                e.TickHitStun(FixedStep);
                moved += before.DistanceTo(e.Position);
            }
            Assert.That(moved, Is.GreaterThan(20.0f), "附加c 硬直期间仍被推开");

            // (d) 敌人死亡抛事件并被移出战场
            Encounter enc = new Encounter();
            RecorderEvents rec = new RecorderEvents(enc);
            enc.Events = rec;
            Combatant player = Combatant.CreatePlayer(0, PlayerHugeHp, Vec2.Zero);
            enc.SetPlayer(player);
            Combatant d1 = Combatant.CreateEnemy(1, new Vec2(500.0f, 0.0f), 10.0f, 1.0f, 0.0f);
            Combatant d2 = Combatant.CreateEnemy(2, new Vec2(600.0f, 0.0f), 10.0f, 1.0f, 0.0f);
            enc.Add(d1);
            enc.Add(d2);
            Assert.That(enc.AliveEnemyCount, Is.EqualTo(2), "附加d 初始 2 只");
            d1.ApplyEnemyDamage(1e6f);
            enc.RemoveDead();
            Assert.That(enc.AliveEnemyCount, Is.EqualTo(1), "附加d 死亡后剩 1 只");

            // (e) 玩家死亡不被内核移出列表（玩家的死亡流程属于上层，不归战斗内核裁决）
            Encounter enc2 = new Encounter();
            Combatant p2 = Combatant.CreatePlayer(0, 10.0f, Vec2.Zero);
            enc2.SetPlayer(p2);
            p2.Hp = 0.0f;
            enc2.RemoveDead();
            Assert.That(enc2.Combatants.Count, Is.EqualTo(1), "附加e 玩家不被内核清出");
        }

        // =====================================================================
        // 内核纯净度（与 CI 的 grep 守卫同义，放这里是为了让红线可被测试复现）
        // =====================================================================

        [Test]
        public void Extra_KernelTypesAreEngineAgnostic()
        {
            // 内核类型所在的程序集不得依赖 UnityEngine。
            //
            // 【这条断言只有在内核被单独划进 asmdef 之后才有意义】
            // 当前工程还没有任何 asmdef，Combat/ 下的内核与 Combat/Unity/ 的 MonoBehaviour 壳
            // 一起落在 Assembly-CSharp 里，而这个程序集**必然**引用 UnityEngine。
            // 此时若照常断言，拿到的是一条与内核纯净度无关的假失败；
            // 若干脆删掉，等 asmdef 拆分完成后又没人记得补回来。
            // 因此这里显式区分两种工程布局：拆分后严格校验，拆分前给 Inconclusive
            // 并说明红线此刻由 CI 的 grep 守卫承担，既不误报也不假绿。
            Assembly kernelAsm = typeof(Encounter).Assembly;
            bool kernelHasOwnAssembly = kernelAsm != typeof(CombatKernelTests).Assembly
                                        || !kernelAsm.GetName().Name.StartsWith("Assembly-CSharp");

            if (!kernelHasOwnAssembly)
            {
                Assert.Inconclusive(
                    "内核尚未拆出独立 asmdef（当前在 " + kernelAsm.GetName().Name + "），"
                    + "无法用程序集引用校验纯净度；此刻由 CI 的 grep 守卫兜底："
                    + "Combat/ 下除 Unity/ 外不得出现 using UnityEngine。");
                return;
            }

            foreach (Type t in new[] { typeof(Vec2), typeof(Combatant), typeof(EnemyAI), typeof(Encounter) })
            {
                foreach (AssemblyName an in t.Assembly.GetReferencedAssemblies())
                {
                    Assert.That(an.Name, Does.Not.StartWith("UnityEngine"),
                        t.Name + " 所在程序集不得引用 " + an.Name);
                }
            }
        }
    }
}
