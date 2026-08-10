// -----------------------------------------------------------------------------
// AudioDirector.cs —— 音频总控（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译、未经实听**。
// voice 池、AudioListener 保障、静音键这三块必须在本地 Unity 里跑一遍才算数。
//
// 【职责边界：它是"闸门"，不是"播放器"】
// 真正发声的是 16 个 AudioSource，本类一行 DSP 都不写。它做的全部事情是回答
// 一个问题：**这一声到底该不该响、该多响、占哪一路。** 四层闸门 + 增益合成 +
// voice 分配，仅此而已。
//
// 【与 HitFeedbackDirector 的同构关系（这不是巧合，是刻意的）】
//   · [DisallowMultipleComponent] + [DefaultExecutionOrder]        —— 同款骨架
//   · Bind() 注入 + 惰性解析（0.25 s 节流）                        —— 抄 :969
//   · 入口第一行 if (!isActiveAndEnabled) return;                  —— 抄 :368 / :439
//   · LateUpdate 里的三态收敛，正常态每帧无条件恢复                —— 抄 :249-259 / :892
// 两个 Director 的读者是同一批人，形状一样就不需要第二次学习成本。
//
// 【★ 三条铁律，违反了不会报错、只会安静地坏掉】
//
// ① 全部内部计时吃 Time.deltaTime，**绝不**碰 FeedbackClock（架构 §2.5）。
//    本文件 grep FeedbackClock 必须为 0。理由里最硬的一条是它会导致**连击静音**：
//    顿帧时长（0.05~0.12 s）系统性地长于节流窗口（0.05 s），用冻结时钟记账的话
//    SinceLastPlay 在顿帧期间一动不动，连击第二下必然被判定"仍在节流窗口内"而丢弃。
//    这不是偶发竞态，是每一次连击都稳定复现。
//
// ② 随机数只用私有的 System.Random _rng（架构 §2.7）。
//    禁 UnityEngine.Random（进程级共享状态，将来有人 InitState 就会静默改变
//    其他所有表现层随机的序列）、禁内核 SkillRng / PCG32（从内核流里多抽一个数，
//    后续所有战斗随机全部错位，2.5294x 确定性指纹当场作废）。
//    ★ 而"合成"用的是另一条固定种子的流 AudioConfig.MakeSynthRandom(key)，
//      两条流职责不同，绝不可互换：_rng 混进合成 ⇒ 每次启动音色都不一样。
//
// ③ 静音是"跳过 Play 调用本身"，不是"把音量设成 0"（R-06）。
//    音量 0 的 AudioSource.Play() 仍然占一路 voice、仍然要解码、仍然进混音总线。
//    跳过则连 voice 都不分配，_keyStates 也不更新。A-13/A-14 验收的就是这个语义。
//
// 【为什么 Play() 整个包在 try/catch 里】
// 它跑在内核 Encounter.OnHit 的调用栈上。一个音频异常把整场战斗掀了是绝对
// 不可接受的（R-07）。异常隔离在这里不是防御式编程的口号，是拓扑决定的必需品。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 音频总控：16 路 voice 池 / 四层闸门 / 增益合成 / <see cref="AudioListener"/> 保障 /
    /// 静音键 / 暂停三态收敛 / 异常隔离。与 <see cref="CombatBridge"/> 同体。
    ///
    /// 【为什么执行顺序是 130】
    /// HitFeedbackDirector 是 120。音频排在受击反馈**之后**：两者都在 LateUpdate 里
    /// 读 CombatBridge.IsGameplayBlocked，排在后面意味着音频看到的是"本帧受击反馈
    /// 已经收敛完毕"的世界。本期两者不共享任何状态，所以这个顺序目前只是纪律；
    /// 但将来若加"顿帧结束时补一声"这类联动，顺序反了就会差一帧。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(130)]
    public sealed class AudioDirector : MonoBehaviour
    {
        // =====================================================================
        // 内部类型
        // =====================================================================

        /// <summary>
        /// 一路发声。定长池的元素。
        ///
        /// 【为什么存活判定用 Age >= Length 记账，而不是 AudioSource.isPlaying】
        /// isPlaying 在 Play() 后的同一帧内返回值不稳定（取决于音频线程何时接手）。
        /// 用它做并发计数会出现"刚播的这一路没被算进 LiveVoices"的漏计，
        /// 直接击穿 R-05 ② 的"同 key 并发 ≤ 3"。自己记账是确定的。
        /// </summary>
        private sealed class Voice
        {
            /// <summary>发声用的 AudioSource，装配期建好后终生不换。</summary>
            public AudioSource Source = null;

            /// <summary>当前占用它的 key；空闲时为 null。</summary>
            public string Key = null;

            /// <summary>已播时长（秒）。吃 Time.deltaTime，见文件头铁律 ①。</summary>
            public float Age = 0.0f;

            /// <summary>本次发声的预期总时长（秒），已按 pitch 折算。</summary>
            public float Length = 0.0f;

            /// <summary>是否正在发声。</summary>
            public bool Alive = false;

            /// <summary>是否豁免抢占（sfx_boss_death / sfx_boss_shockwave）。</summary>
            public bool Exempt = false;
        }

        /// <summary>
        /// 每个 key 的运行时状态。字典大小恒 ≤ 13，无需裁剪。
        /// </summary>
        private sealed class KeyState
        {
            /// <summary>距上次成功播放的时长（秒），用于节流窗口与连触计数复位。</summary>
            public float SinceLastPlay = 999.0f;

            /// <summary>连触计数。已经连续触发过几次（不含正在判定的这一次）。</summary>
            public int ConsecutiveCount = 0;

            /// <summary>本 key 当前占用的 voice 数。</summary>
            public int LiveVoices = 0;

            /// <summary>合成失败，永久跳过（R-07）。</summary>
            public bool Broken = false;
        }

        // =====================================================================
        // 常量
        // =====================================================================

        /// <summary>voice 池根物体名。层级里一眼能认出来，也方便 Editor 里手动检查。</summary>
        private const string VoicePoolName = "AudioVoicePool";

        /// <summary>环境衬底子物体名。</summary>
        private const string AmbienceHostName = "AmbienceLayer";

        /// <summary>
        /// 惰性依赖解析的重试间隔（秒）。与 <c>HitFeedbackDirector.ResolveRetryInterval</c>
        /// （:198）取同一个值 —— 两个 Director 找的是同一个 CombatBridge，
        /// 节奏不一致只会让排查时多一个"为什么这个比那个晚"的问题。
        /// </summary>
        private const float ResolveRetryInterval = 0.25f;

        /// <summary>
        /// voice 时长的收尾余量（秒）。
        ///
        /// 【为什么要多给 20 ms】
        /// Age 是按帧累加的，clip.length 是精确值。不给余量的话，某一帧的 deltaTime
        /// 偏大就会让 Age 提前越过 Length，于是这一路在**声音还没放完**的时候被判定
        /// 空闲、被下一次播放抢走 —— 表现为"偶尔有一声被掐掉半截"。
        /// 20 ms 远小于最短音效（100 ms），不会造成可感知的 voice 占用浪费。
        /// </summary>
        private const float VoiceLengthGuardSec = 0.02f;

        /// <summary>
        /// SinceLastPlay 的累加上限（秒）。防止长时间不触发的 key 把 float 累到
        /// 精度损失区间（float 在 1e7 量级上加 0.016 已经不动了）。60 s 远大于
        /// 最长的节流窗口（0.07 s）与连触复位窗口（0.20 s），钳死没有任何副作用。
        /// </summary>
        private const float SinceLastPlayCap = 60.0f;

        // =====================================================================
        // 状态
        // =====================================================================

        /// <summary>16 路 voice。装配期一次建好，运行期零分配。</summary>
        private Voice[] _voices = null;

        /// <summary>key ⇒ 运行时状态。惰性建立，最多 13 条。</summary>
        private readonly Dictionary<string, KeyState> _keyStates = new Dictionary<string, KeyState>(16);

        /// <summary>
        /// 音高 / 增益抖动用的私有随机流。种子取 <c>Environment.TickCount</c>。
        ///
        /// 【为什么必须是私有实例】见文件头铁律 ②。它是本组件的一个私有字段，
        /// 除了本组件没有任何代码能触碰它 —— 这比"我检查过没人乱用"强得多，
        /// 让 B-4（开/关音频跑两遍，战斗结果逐位一致）在**结构上**成立。
        /// </summary>
        private System.Random _rng = null;

        /// <summary>抑制新音效。由 <see cref="ApplyPauseConvergence"/> 每帧写，见 §2.8。</summary>
        private bool _suppressNew = false;

        /// <summary>暂停状态的唯一来源。只读，绝不反向写它。</summary>
        private CombatBridge _bridge = null;

        /// <summary>环境衬底层（P1）。<see cref="AudioConfig.PrewarmAmbience"/> 为 false 时恒为 null。</summary>
        private AmbienceLayer _ambience = null;

        /// <summary>voice 池根物体的 Transform。</summary>
        private Transform _voiceRoot = null;

        /// <summary>环形分配游标。抄 <c>DamagePopupLayer.PickSlot</c> 的做法。</summary>
        private int _cursor = 0;

        /// <summary>当前存活的 voice 数（不含正在判定的这一次）。并发补偿要用。</summary>
        private int _activeVoices = 0;

        /// <summary>惰性解析的倒计时（秒），吃 Time.deltaTime。</summary>
        private float _resolveRetry = 0.0f;

        /// <summary>已经警告过的条目（去重）。范式抄 <c>SpriteFactory._pngWarned</c>。</summary>
        private readonly HashSet<string> _warned = new HashSet<string>();

        // =====================================================================
        // 生命周期
        // =====================================================================

        /// <summary>
        /// 建 voice 池。放在 Awake 而不是 Start：Bind / Play 都可能早于 Start 到达
        /// （CombatBridge.SetupAudio 在同一帧的 Start 里就调 Prewarm），
        /// 池子必须在那之前就存在。EnsurePool 本身幂等，重复调用是空操作。
        /// </summary>
        private void Awake()
        {
            EnsurePool();
        }

        /// <summary>
        /// <see cref="AudioListener"/> 保障的两个检查点之一（另一个在 <see cref="Prewarm"/>）。
        /// 见架构 §2.3：listener 的增删只可能发生在世界重建时，而世界重建必然重走
        /// Prewarm，所以挂在这两个点上就是完备的，不需要每帧做全场景遍历。
        /// </summary>
        private void OnEnable()
        {
            EnsureSingleAudioListener();
        }

        /// <summary>
        /// 静音键（R-06 / A-13）。
        ///
        /// 【★ 为什么在 Update 而不是 LateUpdate，也不受 _suppressNew 约束】
        /// 静音是**玩家设置**，不是战斗表现。它必须在暂停面板打开时也能用 ——
        /// 实际上"打开暂停菜单去关声音"正是玩家最常见的用法。把它挂到会被
        /// 暂停抑制的路径上，就会出现"越想静音越静不了"的荒诞行为。
        /// 放 Update 也让它与 CombatBridge 的 ESC / R 处于同一层（都是玩家意图输入）。
        /// </summary>
        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(AudioConfig.MuteKey))
            {
                ToggleMute();
            }
        }

        /// <summary>
        /// 每帧推进：解析依赖 → voice 记账 → 三态收敛 → 环境衬底延迟启动。
        ///
        /// 【为什么是 LateUpdate 而不是 Update】
        /// 与 HitFeedbackDirector 同一条理由：暂停状态由 CombatBridge 在 Update 里
        /// 可能被改写（ESC 开关菜单）。在 LateUpdate 读，看到的一定是本帧的最终值，
        /// 不会出现"这一帧还按旧状态放行了一声"。
        /// </summary>
        private void LateUpdate()
        {
            ResolveDependencies();
            TickVoices();
            ApplyPauseConvergence();
            TickAmbienceStart();
        }

        /// <summary>
        /// 兜底释放。
        ///
        /// 【为什么这里也要来一遍，明明 CombatBridge.TeardownAudio 已经做了】
        /// 与 <c>TeardownHitFeedback</c> 末尾那句 <c>FeedbackClock.Frozen = false</c>
        /// 是同一类保险：AudioClip 是非托管资源，**没有人替它兜底**。本组件可能在
        /// CombatBridge 之外的路径被销毁（测试里单独 DestroyImmediate、有人手动
        /// 删组件），那些路径下 TeardownAudio 根本不会执行。
        /// ClearAll 幂等（第二次调用时缓存已空），重复执行零代价。
        /// </summary>
        private void OnDestroy()
        {
            ClearAll();
        }

        // =====================================================================
        // 对外入口
        // =====================================================================

        /// <summary>
        /// 注入依赖。由 <c>CombatBridge.SetupAudio()</c> 调用。
        /// 传 null 表示"这一项保持现状"，与 <c>HitFeedbackDirector.Bind</c> 的契约一致。
        /// </summary>
        /// <param name="bridge">暂停状态来源（只读 <c>IsGameplayBlocked</c> / <c>IsRunOver</c>）。</param>
        public void Bind(CombatBridge bridge)
        {
            if (bridge != null)
            {
                _bridge = bridge;
            }

            EnsureRng();
            EnsurePool();
        }

        /// <summary>
        /// 全量预合成（架构 §2.2）。由 <c>CombatBridge.SetupAudio()</c> 在 Start 里同步调用。
        ///
        /// 【为什么开销可以是隐形的】
        /// CombatBridge.Start() 的末尾无论走哪条分支都会 SetMenuPaused(true) ——
        /// 要么弹主菜单，要么弹操作引导面板。也就是说本方法执行完的那一刻，
        /// 玩家正在看一个静态面板，玩法是冻结的。这是整局游戏里唯一一个
        /// "卡几十毫秒也绝对没人察觉"的窗口。
        ///
        /// 【为什么这里再跑一次 EnsureSingleAudioListener】
        /// 见架构 §2.3 第 3 条：WorldBuilder.SetupCamera 只在世界重建时执行，
        /// 而任何绕过 WorldBuilder 的路径（PlayMode 测试手工摆场景、Editor 里
        /// 拖进第二个相机）都会让放在那边的保障静默失效。放在这里则覆盖所有路径 ——
        /// "保证有且仅有一个"这句话里的"保证"，应该由依赖它的人来做。
        /// </summary>
        public void Prewarm()
        {
            EnsureRng();
            EnsurePool();
            EnsureSingleAudioListener();
            EnsureAmbience();

            string[] keys = AudioConfig.AllKeys();
            int expected = keys != null ? keys.Length : 0;
            if (!AudioConfig.PrewarmAmbience && expected > 0)
            {
                // 逃生阀关掉的那一条不计入预期，否则日志会永远显示"少一个"。
                expected -= 1;
            }

            int ok = AudioClipFactory.Prewarm(keys);
            if (ok < expected)
            {
                // 数字对不上是唯一能在没有实听条件下发现"某张配方悄悄挂了"的信号。
                // 用 LogWarning 而不是 Log：它必须在默认过滤器下也能被看见。
                Debug.LogWarning(AudioConfig.LogPrefix + " 预合成 " + ok + "/" + expected +
                                 " 条就绪，有音效不可用（详见上方逐条警告）。游戏可继续运行。");
            }
            else
            {
                Debug.Log(AudioConfig.LogPrefix + " 预合成完成：" + ok + " 条音效就绪" +
                          (AudioConfig.PrewarmAmbience ? "（含环境衬底）" : "（环境衬底已由逃生阀关闭）") + "。");
            }
        }

        /// <summary>
        /// ★ 播放一个音效。<see cref="CombatEventsUnity.PlaySfx"/> 与
        /// <see cref="CombatEventsT3Unity.PlaySfx"/> 两个出口都接在这里。
        ///
        /// 闸门顺序是有讲究的：**越便宜的判定越靠前**。围攻时本方法每秒被调十几次，
        /// 而且它跑在内核 OnHit 的调用栈上，每一次多余的字典查询都会被放大。
        ///
        /// 【任何路径都不许抛到调用方】见文件头。整个方法体在 try/catch 里。
        /// </summary>
        /// <param name="key">音效 key，来自内核，可能是任意字符串（含未注册的）。</param>
        public void Play(string key)
        {
            try
            {
                // ---- 守卫 0：组件被禁用 ----
                // 【为什么委托挡不住】C# 委托持有的是实例引用，它完全不认识
                // MonoBehaviour 的 enabled / activeInHierarchy —— 一个被禁用的组件，
                // 它的委托照样会被调用。而唯一的退订发生在 CombatBridge.OnDestroy，
                // 也就是说"被禁用但未销毁"这个窗口里订阅关系依然有效。
                // 抄 HitFeedbackDirector.cs:368 / :439（QA 抓出的真缺陷修复）。
                //
                // 对音频来说漏掉它的后果比漏一声轻：voice 的 Age 由本组件的
                // LateUpdate 递减，组件禁用则记账停摆 ⇒ 已占用的 voice 永远不释放
                // ⇒ 重新启用后前 16 声全部被"全池占满且无一豁免"挡掉。
                if (!isActiveAndEnabled)
                {
                    return;
                }

                // ---- 守卫 1：暂停 / 终局抑制（§2.8）----
                //
                // 【维护约束 · 加 UI 音效前必读】（QA 报告 P2-3）
                // 本守卫位于查表**之前**，对所有 key 一视同仁。当前 13 行表里零 UI 类 key，
                // 所以无害。但一旦后续加入「暂停菜单按钮点击音」这类 key，它会在暂停态
                // 被静默吞掉 —— 而暂停菜单恰恰是它唯一需要响的时刻，且不抛任何异常，
                // 排查成本极高。
                // 届时的改法：把本守卫下移到 TryGetSpec 之后，改为
                //     if (_suppressNew && spec.Bus != Channel.Ui) { return; }
                if (_suppressNew)
                {
                    return;
                }

                // ---- 守卫 2：静音 / 空 key ----
                // 静音是跳过 Play 本身，不是把音量设 0（见文件头铁律 ③）。
                if (AudioConfig.Muted)
                {
                    return;
                }
                if (string.IsNullOrEmpty(key))
                {
                    return;
                }

                // ---- 守卫 3：查表 + 通道音量 ----
                SfxSpec spec;
                if (!AudioConfig.TryGetSpec(key, out spec))
                {
                    // 未注册的 key。静默降级 + 只警告一次（R-07）。
                    WarnOnce("key:" + key, "内核传出了未注册的音效 key");
                    return;
                }

                // 用 epsilon 而非 == 0f：滑条 UI 拖到底可能停在 1e-8 这种值上，
                // 浮点相等判断会漏掉，于是"音量拉到 0 了还在占 voice"。
                if (AudioConfig.ChannelVolume(spec.Bus) <= AudioConfig.VolumeEpsilon)
                {
                    return;
                }
                if (AudioConfig.MasterVolume <= AudioConfig.VolumeEpsilon)
                {
                    return;
                }

                KeyState st = StateOf(key);
                if (st.Broken)
                {
                    // 合成失败过，永久跳过。工厂已经警告过，这里不重复刷屏。
                    return;
                }

                // ---- 闸门 ①：同 key 节流（R-05 ①）----
                if (spec.ThrottleSec > 0.0f && st.SinceLastPlay < spec.ThrottleSec)
                {
                    return;
                }

                // ---- 闸门 ②：同 key 并发上限（R-05 ②）----
                if (st.LiveVoices >= AudioConfig.PerKeyVoiceLimit)
                {
                    return;
                }

                AudioClip clip = AudioClipFactory.Get(key);
                if (clip == null)
                {
                    st.Broken = true;
                    return;
                }

                // ---- 闸门 ③：全局并发 + 抢占（R-05 ③）----
                int v = PickVoice(spec);
                if (v < 0)
                {
                    // 全池都是豁免音 ⇒ 丢弃本次播放。
                    // 宁可丢一声杂兵命中，也绝不挤掉 BOSS 冲击波（架构 §2.1 第 ③ 步）。
                    return;
                }

                // ---- 闸门 ④：增益合成（R-05 ④ + R-06 + R-12 + 并发补偿）----
                float gain = ResolveGain(spec, st);
                float pitch = SfxSynth.Lerp(AudioConfig.PitchJitterMin, AudioConfig.PitchJitterMax,
                                            (float)_rng.NextDouble());

                StartVoice(v, clip, spec, gain, pitch);

                st.SinceLastPlay = 0.0f;
                st.ConsecutiveCount++;
                st.LiveVoices++;
            }
            catch (System.Exception e)
            {
                // ★ 整条链路的异常隔离（R-07）。见文件头。
                // key 拼进去区分不同来源，避免一个坏 key 把所有异常都去重掉。
                WarnOnce("exc:" + (key != null ? key : "<null>"), "播放链路抛异常：" + e.Message);
            }
        }

        /// <summary>
        /// 切换静音（<see cref="AudioConfig.MuteKey"/>，默认 M）。
        ///
        /// 【为什么静音时要 StopAllVoices】
        /// 不停的话，按下 M 的那一刻正在播的音会继续放完（最长 1.15 s 的 BOSS 死亡音）。
        /// 玩家的心智模型是"按 M 立刻安静"，等一秒钟会让人以为按键没生效而再按一次 ——
        /// 于是又切回有声。这是一个纯交互问题，不是技术问题。
        ///
        /// 【为什么顺手 SavePrefs】
        /// R-11。PlayerPrefs 默认在 OnApplicationQuit 才落盘，而 Editor 里直接点停止
        /// 播放**不保证**触发它 —— 不显式 Save，调音时的设置下次进 Play 就没了。
        /// </summary>
        public void ToggleMute()
        {
            AudioConfig.Muted = !AudioConfig.Muted;

            if (AudioConfig.Muted)
            {
                StopAllVoices();
            }

            AudioConfig.SavePrefs();
            Debug.Log(AudioConfig.LogPrefix + " 静音：" + (AudioConfig.Muted ? "开" : "关") +
                      "（按 " + AudioConfig.MuteKey + " 切换）");
        }

        /// <summary>
        /// 停下全部 voice 并清空记账。用于终局收敛、静音、重开。
        ///
        /// 【为什么连 ConsecutiveCount 一起清】
        /// 它是"这一串连击"的计数。全停意味着这一串结束了，不清的话下一次播放会
        /// 直接吃到上一局累积的递减系数 —— 表现为"重开后第一声特别小"。
        /// </summary>
        public void StopAllVoices()
        {
            if (_voices != null)
            {
                for (int i = 0; i < _voices.Length; i++)
                {
                    Voice v = _voices[i];
                    if (v == null)
                    {
                        continue;
                    }
                    if (v.Source != null)
                    {
                        v.Source.Stop();
                    }
                    v.Alive = false;
                    v.Key = null;
                    v.Age = 0.0f;
                    v.Length = 0.0f;
                    v.Exempt = false;
                }
            }

            foreach (KeyValuePair<string, KeyState> kv in _keyStates)
            {
                KeyState st = kv.Value;
                st.LiveVoices = 0;
                st.ConsecutiveCount = 0;
            }

            _activeVoices = 0;
        }

        /// <summary>
        /// ★ 全量释放。三步顺序不可交换（架构 §2.10）。
        ///
        ///   ① Stop()            —— 让 AudioSource 不再读采样
        ///   ② source.clip = null —— 摘掉引用
        ///   ③ Destroy(clip)      —— AudioClipFactory.Clear()
        ///
        /// 【为什么顺序不能反】
        /// 在 AudioSource 仍持有并播放某个 AudioClip 时 Destroy 它，Unity 的行为是
        /// 未定义的 —— 轻则 Console 报错，重则播出垃圾采样（一段刺耳的噪声）。
        /// 正常退出时可能看不出区别，但"在 BOSS 死亡音（1.15 s）播到一半时按 R 重开"
        /// 是一条真实存在、且玩家一定会走的路径。A-19 验收的就是这一条。
        ///
        /// 【为什么 voice 的 GameObject 反而不手动 Destroy】
        /// 沿用 CombatBridge.cs:555-559 对飘字层的既有裁定：场景卸载会一并回收，
        /// 手动销毁反而引入销毁顺序依赖（Unity 不保证同帧内的销毁次序）。
        /// 分界线是：**GameObject / Component 归场景管；AudioClip 不归任何
        /// GameObject 管，必须显式 Destroy。**
        /// </summary>
        public void ClearAll()
        {
            // 环境衬底也持有一份 clip 引用，必须先摘掉，否则第 ③ 步会在它仍在
            // 循环播放的时候把 clip 销毁 —— 这是最容易漏的一处，因为它不在 voice 池里。
            if (_ambience != null)
            {
                _ambience.StopAndRelease();
            }

            // ① 全停
            StopAllVoices();

            // ② 摘引用
            if (_voices != null)
            {
                for (int i = 0; i < _voices.Length; i++)
                {
                    Voice v = _voices[i];
                    if (v != null && v.Source != null)
                    {
                        v.Source.clip = null;
                    }
                }
            }

            // ③ 销毁
            AudioClipFactory.Clear();

            _keyStates.Clear();
            _warned.Clear();
            _cursor = 0;
        }

        // =====================================================================
        // 暂停三态收敛
        // =====================================================================

        /// <summary>
        /// 三态收敛（架构 §2.8）。返回 true 表示本帧被暂停 / 终局接管。
        ///
        /// | 状态 | 判据 | 新音效 | 已在播的 voice | 环境衬底 |
        /// |---|---|---|---|---|
        /// | 正常 | !blocked | 允许 | 照常 | 恢复 100%（300 ms 平滑） |
        /// | 菜单暂停 | blocked &amp;&amp; !IsRunOver | 抑制 | **允许自然放完** | duck 到 30% |
        /// | 终局 | blocked &amp;&amp; IsRunOver | 抑制 | **立即全停** | duck 到 30% |
        ///
        /// 【为什么菜单暂停放完、终局全停 —— 这不是不一致，是两种不同的语义】
        /// · 菜单暂停是**可逆**的瞬时状态，而所有战斗音效都 &lt; 1.4 s。强行切断会在
        ///   波形中间产生阶跃 → 一声咔哒，正是 A-10 明令禁止的那种声音。放完最多
        ///   一秒，玩家几乎察觉不到。
        /// · 终局**不可逆**（IsRunOver 一旦为真不会翻回来），"恢复后接着播"的语义
        ///   根本不存在；而 A-18 明确要求"结算界面上无战斗音效"。
        /// 这与 HitFeedbackDirector 对飘字的处理（:912-925 菜单冻结保留、终局清空）
        /// 是同一套三态语义，只是"冻结"在音频上不可行（§2.5），退化成"让它放完"。
        ///
        /// 【★ 正常态必须每帧无条件写，不能只在"从暂停切回来"的那一帧写】
        /// 抄 HitFeedbackDirector.cs:249-259 的第三态纪律。漏了它就是"暂停过一次
        /// 之后音效永久消失" —— 而这类 bug 只在暂停过之后才出现，很容易漏测。
        ///
        /// 【为什么轮询而不是给 CombatBridge 加事件】
        /// IsGameplayBlocked 是派生只读属性（= IsRunOver || _menuPaused），它的值
        /// 可以在**没有任何人调 SetMenuPaused 的情况下**变化（IsRunOver 由内核决定）。
        /// 要让事件不漏发就得在内核终局路径上补通知点，那就碰内核了（B-2 红线）。
        /// 而且 R-09 明令"不得新增第二个音频专用暂停标志"。
        /// </summary>
        /// <returns>被暂停 / 终局接管返回 true。</returns>
        private bool ApplyPauseConvergence()
        {
            if (_bridge == null || !_bridge.IsGameplayBlocked)
            {
                // ★ 正常态：每帧无条件恢复。见上面的第三态纪律。
                _suppressNew = false;
                if (_ambience != null)
                {
                    _ambience.Duck(false);
                }
                return false;
            }

            // 逐字沿用 HeroineAnimator / HitFeedbackDirector 已经建立的三态写法：
            // IsGameplayBlocked = IsRunOver || _menuPaused。
            bool menuPaused = !_bridge.IsRunOver;

            _suppressNew = true;

            if (!menuPaused)
            {
                // 终局：立即全停，结算界面上零残留。
                StopAllVoices();
            }

            if (_ambience != null)
            {
                _ambience.Duck(true);
            }

            return true;
        }

        /// <summary>
        /// 环境衬底的延迟启动（Q3：主菜单不播）。
        ///
        /// 【为什么这一条是免费的，不需要任何跨场景机制】
        /// 主菜单和战斗在**同一个场景**里（MainMenuHud 由 CombatBridge.Start() 的
        /// :365 创建），且主菜单期间 IsGameplayBlocked == true（:412 的
        /// SetMenuPaused(true)）。所以"主菜单不播"退化成一行"首次观察到
        /// !blocked 才 Begin()"。零跨场景单例、零 DontDestroyOnLoad、零静态旗标。
        ///
        /// Begin() 自带 _started 幂等守卫，所以这里每帧调用是安全的、也是必要的 ——
        /// 玩家可能在引导面板上停留任意久，"第一次解冻"发生在哪一帧无法预知。
        /// </summary>
        private void TickAmbienceStart()
        {
            if (_ambience == null || _bridge == null)
            {
                return;
            }
            if (_bridge.IsGameplayBlocked)
            {
                return;
            }
            _ambience.Begin();
        }

        // =====================================================================
        // voice 池
        // =====================================================================

        /// <summary>
        /// 建 16 路 voice。幂等 —— 已建好则直接返回。
        ///
        /// 【为什么定长而不是动态扩容】
        /// 动态扩容意味着 AddComponent&lt;AudioSource&gt;() 发生在最忙的那一帧
        /// （围攻爆发时），这正是最不能承受额外开销的时刻。定长池把全部分配成本
        /// 前置到装配期，运行期零分配 —— 与 DamagePopupLayer 的 12 个常驻槽位、
        /// HitEffects 的 24 长看板是同一条理由。
        ///
        /// 【为什么 spatialBlend = 0（纯 2D）】
        /// 本作是俯视 2D，声源没有真实的三维位置；开 3D 会让音量随 listener 与
        /// voice 池物体的距离衰减，而 voice 池挂在 Director 身上（跟着玩家或场景根），
        /// 于是同一个音效在不同位置响度不同 —— 那不是空间感，那是 bug。
        /// </summary>
        private void EnsurePool()
        {
            if (_voices != null)
            {
                return;
            }

            var rootGo = new GameObject(VoicePoolName);
            rootGo.transform.SetParent(transform, false);
            _voiceRoot = rootGo.transform;

            int size = AudioConfig.VoicePoolSize;
            if (size < 1)
            {
                size = 1;
            }

            _voices = new Voice[size];
            for (int i = 0; i < size; i++)
            {
                var go = new GameObject("voice_" + i.ToString("00"));
                go.transform.SetParent(_voiceRoot, false);

                AudioSource src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 0.0f;   // 纯 2D，见上
                src.dopplerLevel = 0.0f;
                src.bypassEffects = true;
                src.bypassListenerEffects = true;
                src.bypassReverbZones = true;
                src.volume = 0.0f;
                src.pitch = 1.0f;

                var v = new Voice();
                v.Source = src;
                _voices[i] = v;
            }
        }

        /// <summary>
        /// 选一路 voice（架构 §2.1，逐字复用 <c>DamagePopupLayer.PickSlot</c> 的策略）。
        ///
        ///   ① 空闲优先：从 _cursor 起环形扫一圈，第一个 !Alive 的直接用
        ///   ② 全忙则抢占：再环形扫一圈，取 Age 最大者
        ///      ★ 但跳过 Exempt == true 的（sfx_boss_death / sfx_boss_shockwave）
        ///   ③ 若一圈下来全是豁免音 → 返回 -1，丢弃本次播放
        ///
        /// 【第 ③ 步是设计补充，PRD 只说了"豁免抢占"没说"豁免不下时怎么办"】
        /// 宁可丢一声杂兵命中，也绝不挤掉 BOSS 冲击波 —— 冲击波是 US-3 的
        /// "你该躲了"信号，挤掉它是**玩法伤害**；丢一声命中只是听感损失。
        /// 两者不在一个量级上，所以这个取舍没有第二种答案。
        ///
        /// 【为什么新声不能豁免自己】
        /// 参数 spec 在这里只用来读注释里说的"要不要给新 voice 打豁免标记"，
        /// 打标记发生在 StartVoice。本方法不因为"新来的是 BOSS 音"就放宽抢占规则 ——
        /// 那会让两声 BOSS 音互相挤，而它们本来就该共存。
        /// </summary>
        /// <param name="spec">本次要播的规格（当前仅用于可读性与将来的策略扩展）。</param>
        /// <returns>voice 下标；全池豁免时返回 -1。</returns>
        private int PickVoice(SfxSpec spec)
        {
            if (_voices == null || _voices.Length == 0)
            {
                return -1;
            }

            int n = _voices.Length;

            // ① 空闲优先
            for (int k = 0; k < n; k++)
            {
                int i = (_cursor + k) % n;
                Voice v = _voices[i];
                if (v != null && !v.Alive)
                {
                    return i;
                }
            }

            // ② 全忙则抢占最旧的非豁免者
            int best = -1;
            float bestAge = -1.0f;
            for (int k = 0; k < n; k++)
            {
                int i = (_cursor + k) % n;
                Voice v = _voices[i];
                if (v == null || v.Exempt)
                {
                    continue;
                }
                if (v.Age > bestAge)
                {
                    bestAge = v.Age;
                    best = i;
                }
            }

            // ③ best 仍为 -1 ⇒ 全池都是豁免音，丢弃
            return best;
        }

        /// <summary>
        /// 真正让一路 voice 发声。
        ///
        /// 【为什么预期时长要除以 pitch】
        /// AudioSource.pitch 同时改变播放速度：pitch = 1.06 时 clip 会**提前**放完。
        /// 用 clip.length 直接当 Length 会让这一路多占 6% 的时间 —— 单看无所谓，
        /// 但它系统性地缩小了有效池容量，围攻时会更早触发抢占。
        /// </summary>
        /// <param name="index">voice 下标。</param>
        /// <param name="clip">已合成好的 clip。</param>
        /// <param name="spec">规格（取 Key 与 ExemptPreempt）。</param>
        /// <param name="gain">最终音量 [0,1]。</param>
        /// <param name="pitch">音高倍率。</param>
        private void StartVoice(int index, AudioClip clip, SfxSpec spec, float gain, float pitch)
        {
            Voice v = _voices[index];
            if (v == null || v.Source == null)
            {
                return;
            }

            // 抢占路径：先按正常退役流程结账，保证被挤掉那个 key 的 LiveVoices 不漏减。
            // 漏减的症状是"某个音效播过三次之后就再也不响了"——因为它的并发计数
            // 永远停在上限上，而没有任何人会把它降回来。
            if (v.Alive)
            {
                RetireVoice(v);
            }

            v.Source.clip = clip;
            v.Source.volume = gain;
            v.Source.pitch = pitch;
            v.Source.loop = false;
            v.Source.Play();

            float safePitch = pitch > 0.01f ? pitch : 1.0f;
            v.Key = spec.Key;
            v.Age = 0.0f;
            v.Length = (clip.length / safePitch) + VoiceLengthGuardSec;
            v.Alive = true;
            v.Exempt = spec.ExemptPreempt;

            _activeVoices++;
            _cursor = (index + 1) % _voices.Length;
        }

        /// <summary>
        /// 让一路 voice 退役并把账结清。**不**摘 clip 引用（那是 ClearAll 的活）。
        /// </summary>
        /// <param name="v">要退役的 voice。</param>
        private void RetireVoice(Voice v)
        {
            if (v == null || !v.Alive)
            {
                return;
            }

            if (v.Source != null)
            {
                // 自然播完时 Stop 是空操作；抢占时它是必需的 ——
                // 不停就直接换 clip 会在波形中间产生阶跃（一声咔哒）。
                v.Source.Stop();
            }

            KeyState st;
            if (v.Key != null && _keyStates.TryGetValue(v.Key, out st))
            {
                st.LiveVoices--;
                if (st.LiveVoices < 0)
                {
                    st.LiveVoices = 0;
                }
            }

            v.Alive = false;
            v.Key = null;
            v.Age = 0.0f;
            v.Length = 0.0f;
            v.Exempt = false;

            _activeVoices--;
            if (_activeVoices < 0)
            {
                _activeVoices = 0;
            }
        }

        /// <summary>
        /// 每帧记账：voice 年龄 + key 的节流窗口与连触计数。
        ///
        /// 【为什么吃 Time.deltaTime】见文件头铁律 ①。这是**记账**，不是表现 ——
        /// 用被自己冻住的时钟去数记账周期，顿帧期间就永远轮不到下一次。
        /// </summary>
        private void TickVoices()
        {
            float dt = Time.deltaTime;

            if (_voices != null)
            {
                int alive = 0;
                for (int i = 0; i < _voices.Length; i++)
                {
                    Voice v = _voices[i];
                    if (v == null || !v.Alive)
                    {
                        continue;
                    }

                    v.Age += dt;
                    if (v.Age >= v.Length)
                    {
                        RetireVoice(v);
                        continue;
                    }
                    alive++;
                }

                // 权威重算。RetireVoice 里的自减是给"抢占"这条不经过本循环的路径用的，
                // 两处都写才能保证任何一条路径下计数都是对的。
                _activeVoices = alive;
            }

            foreach (KeyValuePair<string, KeyState> kv in _keyStates)
            {
                KeyState st = kv.Value;

                if (st.SinceLastPlay < SinceLastPlayCap)
                {
                    st.SinceLastPlay += dt;
                }

                // 超过复位窗口没再触发，就认为"这一串结束了"，连触计数归零。
                if (st.ConsecutiveCount > 0 && st.SinceLastPlay >= AudioConfig.ConsecutiveResetSec)
                {
                    st.ConsecutiveCount = 0;
                }
            }
        }

        // =====================================================================
        // 增益
        // =====================================================================

        /// <summary>
        /// 合成最终音量（架构 §2.4 / §2.6）：
        ///
        ///   Clamp01( Master × Channel × KeyGain × 连触递减 × 随机增益 × 并发补偿 )
        ///
        /// | 层 | 取值 |
        /// |---|---|
        /// | 连触递减 | max(0.7^(n-1), 0.40)，n 为本次是第几连 |
        /// | 随机增益 | [0.92, 1.00]（R-12） |
        /// | 并发补偿 | 1/(1+0.06·(active-1))，钳到 [0.45, 1.0] |
        ///
        /// 【连触递减的指数为什么正好是 ConsecutiveCount】
        /// ConsecutiveCount 记的是"**不含本次**已经连续触发过几次"。所以本次是第
        /// n = ConsecutiveCount + 1 连，指数 n-1 = ConsecutiveCount，直接用，不用 ±1。
        /// 调用方在本方法**之后**才 ConsecutiveCount++，顺序不能反 ——
        /// 反了的话第一声就已经被打了 0.7 折，听起来像"开头那一下漏了"。
        ///
        /// 【并发补偿为什么用 _activeVoices + 1 这个近似】
        /// 精确值应该是"本次播放之后的存活数"，而抢占路径下它等于 _activeVoices
        /// （挤掉一个、补上一个，净变化 0），空闲路径下才是 _activeVoices + 1。
        /// 这里统一按 +1 算，只在**全池满且发生抢占**时偏大 1，对应系数差
        /// 0.53 → 0.51，听感上不可分辨。用一个不可分辨的近似换掉一个多出来的参数，
        /// 也让本方法的签名与设计类图（§5）逐字一致。
        /// </summary>
        /// <param name="spec">音效规格。</param>
        /// <param name="st">该 key 的运行时状态（读 ConsecutiveCount）。</param>
        /// <returns>最终音量，[0,1]。</returns>
        private float ResolveGain(SfxSpec spec, KeyState st)
        {
            EnsureRng();

            float master = AudioConfig.MasterVolume;
            float channel = AudioConfig.ChannelVolume(spec.Bus);
            float keyGain = spec.Gain;

            // ---- 连触递减 ----
            float decay = 1.0f;
            int n = st != null ? st.ConsecutiveCount : 0;
            if (n > 0)
            {
                decay = (float)System.Math.Pow(AudioConfig.ConsecutiveDecay, n);
                if (decay < AudioConfig.ConsecutiveFloor)
                {
                    decay = AudioConfig.ConsecutiveFloor;
                }
            }

            // ---- 随机增益（R-12）----
            float jitter = SfxSynth.Lerp(AudioConfig.GainJitterMin, AudioConfig.GainJitterMax,
                                         (float)_rng.NextDouble());

            // ---- 并发补偿（§2.6，一个不需要 Mixer 的迷你压缩器）----
            // 围攻越密，每一声越轻 —— 这正好是 US-5「密集的雨点而不是噪音墙」在
            // 数学上的表达：雨点之所以是雨点，就是因为单点很轻。
            int active = _activeVoices + 1;
            if (active < 1)
            {
                active = 1;
            }
            float comp = 1.0f / (1.0f + AudioConfig.ConcurrencyDuck * (active - 1));
            if (comp < AudioConfig.ConcurrencyFloor)
            {
                comp = AudioConfig.ConcurrencyFloor;
            }
            if (comp > 1.0f)
            {
                comp = 1.0f;
            }

            return SfxSynth.Clamp01(master * channel * keyGain * decay * jitter * comp);
        }

        // =====================================================================
        // AudioListener 保障（R-08，P0）
        // =====================================================================

        /// <summary>
        /// 保证场景里**有且仅有一个**启用中的 <see cref="AudioListener"/>（架构 §2.3）。
        ///
        ///   count == 1 → 什么都不做，一条日志都不打（绝大多数情况）
        ///   count == 0 → 优先挂 Camera.main；为 null 则挂本组件自己。警告一次
        ///   count &gt;  1 → 保留 Camera.main 上那个（没有则保留第一个），
        ///                 其余 enabled = false。警告一次
        ///
        /// 【为什么多余的用 enabled = false 而不是 Destroy】
        /// WorldBuilder.cs:638 的注释已经把规矩写死了 —— **相机是场景自带对象，
        /// 不打生成标记，不创建也不销毁**，否则 Clean And Rebuild 会把它删掉、
        /// 场景变黑屏。删它身上的组件是同一类破坏：不可逆，且下次 Rebuild 补不回来。
        /// 而 Unity 只对**启用中**的多个 listener 刷警告，enabled = false 已经
        /// 完全解决问题，还保留了可逆性。
        ///
        /// 【为什么必须自己过滤 .enabled】
        /// FindObjectsByType / FindObjectsOfType 过滤的是 GameObject 的
        /// activeInHierarchy，**不看组件的 enabled**。不自己过滤的话，上一次保障
        /// 关掉的那些会被重新数进来，于是每次 Prewarm 都"发现重复"并刷一条警告。
        /// </summary>
        private void EnsureSingleAudioListener()
        {
            AudioListener[] all;
#if UNITY_2023_1_OR_NEWER
            all = Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
#else
            all = Object.FindObjectsOfType<AudioListener>();
#endif
            if (all == null)
            {
                all = new AudioListener[0];
            }

            Camera main = Camera.main;

            int enabledCount = 0;
            AudioListener first = null;
            AudioListener onMain = null;

            for (int i = 0; i < all.Length; i++)
            {
                AudioListener l = all[i];
                if (l == null || !l.enabled)
                {
                    continue;
                }
                enabledCount++;
                if (first == null)
                {
                    first = l;
                }
                if (main != null && l.gameObject == main.gameObject)
                {
                    onMain = l;
                }
            }

            // 绝大多数情况：正好一个。静默返回，连日志都不打 ——
            // 一条"一切正常"的日志乘以每次 Prewarm，就是把 Console 变成噪声。
            if (enabledCount == 1)
            {
                return;
            }

            if (enabledCount == 0)
            {
                GameObject host = main != null ? main.gameObject : gameObject;
                AudioListener listener = host.GetComponent<AudioListener>();
                if (listener == null)
                {
                    listener = host.AddComponent<AudioListener>();
                }
                listener.enabled = true;

                WarnOnce("listener:none",
                         "场景里没有启用中的 AudioListener，已补挂到 '" + host.name +
                         "' 上。没有它的话所有音效都会静默失败且不报错");
                return;
            }

            // enabledCount > 1：保留一个，其余关掉。
            AudioListener keep = onMain != null ? onMain : first;
            int disabled = 0;
            for (int i = 0; i < all.Length; i++)
            {
                AudioListener l = all[i];
                if (l == null || !l.enabled || l == keep)
                {
                    continue;
                }
                l.enabled = false;
                disabled++;
            }

            WarnOnce("listener:dup",
                     "场景里有 " + enabledCount + " 个启用中的 AudioListener，已保留 '" +
                     (keep != null ? keep.gameObject.name : "?") + "' 上那个，其余 " + disabled +
                     " 个置为 enabled = false（不销毁，可逆）");
        }

        // =====================================================================
        // 内部小工具
        // =====================================================================

        /// <summary>
        /// 取（或惰性建立）某个 key 的运行时状态。
        ///
        /// 【为什么初始 SinceLastPlay 是 999 而不是 0】
        /// 0 的话，一个 key 的**第一声**会被自己的节流窗口挡掉
        /// （SinceLastPlay 0 &lt; ThrottleSec 0.05 ⇒ 判定"仍在窗口内"）。
        /// 症状是"进场第一次命中没有声音，第二次才有" —— 极难联想到是初值问题。
        /// </summary>
        /// <param name="key">音效 key。</param>
        /// <returns>该 key 的状态对象，永不为 null。</returns>
        private KeyState StateOf(string key)
        {
            KeyState st;
            if (_keyStates.TryGetValue(key, out st) && st != null)
            {
                return st;
            }
            st = new KeyState();
            _keyStates[key] = st;
            return st;
        }

        /// <summary>
        /// 惰性建立私有随机流。种子取 <c>Environment.TickCount</c>。
        ///
        /// 【为什么不在字段初始化器里 new】
        /// 字段初始化器在 Awake 之前运行，那时 Environment.TickCount 对于同一帧内
        /// 创建的多个对象是同一个值。本期只有一个 Director 所以无所谓，但把
        /// "什么时候取种子"显式化，比依赖一个隐含的时序更容易读。
        /// 更实际的理由：Bind / Prewarm / Play 三条路径都可能是第一个到达的，
        /// 有一个统一的 EnsureRng 就不需要在每条路径上重复判空。
        /// </summary>
        private void EnsureRng()
        {
            if (_rng == null)
            {
                _rng = new System.Random(System.Environment.TickCount);
            }
        }

        /// <summary>
        /// 惰性建立环境衬底层。<see cref="AudioConfig.PrewarmAmbience"/> 为 false 时
        /// 整条链路都不启用（见 <see cref="AudioClipFactory.Prewarm"/> 的注释）。
        /// </summary>
        private void EnsureAmbience()
        {
            if (!AudioConfig.PrewarmAmbience)
            {
                return;
            }
            if (_ambience != null)
            {
                return;
            }

            var go = new GameObject(AmbienceHostName);
            go.transform.SetParent(transform, false);
            _ambience = go.AddComponent<AmbienceLayer>();
        }

        /// <summary>
        /// 惰性补齐依赖（抄 <c>HitFeedbackDirector.ResolveDependencies</c>，:969）。
        ///
        /// 【为什么不能只在 Awake 里找一次】
        /// 本组件可能在 PlayMode 测试里被手工挂到一个还没有 CombatBridge 的对象上，
        /// 或者装配次序被将来的改动打乱。只找一次就会永久拿到 null，
        /// 表现为"暂停时音效照样响" —— 一条不报错的静默失效。
        ///
        /// 【★ 但绝不能每帧去找】
        /// FindFirstObjectByType 会遍历整个场景。拿到之后是一次 null 比较，代价为零；
        /// 但在**永远找不到**的场景里，不节流就是每帧一次全场景遍历。
        /// HeroineAnimator.cs:379 已经因为同一个原因加了节流。
        ///
        /// 【为什么倒计时吃 Time.deltaTime】这是记账，不是表现。见文件头铁律 ①。
        /// </summary>
        private void ResolveDependencies()
        {
            if (_bridge != null)
            {
                return;
            }

            _resolveRetry -= Time.deltaTime;
            if (_resolveRetry > 0.0f)
            {
                return;
            }
            _resolveRetry = ResolveRetryInterval;

#if UNITY_2023_1_OR_NEWER
            _bridge = Object.FindFirstObjectByType<CombatBridge>();
#else
            _bridge = Object.FindObjectOfType<CombatBridge>();
#endif
        }

        /// <summary>
        /// 同一个 tag 只警告一次。范式抄 <c>SpriteFactory.WarnOnce</c>（:535）。
        ///
        /// 【为什么去重的粒度是 tag 而不是消息文本】
        /// 消息里常常拼着变量（key 名、数量），同一类问题的文本每次都不同，
        /// 按文本去重等于没去重。tag 由调用方给，形如 "key:xxx" / "listener:dup"，
        /// 一眼能看出它去重的是哪一类。
        /// </summary>
        /// <param name="tag">去重标签。</param>
        /// <param name="detail">具体原因。</param>
        private void WarnOnce(string tag, string detail)
        {
            if (!_warned.Add(tag))
            {
                return;
            }
            Debug.LogWarning(AudioConfig.LogPrefix + " " + detail + "。→ 已静音降级，游戏可继续运行。");
        }
    }
}
