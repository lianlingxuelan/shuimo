// -----------------------------------------------------------------------------
// AmbienceLayer.cs —— 环境衬底（R-10 / A-9，asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译、未经实听**。
// A-9（连听 60 秒听不出循环接缝）是一票否决项，**必须由用户在本地实听验证**。
//
// 【它只做三件事，别的一概不做】
//   ① 循环播放 12 秒的风声衬底（clip 由 AudioClipFactory 提供，本类不合成）
//   ② 暂停 / 终局时 duck 到 30%，恢复时回到 100%，两向都是 300 ms 平滑
//   ③ 延迟启动：首次观察到 !IsGameplayBlocked 才真正开播（Q3：主菜单不播）
//
// 【为什么它是独立组件而不是 AudioDirector 里的一个字段】
// 三条理由，第二条是决定性的：
//   1. 它的 AudioSource 是 loop = true 的长音，与 16 路 one-shot voice 是完全
//      不同的生命周期 —— 混进 voice 池会让"抢占最旧的一路"把环境音挤掉。
//   2. ★ 它需要自己的 Update 来跑 duck 淡变。塞进 Director 就意味着 Director 的
//      LateUpdate 里多一段与 voice 记账无关的状态机，而那段状态机的正确性
//      （淡入淡出对称、静音时暂停、重开时释放）需要单独读一遍才能确认。
//      分开之后，两边各自的不变量都能一眼看完。
//   3. 关掉它是一行 if（AudioConfig.PrewarmAmbience），Director 侧零改动。
//
// 【★ 它不拥有那个 clip】
// clip 归 AudioClipFactory 所有。本类的 StopAndRelease() 只负责 Stop + 摘引用，
// **绝不 Destroy** —— 销毁是 AudioClipFactory.Clear() 的第 ③ 步，顺序由
// AudioDirector.ClearAll() 编排（架构 §2.10）。在这里 Destroy 会让第 ③ 步
// 对着一个已销毁对象再来一次，也会让"谁拥有这个资源"变成两个答案。
//
// 【时间源：吃 Time.deltaTime，不碰 FeedbackClock】
// 与 AudioDirector 同一条铁律（架构 §2.5）。环境音在顿帧时继续走是**对的**：
// 顿帧的意义就是"画面定住，听觉继续推进"。把衬底也冻住，顿帧就只是卡了一下。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 环境衬底层：循环播放 + duck/恢复平滑 + 延迟启动。
    /// 由 <see cref="AudioDirector"/> 在自己的子物体上创建并驱动。
    ///
    /// 【为什么执行顺序是 131】
    /// 紧跟 AudioDirector（130）之后。Director 在 LateUpdate 里调 Duck() 写目标值，
    /// 本类在自己的 Update 里向目标值逼近 —— 顺序上"先收到指令，下一帧再执行"，
    /// 差一帧完全不可感知（淡变全程 300 ms ≈ 18 帧）。把它排在 Director 之后
    /// 只是为了让层级面板里的执行顺序与调用关系一致，便于排查。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(131)]
    public sealed class AmbienceLayer : MonoBehaviour
    {
        // =====================================================================
        // 状态
        // =====================================================================

        /// <summary>循环播放用的 AudioSource。Awake 里建好，终生不换。</summary>
        private AudioSource _source = null;

        /// <summary>是否已经开播（Q3 延迟启动的幂等守卫）。</summary>
        private bool _started = false;

        /// <summary>clip 取不到，本局不再重试。静音降级，不是错误。</summary>
        private bool _broken = false;

        /// <summary>当前 duck 系数，[AmbienceDuckLevel, 1]。</summary>
        private float _level = 1.0f;

        /// <summary>目标 duck 系数。由 <see cref="Duck"/> 写，<see cref="Update"/> 逼近。</summary>
        private float _target = 1.0f;

        /// <summary>是否因静音 / 音量为 0 而处于暂停态。</summary>
        private bool _paused = false;

        /// <summary>已经警告过（去重）。缺一个环境音会每帧命中失败分支。</summary>
        private bool _warned = false;

        // =====================================================================
        // 生命周期
        // =====================================================================

        /// <summary>建 AudioSource。幂等，重复调用是空操作。</summary>
        private void Awake()
        {
            EnsureSource();
        }

        /// <summary>
        /// 逼近目标 duck 系数并同步到 AudioSource；顺带处理静音时的暂停 / 恢复。
        ///
        /// 【为什么用"固定速率线性逼近"而不是 Lerp(current, target, t) 指数平滑】
        /// 指数平滑永远到不了目标，只会无限接近 —— 表现为 duck 之后音量停在 30.4%
        /// 而不是 30%，恢复之后停在 99.6% 而不是 100%。差值本身听不出来，但它让
        /// "当前到底 duck 没 duck"变成一个无法用相等判断回答的问题，测试和排查
        /// 都会因此变得含糊。固定速率能精确落到端点，而且"满程 300 ms"这个承诺
        /// 是字面成立的（指数平滑的 300 ms 只是一个时间常数，不是总时长）。
        /// </summary>
        private void Update()
        {
            StepLevel(Time.deltaTime);
            ApplyVolume();
            ApplyMuteGate();
        }

        // =====================================================================
        // 对外入口
        // =====================================================================

        /// <summary>
        /// 开播（幂等）。由 <c>AudioDirector.TickAmbienceStart()</c> 在首次观察到
        /// <c>!IsGameplayBlocked</c> 时调用，之后每帧还会再调，靠 <see cref="_started"/> 挡住。
        ///
        /// 【为什么幂等守卫放在这里，而不是让调用方记一个 bool】
        /// 调用方（Director）已经有 _suppressNew / _activeVoices 一堆状态了。
        /// "我开播过没有"是本类自己的事实，放在本类里，调用方就退化成一句
        /// 无脑的 Begin() —— 这也让"主菜单不播"这条规则在 Director 侧只占三行。
        ///
        /// 【为什么这里也要 isActiveAndEnabled 守卫】
        /// 与 AudioDirector.Play 同一条理由：组件被禁用时不该起新声，而 Update
        /// 停摆意味着 duck 淡变也停摆 —— 开了播就再没人管它的音量了。
        /// </summary>
        public void Begin()
        {
            if (_started || _broken)
            {
                return;
            }
            if (!isActiveAndEnabled)
            {
                return;
            }
            if (!AudioConfig.PrewarmAmbience)
            {
                // 逃生阀（架构 §2.2 / Q6）：关掉的是**整条**环境衬底链路，
                // 不是"改成懒合成"。懒合成只会把 265k 样本的开销从"看着主菜单"
                // 挪到"刚进战斗"，那是更糟的位置。
                _broken = true;
                return;
            }

            EnsureSource();

            AudioClip clip = AudioClipFactory.Get(AudioConfig.AmbienceKey);
            if (clip == null)
            {
                // 工厂已经警告过具体原因，这里只补一句"所以环境音没了"，仍然只说一次。
                _broken = true;
                WarnOnce("环境衬底 clip 不可用，本局无环境音");
                return;
            }

            _source.clip = clip;
            _source.loop = true;
            _source.volume = CurrentVolume();
            _source.Play();

            _paused = false;
            _started = true;
        }

        /// <summary>
        /// 设置 duck 目标。true = 压到 <see cref="AudioConfig.AmbienceDuckLevel"/>，
        /// false = 回到 100%。两向都是 <see cref="AudioConfig.AmbienceDuckFadeSec"/> 满程。
        ///
        /// 【为什么只写目标值，不在这里做淡变】
        /// 本方法被 Director 的 LateUpdate **每帧无条件调用**（正常态每帧 Duck(false)，
        /// 这是架构 §2.8 明令的第三态纪律）。如果它自己带副作用（比如启动一个协程、
        /// 或者直接改 volume），每帧调一次就会不断重启淡变，音量永远停在起点。
        /// 写成"纯赋值 + Update 里逼近"之后，每帧调用是安全的、幂等的。
        /// </summary>
        /// <param name="on">true = duck 下去。</param>
        public void Duck(bool on)
        {
            _target = on ? AudioConfig.AmbienceDuckLevel : 1.0f;
        }

        /// <summary>
        /// 停播并摘掉 clip 引用。**不销毁 clip**（见文件头）。
        ///
        /// 由 <c>AudioDirector.ClearAll()</c> 在三步释放的**最前面**调用 ——
        /// 环境音也持有一份 clip 引用，而它不在 voice 池里，是最容易漏的一处。
        /// 漏掉的后果：AudioClipFactory.Clear() 会在它仍在循环播放时 Destroy 那个
        /// clip，Unity 的行为未定义（轻则报错，重则播出垃圾采样）。
        /// </summary>
        public void StopAndRelease()
        {
            if (_source != null)
            {
                _source.Stop();
                _source.clip = null;
            }

            _started = false;
            _paused = false;
            _level = 1.0f;
            _target = 1.0f;
        }

        // =====================================================================
        // 内部
        // =====================================================================

        /// <summary>
        /// 建（或补建）AudioSource 并配好参数。幂等。
        ///
        /// 【为什么 spatialBlend = 0】
        /// 与 voice 池同理：本作是俯视 2D，环境衬底更是"整个世界的底噪"，
        /// 它按定义就没有位置。开 3D 会让它随 listener 与本物体的距离衰减。
        ///
        /// 【为什么 priority 给 0（最高）】
        /// Unity 在同时发声数超过平台上限时会按 priority 丢弃低优先级的 source。
        /// 环境衬底是唯一一条"被丢掉会立刻被察觉"的音轨（它一停，整个世界就哑了），
        /// 而 one-shot 丢一声只是听感损失。默认值 128 会让它和 16 路音效平起平坐。
        /// </summary>
        private void EnsureSource()
        {
            if (_source != null)
            {
                return;
            }

            _source = gameObject.GetComponent<AudioSource>();
            if (_source == null)
            {
                _source = gameObject.AddComponent<AudioSource>();
            }

            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0.0f;
            _source.dopplerLevel = 0.0f;
            _source.bypassEffects = true;
            _source.bypassListenerEffects = true;
            _source.bypassReverbZones = true;
            _source.priority = 0;
            _source.pitch = 1.0f;
            _source.volume = 0.0f;
        }

        /// <summary>
        /// 以固定速率把 <see cref="_level"/> 推向 <see cref="_target"/>。
        /// 速率由"满程（1.0 → DuckLevel）耗时 DuckFadeSec"反推，两向同速。
        /// </summary>
        /// <param name="dt">本帧时长（秒），来自 <c>Time.deltaTime</c>。</param>
        private void StepLevel(float dt)
        {
            if (dt <= 0.0f)
            {
                return;
            }

            float span = 1.0f - AudioConfig.AmbienceDuckLevel;
            if (span <= 0.0f)
            {
                // DuckLevel 被调成 1.0（等于不 duck）。直接贴到目标，避免除零。
                _level = _target;
                return;
            }

            float fade = AudioConfig.AmbienceDuckFadeSec;
            if (fade <= 0.0001f)
            {
                // 淡变时长被调成 0 = 要求硬切。这是一个合法的调音选择，不是错误。
                _level = _target;
                return;
            }

            float step = (span / fade) * dt;
            if (_level < _target)
            {
                _level += step;
                if (_level > _target)
                {
                    _level = _target;
                }
            }
            else if (_level > _target)
            {
                _level -= step;
                if (_level < _target)
                {
                    _level = _target;
                }
            }
        }

        /// <summary>当前应有的实际音量 = Master × Bgm 通道 × duck 系数。</summary>
        /// <returns>线性音量 [0,1]。</returns>
        private float CurrentVolume()
        {
            return SfxSynth.Clamp01(AudioConfig.MasterVolume *
                                    AudioConfig.ChannelVolume(Channel.Bgm) *
                                    _level);
        }

        /// <summary>把当前音量写进 AudioSource。每帧一次，代价是一次浮点乘法。</summary>
        private void ApplyVolume()
        {
            if (_source == null || !_started)
            {
                return;
            }
            _source.volume = CurrentVolume();
        }

        /// <summary>
        /// 静音闸门：静音 / 总音量或 BGM 通道音量归零时 <c>Pause()</c>，恢复时 <c>UnPause()</c>。
        ///
        /// 【为什么是 Pause 而不是把 volume 设 0】
        /// 与 R-06 对 one-shot 的裁定同源：音量 0 的 source 仍然在解码、仍然进
        /// 混音总线。区别在于环境音是**长期**存在的，那份开销会一直挂着。
        ///
        /// 【为什么是 Pause 而不是 Stop】
        /// Stop 会把播放位置归零。玩家按 M 静音再按 M 取消，风声会从头开始 ——
        /// 而 12 秒循环的"从头"恰好是交叉淡化的接缝处，等于每次切静音都送一次
        /// A-9 要否决的那个瑕疵。Pause 保留播放位置，取消静音时无缝接上。
        ///
        /// 【为什么自己记 _paused 而不是读 source.isPlaying】
        /// isPlaying 在 Play() 后的同一帧内返回值不稳定（取决于音频线程何时接手），
        /// 用它判断"现在是不是暂停着"会在开播那一帧误判成暂停并立刻 UnPause，
        /// 虽然无害但会让状态机的行为依赖时序。自己记账是确定的 ——
        /// 与 AudioDirector 不用 isPlaying 做并发计数是同一条理由。
        /// </summary>
        private void ApplyMuteGate()
        {
            if (_source == null || !_started)
            {
                return;
            }

            float bus = AudioConfig.MasterVolume * AudioConfig.ChannelVolume(Channel.Bgm);
            bool audible = !AudioConfig.Muted && bus > AudioConfig.VolumeEpsilon;

            if (!audible && !_paused)
            {
                _source.Pause();
                _paused = true;
            }
            else if (audible && _paused)
            {
                _source.UnPause();
                _paused = false;
            }
        }

        /// <summary>只警告一次。缺环境音会每帧命中失败分支，不去重会刷爆 Console。</summary>
        /// <param name="detail">具体原因。</param>
        private void WarnOnce(string detail)
        {
            if (_warned)
            {
                return;
            }
            _warned = true;
            Debug.LogWarning(AudioConfig.LogPrefix + " " + detail + "。→ 已静音降级，游戏可继续运行。");
        }
    }
}
