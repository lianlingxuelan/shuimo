// -----------------------------------------------------------------------------
// HeroineFramesTests.cs —— 女主 39 帧清单表（HeroineFrames）EditMode 验收套件
//
// 【测试范围】
// 只测 HeroineFrames 这张**纯数据表**的自洽性：8 个 pose 齐不齐、帧数求和对不对、
// 路径拼接的 off-by-one 对不对、朝向判定的分支对不对。
// 这些断言不碰磁盘、不建 GameObject、不依赖场景与渲染管线，因此用 NUnit 的纯
// [Test] 在 EditMode 下就能跑完。
//
// 【为什么值得单独测】
// 清单表是"39 帧"这个数字唯一的代码化事实来源，而它最容易出的两类错——
// 帧号 off-by-one（PathOf(0) 应指向 _1 而非 _0）和 表下标与枚举错位——
// 在 PlayMode 里表现为"某个动作播错图"，肉眼极难定位；在这里却是一行断言的事。
//
// 【验证状态】
// 编写环境无 Unity / dotnet，本文件未经编译运行。请在本地 Unity 编辑器确认：
//   Window → General → Test Runner → EditMode → 选 Xianxia.Unity.T2.Tests → Run All
//
// 【禁止事项】不测 HeroineAnimator（它需要 MonoBehaviour 生命周期与真实 PNG，
// 属于 PlayMode 范畴）；不反射私有字段。
// -----------------------------------------------------------------------------

using System;
using NUnit.Framework;
using UnityEngine;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>HeroineFrames 静态清单表的数据自洽性断言。</summary>
    [TestFixture]
    public sealed class HeroineFramesTests
    {
        /// <summary>枚举里声明的全部状态，供逐个遍历。</summary>
        private static AnimState[] AllStates()
        {
            return (AnimState[])Enum.GetValues(typeof(AnimState));
        }

        // ── 表结构 ────────────────────────────────────────────────────────────

        [Test]
        public void PoseCount_MatchesEnumLength()
        {
            Assert.AreEqual(AllStates().Length, HeroineFrames.PoseCount,
                            "清单表条目数必须与 AnimState 枚举值个数一致，否则下标会错位。");
            Assert.AreEqual(8, HeroineFrames.PoseCount, "本期设计恰好 8 个 pose。");
        }

        [Test]
        public void Get_EveryState_ReturnsUsablePose()
        {
            foreach (AnimState st in AllStates())
            {
                PoseDef pose = HeroineFrames.Get(st);

                Assert.IsFalse(string.IsNullOrEmpty(pose.FilePrefix), st + " 的文件前缀不能为空。");
                Assert.Greater(pose.FrameCount, 0, st + " 的帧数必须为正。");
                Assert.Greater(pose.FrameW, 0, st + " 的画布宽必须为正。");
                Assert.Greater(pose.FrameH, 0, st + " 的画布高必须为正。");
                Assert.Greater(pose.Fps, 0.0f, st + " 的帧率必须为正（Advance 会拿它做除数）。");
                StringAssert.StartsWith("heroine_", pose.FilePrefix, st + " 的前缀应以 heroine_ 开头。");
            }
        }

        [Test]
        public void FrameCounts_SumTo39()
        {
            int sum = 0;
            foreach (AnimState st in AllStates())
            {
                sum += HeroineFrames.Get(st).FrameCount;
            }

            Assert.AreEqual(39, sum, "帧数求和应为 4+6+6+6+6+2+4+5 = 39。");
            Assert.AreEqual(HeroineFrames.TotalFrames, sum,
                            "TotalFrames 常量必须与清单表求和一致，否则预热完整性告警会误报。");
        }

        [Test]
        public void Table_MatchesDesignManifest()
        {
            // 人形立绘统一锚"脚底略上一点"；attack / death 是特例画布，各有独立轴心。
            Vector2 foot = new Vector2(0.50f, 0.28f);
            Vector2 attack = new Vector2(0.25f, 0.28f);   // 96 宽画布，身体占左半 ⇒ 轴心偏左
            Vector2 death = new Vector2(0.50f, 0.22f);    // 倒地帧重心下移 ⇒ 轴心更低

            // 与设计文档 §3 的 39 帧清单表逐行对齐，同时也与磁盘上 PNG 的实际尺寸一致。
            AssertPose(AnimState.Idle, "heroine_idle", 4, 48, 64, foot, true);
            AssertPose(AnimState.WalkDown, "heroine_walk_down", 6, 48, 64, foot, true);
            AssertPose(AnimState.WalkUp, "heroine_walk_up", 6, 48, 64, foot, true);
            AssertPose(AnimState.WalkSide, "heroine_walk_side", 6, 48, 64, foot, true);
            AssertPose(AnimState.Attack, "heroine_attack", 6, 96, 64, attack, false);
            AssertPose(AnimState.Hurt, "heroine_hurt", 2, 48, 64, foot, false);
            AssertPose(AnimState.Dodge, "heroine_dodge", 4, 48, 64, foot, false);
            AssertPose(AnimState.Death, "heroine_death", 5, 64, 64, death, false);
        }

        private static void AssertPose(AnimState st, string prefix, int count, int w, int h,
                                       Vector2 pivot, bool loop)
        {
            PoseDef pose = HeroineFrames.Get(st);
            Assert.AreEqual(prefix, pose.FilePrefix, st + " 前缀不符。");
            Assert.AreEqual(count, pose.FrameCount, st + " 帧数不符。");
            Assert.AreEqual(w, pose.FrameW, st + " 画布宽不符。");
            Assert.AreEqual(h, pose.FrameH, st + " 画布高不符。");
            Assert.AreEqual(loop, pose.Loop, st + " 循环标志不符。");

            // 轴心逐分量比（Vector2.Equals 是精确浮点相等，会被 0.28f 这类字面量的
            // 舍入噪声误伤）。attack / death 的轴心尚待 PlayMode 目测校准 —— 校准后
            // 请同步改这里的期望值，本断言就是那次改动的回归保护。
            Assert.AreEqual(pivot.x, pose.Pivot.x, 1e-4f, "pose " + st + " Pivot.x 失配。");
            Assert.AreEqual(pivot.y, pose.Pivot.y, 1e-4f, "pose " + st + " Pivot.y 失配。");
        }

        [Test]
        public void OnlyIdleAndWalks_Loop()
        {
            Assert.IsTrue(HeroineFrames.Get(AnimState.Idle).Loop);
            Assert.IsTrue(HeroineFrames.Get(AnimState.WalkDown).Loop);
            Assert.IsTrue(HeroineFrames.Get(AnimState.WalkUp).Loop);
            Assert.IsTrue(HeroineFrames.Get(AnimState.WalkSide).Loop);

            // 一次性动作必须不循环，否则 Death 会一直重播、Attack 永不回落。
            Assert.IsFalse(HeroineFrames.Get(AnimState.Attack).Loop);
            Assert.IsFalse(HeroineFrames.Get(AnimState.Hurt).Loop);
            Assert.IsFalse(HeroineFrames.Get(AnimState.Dodge).Loop);
            Assert.IsFalse(HeroineFrames.Get(AnimState.Death).Loop);
        }

        [Test]
        public void Get_OutOfRangeState_FallsBackToIdle()
        {
            PoseDef pose = HeroineFrames.Get((AnimState)999);
            Assert.AreEqual("heroine_idle", pose.FilePrefix, "非法状态应安全回落到 Idle，而不是抛异常。");
        }

        // ── 路径拼接（off-by-one 是本模块唯一的高危点）─────────────────────────

        [Test]
        public void PathOf_FirstFrame_EndsWithUnderscoreOne()
        {
            foreach (AnimState st in AllStates())
            {
                PoseDef pose = HeroineFrames.Get(st);
                StringAssert.EndsWith("_1", pose.PathOf(0),
                                      st + "：索引 0 必须映射到磁盘上的 _1（文件名从 1 起）。");
            }
        }

        [Test]
        public void PathOf_LastFrame_EndsWithFrameCount()
        {
            foreach (AnimState st in AllStates())
            {
                PoseDef pose = HeroineFrames.Get(st);
                StringAssert.EndsWith("_" + pose.FrameCount, pose.PathOf(pose.FrameCount - 1),
                                      st + "：末帧索引必须映射到 _<FrameCount>。");
            }
        }

        [Test]
        public void PathOf_UsesRootDirAndForwardSlashes()
        {
            PoseDef idle = HeroineFrames.Get(AnimState.Idle);
            Assert.AreEqual("characters/heroine/heroine_idle_1", idle.PathOf(0));
            Assert.AreEqual("characters/heroine", HeroineFrames.RootDir);

            foreach (AnimState st in AllStates())
            {
                string p = HeroineFrames.Get(st).PathOf(0);
                StringAssert.StartsWith(HeroineFrames.RootDir + "/", p, st + " 路径应挂在 RootDir 下。");
                Assert.IsFalse(p.Contains("\\"), st + " 路径必须用正斜杠（跨平台）。");
                Assert.IsFalse(p.EndsWith(".png"), st + " 路径不得含扩展名（由 TryLoadPng 内部拼）。");
            }
        }

        [Test]
        public void PathOf_OutOfRangeIndex_IsClamped()
        {
            PoseDef idle = HeroineFrames.Get(AnimState.Idle);
            Assert.AreEqual(idle.PathOf(0), idle.PathOf(-5), "负索引应钳到首帧。");
            Assert.AreEqual(idle.PathOf(idle.FrameCount - 1), idle.PathOf(999), "超界索引应钳到末帧。");
        }

        [Test]
        public void PathOf_AllFrames_AreDistinct()
        {
            System.Collections.Generic.HashSet<string> seen =
                new System.Collections.Generic.HashSet<string>();
            foreach (AnimState st in AllStates())
            {
                PoseDef pose = HeroineFrames.Get(st);
                for (int i = 0; i < pose.FrameCount; i++)
                {
                    Assert.IsTrue(seen.Add(pose.PathOf(i)), "路径重复：" + pose.PathOf(i));
                }
            }
            Assert.AreEqual(HeroineFrames.TotalFrames, seen.Count, "去重后应恰好 39 条互异路径。");
        }

        // ── 清单表 ↔ 磁盘一致性 ────────────────────────────────────────────────

        /// <summary>
        /// 清单表声明的每一帧在 StreamingAssets 下都必须有对应 PNG。
        /// <para>【为什么必须自动化】此前"39 张齐不齐"是靠人工数文件核对的，
        /// 一旦有人改了 FrameCount 或重命名了 PNG，回归时无人再数一遍，
        /// 表现是运行期某个动作静默定格在上一帧（SpriteFactory 只告警不报错）。
        /// 这里把那次手工核对固化成断言。</para>
        /// <para>本测试程序集是 Editor-only，
        /// <see cref="Application.streamingAssetsPath"/> 在编辑器下即
        /// &lt;工程&gt;/Assets/StreamingAssets，可直接落到磁盘。</para>
        /// </summary>
        [Test]
        public void ManifestPaths_ExistOnDisk()
        {
            string root = Application.streamingAssetsPath;
            Assert.IsTrue(System.IO.Directory.Exists(root),
                          "StreamingAssets 目录不存在：" + root);

            int checkedCount = 0;
            foreach (AnimState st in AllStates())
            {
                PoseDef pose = HeroineFrames.Get(st);
                for (int i = 0; i < pose.FrameCount; i++)
                {
                    // 按清单表字段独立拼一遍（不复用 PathOf，避免"错得一致"）。
                    string rel = HeroineFrames.RootDir + "/" + pose.FilePrefix + "_" + (i + 1) + ".png";
                    string abs = root + "/" + rel;

                    Assert.IsTrue(System.IO.File.Exists(abs),
                                  st + " 第 " + (i + 1) + " 帧在磁盘上不存在：" + rel);

                    // 生产路径（SpriteFactory 实际用的那条）必须指向同一个文件，
                    // 否则清单表对了、加载器却读错名。
                    Assert.AreEqual(rel, pose.PathOf(i) + ".png",
                                    st + " 第 " + (i + 1) + " 帧：PathOf 与清单表拼法不一致。");
                    checkedCount++;
                }
            }

            Assert.AreEqual(HeroineFrames.TotalFrames, checkedCount,
                            "应恰好校验 39 帧，实际 " + checkedCount + " 帧。");
        }

        // ── Duration（一次性动作的倒计时来源）──────────────────────────────────

        [Test]
        public void Duration_EqualsFrameCountOverFps()
        {
            PoseDef attack = HeroineFrames.Get(AnimState.Attack);
            Assert.AreEqual(6.0f / 12.0f, attack.Duration, 1e-4f, "attack 6 帧 @12fps = 0.5s。");

            PoseDef death = HeroineFrames.Get(AnimState.Death);
            Assert.AreEqual(5.0f / 6.0f, death.Duration, 1e-4f);
        }

        // ── FromMovement ─────────────────────────────────────────────────────

        [Test]
        public void FromMovement_NotMoving_IsIdle()
        {
            Assert.AreEqual(AnimState.Idle, HeroineFrames.FromMovement(Vector2.right, false));
            Assert.AreEqual(AnimState.Idle, HeroineFrames.FromMovement(Vector2.up, false));
            Assert.AreEqual(AnimState.Idle, HeroineFrames.FromMovement(Vector2.zero, false));
        }

        [Test]
        public void FromMovement_VerticalDominant_PicksUpOrDown()
        {
            Assert.AreEqual(AnimState.WalkUp, HeroineFrames.FromMovement(new Vector2(0.0f, 1.0f), true));
            Assert.AreEqual(AnimState.WalkDown, HeroineFrames.FromMovement(new Vector2(0.0f, -1.0f), true));

            // 纵向分量略占优也应判纵向。
            Assert.AreEqual(AnimState.WalkUp, HeroineFrames.FromMovement(new Vector2(0.5f, 0.6f), true));
            Assert.AreEqual(AnimState.WalkDown, HeroineFrames.FromMovement(new Vector2(-0.5f, -0.6f), true));
        }

        [Test]
        public void FromMovement_HorizontalDominant_PicksWalkSide()
        {
            Assert.AreEqual(AnimState.WalkSide, HeroineFrames.FromMovement(new Vector2(1.0f, 0.0f), true));
            Assert.AreEqual(AnimState.WalkSide, HeroineFrames.FromMovement(new Vector2(-1.0f, 0.0f), true));
            Assert.AreEqual(AnimState.WalkSide, HeroineFrames.FromMovement(new Vector2(0.6f, 0.5f), true));
        }

        [Test]
        public void FromMovement_PerfectDiagonal_PrefersWalkSide()
        {
            // |y| > |x| 为假 ⇒ 落到侧身。斜向走时侧身图比正面图更自然。
            Assert.AreEqual(AnimState.WalkSide, HeroineFrames.FromMovement(new Vector2(1.0f, 1.0f), true));
            Assert.AreEqual(AnimState.WalkSide, HeroineFrames.FromMovement(new Vector2(-1.0f, -1.0f), true));
        }

        // ── ShouldFlipX ──────────────────────────────────────────────────────

        [Test]
        public void ShouldFlipX_FacingLeft_OnlyForSideFacingPoses()
        {
            Vector2 left = new Vector2(-1.0f, 0.0f);

            Assert.IsTrue(HeroineFrames.ShouldFlipX(AnimState.WalkSide, left));
            Assert.IsTrue(HeroineFrames.ShouldFlipX(AnimState.Attack, left));
            Assert.IsTrue(HeroineFrames.ShouldFlipX(AnimState.Idle, left));

            // 正/背面视角与其余动作翻转没有意义。
            Assert.IsFalse(HeroineFrames.ShouldFlipX(AnimState.WalkUp, left));
            Assert.IsFalse(HeroineFrames.ShouldFlipX(AnimState.WalkDown, left));
            Assert.IsFalse(HeroineFrames.ShouldFlipX(AnimState.Hurt, left));
            Assert.IsFalse(HeroineFrames.ShouldFlipX(AnimState.Dodge, left));
            Assert.IsFalse(HeroineFrames.ShouldFlipX(AnimState.Death, left));
        }

        [Test]
        public void ShouldFlipX_FacingRightOrZero_IsNeverFlipped()
        {
            Vector2 right = new Vector2(1.0f, 0.0f);
            Vector2 zero = Vector2.zero;

            foreach (AnimState st in AllStates())
            {
                Assert.IsFalse(HeroineFrames.ShouldFlipX(st, right), st + "：朝右是源图方向，不该翻转。");
                Assert.IsFalse(HeroineFrames.ShouldFlipX(st, zero), st + "：零向量不该翻转。");
            }
        }
    }
}
