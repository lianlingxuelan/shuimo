// -----------------------------------------------------------------------------
// P1_3_AudioTests.cs —— P1-3「程序化音效系统」EditMode 验收套件
//
// 【验证状态 —— 先说清楚，免得被误读为"已通过"】
// 编写环境**没有 Unity、没有 dotnet**，本文件**未经编译、未经运行**。
// 下面所有断言都是静态审查的产物，等待在本地 Unity 里跑一次才算数：
//   Window → General → Test Runner → EditMode → Xianxia.Unity.T2.Tests → Run All
//
// 【测试范围 —— 只测在 EditMode 下能确定性求值的部分】
//   · AudioConfig 那张 13 行表的跨文件不变量（key 逐字符、豁免集合、节流集合）
//   · 四层闸门的常量契约，以及"抢占返回 -1 那条分支在当前配置下不可达"的证明
//   · 增益链五个乘数的数学性质（连触递减触底点、并发补偿单调性与最坏值）
//   · SfxSynth 的归一化 / 软削波 / ★等功率交叉淡化（这一条最容易做错）
//   · SfxRecipes 的确定性（同 key 两次渲染逐位相同）与 13 条配方的成品指标
//   · AudioClipFactory 的缓存 / 黑名单 / 释放
//   · AudioDirector 的装配期形态、AudioListener 保障、节流闸门、并发闸门
//   · AmbienceLayer 的"不自动开播"与 duck 目标值
//
// 【★ EditMode 的三条硬约束，决定了哪些用例**故意**不写】
//   1. **Update / LateUpdate 不会被驱动。** AddComponent 会立刻跑 Awake/OnEnable，
//      但此后引擎不再推进任何一帧。于是"节流窗口 50 ms 之后放行""voice 播完自动
//      退役""duck 300 ms 淡到 30%"这类**依赖时间推进**的行为一律无法断言。
//      硬写会得到一条永远不动的假绿。它们属于 PlayMode / 本地实听。
//      —— 但这条约束也送了一份大礼：因为时间**恒定不动**，节流闸门与并发闸门
//      反而成了可以精确断言的纯逻辑（见 Gate_ 开头的四条用例）。
//   2. **AudioSource 在 EditMode 下不会真的出声。** 所以本文件放心地调 Play()，
//      断言落在"哪几路 voice 的 .clip 被挂上了"这个可观测量上，而不是听感。
//   3. **[RuntimeInitializeOnLoadMethod] 不触发。** AudioConfig.ResetStatics()
//      在 EditMode 测试里永远不会自己跑，四个可写 static 的复位必须由本文件负责。
//
// 【★ TearDown 的复位责任 —— 本文件绝不能变成下一个 MENU09】
// 本套件要动的全局可写状态比 P1-2 那套更多，共三类，缺一条就会污染后续用例：
//   ① AudioConfig 的 4 个 static（MasterVolume / SfxVolume / BgmVolume / Muted）
//   ② AudioClipFactory 的 3 个静态集合（缓存 / 黑名单 / 警告去重）—— 它还持有
//      **非托管的 AudioClip**，不 Clear 就是在测试进程里稳定泄漏
//   ③ PlayerPrefs 的 4 个键 —— ToggleMute() 会 SavePrefs()，那会把开发者本机
//      调好的音量真的写坏。所以 SetUp 拍快照、TearDown 精确还原
//      （原本没有这个键就 DeleteKey，而不是写一个默认值进去）
//
// 【禁止事项 —— 沿用 P1_2_HitFeedbackTests 立下的规矩】
//   · 不反射读写生产代码的私有字段来"造"状态。本文件对 voice 池的全部观察
//     都通过公开的 AudioSource.clip 完成，一次反射都没有。
//   · 不写"两侧取自同一来源"的等值断言（恒真假绿）。凡是断言，左右两侧要么
//     来自**不同的**责任方（如 AudioConfig 的表 vs CombatEventsUnity 里的字面量），
//     要么与字面量常量比较。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Xianxia.Combat;

namespace Xianxia.Unity.T2.Tests
{
    /// <summary>P1-3 程序化音效系统 EditMode 验收。</summary>
    [TestFixture]
    public sealed class P1_3_AudioTests
    {
        // =====================================================================
        // 夹具
        // =====================================================================

        /// <summary>本用例创建的全部根对象，TearDown 里统一销毁。</summary>
        private List<GameObject> _spawned;

        /// <summary>PlayerPrefs 四个键在进入用例之前是否存在。</summary>
        private bool[] _prefExisted;

        /// <summary>PlayerPrefs 三个 float 键的原值（不存在时无意义）。</summary>
        private float[] _prefFloats;

        /// <summary>PlayerPrefs 静音键的原值（不存在时无意义）。</summary>
        private int _prefMuted;

        [SetUp]
        public void SetUp()
        {
            _spawned = new List<GameObject>();

            // ★ 先拍 PlayerPrefs 快照，再动任何可能触发 SavePrefs 的代码。
            //   顺序反了就拍到脏值，等于没拍。
            _prefExisted = new bool[4];
            _prefFloats = new float[3];

            _prefExisted[0] = PlayerPrefs.HasKey(AudioConfig.PrefKeyMaster);
            _prefExisted[1] = PlayerPrefs.HasKey(AudioConfig.PrefKeySfx);
            _prefExisted[2] = PlayerPrefs.HasKey(AudioConfig.PrefKeyBgm);
            _prefExisted[3] = PlayerPrefs.HasKey(AudioConfig.PrefKeyMuted);

            _prefFloats[0] = PlayerPrefs.GetFloat(AudioConfig.PrefKeyMaster, AudioConfig.MasterVolumeDefault);
            _prefFloats[1] = PlayerPrefs.GetFloat(AudioConfig.PrefKeySfx, AudioConfig.SfxVolumeDefault);
            _prefFloats[2] = PlayerPrefs.GetFloat(AudioConfig.PrefKeyBgm, AudioConfig.BgmVolumeDefault);
            _prefMuted = PlayerPrefs.GetInt(AudioConfig.PrefKeyMuted, AudioConfig.MutedDefault ? 1 : 0);

            ResetAudioStatics();
            AudioClipFactory.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            // ★★★ 第一纪律：无条件复位所有全局可写状态，三类一个都不能少。

            // ① AudioConfig 的 4 个 static
            ResetAudioStatics();

            // ② 工厂的三个静态集合 + 它持有的非托管 AudioClip
            AudioClipFactory.Clear();

            // ③ 本用例创建的场景对象（Director 的 OnDestroy 里还会再 Clear 一次，幂等）
            if (_spawned != null)
            {
                for (int i = 0; i < _spawned.Count; i++)
                {
                    if (_spawned[i] != null)
                    {
                        Object.DestroyImmediate(_spawned[i]);
                    }
                }
                _spawned.Clear();
            }

            // ④ PlayerPrefs 精确还原：原本没有的键要**删掉**，不能留一个默认值下来。
            RestorePref(AudioConfig.PrefKeyMaster, _prefExisted[0], _prefFloats[0]);
            RestorePref(AudioConfig.PrefKeySfx, _prefExisted[1], _prefFloats[1]);
            RestorePref(AudioConfig.PrefKeyBgm, _prefExisted[2], _prefFloats[2]);
            if (_prefExisted[3])
            {
                PlayerPrefs.SetInt(AudioConfig.PrefKeyMuted, _prefMuted);
            }
            else
            {
                PlayerPrefs.DeleteKey(AudioConfig.PrefKeyMuted);
            }
            PlayerPrefs.Save();
        }

        /// <summary>把 AudioConfig 的四个可写 static 摆回出厂值。</summary>
        private static void ResetAudioStatics()
        {
            AudioConfig.MasterVolume = AudioConfig.MasterVolumeDefault;
            AudioConfig.SfxVolume = AudioConfig.SfxVolumeDefault;
            AudioConfig.BgmVolume = AudioConfig.BgmVolumeDefault;
            AudioConfig.Muted = AudioConfig.MutedDefault;
        }

        /// <summary>还原一个 float 型 PlayerPrefs 键。</summary>
        /// <param name="key">键名。</param>
        /// <param name="existed">进入用例前它是否存在。</param>
        /// <param name="value">原值。</param>
        private static void RestorePref(string key, bool existed, float value)
        {
            if (existed)
            {
                PlayerPrefs.SetFloat(key, value);
            }
            else
            {
                PlayerPrefs.DeleteKey(key);
            }
        }

        /// <summary>建一个受管理的空 GameObject（TearDown 会销毁它）。</summary>
        /// <param name="name">对象名。</param>
        /// <returns>新建的对象。</returns>
        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        /// <summary>
        /// 建一个受管理的 AudioDirector。
        ///
        /// 【注意它的副作用】AddComponent 会立刻跑 Awake（建 16 路 voice）与
        /// OnEnable（AudioListener 保障）。后者在一个没有任何 listener 的空测试
        /// 场景里会给自己补挂一个并打一条 LogWarning —— 那是**预期行为**，
        /// 不是测试失败（Unity Test Framework 只对 LogError / LogException 判红）。
        /// </summary>
        /// <param name="name">对象名。</param>
        /// <returns>新建的调度器。</returns>
        private AudioDirector NewDirector(string name)
        {
            return NewGo(name).AddComponent<AudioDirector>();
        }

        /// <summary>
        /// 取某个 Director 的 voice 池里"已经挂上 clip"的路数。
        ///
        /// 这是本文件观察闸门行为的**唯一**手段：Play() 成功走完全部四层闸门时
        /// 一定会执行 <c>v.Source.clip = clip</c>，被任何一层挡下则不会。
        /// 全程只用公开 API，不碰私有字段。
        /// </summary>
        /// <param name="director">调度器。</param>
        /// <returns>已挂 clip 的 voice 数。</returns>
        private static int CountLoadedVoices(AudioDirector director)
        {
            Transform pool = director.transform.Find("AudioVoicePool");
            if (pool == null)
            {
                return 0;
            }

            AudioSource[] sources = pool.GetComponentsInChildren<AudioSource>(true);
            int n = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null && sources[i].clip != null)
                {
                    n++;
                }
            }
            return n;
        }

        // =====================================================================
        // A. 13 行表的跨文件不变量（key 一个字符都不许错）
        // =====================================================================

        /// <summary>表必须正好 13 行：8 个 T2 出口 + 4 个 T3 出口 + 1 条环境衬底。</summary>
        [Test]
        public void Table_HasExactlyThirteenEntries()
        {
            Assert.AreEqual(13, AudioConfig.Table.Length,
                            "表的行数变了。改行数是允许的，但必须同步改这条断言与架构 §4 的清单。");
        }

        /// <summary>AllKeys() 必须与 Table 一一对应、同序、且无重复 key。</summary>
        [Test]
        public void AllKeys_MirrorsTable_InOrder_AndHasNoDuplicates()
        {
            string[] keys = AudioConfig.AllKeys();

            Assert.IsNotNull(keys, "AllKeys() 返回 null，预合成会整体空转。");
            Assert.AreEqual(AudioConfig.Table.Length, keys.Length,
                            "AllKeys() 与 Table 长度不一致，预合成会漏掉音效。");

            var seen = new HashSet<string>();
            for (int i = 0; i < keys.Length; i++)
            {
                Assert.AreEqual(AudioConfig.Table[i].Key, keys[i],
                                "AllKeys()[" + i + "] 与 Table[" + i + "].Key 不同序。");
                Assert.IsFalse(string.IsNullOrEmpty(keys[i]), "第 " + i + " 行的 key 是空串。");
                Assert.IsTrue(seen.Add(keys[i]),
                              "key 重复：'" + keys[i] + "'。重复会让后一行静默盖掉前一行。");
            }
        }

        /// <summary>
        /// ★ 8 个 T2 侧 key 必须逐字符匹配 CombatEventsUnity 里的裸字面量。
        ///
        /// 【为什么这条断言不是恒真】
        /// 右边这 8 个字符串是从 CombatEventsUnity.cs:124/143/160/177/196/216 抄来的，
        /// 那是**另一个责任方**（内核事件面，B-2 红线，本期一行都不许改）。
        /// 内核那边一旦手滑改一个字母，这里立刻红 —— 而线上表现只是"某个音效没了"，
        /// 控制台干干净净，没人会发现。
        /// </summary>
        [Test]
        public void Table_CoversAllEightCombatEventsUnityKeys()
        {
            string[] kernelKeys =
            {
                "sfx_enemy_hit",
                "sfx_player_hurt",
                "sfx_enemy_death",
                "sfx_boss_death",
                "sfx_boss_phase",
                "sfx_boss_summon",
                "sfx_boss_shockwave",
                "sfx_poise_break"
            };

            for (int i = 0; i < kernelKeys.Length; i++)
            {
                SfxSpec spec;
                Assert.IsTrue(AudioConfig.TryGetSpec(kernelKeys[i], out spec),
                              "内核 CombatEventsUnity 会发出 '" + kernelKeys[i] +
                              "'，但音效表里查不到它 ⇒ 这个音效永远不会响。");
                Assert.AreEqual(Channel.Sfx, spec.Bus, "战斗音效必须走 Sfx 通道。");
            }
        }

        /// <summary>
        /// ★ 4 个 T3 侧 key 必须引 SkillConfig 常量，且 SKILL_DODGE_ROLL 确实没有前缀。
        ///
        /// 后半句是本工程一个真实的"反直觉既成事实"：SKILL_DODGE_ROLL 的值是
        /// "dodge_roll"，**没有 skill_ 前缀**。把它写进断言，是为了让将来那个
        /// "顺手统一命名"的人在测试里当场被拦下，而不是在游戏里发现闪避没声音。
        /// </summary>
        [Test]
        public void Table_CoversAllFourSkillConfigKeys_AndDodgeRollHasNoPrefix()
        {
            Assert.AreEqual("skill_basic_slash", SkillConfig.SKILL_BASIC_SLASH);
            Assert.AreEqual("skill_circle_burst", SkillConfig.SKILL_CIRCLE_BURST);
            Assert.AreEqual("skill_blood_lotus", SkillConfig.SKILL_BLOOD_LOTUS);
            Assert.AreEqual("dodge_roll", SkillConfig.SKILL_DODGE_ROLL,
                            "SKILL_DODGE_ROLL 没有 skill_ 前缀是既成事实，不要'顺手统一'。");

            string[] t3Keys =
            {
                SkillConfig.SKILL_BASIC_SLASH,
                SkillConfig.SKILL_CIRCLE_BURST,
                SkillConfig.SKILL_BLOOD_LOTUS,
                SkillConfig.SKILL_DODGE_ROLL
            };

            for (int i = 0; i < t3Keys.Length; i++)
            {
                SfxSpec spec;
                Assert.IsTrue(AudioConfig.TryGetSpec(t3Keys[i], out spec),
                              "CombatEventsT3Unity 会发出 '" + t3Keys[i] + "'，但表里查不到。");
                Assert.AreEqual(Channel.Sfx, spec.Bus);
            }
        }

        /// <summary>
        /// 环境衬底是全表唯一一行"形状不同"的：唯一 Loop、唯一 Bgm、唯一 Rms。
        /// 三个"唯一"必须落在同一行上，否则说明有人复制粘贴时把标志带歪了。
        /// </summary>
        [Test]
        public void AmbienceRow_IsTheOnlyLoopingBgmRmsEntry()
        {
            int loops = 0;
            int bgm = 0;
            int rms = 0;

            for (int i = 0; i < AudioConfig.Table.Length; i++)
            {
                SfxSpec s = AudioConfig.Table[i];
                if (s.Loop)
                {
                    loops++;
                    Assert.AreEqual(AudioConfig.AmbienceKey, s.Key, "除环境衬底外不该有循环音。");
                }
                if (s.Bus == Channel.Bgm)
                {
                    bgm++;
                    Assert.AreEqual(AudioConfig.AmbienceKey, s.Key, "除环境衬底外不该走 Bgm 通道。");
                }
                if (s.NormMode == NormalizeMode.Rms)
                {
                    rms++;
                    Assert.AreEqual(AudioConfig.AmbienceKey, s.Key, "除环境衬底外不该用 Rms 归一化。");
                }
            }

            Assert.AreEqual(1, loops, "循环音必须有且仅有一条。");
            Assert.AreEqual(1, bgm, "Bgm 通道音必须有且仅有一条。");
            Assert.AreEqual(1, rms, "Rms 归一化必须有且仅有一条。");
        }

        /// <summary>
        /// 抢占豁免集合必须正好是 {BOSS 死亡, BOSS 冲击波, 环境衬底}。
        ///
        /// 前两个是 US-3 的"你该躲了"信号，挤掉它们是**玩法伤害**；环境衬底根本
        /// 不走 voice 池，标豁免只是一个无害的自证。多一个都不行 —— 豁免集合越大，
        /// 下面那条"-1 分支不可达"的证明就越接近失效。
        /// </summary>
        [Test]
        public void ExemptPreempt_SetIsExactlyThreeKnownKeys()
        {
            var exempt = new List<string>();
            for (int i = 0; i < AudioConfig.Table.Length; i++)
            {
                if (AudioConfig.Table[i].ExemptPreempt)
                {
                    exempt.Add(AudioConfig.Table[i].Key);
                }
            }

            Assert.AreEqual(3, exempt.Count, "豁免集合的大小变了：" + string.Join(", ", exempt.ToArray()));
            Assert.Contains("sfx_boss_death", exempt);
            Assert.Contains("sfx_boss_shockwave", exempt);
            Assert.Contains(AudioConfig.AmbienceKey, exempt);
        }

        /// <summary>
        /// ★ 证明 PickVoice 的"全池豁免 ⇒ 返回 -1"分支在当前配置下**不可达**。
        ///
        /// 走 voice 池的豁免 key 只有 2 个（环境衬底不走池），每个受
        /// PerKeyVoiceLimit = 3 约束，所以池子里最多同时存在 2 × 3 = 6 路豁免音，
        /// 而池子有 16 路 —— 永远至少剩 10 路可抢。也就是说那条 return -1 是一条
        /// **纯防御分支**，不是正常路径。
        ///
        /// 【为什么要把它写成测试而不是注释】
        /// 这个结论依赖三个可调常量的相对大小。哪天有人把池子从 16 缩到 4、
        /// 或者给第三、第四个音效加豁免，"丢掉一声"就会从不可能变成常态，
        /// 而症状（偶发丢音）几乎不可能被定位。让它在这里当场红掉。
        /// </summary>
        [Test]
        public void ExemptVoices_CanNeverFillThePool_SoTheMinusOneBranchIsUnreachable()
        {
            int poolBoundExempt = 0;
            for (int i = 0; i < AudioConfig.Table.Length; i++)
            {
                SfxSpec s = AudioConfig.Table[i];
                if (s.ExemptPreempt && !s.Loop)
                {
                    poolBoundExempt++;
                }
            }

            Assert.AreEqual(2, poolBoundExempt, "走 voice 池的豁免 key 应当只有 BOSS 死亡与冲击波。");
            Assert.Less(poolBoundExempt * AudioConfig.PerKeyVoiceLimit, AudioConfig.VoicePoolSize,
                        "豁免音理论上能占满整个 voice 池了 ⇒ PickVoice 会开始返回 -1 丢音。");
        }

        /// <summary>
        /// 节流集合必须正好是 5 个高频音，且节流值与架构 §4 的清单逐字一致。
        /// 其中 sfx_player_hurt(70 ms) 与 sfx_poise_break(60 ms) 是对 PRD R-05 ①
        /// 的**有意偏离**（PM 已知悉，理由写在 AudioConfig 表头）。断言把这个
        /// "有意"钉死，免得后来的人当成 bug 顺手删掉。
        /// </summary>
        [Test]
        public void Throttle_SetAndValuesMatchTheContract()
        {
            var throttled = new Dictionary<string, float>();
            for (int i = 0; i < AudioConfig.Table.Length; i++)
            {
                SfxSpec s = AudioConfig.Table[i];
                if (s.ThrottleSec > 0.0f)
                {
                    throttled[s.Key] = s.ThrottleSec;
                }
                Assert.GreaterOrEqual(s.ThrottleSec, 0.0f, "'" + s.Key + "' 的节流值为负。");
            }

            Assert.AreEqual(5, throttled.Count, "带节流的音效数量变了。");
            Assert.AreEqual(0.050f, throttled["sfx_enemy_hit"], 1e-6f);
            Assert.AreEqual(0.070f, throttled["sfx_player_hurt"], 1e-6f);
            Assert.AreEqual(0.060f, throttled["sfx_poise_break"], 1e-6f);
            Assert.AreEqual(0.050f, throttled[SkillConfig.SKILL_BASIC_SLASH], 1e-6f);
            Assert.AreEqual(0.050f, throttled[SkillConfig.SKILL_DODGE_ROLL], 1e-6f);
        }

        /// <summary>每一行的采样率 / 时长 / 增益 / 归一化目标都必须在可用区间内。</summary>
        [Test]
        public void EverySpec_HasSaneParameters()
        {
            var kinds = new HashSet<RecipeKind>();

            for (int i = 0; i < AudioConfig.Table.Length; i++)
            {
                SfxSpec s = AudioConfig.Table[i];
                string tag = "第 " + i + " 行 '" + s.Key + "'：";

                Assert.IsTrue(s.SampleRate == 22050 || s.SampleRate == 44100,
                              tag + "采样率只允许 22050 / 44100，实际 " + s.SampleRate);
                Assert.Greater(s.DurationSec, 0.0f, tag + "时长必须为正。");
                Assert.LessOrEqual(s.DurationSec, AudioConfig.AmbienceLoopSec,
                                   tag + "时长超过了环境衬底，内存预算按 §2.2 需要重算。");
                Assert.Greater(s.Gain, 0.0f, tag + "增益为 0 等于这条永远听不见。");
                Assert.LessOrEqual(s.Gain, 1.0f, tag + "增益 > 1 会在混音总线上溢出。");
                Assert.Greater(s.NormTarget, 0.0f, tag + "归一化目标必须为正。");
                Assert.LessOrEqual(s.NormTarget, 1.0f, tag + "归一化目标 > 1 必然削波。");

                Assert.IsTrue(kinds.Add(s.Kind),
                              tag + "配方 " + s.Kind + " 被两行共用 —— 多半是复制粘贴忘了改。");
            }
        }

        // =====================================================================
        // B. 四层闸门与增益链的常量契约
        // =====================================================================

        /// <summary>voice 池与并发相关的常量，与架构 §2.1 / §2.6 逐字对齐。</summary>
        [Test]
        public void VoicePoolConstants_MatchArchitecture()
        {
            Assert.AreEqual(16, AudioConfig.VoicePoolSize);
            Assert.AreEqual(3, AudioConfig.PerKeyVoiceLimit);
            Assert.AreEqual(1e-4f, AudioConfig.VolumeEpsilon, 1e-9f);
            Assert.AreEqual(KeyCode.M, AudioConfig.MuteKey);
        }

        /// <summary>增益链五个乘数的常量，与架构 §2.4 逐字对齐。</summary>
        [Test]
        public void GainChainConstants_MatchArchitecture()
        {
            Assert.AreEqual(0.70f, AudioConfig.ConsecutiveDecay, 1e-6f);
            Assert.AreEqual(0.40f, AudioConfig.ConsecutiveFloor, 1e-6f);
            Assert.AreEqual(0.20f, AudioConfig.ConsecutiveResetSec, 1e-6f);
            Assert.AreEqual(0.94f, AudioConfig.PitchJitterMin, 1e-6f);
            Assert.AreEqual(1.06f, AudioConfig.PitchJitterMax, 1e-6f);
            Assert.AreEqual(0.92f, AudioConfig.GainJitterMin, 1e-6f);
            Assert.AreEqual(1.00f, AudioConfig.GainJitterMax, 1e-6f);
            Assert.AreEqual(0.06f, AudioConfig.ConcurrencyDuck, 1e-6f);
            Assert.AreEqual(0.45f, AudioConfig.ConcurrencyFloor, 1e-6f);

            // 抖动区间必须是"只会变轻"的：上界压在 1.0，否则随机增益会自己越界。
            Assert.LessOrEqual(AudioConfig.GainJitterMax, 1.0f);
            Assert.Less(AudioConfig.GainJitterMin, AudioConfig.GainJitterMax);
            Assert.Less(AudioConfig.PitchJitterMin, AudioConfig.PitchJitterMax);
            Assert.Greater(AudioConfig.PitchJitterMin, 0.0f, "音高倍率 ≤ 0 会让 clip 长度折算除零。");
        }

        /// <summary>环境衬底与出厂音量的常量，与架构 §3.4 / R-11 逐字对齐。</summary>
        [Test]
        public void AmbienceAndVolumeConstants_MatchArchitecture()
        {
            Assert.AreEqual("amb_wind_loop", AudioConfig.AmbienceKey);
            Assert.AreEqual(12.0f, AudioConfig.AmbienceLoopSec, 1e-6f);
            Assert.AreEqual(0.20f, AudioConfig.AmbienceCrossfadeSec, 1e-6f);
            Assert.AreEqual(0.12f, AudioConfig.AmbienceRmsTarget, 1e-6f);
            Assert.AreEqual(0.30f, AudioConfig.AmbienceDuckLevel, 1e-6f);
            Assert.AreEqual(0.30f, AudioConfig.AmbienceDuckFadeSec, 1e-6f);

            Assert.AreEqual(0.80f, AudioConfig.MasterVolumeDefault, 1e-6f);
            Assert.AreEqual(0.90f, AudioConfig.SfxVolumeDefault, 1e-6f);
            Assert.AreEqual(0.50f, AudioConfig.BgmVolumeDefault, 1e-6f);
            Assert.IsFalse(AudioConfig.MutedDefault, "出厂即静音会让人以为音频系统没做。");

            // 衬底比战斗音低整整一档 —— 它的作用是"填掉死寂"，不是"被听见"。
            Assert.Less(AudioConfig.BgmVolumeDefault, AudioConfig.SfxVolumeDefault);
        }

        /// <summary>
        /// 连触递减 0.7^n 必须在第 4 声触底（0.343 &lt; 0.40），且此后恒为 0.40。
        /// 这是"连击不会越来越吵、也不会渐渐消失"的数学保证。
        /// </summary>
        [Test]
        public void ConsecutiveDecay_HitsTheFloorOnTheFourthHit()
        {
            float d1 = DecayOf(1);
            float d2 = DecayOf(2);
            float d3 = DecayOf(3);
            float d4 = DecayOf(4);
            float d9 = DecayOf(9);

            Assert.AreEqual(0.70f, d1, 1e-5f, "第 1 声之后应当是 0.7^1。");
            Assert.AreEqual(0.49f, d2, 1e-5f);
            Assert.AreEqual(0.40f, d3, 1e-5f, "0.7^3 = 0.343 已经低于地板，应被钳到 0.40。");
            Assert.AreEqual(0.40f, d4, 1e-5f);
            Assert.AreEqual(0.40f, d9, 1e-5f, "地板必须是硬地板，不能继续往下漏。");

            Assert.Greater(AudioConfig.ConsecutiveFloor, 0.0f,
                           "地板为 0 会让长连击彻底静音 —— 那是 bug 不是设计。");
        }

        /// <summary>
        /// 并发补偿 1/(1+0.06·(n-1)) 必须单调递减，且**在满池 16 路时仍高于地板**。
        ///
        /// 【这条断言在说什么】ConcurrencyFloor = 0.45 是一道安全网，不是常态。
        /// 16 路满池时系数是 1/(1+0.06×15) = 0.526 > 0.45，也就是说正常玩法下
        /// 地板永远不会被触发。如果哪天它被触发了，说明池子或系数被改过，
        /// 而症状是"人越多声音反而不再变轻"（压缩器失效）。
        /// </summary>
        [Test]
        public void ConcurrencyCompensation_IsMonotonic_AndFloorIsOnlyASafetyNet()
        {
            float prev = 2.0f;
            for (int active = 1; active <= AudioConfig.VoicePoolSize; active++)
            {
                float comp = CompOf(active);
                Assert.LessOrEqual(comp, 1.0f, "补偿系数不该放大音量。");
                Assert.Less(comp, prev, "补偿系数必须随并发数严格递减（active=" + active + "）。");
                prev = comp;
            }

            Assert.AreEqual(1.0f, CompOf(1), 1e-6f, "单声时不该被补偿。");

            float full = CompOf(AudioConfig.VoicePoolSize);
            Assert.AreEqual(1.0f / (1.0f + 0.06f * 15.0f), full, 1e-5f);
            Assert.Greater(full, AudioConfig.ConcurrencyFloor,
                           "满池时已经压到地板了 ⇒ 并发补偿在最需要它的时候反而失效。");
        }

        /// <summary>
        /// 最坏情况的最终增益必须留有余量：16 路同时发声、每路都取最响的参数，
        /// 单路音量仍应远低于 1.0。这是 MasterVolume 默认 0.80 那 20% 顶部余量的意义。
        /// </summary>
        [Test]
        public void WorstCaseGain_StaysWellWithinHeadroom()
        {
            float maxKeyGain = 0.0f;
            for (int i = 0; i < AudioConfig.Table.Length; i++)
            {
                if (AudioConfig.Table[i].Gain > maxKeyGain)
                {
                    maxKeyGain = AudioConfig.Table[i].Gain;
                }
            }

            // 最响的一路：无连触递减(1.0)、随机增益取上界(1.0)、满池并发补偿。
            float worst = AudioConfig.MasterVolumeDefault
                          * AudioConfig.SfxVolumeDefault
                          * maxKeyGain
                          * 1.0f
                          * AudioConfig.GainJitterMax
                          * CompOf(AudioConfig.VoicePoolSize);

            Assert.Greater(worst, 0.0f);
            Assert.Less(worst, 0.60f,
                        "满池时单路仍然过响（" + worst + "），16 路叠加会撞上限。");
        }

        /// <summary>连触递减系数的参考实现（与生产代码同公式，但由本文件独立写出）。</summary>
        /// <param name="consecutiveCount">该 key 已连续播放的次数。</param>
        /// <returns>递减系数。</returns>
        private static float DecayOf(int consecutiveCount)
        {
            if (consecutiveCount <= 0)
            {
                return 1.0f;
            }
            float d = (float)System.Math.Pow(AudioConfig.ConsecutiveDecay, consecutiveCount);
            return d < AudioConfig.ConsecutiveFloor ? AudioConfig.ConsecutiveFloor : d;
        }

        /// <summary>并发补偿系数的参考实现。</summary>
        /// <param name="activeVoices">当前活跃 voice 数（含本次）。</param>
        /// <returns>补偿系数。</returns>
        private static float CompOf(int activeVoices)
        {
            int a = activeVoices < 1 ? 1 : activeVoices;
            float c = 1.0f / (1.0f + AudioConfig.ConcurrencyDuck * (a - 1));
            if (c < AudioConfig.ConcurrencyFloor)
            {
                c = AudioConfig.ConcurrencyFloor;
            }
            return c > 1.0f ? 1.0f : c;
        }

        // =====================================================================
        // C. 通道路由 / 查表 / 持久化
        // =====================================================================

        /// <summary>两条通道必须真的分开路由，不能都读同一个字段。</summary>
        [Test]
        public void ChannelVolume_RoutesSfxAndBgmIndependently()
        {
            AudioConfig.SfxVolume = 0.77f;
            AudioConfig.BgmVolume = 0.11f;

            Assert.AreEqual(0.77f, AudioConfig.ChannelVolume(Channel.Sfx), 1e-6f);
            Assert.AreEqual(0.11f, AudioConfig.ChannelVolume(Channel.Bgm), 1e-6f);
        }

        /// <summary>未注册 key / null / 空串一律返回 false，且不抛异常。</summary>
        [Test]
        public void TryGetSpec_RejectsNullEmptyAndUnknownKeys()
        {
            SfxSpec spec;

            Assert.IsFalse(AudioConfig.TryGetSpec(null, out spec));
            Assert.IsFalse(AudioConfig.TryGetSpec(string.Empty, out spec));
            Assert.IsFalse(AudioConfig.TryGetSpec("sfx_this_does_not_exist", out spec));

            // 大小写敏感：内核那边全是小写字面量，混用大小写属于打错字，必须查不到。
            Assert.IsFalse(AudioConfig.TryGetSpec("SFX_ENEMY_HIT", out spec));
        }

        /// <summary>
        /// ResetStatics() 的语义是"从 PlayerPrefs 重载，读不到则取出厂值"。
        /// 本用例先删干净四个键，再把 static 改脏，然后断言它们回到出厂值。
        /// </summary>
        [Test]
        public void ResetStatics_FallsBackToFactoryDefaults_WhenPrefsAreAbsent()
        {
            PlayerPrefs.DeleteKey(AudioConfig.PrefKeyMaster);
            PlayerPrefs.DeleteKey(AudioConfig.PrefKeySfx);
            PlayerPrefs.DeleteKey(AudioConfig.PrefKeyBgm);
            PlayerPrefs.DeleteKey(AudioConfig.PrefKeyMuted);

            AudioConfig.MasterVolume = 0.01f;
            AudioConfig.SfxVolume = 0.02f;
            AudioConfig.BgmVolume = 0.03f;
            AudioConfig.Muted = true;

            AudioConfig.ResetStatics();

            Assert.AreEqual(AudioConfig.MasterVolumeDefault, AudioConfig.MasterVolume, 1e-6f);
            Assert.AreEqual(AudioConfig.SfxVolumeDefault, AudioConfig.SfxVolume, 1e-6f);
            Assert.AreEqual(AudioConfig.BgmVolumeDefault, AudioConfig.BgmVolume, 1e-6f);
            Assert.IsFalse(AudioConfig.Muted, "复位后仍是静音 ⇒ 下一局开局全哑且不报错。");
        }

        /// <summary>存了再读必须拿回同一组值（R-11 的最小闭环）。</summary>
        [Test]
        public void SavePrefs_ThenLoadPrefs_RoundTripsAllFourValues()
        {
            AudioConfig.MasterVolume = 0.42f;
            AudioConfig.SfxVolume = 0.37f;
            AudioConfig.BgmVolume = 0.21f;
            AudioConfig.Muted = true;
            AudioConfig.SavePrefs();

            AudioConfig.MasterVolume = 0.0f;
            AudioConfig.SfxVolume = 0.0f;
            AudioConfig.BgmVolume = 0.0f;
            AudioConfig.Muted = false;

            AudioConfig.LoadPrefs();

            Assert.AreEqual(0.42f, AudioConfig.MasterVolume, 1e-4f);
            Assert.AreEqual(0.37f, AudioConfig.SfxVolume, 1e-4f);
            Assert.AreEqual(0.21f, AudioConfig.BgmVolume, 1e-4f);
            Assert.IsTrue(AudioConfig.Muted);
        }

        // =====================================================================
        // D. SfxSynth —— 零 Unity 依赖的纯 DSP
        // =====================================================================

        /// <summary>Peak 归一化后峰值必须**精确**等于目标（这是 12 条配方的收尾动作）。</summary>
        [Test]
        public void Normalize_Peak_LandsExactlyOnTarget()
        {
            var buf = new float[64];
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = (i % 7) * 0.031f - 0.09f;
            }
            buf[13] = 0.4f;

            SfxSynth.Normalize(buf, NormalizeMode.Peak, 0.89f);

            Assert.AreEqual(0.89f, SfxSynth.Peak(buf), 1e-5f);
        }

        /// <summary>Rms 归一化后均方根接近目标（软削波会略微压低，允许 5% 偏差）。</summary>
        [Test]
        public void Normalize_Rms_ApproachesTarget()
        {
            var rng = new System.Random(20240817);
            var buf = new float[4096];
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = SfxSynth.WhiteNoise(rng) * 0.02f;
            }

            SfxSynth.Normalize(buf, NormalizeMode.Rms, AudioConfig.AmbienceRmsTarget);

            float rms = SfxSynth.Rms(buf);
            Assert.AreEqual(AudioConfig.AmbienceRmsTarget, rms, AudioConfig.AmbienceRmsTarget * 0.05f);
            Assert.LessOrEqual(SfxSynth.Peak(buf), 1.0f, "Rms 模式必须自带软削波兜底。");
        }

        /// <summary>全静音缓冲不能被归一化成 NaN / Infinity（除以 0 的经典事故）。</summary>
        [Test]
        public void Normalize_LeavesSilentBufferFinite()
        {
            var buf = new float[32];

            SfxSynth.Normalize(buf, NormalizeMode.Peak, 0.9f);
            SfxSynth.Normalize(buf, NormalizeMode.Rms, 0.12f);

            for (int i = 0; i < buf.Length; i++)
            {
                Assert.IsFalse(float.IsNaN(buf[i]), "第 " + i + " 个样本变成了 NaN。");
                Assert.IsFalse(float.IsInfinity(buf[i]), "第 " + i + " 个样本变成了 Infinity。");
                Assert.AreEqual(0.0f, buf[i], 1e-9f);
            }
        }

        /// <summary>软削波必须把任何输入压回单位区间内，且低电平段不染色。</summary>
        [Test]
        public void SoftClipBuffer_BoundsEverything_AndLeavesQuietPartsUntouched()
        {
            var buf = new float[] { -3.0f, -1.5f, -0.5f, 0.0f, 0.25f, 0.5f, 1.5f, 9.0f };
            var quiet = new float[] { -0.5f, 0.0f, 0.25f, 0.5f };

            SfxSynth.SoftClipBuffer(buf);

            for (int i = 0; i < buf.Length; i++)
            {
                Assert.LessOrEqual(Mathf.Abs(buf[i]), 1.0f, "软削波没能把第 " + i + " 个样本压住。");
            }

            // 阈值 0.70 以下应当逐位不变 —— 削波不该给正常电平染色。
            Assert.AreEqual(quiet[0], buf[2], 1e-6f);
            Assert.AreEqual(quiet[1], buf[3], 1e-6f);
            Assert.AreEqual(quiet[2], buf[4], 1e-6f);
            Assert.AreEqual(quiet[3], buf[5], 1e-6f);
        }

        /// <summary>常量缓冲的 Peak 与 Rms 必须相等（最基本的自洽性）。</summary>
        [Test]
        public void PeakAndRms_AgreeOnAConstantBuffer()
        {
            var buf = new float[128];
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = 0.5f;
            }

            Assert.AreEqual(0.5f, SfxSynth.Peak(buf), 1e-6f);
            Assert.AreEqual(0.5f, SfxSynth.Rms(buf), 1e-6f);
        }

        /// <summary>Clamp01 与 Lerp 的边界契约（增益链与音高抖动都压在它们身上）。</summary>
        [Test]
        public void Clamp01AndLerp_HonourTheirBounds()
        {
            Assert.AreEqual(0.0f, SfxSynth.Clamp01(-5.0f), 1e-6f);
            Assert.AreEqual(1.0f, SfxSynth.Clamp01(5.0f), 1e-6f);
            Assert.AreEqual(0.33f, SfxSynth.Clamp01(0.33f), 1e-6f);

            Assert.AreEqual(0.94f, SfxSynth.Lerp(0.94f, 1.06f, 0.0f), 1e-6f);
            Assert.AreEqual(1.06f, SfxSynth.Lerp(0.94f, 1.06f, 1.0f), 1e-6f);
            Assert.AreEqual(1.00f, SfxSynth.Lerp(0.94f, 1.06f, 0.5f), 1e-6f);
        }

        /// <summary>
        /// 交叉淡化后长度收回到 L，且接缝处的跳变量降到"原素材相邻两样本之差"的量级。
        ///
        /// 【构造方法】用一条严格递增的斜坡当素材：直接截断的话 out[L-1] → out[0]
        /// 是从 ~1 跳回 0（一声响亮的咔哒）；正确交叉淡化之后 out[0] 取的是
        /// src[L]，而它与 out[L-1] = src[L-1] 在原素材里本来就相邻。
        /// </summary>
        [Test]
        public void CrossfadeLoop_RemovesTheSeamDiscontinuity()
        {
            const int loopLen = 1000;
            const int xf = 200;

            var src = new float[loopLen + xf];
            for (int i = 0; i < src.Length; i++)
            {
                src[i] = i / (float)src.Length;
            }

            float naiveSeam = Mathf.Abs(src[loopLen - 1] - src[0]);
            float[] dst = SfxSynth.CrossfadeLoop(src, loopLen, xf);

            Assert.AreEqual(loopLen, dst.Length, "输出长度必须收回到 L。");

            float seam = Mathf.Abs(dst[loopLen - 1] - dst[0]);
            Assert.Less(seam, naiveSeam * 0.05f,
                        "接缝跳变量 " + seam + " 没有被显著压下去（直接截断是 " + naiveSeam + "）。");
            Assert.AreEqual(src[loopLen], dst[0], 1e-6f,
                            "out[0] 应当等于 src[L]，也就是 out[L-1] 在原素材里的下一个样本。");
        }

        /// <summary>
        /// ★★ 交叉淡化必须是**等功率**（sin/cos），不能是线性（w / 1-w）。
        ///
        /// 【为什么这条最重要】两段互不相关的噪声做线性淡化时，中点两路各占 0.5，
        /// 而不相关信号的功率是平方相加：0.5² + 0.5² = 0.50，比两端低 3 dB。
        /// 表现为每 12 秒"喘一口气"的音量凹陷 —— 比阶跃咔哒隐蔽得多，
        /// 但"听 60 秒听不出接缝"的验收一定会栽在这里。
        ///
        /// 【构造方法】跑两遍：一遍只留头部素材（尾部置 0），一遍只留尾部素材。
        /// 于是 dstHead[i] = fadeIn(i)、dstTail[i] = fadeOut(i)，
        /// 断言 fadeIn² + fadeOut² ≡ 1。线性淡化在中点会给出 0.5，当场红。
        /// </summary>
        [Test]
        public void CrossfadeLoop_UsesEqualPowerCurves_NotLinear()
        {
            const int loopLen = 64;
            const int xf = 32;

            var headOnly = new float[loopLen + xf];
            var tailOnly = new float[loopLen + xf];
            for (int i = 0; i < xf; i++)
            {
                headOnly[i] = 1.0f;         // 头部素材 = 1，尾部素材 = 0
                tailOnly[loopLen + i] = 1.0f; // 头部素材 = 0，尾部素材 = 1
            }

            float[] fadeIn = SfxSynth.CrossfadeLoop(headOnly, loopLen, xf);
            float[] fadeOut = SfxSynth.CrossfadeLoop(tailOnly, loopLen, xf);

            for (int i = 0; i < xf; i++)
            {
                float power = fadeIn[i] * fadeIn[i] + fadeOut[i] * fadeOut[i];
                Assert.AreEqual(1.0f, power, 1e-4f,
                                "第 " + i + " 个样本处的功率是 " + power +
                                "（线性淡化在中点会给出 0.5，等功率恒为 1.0）。");
            }
        }

        /// <summary>参数非法时不许越界、不许抛异常，最差也要返回一个长度正确的缓冲。</summary>
        [Test]
        public void CrossfadeLoop_DegradesGracefullyOnBadArguments()
        {
            Assert.AreEqual(0, SfxSynth.CrossfadeLoop(null, 0, 0).Length);
            Assert.AreEqual(8, SfxSynth.CrossfadeLoop(null, 8, 4).Length);

            var src = new float[10];
            Assert.AreEqual(10, SfxSynth.CrossfadeLoop(src, 10, 999).Length,
                            "尾部素材不够时应当退化成截断，而不是越界。");
            Assert.AreEqual(10, SfxSynth.CrossfadeLoop(src, 99, 4).Length,
                            "L 超过素材长度时应当收敛到素材长度。");
        }

        // =====================================================================
        // E. SfxRecipes —— 确定性与成品指标
        // =====================================================================

        /// <summary>
        /// ★ 同一个 key 渲染两次必须**逐位相同**。
        ///
        /// 这是"程序化音效可复现"的底线：种子由 MakeSynthRandom(key) 固定，
        /// 配方内部不许偷偷摸 UnityEngine.Random 或 DateTime。哪天有人加了一处，
        /// 这条会立刻红 —— 而线上表现只是"音效每次听起来有点不一样"，没人会报 bug。
        /// </summary>
        [Test]
        public void Render_IsBitExactlyDeterministic_PerKey()
        {
            string[] probes =
            {
                "sfx_enemy_hit",
                "sfx_boss_shockwave",
                SkillConfig.SKILL_BLOOD_LOTUS,
                AudioConfig.AmbienceKey
            };

            for (int p = 0; p < probes.Length; p++)
            {
                SfxSpec spec;
                Assert.IsTrue(AudioConfig.TryGetSpec(probes[p], out spec));

                float[] a = SfxRecipes.Render(spec, AudioConfig.MakeSynthRandom(spec.Key));
                float[] b = SfxRecipes.Render(spec, AudioConfig.MakeSynthRandom(spec.Key));

                Assert.AreEqual(a.Length, b.Length, "'" + spec.Key + "' 两次渲染长度不同。");
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i])
                    {
                        Assert.Fail("'" + spec.Key + "' 第 " + i + " 个样本不可复现：" +
                                    a[i] + " vs " + b[i] + " ⇒ 配方里混进了非确定性随机源。");
                    }
                }
            }
        }

        /// <summary>不同 key 的种子必须不同，否则所有音效会共用同一串噪声。</summary>
        [Test]
        public void MakeSynthRandom_IsStablePerKey_AndDiffersAcrossKeys()
        {
            System.Random a1 = AudioConfig.MakeSynthRandom("sfx_enemy_hit");
            System.Random a2 = AudioConfig.MakeSynthRandom("sfx_enemy_hit");
            System.Random b1 = AudioConfig.MakeSynthRandom("sfx_player_hurt");

            double x1 = a1.NextDouble();
            double x2 = a2.NextDouble();
            double y1 = b1.NextDouble();

            Assert.AreEqual(x1, x2, 1e-12, "同 key 两次取种子必须给出同一条流。");
            Assert.AreNotEqual(x1, y1, "不同 key 的流撞车了 —— 所有音效会共用同一串噪声。");
        }

        /// <summary>
        /// ★ 13 条配方全量渲染一遍：长度、有限性、峰值三项都必须达标。
        ///
        /// 【为什么 Peak 模式可以断言"精确等于"】12 条走 Finish 的配方最后一步就是
        /// Normalize(Peak, NormTarget)，之后不再有任何增益改动，所以峰值必然
        /// 逐位落在目标上。WindLoop 是全文件唯一不走 Finish 的一条，单独按 Rms 判。
        /// </summary>
        [Test]
        public void Render_AllThirteenRecipes_ProduceFiniteInRangeAudio()
        {
            for (int i = 0; i < AudioConfig.Table.Length; i++)
            {
                SfxSpec spec = AudioConfig.Table[i];
                string tag = "'" + spec.Key + "'：";

                float[] buf = SfxRecipes.Render(spec, AudioConfig.MakeSynthRandom(spec.Key));

                Assert.IsNotNull(buf, tag + "渲染返回 null。");
                Assert.Greater(buf.Length, 0, tag + "渲染出 0 个样本。");

                int expected = Mathf.RoundToInt(spec.DurationSec * spec.SampleRate);
                Assert.AreEqual(expected, buf.Length, Mathf.Max(4, expected / 100),
                                tag + "样本数与 时长 × 采样率 对不上。");

                for (int k = 0; k < buf.Length; k++)
                {
                    if (float.IsNaN(buf[k]) || float.IsInfinity(buf[k]))
                    {
                        Assert.Fail(tag + "第 " + k + " 个样本是 NaN/Infinity ⇒ 播出来是刺耳噪声。");
                    }
                }

                float peak = SfxSynth.Peak(buf);
                Assert.LessOrEqual(peak, 1.0f, tag + "峰值越界，混音总线上必然削波。");

                if (spec.NormMode == NormalizeMode.Peak)
                {
                    Assert.AreEqual(spec.NormTarget, peak, 1e-3f,
                                    tag + "Peak 归一化没有精确落在目标上（是不是漏调 Finish？）。");
                }
                else
                {
                    float rms = SfxSynth.Rms(buf);
                    Assert.AreEqual(spec.NormTarget, rms, spec.NormTarget * 0.10f,
                                    tag + "Rms 归一化偏离目标超过 10%。");
                }
            }
        }

        /// <summary>
        /// 未知配方必须**抛** NotSupportedException，而不是静默返回静音。
        ///
        /// 这是故意设计的：加了枚举值却忘了写配方，在预合成期当场炸掉（并被
        /// AudioClipFactory 的 try/catch 接住、拉黑、警告一次），远好过上线之后
        /// 某个音效永远是一段静音而没人知道。
        /// </summary>
        [Test]
        public void Render_ThrowsNotSupported_ForAnUnimplementedRecipeKind()
        {
            var bogus = new SfxSpec("test_bogus_kind", (RecipeKind)9999, 22050, 0.1f, 1.0f, 0.0f, false, 0.9f);

            Assert.Throws<System.NotSupportedException>(
                () => SfxRecipes.Render(bogus, new System.Random(1)),
                "default 分支必须抛异常，静默返回静音会让漏写配方永远不被发现。");
        }

        // =====================================================================
        // F. AudioClipFactory —— 缓存 / 黑名单 / 释放
        // =====================================================================

        /// <summary>未注册 key 返回 null（合法降级）、进黑名单、且绝不抛异常。</summary>
        [Test]
        public void Factory_ReturnsNull_AndBlacklists_ForUnknownKey()
        {
            AudioClip clip = null;
            Assert.DoesNotThrow(() => { clip = AudioClipFactory.Get("sfx_nope_not_here"); });

            Assert.IsNull(clip, "未注册 key 必须返回 null，让调用方静默降级。");
            Assert.IsTrue(AudioClipFactory.IsFailed("sfx_nope_not_here"),
                          "失败的 key 必须进黑名单，否则每一次命中都会重跑一遍查表。");

            Assert.IsNull(AudioClipFactory.Get(null), "null key 不该抛。");
            Assert.IsNull(AudioClipFactory.Get(string.Empty), "空 key 不该抛。");
        }

        /// <summary>同一个 key 取两次必须是同一个实例（缓存生效，不重复合成）。</summary>
        [Test]
        public void Factory_CachesClips_AndReportsCachedCount()
        {
            Assert.AreEqual(0, AudioClipFactory.CachedCount, "SetUp 之后缓存应当是空的。");

            AudioClip a = AudioClipFactory.Get("sfx_enemy_hit");
            AudioClip b = AudioClipFactory.Get("sfx_enemy_hit");

            Assert.IsNotNull(a, "sfx_enemy_hit 合成失败 —— 这条是全局最高频音效。");
            Assert.AreSame(a, b, "第二次取回了不同实例 ⇒ 每次命中都在重新合成 100 ms 音频。");
            Assert.AreEqual(1, AudioClipFactory.CachedCount);

            // 合出来的 clip 形状要对：单声道、采样率与规格一致。
            SfxSpec spec;
            AudioConfig.TryGetSpec("sfx_enemy_hit", out spec);
            Assert.AreEqual(1, a.channels, "本作全部音效都是单声道。");
            Assert.AreEqual(spec.SampleRate, a.frequency, "clip 采样率与规格不一致，会变调。");
        }

        /// <summary>Clear() 必须真的销毁 clip 并把三个静态集合一起清空。</summary>
        [Test]
        public void Factory_Clear_DestroysClips_AndResetsAllCounters()
        {
            AudioClip kept = AudioClipFactory.Get("sfx_enemy_death");
            AudioClipFactory.Get("sfx_still_not_here");

            Assert.IsNotNull(kept);
            Assert.AreEqual(1, AudioClipFactory.CachedCount);
            Assert.AreEqual(1, AudioClipFactory.FailedCount);

            AudioClipFactory.Clear();

            Assert.AreEqual(0, AudioClipFactory.CachedCount, "缓存没清空。");
            Assert.AreEqual(0, AudioClipFactory.FailedCount, "黑名单没清空，重开后旧失败记录会继续生效。");
            Assert.IsFalse(AudioClipFactory.IsFailed("sfx_still_not_here"));

            // ★ Unity 的"假 null"：被 Destroy 的 UnityEngine.Object 与 null 相等。
            //   这一句才是真正验证"AudioClip 被释放了"，而不是只把字典清了。
            Assert.IsTrue(kept == null, "clip 没有被 Destroy ⇒ 每重开一局泄漏一份 PCM 缓冲。");

            Assert.DoesNotThrow(() => AudioClipFactory.Clear(), "Clear 必须幂等。");
        }

        /// <summary>Prewarm 必须把 13 条全部合出来并如实报数。</summary>
        [Test]
        public void Factory_Prewarm_SynthesizesEveryRegisteredKey()
        {
            int ok = AudioClipFactory.Prewarm(AudioConfig.AllKeys());

            Assert.AreEqual(AudioConfig.Table.Length, ok,
                            "有配方在预合成期挂了 —— 详见 Console 里的逐条警告。");
            Assert.AreEqual(AudioConfig.Table.Length, AudioClipFactory.CachedCount);
            Assert.AreEqual(0, AudioClipFactory.FailedCount);

            Assert.DoesNotThrow(() => AudioClipFactory.Prewarm(null), "null 数组不该把预热掀了。");
        }

        // =====================================================================
        // G. AudioDirector —— 装配期形态与四层闸门
        // =====================================================================

        /// <summary>Awake 必须建出 16 路纯 2D、不自动播放的 voice。</summary>
        [Test]
        public void Director_Awake_BuildsSixteenTwoDimensionalVoices()
        {
            AudioDirector director = NewDirector("AudioDirector_Pool");

            Transform pool = director.transform.Find("AudioVoicePool");
            Assert.IsNotNull(pool, "没有建出 voice 池根物体。");

            AudioSource[] sources = pool.GetComponentsInChildren<AudioSource>(true);
            Assert.AreEqual(AudioConfig.VoicePoolSize, sources.Length,
                            "voice 路数与 VoicePoolSize 不一致。");

            for (int i = 0; i < sources.Length; i++)
            {
                Assert.IsFalse(sources[i].playOnAwake, "voice " + i + " 会在装配期自己响一声。");
                Assert.IsFalse(sources[i].loop, "voice " + i + " 是 one-shot，不该 loop。");
                Assert.AreEqual(0.0f, sources[i].spatialBlend, 1e-6f,
                                "voice " + i + " 开了 3D ⇒ 同一个音效在不同位置响度不同。");
                Assert.IsNull(sources[i].clip, "装配期不该有任何 clip 被挂上。");
            }
        }

        /// <summary>重复调用不该建出第二个池（EnsurePool 幂等）。</summary>
        [Test]
        public void Director_PoolIsBuiltOnlyOnce_EvenAfterBindAndPrewarm()
        {
            AudioDirector director = NewDirector("AudioDirector_Idempotent");

            director.Bind(null);
            director.Bind(null);

            AudioSource[] all = director.GetComponentsInChildren<AudioSource>(true);
            Assert.AreEqual(AudioConfig.VoicePoolSize, all.Length,
                            "重复 Bind 之后 AudioSource 变多了 ⇒ EnsurePool 不幂等。");
        }

        /// <summary>
        /// AudioListener 保障：无论进来之前场景里有几个，之后必须**有且仅有一个**启用中的。
        /// 且多余的那些只是 enabled = false，**不能被销毁**（相机是场景自带对象，删不得）。
        /// </summary>
        [Test]
        public void Director_EnsuresExactlyOneEnabledAudioListener_WithoutDestroyingAny()
        {
            GameObject l1 = NewGo("Listener_A");
            GameObject l2 = NewGo("Listener_B");
            l1.AddComponent<AudioListener>();
            l2.AddComponent<AudioListener>();

            // 构造函数式地触发 OnEnable → EnsureSingleAudioListener
            NewDirector("AudioDirector_Listener");

            Assert.AreEqual(1, CountEnabledListeners(),
                            "启用中的 AudioListener 不是恰好一个，Unity 会持续刷警告且混音行为未定义。");

            // 两个宿主都还在 —— 保障是可逆的（enabled = false），不是破坏性的。
            Assert.IsNotNull(l1.GetComponent<AudioListener>(), "多余的 listener 被销毁了，不可逆。");
            Assert.IsNotNull(l2.GetComponent<AudioListener>(), "多余的 listener 被销毁了，不可逆。");
        }

        /// <summary>场景里一个 listener 都没有时，必须自动补一个（否则全程静默且不报错）。</summary>
        [Test]
        public void Director_AddsAListener_WhenSceneHasNone()
        {
            // 先把测试场景里可能残留的启用中 listener 全部关掉，构造 count == 0。
            AudioListener[] pre = FindAllListeners();
            for (int i = 0; i < pre.Length; i++)
            {
                if (pre[i] != null)
                {
                    pre[i].enabled = false;
                }
            }

            NewDirector("AudioDirector_NoListener");

            Assert.AreEqual(1, CountEnabledListeners(),
                            "场景无 listener 时没有补挂 ⇒ 所有音效静默失败且不报错。");
        }

        /// <summary>
        /// ★ 闸门 ①（同 key 节流）：EditMode 下时间不推进，所以同一个 key 连打 5 次
        /// 必须只有**第一次**通过。这正是"顿帧期间用冻结时钟记账会导致连击静音"
        /// 那条推理的镜像验证 —— 时间不动，节流窗口就永远不过期。
        /// </summary>
        [Test]
        public void Gate1_Throttle_AllowsOnlyTheFirstHitWhileTimeIsFrozen()
        {
            AudioDirector director = NewDirector("AudioDirector_Throttle");

            for (int i = 0; i < 5; i++)
            {
                director.Play("sfx_enemy_hit");
            }

            Assert.AreEqual(1, CountLoadedVoices(director),
                            "sfx_enemy_hit 带 50 ms 节流，时间不推进时应当只放行第一次。");
        }

        /// <summary>
        /// ★ 闸门 ②（同 key 并发上限）：拿一个**不带节流**的 key 连打 6 次，
        /// 必须正好占用 PerKeyVoiceLimit = 3 路。多一路都说明 LiveVoices 记账漏了。
        /// </summary>
        [Test]
        public void Gate2_PerKeyConcurrency_CapsAtThreeVoices()
        {
            SfxSpec spec;
            Assert.IsTrue(AudioConfig.TryGetSpec("sfx_enemy_death", out spec));
            Assert.AreEqual(0.0f, spec.ThrottleSec, 1e-6f,
                            "本用例依赖 sfx_enemy_death 不带节流；它变了就得换一个探针 key。");

            AudioDirector director = NewDirector("AudioDirector_Concurrency");

            for (int i = 0; i < 6; i++)
            {
                director.Play("sfx_enemy_death");
            }

            Assert.AreEqual(AudioConfig.PerKeyVoiceLimit, CountLoadedVoices(director),
                            "同 key 并发没有被钳在 " + AudioConfig.PerKeyVoiceLimit + " 路。");
        }

        /// <summary>
        /// 闸门 ②' 换 key 就该另开 voice —— 上限是**每 key** 的，不是全局的。
        /// 写这条是因为"把 LiveVoices 写成全局计数"是一个非常容易犯且很难察觉的错。
        /// </summary>
        [Test]
        public void Gate2_LimitIsPerKey_NotGlobal()
        {
            AudioDirector director = NewDirector("AudioDirector_PerKey");

            director.Play("sfx_enemy_death");
            director.Play("sfx_boss_phase");
            director.Play("sfx_boss_summon");
            director.Play("sfx_boss_shockwave");

            Assert.AreEqual(4, CountLoadedVoices(director),
                            "四个不同 key 只占到了少于 4 路 ⇒ 并发上限被写成全局的了。");
        }

        /// <summary>闸门 ②（静音）：Muted 时一路 voice 都不许被占用 —— 语义是"跳过 Play 本身"。</summary>
        [Test]
        public void Gate2_Mute_SkipsPlayEntirely_AndCostsNoVoice()
        {
            AudioDirector director = NewDirector("AudioDirector_Muted");
            AudioConfig.Muted = true;

            director.Play("sfx_enemy_death");
            director.Play("sfx_boss_phase");

            Assert.AreEqual(0, CountLoadedVoices(director),
                            "静音时仍然占了 voice ⇒ 语义写成了'音量设 0'，而不是'跳过 Play'。");
            Assert.AreEqual(0, AudioClipFactory.CachedCount,
                            "静音时还去合成了 clip ⇒ 白白付了合成开销。");
        }

        /// <summary>闸门 ③（音量视同为零）：主音量或通道音量塌到 epsilon 以下时不占 voice。</summary>
        [Test]
        public void Gate3_ZeroVolume_IsTreatedAsSilentByEpsilon()
        {
            AudioDirector zeroMaster = NewDirector("AudioDirector_ZeroMaster");
            AudioConfig.MasterVolume = 1e-8f;
            zeroMaster.Play("sfx_enemy_death");
            Assert.AreEqual(0, CountLoadedVoices(zeroMaster),
                            "主音量 1e-8 仍然发声 ⇒ 用了 == 0f 而不是 epsilon 比较。");

            ResetAudioStatics();

            AudioDirector zeroSfx = NewDirector("AudioDirector_ZeroSfx");
            AudioConfig.SfxVolume = 0.0f;
            zeroSfx.Play("sfx_enemy_death");
            Assert.AreEqual(0, CountLoadedVoices(zeroSfx), "Sfx 通道为 0 时不该占 voice。");
        }

        /// <summary>未注册 key / null / 空串走到 Play 时必须静默降级，绝不抛到内核调用栈上。</summary>
        [Test]
        public void Play_IsAlwaysThrowless_EvenForGarbageKeys()
        {
            AudioDirector director = NewDirector("AudioDirector_Garbage");

            Assert.DoesNotThrow(() => director.Play(null));
            Assert.DoesNotThrow(() => director.Play(string.Empty));
            Assert.DoesNotThrow(() => director.Play("sfx_from_a_future_version"));
            Assert.DoesNotThrow(() => director.Play("   "));

            Assert.AreEqual(0, CountLoadedVoices(director), "垃圾 key 不该占用任何 voice。");
        }

        /// <summary>
        /// ★ StopAllVoices 必须把**每个 key 的 LiveVoices 一起清零**，不只是停声音。
        ///
        /// 【这条断言是怎么工作的】先把 sfx_enemy_death 打满 3 路（并发上限）。
        /// 如果 StopAllVoices 只调了 AudioSource.Stop() 而忘了清记账，LiveVoices
        /// 会永远停在 3，之后这个 key 再也不可能发声。清干净了的话，接下来 6 次
        /// 调用会重新放行 3 次，占到另外 3 路 —— 于是"挂着 clip 的 voice"从 3 变 6。
        ///
        /// 【为什么不去断言 isPlaying】EditMode 下 AudioSource 根本不会真的播，
        /// isPlaying 恒为 false，断言它等于写了一条永远绿的假断言。
        /// 记账是否清干净则是纯逻辑，在 EditMode 下反而能精确验证。
        /// </summary>
        [Test]
        public void StopAllVoices_ResetsPerKeyAccounting_NotJustTheSound()
        {
            AudioDirector director = NewDirector("AudioDirector_StopAll");

            Assert.DoesNotThrow(() => director.StopAllVoices(), "空池上调用不该抛。");

            for (int i = 0; i < 6; i++)
            {
                director.Play("sfx_enemy_death");
            }
            Assert.AreEqual(AudioConfig.PerKeyVoiceLimit, CountLoadedVoices(director),
                            "前置条件不成立：并发上限没有把它钳在 3 路。");

            director.StopAllVoices();

            for (int i = 0; i < 6; i++)
            {
                director.Play("sfx_enemy_death");
            }

            Assert.AreEqual(AudioConfig.PerKeyVoiceLimit * 2, CountLoadedVoices(director),
                            "全停之后这个 key 再也发不出声 ⇒ LiveVoices 没被清零。");
        }

        /// <summary>
        /// ★ ClearAll 的三步释放：Stop → clip = null → Destroy(clip)。
        /// 断言落在"voice 上再没有 clip 引用"与"clip 实例真的没了"两点上。
        /// </summary>
        [Test]
        public void ClearAll_PerformsTheThreeStepRelease_AndIsIdempotent()
        {
            AudioDirector director = NewDirector("AudioDirector_ClearAll");
            director.Play("sfx_enemy_death");

            AudioClip clip = AudioClipFactory.Get("sfx_enemy_death");
            Assert.IsNotNull(clip);
            Assert.AreEqual(1, CountLoadedVoices(director), "前置条件不成立：这一声没播出去。");

            director.ClearAll();

            Assert.AreEqual(0, CountLoadedVoices(director), "第 ② 步没做：voice 上还挂着 clip 引用。");
            Assert.AreEqual(0, AudioClipFactory.CachedCount, "第 ③ 步没做：工厂缓存还在。");
            Assert.IsTrue(clip == null, "第 ③ 步没做：AudioClip 没有被 Destroy。");

            Assert.DoesNotThrow(() => director.ClearAll(), "ClearAll 必须幂等（OnDestroy 里还会再调一次）。");
        }

        /// <summary>
        /// Prewarm 必须建出环境衬底宿主并把 13 条全部合出来。
        /// 顺带验证"衬底不在 voice 池里"—— 它是 Director 的另一个子物体。
        /// </summary>
        [Test]
        public void Prewarm_CreatesAmbienceHost_AndFillsTheCache()
        {
            AudioDirector director = NewDirector("AudioDirector_Prewarm");

            director.Prewarm();

            Assert.AreEqual(AudioConfig.Table.Length, AudioClipFactory.CachedCount,
                            "预合成之后缓存条数不对。");

            Transform host = director.transform.Find("AmbienceLayer");
            Assert.IsNotNull(host, "PrewarmAmbience 为 true 时必须建出环境衬底宿主。");
            Assert.IsNotNull(host.GetComponent<AmbienceLayer>(), "宿主上没有 AmbienceLayer 组件。");

            // 衬底自己的 AudioSource 不属于 voice 池 —— 池子仍然是干净的 16 路。
            Assert.AreEqual(0, CountLoadedVoices(director), "预合成不该让任何 voice 发声。");

            Assert.DoesNotThrow(() => director.Prewarm(), "重复预合成必须是空操作，不能建第二个宿主。");
            Assert.AreEqual(1, director.GetComponentsInChildren<AmbienceLayer>(true).Length,
                            "重复预合成建出了第二个环境衬底。");
        }

        /// <summary>
        /// ToggleMute 必须翻转标志、立刻掐断新声，并且完全可逆。
        ///
        /// 【它会写 PlayerPrefs】所以本文件在 SetUp 拍了快照、TearDown 精确还原，
        /// 不会把开发者本机调好的音量弄脏。
        ///
        /// 【为什么用"新声占不占 voice"当探针，而不是 isPlaying】
        /// EditMode 下 AudioSource 不会真的播，isPlaying 恒为 false。而"静音期间
        /// 调 Play 不占 voice"是纯逻辑，可以精确断言，并且它验证的正是 R-06 那句
        /// "语义是跳过 Play 本身，不是把音量设成 0"。
        /// </summary>
        [Test]
        public void ToggleMute_FlipsTheFlag_BlocksNewSounds_AndIsReversible()
        {
            AudioDirector director = NewDirector("AudioDirector_Mute");
            director.Play("sfx_enemy_death");
            Assert.AreEqual(1, CountLoadedVoices(director), "前置条件不成立：这一声没播出去。");

            director.ToggleMute();
            Assert.IsTrue(AudioConfig.Muted, "第一次切换应当进入静音。");

            director.Play("sfx_boss_phase");
            director.Play("sfx_boss_summon");
            Assert.AreEqual(1, CountLoadedVoices(director),
                            "静音期间仍然占用了新的 voice ⇒ Play 的静音闸门失效。");

            director.ToggleMute();
            Assert.IsFalse(AudioConfig.Muted, "静音必须可逆。");

            director.Play("sfx_boss_phase");
            Assert.AreEqual(2, CountLoadedVoices(director), "解除静音之后应当恢复发声。");
        }

        /// <summary>取当前场景里全部 AudioListener（含被禁用的组件）。</summary>
        /// <returns>listener 数组，永不为 null。</returns>
        private static AudioListener[] FindAllListeners()
        {
#if UNITY_2023_1_OR_NEWER
            AudioListener[] all = Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
#else
            AudioListener[] all = Object.FindObjectsOfType<AudioListener>();
#endif
            return all != null ? all : new AudioListener[0];
        }

        /// <summary>数一数当前有几个**启用中**的 AudioListener。</summary>
        /// <returns>启用中的数量。</returns>
        private static int CountEnabledListeners()
        {
            AudioListener[] all = FindAllListeners();
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].enabled)
                {
                    n++;
                }
            }
            return n;
        }

        // =====================================================================
        // H. AmbienceLayer —— 延迟启动与 duck 目标
        // =====================================================================

        /// <summary>
        /// ★ Q3 延迟启动：Awake 里只准备 AudioSource，**绝不**自己开播。
        /// 开播权在 Director 首次观察到 !IsGameplayBlocked 时才交出去 ——
        /// 否则主菜单画面上就会有风声，而 A-18 明确要求菜单上没有玩法音。
        /// </summary>
        [Test]
        public void Ambience_DoesNotStartOnAwake_AndConfiguresItsSourceCorrectly()
        {
            var layer = NewGo("AmbienceProbe").AddComponent<AmbienceLayer>();

            AudioSource src = layer.GetComponent<AudioSource>();
            Assert.IsNotNull(src, "Awake 里应当已经备好 AudioSource。");
            Assert.IsFalse(src.playOnAwake, "playOnAwake 会让主菜单上直接起风声。");

            // clip 为 null 是"没开播"的可靠证据：Begin() 的倒数第三行才挂 clip。
            // （不断言 isPlaying —— EditMode 下 AudioSource 不会真的播，那会是条假绿。）
            Assert.IsNull(src.clip, "Awake 不该持有 clip —— 开播权在 Director（Q3 延迟启动）。");
            Assert.AreEqual(0, AudioClipFactory.CachedCount,
                            "Awake 就去合成环境衬底 ⇒ 265k 样本的开销跑到了错误的时机。");
            Assert.IsTrue(src.loop, "环境衬底必须是循环的。");
            Assert.AreEqual(0.0f, src.spatialBlend, 1e-6f, "衬底是'整个世界的底噪'，没有位置。");
            Assert.AreEqual(0, src.priority,
                            "priority 必须是 0（最高）：它一被系统丢弃，整个世界就哑了。");
        }

        /// <summary>Duck 是纯赋值，必须可以每帧无条件调用而不产生副作用。</summary>
        [Test]
        public void Ambience_Duck_IsAPureSetter_SafeToCallEveryFrame()
        {
            var layer = NewGo("AmbienceDuck").AddComponent<AmbienceLayer>();

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 120; i++)
                {
                    layer.Duck(i % 2 == 0);
                }
                layer.Duck(false);
            }, "Duck 带了副作用 ⇒ Director 每帧调用会不断重启淡变，音量永远停在起点。");
        }

        /// <summary>开播之前调 StopAndRelease 必须是安全的空操作（ClearAll 会无条件调它）。</summary>
        [Test]
        public void Ambience_StopAndRelease_IsSafeBeforeBegin_AndDoesNotDestroyItsSource()
        {
            var layer = NewGo("AmbienceRelease").AddComponent<AmbienceLayer>();

            Assert.DoesNotThrow(() => layer.StopAndRelease());
            Assert.DoesNotThrow(() => layer.StopAndRelease(), "必须幂等。");

            AudioSource src = layer.GetComponent<AudioSource>();
            Assert.IsNotNull(src, "StopAndRelease 只摘 clip 引用，不该把 AudioSource 也拆了。");
            Assert.IsNull(src.clip, "clip 引用没摘干净 ⇒ 三步释放的第 ③ 步会在它仍被持有时执行。");
        }
    }
}
