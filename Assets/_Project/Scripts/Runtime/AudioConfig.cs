// -----------------------------------------------------------------------------
// AudioConfig.cs —— 音效系统里「会被横向比较」的全部数字（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译、未经实听**。
// 引用 UnityEngine，不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【为什么需要它，以及它的边界在哪】
// 与 HitFeedbackConfig 是同一条房规：任何一个可调数字在全仓只允许出现一次。
// 但音频比手感多出一层麻烦 —— 一个音效内部有几十个 DSP 数字（滤波截止、扫频
// 端点、泛音比、LFO 频率……）。如果把它们也全塞进来，这张表会从「一屏能横向
// 对比 13 个音效」退化成「一份 500 行的、按音效分段的流水账」，**恰好摧毁房规
// 想要的那个性质**。所以分界线是这一条（架构 §7.1）：
//
//   · 会被横向比较的 → 进本文件。
//     「blood_lotus 是不是比 circle_burst 长？」「dodge_roll 的增益是不是全场
//     最低？」—— 这种问题必须能在同一屏里用眼睛回答。
//   · 不会被横向比较的 → 留在 SfxRecipes 的 private const。
//     「blood_lotus 的 LFO 是 6 Hz」—— 没有人会拿它跟 boss_death 的梳状混响
//     延迟做比较。它定义的是**这个声音本身**，属于 CombatView.DefaultFlashDuration
//     那一类有据例外。
//
// 房规本身不打折：右边那些数字同样只允许出现一次，只是「这一次」放在配方里。
//
// 【明确不要什么：Audio Mixer】
// .mixer 是二进制资产，破「零资产、全程序化生成」的基调；更实际的问题是
// Mixer 的参数躺在资产里 **grep 不到** —— 与 HitFeedbackConfig 拒绝
// ScriptableObject 的第 (c) 条理由一字不差。三级音量用三个 float 相乘就够了。
//
// 【★ 13 行 SfxSpec 表：key 一个字符都不许改】
// 表里的 key 是内核**原样传出**的字符串，本表对命名规则完全无知 —— sfx_enemy_hit
// 和 dodge_roll 在它眼里没有任何区别。这就是为什么「命名不统一」在这里不构成
// 成本：不一致的命名从来不是问题，只有「假设了命名规则的代码」才是问题，
// 而我们不写那种代码。**不要顺手给 dodge_roll 补上 skill_ 前缀** —— 那是既成
// 事实，改了不会报错、不会崩，只会安静地没声音，是最难查的一类 bug。
//
// 四个技能 key 一律引用 Xianxia.Combat.SkillConfig 的常量而非字面量：内核改了
// 常量值这里跟着变，内核删了常量这里编译失败。让编译器替人做逐字符核对。
// 其余 8 个在内核里是裸字面量（CombatEventsUnity.cs:124/143/160/177/196/216），
// 无常量可引，已逐字符人工核对过两遍。
//
// 【为什么不用全限定名之外的写法引 SkillConfig】
// 本文件同时 using UnityEngine 与需要 Xianxia.Combat 的类型。SpriteFactory.cs
// 的文件头记着一次 CS0104 二义事故（System.Object vs UnityEngine.Object）。
// 这里用全限定名 Xianxia.Combat.SkillConfig.XXX，从根上排除同类风险，
// 代价只是四处多打二十个字符。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    // =========================================================================
    // 类型定义（架构 §5.1）
    // =========================================================================

    /// <summary>
    /// 配方种类。每一个枚举值在 <see cref="SfxRecipes"/> 里对应一个**同名**的
    /// 私有静态函数，这条命名约定是 code review 检查项 —— 名字对不上就说明
    /// 有配方漏实现了，而漏实现的症状是「安静地没声音」。
    /// </summary>
    public enum RecipeKind
    {
        /// <summary>敌人被命中（清脆短促）。</summary>
        EnemyHit,

        /// <summary>玩家被命中（钝、闷、带下滑）。</summary>
        PlayerHurt,

        /// <summary>杂兵死亡（溃散，无瞬态）。</summary>
        EnemyDeath,

        /// <summary>BOSS 死亡（低频冲击 + 石磬泛音 + 混响）。</summary>
        BossDeath,

        /// <summary>BOSS 阶段转换（上行滑音 + 迟到的钟击）。</summary>
        BossPhase,

        /// <summary>BOSS 召唤（五颗高频颗粒，全程零低频）。</summary>
        BossSummon,

        /// <summary>BOSS 冲击波（硬瞬态 + 低频推力 + 带宽张开）。</summary>
        BossShockwave,

        /// <summary>韧性打破（咔 + 裂纹 + 余韵）。</summary>
        PoiseBreak,

        /// <summary>水剑斩（薄，全程零低频）。</summary>
        BasicSlash,

        /// <summary>法阵冲击（带宽快速张开）。</summary>
        CircleBurst,

        /// <summary>血莲侵蚀（粘稠，6 Hz 颤动）。</summary>
        BloodLotus,

        /// <summary>踏雪闪避（掠过窗）。</summary>
        DodgeRoll,

        /// <summary>环境衬底（12 s 无缝循环风声）。</summary>
        WindLoop
    }

    /// <summary>
    /// 混音通道。本期 BGM 通道只跑环境衬底；R-16 旋律 BGM 挂起，但枚举位预留 ——
    /// 将来接旋律 BGM 只需多一条 <see cref="SfxSpec"/>，AudioDirector 与
    /// AmbienceLayer 都不用改结构。
    /// </summary>
    public enum Channel
    {
        /// <summary>战斗音效。</summary>
        Sfx,

        /// <summary>背景 / 环境层。</summary>
        Bgm
    }

    /// <summary>
    /// 归一化模式。
    ///
    /// 【为什么必须分两种 —— 这是一个真实的 DSP 陷阱，不是过度设计】
    /// 噪声的**峰值**由极少数离群样本决定，与人耳感知的响度几乎无关。一段风声
    /// 按峰值归一化到 0.89，听起来会响得离谱，因为它的能量密度远高于一个
    /// 100 ms 的瞬态。噪声类内容必须按 RMS 归一化 —— 这是「响度」与「峰值」
    /// 在噪声信号上解耦的直接后果。
    /// </summary>
    public enum NormalizeMode
    {
        /// <summary>峰值归一化。用于 12 个瞬态类音效。</summary>
        Peak,

        /// <summary>RMS 归一化，之后再过一遍软削波兜住偶发尖峰。用于环境衬底。</summary>
        Rms
    }

    /// <summary>
    /// 一个音效的全部静态属性。纯数据，无行为。
    ///
    /// 【为什么是 readonly struct 而不是 class】
    /// 这张表在预合成期被读 13 次、运行期每次 Play 被读一次，全程只读。
    /// readonly struct 让「有人在运行时改了表里的值」在**编译期**就不可能发生，
    /// 而 class 只能靠约定。13 个元素的数组，装箱/拷贝成本可以忽略。
    /// </summary>
    public readonly struct SfxSpec
    {
        /// <summary>内核传出的字符串，一字不改、不加前缀、不 ToLower()。</summary>
        public readonly string Key;

        /// <summary>配方种类，决定走 <see cref="SfxRecipes"/> 的哪一条分支。</summary>
        public readonly RecipeKind Kind;

        /// <summary>采样率（Hz）。仅 22050 / 44100 两个值经过验证，其余未验证。</summary>
        public readonly int SampleRate;

        /// <summary>时长（秒）。配方内部按需转样本数：n = (int)(sec × rate)。</summary>
        public readonly float DurationSec;

        /// <summary>单音效增益，线性 [0,1]，不是 dB。归一化之后它才真正表示「相对其他音效有多响」。</summary>
        public readonly float Gain;

        /// <summary>同 key 节流窗口（秒）。0 = 不节流。</summary>
        public readonly float ThrottleSec;

        /// <summary>是否豁免 voice 抢占。只有 BOSS 死亡与冲击波为 true。</summary>
        public readonly bool ExemptPreempt;

        /// <summary>归一化模式。</summary>
        public readonly NormalizeMode NormMode;

        /// <summary>归一化目标（Peak 模式为峰值，Rms 模式为均方根），线性 [0,1]。</summary>
        public readonly float NormTarget;

        /// <summary>是否循环播放。本期只有环境衬底为 true。</summary>
        public readonly bool Loop;

        /// <summary>所属混音通道。</summary>
        public readonly Channel Bus;

        /// <summary>
        /// 构造一条音效规格。
        ///
        /// 【参数顺序刻意与架构 §2.9 的示例一致】
        /// 前八个是必填且逐音效都不同的，后三个绝大多数音效取默认值 ——
        /// 只有环境衬底这一行需要显式写出来。这样 13 行表里 12 行的形状完全
        /// 相同，多出来的那一行一眼就能看见，这正是它应该被看见的。
        /// </summary>
        /// <param name="key">内核传出的音效 key，原样填入。</param>
        /// <param name="kind">配方种类。</param>
        /// <param name="sampleRate">采样率（Hz）。</param>
        /// <param name="durationSec">时长（秒）。</param>
        /// <param name="gain">单音效增益，线性 [0,1]。</param>
        /// <param name="throttleSec">同 key 节流窗口（秒），0 = 不节流。</param>
        /// <param name="exemptPreempt">是否豁免 voice 抢占。</param>
        /// <param name="normTarget">归一化目标，线性 [0,1]。</param>
        /// <param name="normMode">归一化模式，默认峰值。</param>
        /// <param name="loop">是否循环，默认否。</param>
        /// <param name="bus">混音通道，默认 Sfx。</param>
        public SfxSpec(
            string key,
            RecipeKind kind,
            int sampleRate,
            float durationSec,
            float gain,
            float throttleSec,
            bool exemptPreempt,
            float normTarget,
            NormalizeMode normMode = NormalizeMode.Peak,
            bool loop = false,
            Channel bus = Channel.Sfx)
        {
            Key = key;
            Kind = kind;
            SampleRate = sampleRate;
            DurationSec = durationSec;
            Gain = gain;
            ThrottleSec = throttleSec;
            ExemptPreempt = exemptPreempt;
            NormMode = normMode;
            NormTarget = normTarget;
            Loop = loop;
            Bus = bus;
        }
    }

    // =========================================================================
    // 常量表
    // =========================================================================

    /// <summary>
    /// 音效系统的常量表。纯数据 + 极少量纯函数查询，无生命周期。
    ///
    /// 【为什么是 static class 而不是单例 MonoBehaviour】
    /// 与 HitFeedbackConfig 同一条理由：这里没有一个字段需要 Update，
    /// 给纯常量套一个 MonoBehaviour 只会引入「实例还没 Awake 就被读」的时序风险。
    /// </summary>
    public static class AudioConfig
    {
        // =====================================================================
        // 日志前缀
        // =====================================================================

        /// <summary>
        /// 全部音频日志的前缀，与既有的 [T2] / [Combat] / [T3] 对齐，
        /// 便于在 Console 里一键过滤出音频这一路。
        /// </summary>
        public const string LogPrefix = "[T2·Audio]";

        // =====================================================================
        // 三通道音量 + 静音 —— 全仓第 4~7 个可写 static
        // =====================================================================
        //
        // 【★ 复位责任归属】
        // 这四个字段是全仓继 FeedbackClock.Frozen、HitFeedbackConfig.FeedbackIntensity、
        // MainMenuHud.SkipOnNextLoad 之后的第 4~7 个全局可写 static，纪律与前三者
        // 逐条对齐，不能只靠「用完记得改回来」：
        //   1. ResetStatics() 带 [RuntimeInitializeOnLoadMethod(SubsystemRegistration)]，
        //      专治「Enter Play Mode without Domain Reload」下静态字段跨 PlayMode
        //      存活 —— 上一局把音量拖到 0，下一局进游戏就是「全哑、还一条报错都没有」。
        //      本工程已经因为 MainMenuHud.SkipOnNextLoad 这个同构的 static 吃过一次
        //      亏（MENU09 假红），这里不犯第二次。
        //   2. EditMode / PlayMode 测试的 SetUp / TearDown 必须**无条件**自己复位 ——
        //      [RuntimeInitializeOnLoadMethod] 在 EditMode 测试里根本不触发。
        //      那是测试文件的第一纪律，不是本类的责任，两层各管各的，缺一不可。

        /// <summary>
        /// 总音量，线性 [0,1]。默认 0.80。
        ///
        /// 【为什么不是 1.0】
        /// 留 20% 顶部余量给「16 路叠加」这个最坏情况（架构 §2.6）。三级防护之后
        /// 理论最坏仍可能轻微越界，这 0.2 是最后一层、也是最便宜的一层保险。
        /// </summary>
        public static float MasterVolume = MasterVolumeDefault;

        /// <summary>战斗音效通道音量，线性 [0,1]。默认 0.90。</summary>
        public static float SfxVolume = SfxVolumeDefault;

        /// <summary>
        /// 背景 / 环境通道音量，线性 [0,1]。默认 0.50。
        /// 衬底的作用是「填掉死寂」，不是「被听见」，所以比 Sfx 低整整一档。
        /// </summary>
        public static float BgmVolume = BgmVolumeDefault;

        /// <summary>
        /// 全局静音（M 键切换）。
        ///
        /// 【★ 语义是「跳过 Play 调用本身」，不是「把音量设成 0」】
        /// 音量 0 的 AudioSource.Play() 仍然占用一路 voice、仍然要解码、仍然进
        /// 混音总线。跳过 Play 则连 voice 都不分配，节流状态也不更新。
        /// R-06 要的就是这个语义，A-13 / A-14 验收的就是「静音后一切照常且无开销」。
        /// </summary>
        public static bool Muted = MutedDefault;

        /// <summary><see cref="MasterVolume"/> 的出厂默认值。</summary>
        public const float MasterVolumeDefault = 0.80f;

        /// <summary><see cref="SfxVolume"/> 的出厂默认值。</summary>
        public const float SfxVolumeDefault = 0.90f;

        /// <summary><see cref="BgmVolume"/> 的出厂默认值。</summary>
        public const float BgmVolumeDefault = 0.50f;

        /// <summary><see cref="Muted"/> 的出厂默认值。</summary>
        public const bool MutedDefault = false;

        /// <summary>
        /// 通道音量的「视同为零」阈值。
        ///
        /// 【为什么用 epsilon 而不是 == 0f】
        /// 将来的音量滑条（P2 R-17）拖到底可能停在 1e-8 这种值上，浮点相等判断
        /// 会漏掉，于是「拖到底了还有声音」。这类 bug 只在滑条上出现，
        /// 键盘静音测不出来。
        /// </summary>
        public const float VolumeEpsilon = 1e-4f;

        // =====================================================================
        // PlayerPrefs 持久化（R-11）
        // =====================================================================

        /// <summary>PlayerPrefs 键名：总音量。</summary>
        public const string PrefKeyMaster = "xianxia.audio.master";

        /// <summary>PlayerPrefs 键名：音效通道音量。</summary>
        public const string PrefKeySfx = "xianxia.audio.sfx";

        /// <summary>PlayerPrefs 键名：背景通道音量。</summary>
        public const string PrefKeyBgm = "xianxia.audio.bgm";

        /// <summary>PlayerPrefs 键名：静音开关（0 / 1，PlayerPrefs 无 bool）。</summary>
        public const string PrefKeyMuted = "xianxia.audio.muted";

        /// <summary>
        /// 把当前四个可写 static 写回 PlayerPrefs 并落盘。
        ///
        /// 【为什么显式调 Save()】
        /// PlayerPrefs 默认在 OnApplicationQuit 才落盘。而本工程最常见的退出方式
        /// 是在 Editor 里直接点停止播放 —— 那条路径**不保证**触发 OnApplicationQuit。
        /// 不显式 Save，调音时改的音量下次进 Play 就没了，而人会以为是持久化没写对。
        ///
        /// 【为什么整段 try/catch】
        /// PlayerPrefs 在某些平台（只读文件系统 / 权限受限）会抛异常。音量存不上
        /// 是可以接受的降级，把游戏掀了不是。
        /// </summary>
        public static void SavePrefs()
        {
            try
            {
                PlayerPrefs.SetFloat(PrefKeyMaster, MasterVolume);
                PlayerPrefs.SetFloat(PrefKeySfx, SfxVolume);
                PlayerPrefs.SetFloat(PrefKeyBgm, BgmVolume);
                PlayerPrefs.SetInt(PrefKeyMuted, Muted ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(LogPrefix + " 音量持久化写入失败，本次设置不会保留：" + e.Message);
            }
        }

        /// <summary>
        /// 从 PlayerPrefs 读回四个可写 static；读不到任何一项就取该项的出厂值。
        ///
        /// 【为什么逐项 fallback 而不是「有一项缺就全用出厂值」】
        /// 将来加第五个设置项时，老玩家的 Prefs 里没有那一项。整体 fallback 会让
        /// 他们**已经调好的前四项**一起被重置 —— 一次版本升级清掉玩家设置，
        /// 是那种事后才被发现、且无法补救的问题。
        /// </summary>
        public static void LoadPrefs()
        {
            try
            {
                MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefKeyMaster, MasterVolumeDefault));
                SfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefKeySfx, SfxVolumeDefault));
                BgmVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefKeyBgm, BgmVolumeDefault));
                Muted = PlayerPrefs.GetInt(PrefKeyMuted, MutedDefault ? 1 : 0) != 0;
            }
            catch (System.Exception e)
            {
                // 读失败就退回出厂值。绝不能让一个坏掉的 Prefs 文件把音频系统卡住。
                MasterVolume = MasterVolumeDefault;
                SfxVolume = SfxVolumeDefault;
                BgmVolume = BgmVolumeDefault;
                Muted = MutedDefault;
                Debug.LogWarning(LogPrefix + " 音量持久化读取失败，已退回出厂值：" + e.Message);
            }
        }

        /// <summary>
        /// 复位本类的全部可写静态状态。由 Unity 在每次进入运行时自动调用，
        /// 也可以被测试显式调用来隔离用例之间的污染。
        ///
        /// 【为什么是 SubsystemRegistration 而不是 AfterSceneLoad】
        /// 与 FeedbackClock.ResetStatics / HitFeedbackConfig.ResetStatics 取同一档，
        /// 理由也同一条：SubsystemRegistration 是 RuntimeInitializeLoadType 里
        /// **最早**的一档，早于任何 Awake。放晚了就会出现「第一个 Awake 已经读到
        /// 脏的音量」的窗口 —— 而 AudioDirector.OnEnable 恰恰就在那个窗口里，
        /// 它一旦读到 Muted == true，开局全程静音且没有任何报错。
        ///
        /// 【★ 与前三个 static 的一处差异：语义不是「恢复出厂值」】
        /// R-11 落地之后，本方法的语义变成「**从 PlayerPrefs 重新加载，读不到则取
        /// 出厂值**」。这正是 HitFeedbackConfig.cs 预留的那句「将来接玩家设置持久化
        /// 时改这里」所描述的情形，本期是它第一次真正发生。
        /// 复位点仍然只有这一个，**不要在别处再开第二个入口**。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetStatics()
        {
            LoadPrefs();
        }

        // =====================================================================
        // voice 池与四层闸门（供 T03 的 AudioDirector 使用）
        // =====================================================================

        /// <summary>
        /// voice 池大小，固定 16 路。
        ///
        /// 【为什么定长而不是动态扩容】
        /// 动态扩容意味着 AddComponent&lt;AudioSource&gt;() 发生在最忙的那一帧
        /// （围攻爆发时），这正是最不能承受额外开销的时刻。定长池把全部分配成本
        /// 前置到装配期，运行期零分配 —— 与 DamagePopupLayer 的 12 个常驻槽位、
        /// HitEffects 的 24 长看板是同一条理由。
        /// </summary>
        public const int VoicePoolSize = 16;

        /// <summary>同一个 key 的最大并发发声数（R-05 ②）。超过就丢弃新声。</summary>
        public const int PerKeyVoiceLimit = 3;

        /// <summary>
        /// 连触递减的公比：第 n 次连续触发乘 0.7^(n-1)。
        /// 让「同一个音连着响」在听感上自然退到背景，而不是每一下都同样大声。
        /// </summary>
        public const float ConsecutiveDecay = 0.7f;

        /// <summary>连触递减的地板。再连也不会低于 40%，否则第五下之后等于消失。</summary>
        public const float ConsecutiveFloor = 0.40f;

        /// <summary>
        /// 连触计数的复位窗口（秒）。超过 200 ms 没再触发就认为「这一串结束了」，
        /// 计数归零。吃 Time.deltaTime，**不是** FeedbackClock.Delta（见下方规约）。
        /// </summary>
        public const float ConsecutiveResetSec = 0.20f;

        /// <summary>音高抖动下界（倍率）。</summary>
        public const float PitchJitterMin = 0.94f;

        /// <summary>
        /// 音高抖动上界（倍率）。
        ///
        /// 【它有两个作用，第二个才是硬需求】
        /// ① 消除「机关枪感」（R-12 的原始意图）。
        /// ② **让同 key 的多路叠加在相位上去相关**。完全相干叠加的 N 路振幅是 N 倍，
        ///    完全不相干只有 √N 倍 —— 16 路差 4 倍。音高抖动让两路同 key voice 在
        ///    几十毫秒后就完全错开相位，把最坏情况从 N 拉到 √N。这是防削波的第三级。
        /// </summary>
        public const float PitchJitterMax = 1.06f;

        /// <summary>随机增益下界。比音高抖动窄得多，只是为了打破「完全一致」的机械感。</summary>
        public const float GainJitterMin = 0.92f;

        /// <summary>随机增益上界。</summary>
        public const float GainJitterMax = 1.00f;

        /// <summary>
        /// 并发补偿系数：gain × 1 / (1 + ConcurrencyDuck × (active - 1))。
        ///
        /// 【这是一个不需要 Mixer 的迷你压缩器】
        /// 1 路 → 1.00，4 路 → 0.85，8 路 → 0.70，16 路 → 0.53。
        /// 围攻越密，每一声越轻 —— 这正好也是「密集的雨点而不是噪音墙」在数学上
        /// 的表达：雨点之所以是雨点，就是因为单点很轻。
        ///
        /// 【★ 这是实测削波时的唯一旋钮】
        /// 若本地实听发现围攻爆音，把它从 0.06 调到 0.10（16 路补偿降到 0.40）。
        /// 改这一个数字，不用动任何合成代码。
        /// </summary>
        public const float ConcurrencyDuck = 0.06f;

        /// <summary>并发补偿的地板。再密也不会低于 45%，否则围攻时等于听不见。</summary>
        public const float ConcurrencyFloor = 0.45f;

        /// <summary>
        /// 静音切换键。
        /// 【已复核全仓 KeyCode.* ，M 无任何命中】全仓实际占用：
        /// A / D / S / W / K / J / L / R / Space / Tab / F1 / Escape / Return /
        /// KeypadEnter / LeftShift / RightShift / 四个方向键 / Mouse0-2 / JoystickButton0。
        /// </summary>
        public const KeyCode MuteKey = KeyCode.M;

        // =====================================================================
        // 环境衬底（R-10，供 T05 的 AmbienceLayer 使用）
        // =====================================================================

        /// <summary>环境衬底的 key。它不来自内核，是本系统自己起的名字。</summary>
        public const string AmbienceKey = "amb_wind_loop";

        /// <summary>
        /// 环境衬底的循环长度（秒）。
        ///
        /// 【为什么是 12 而不是 8 或 16】
        /// 12 能被 1/2/3/4/6 整除，给 LFO 周期分配留了最多余地 —— 而「所有 LFO
        /// 周期必须整除循环总长」是无缝循环的**必要条件之一**（另一个是等功率
        /// 交叉淡化）。若嫌内存大（它一个人占全部样本量的 67%），改成 8 s，
        /// LFO 自动跟着变，因为配方里的三条 LFO 频率是从本常量**算出来**的，
        /// 不是写死的。
        /// </summary>
        public const float AmbienceLoopSec = 12.0f;

        /// <summary>
        /// 环境衬底首尾交叉淡化长度（秒）。
        /// 200 ms 足够盖住噪声的不连续，又短到不会吃掉可辨识的内容。
        /// </summary>
        public const float AmbienceCrossfadeSec = 0.20f;

        /// <summary>环境衬底的 RMS 归一化目标。起调值，待本地实听校准。</summary>
        public const float AmbienceRmsTarget = 0.12f;

        /// <summary>环境衬底 duck 到的电平（30%）。</summary>
        public const float AmbienceDuckLevel = 0.30f;

        /// <summary>
        /// 环境衬底 duck / 恢复的淡化时长（秒）。
        /// 吃 Time.deltaTime —— 它虽然是「表现」，但必须在**菜单暂停期间继续走**
        /// （R-09 要求暂停时完成 duck 淡入），所以不能用任何会被冻住的时钟。
        /// </summary>
        public const float AmbienceDuckFadeSec = 0.30f;

        /// <summary>
        /// 是否在预合成阶段就把环境衬底一起合成出来。
        ///
        /// 【这是一个逃生阀，刻意做成 const 而不是可写 static】
        /// 环境衬底一个人占了全部样本量的 67%（265k / 393k）。如果本地实测启动有
        /// 可感知卡顿，把这一个 bool 改成 false 就能砍掉三分之二的合成成本，
        /// 其余 12 个 P0 音效仍然预合成，**不需要动任何合成代码**。
        ///
        /// 【为什么不做成可写 static】
        /// 房规里可写 static 的数量本身就是负债 —— 每多一个就多一份「复位纪律」
        /// 要维护（见本文件上方那段）。这个旋钮的使用场景是「改源码重编译」，
        /// 不是「运行时切换」，const 完全够用，还顺带保住了 static 只有 4 个这条
        /// 可以一眼数清的性质。
        /// </summary>
        public const bool PrewarmAmbience = true;

        // =====================================================================
        // 合成随机流（架构 §3.1 末）
        // =====================================================================

        /// <summary>
        /// 合成用随机流的基种子。
        ///
        /// 【★ 为什么合成必须用固定种子，而不是 AudioDirector 那条运行期流】
        /// 运行期那条流（音高抖动）每帧被抽用，序列不可复现。而合成必须可复现 ——
        /// 否则用户改一个 KeyGain 重进 Play，听到的是「改了参数 + 换了一段噪声」
        /// 的叠加效果，**根本无法判断刚才那个数字改对没有**。
        /// 调音是一个对比过程，被比较的两次之间只允许有一个变量。
        /// </summary>
        public const int SynthSeed = 0x5A17;

        /// <summary>
        /// 为某个 key 造一条**确定性**的合成随机流。
        ///
        /// 【★ 为什么不用 key.GetHashCode()】
        /// 架构 §3.1 末尾已经预警过：string.GetHashCode() 在 .NET Core / Mono 上
        /// 启用了随机化哈希种子，**跨进程不稳定**。用它当种子的直接症状是
        /// 「每次启动音色略有不同」—— 而这恰恰摧毁了固定种子存在的全部理由。
        /// 与其等实测发现再改，不如一开始就用一个自己写的确定性哈希。
        /// 这里用 FNV-1a：五行、无分配、跨平台逐位一致。
        /// </summary>
        /// <param name="key">音效 key。null 或空串按空串处理，仍然返回可用的流。</param>
        /// <returns>该 key 专属的、每次运行都完全相同的随机流。</returns>
        public static System.Random MakeSynthRandom(string key)
        {
            return new System.Random(SynthSeed ^ Fnv1a(key));
        }

        /// <summary>
        /// FNV-1a 32 位哈希。确定性、跨进程稳定，专门用来替代不稳定的
        /// <c>string.GetHashCode()</c>。
        /// </summary>
        /// <param name="s">待哈希的字符串，允许为 null。</param>
        /// <returns>32 位哈希值（以 int 承载，可能为负，用作种子无妨）。</returns>
        public static int Fnv1a(string s)
        {
            unchecked
            {
                // 2166136261 / 16777619 是 FNV-1a 32 位的标准偏移基与质数，不要改。
                uint hash = 2166136261u;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        hash ^= s[i];
                        hash *= 16777619u;
                    }
                }
                return (int)hash;
            }
        }

        // =====================================================================
        // ★ 13 行 SfxSpec 表 —— key 一个字符都不许改
        // =====================================================================
        //
        // 列序：key / Kind / 采样率 / 时长 / 增益 / 节流 / 豁免抢占 / 归一化目标
        //
        // 【采样率为什么不统一】
        // 只有三个音效取 44100：sfx_enemy_hit（每秒响十几次，最需要清脆）、
        // sfx_poise_break（裂纹在 6.3 kHz，22050 的奈奎斯特会削掉它）、
        // skill_basic_slash（4 kHz 起扫，低采样率下会发闷）。其余 22050 省一半内存。
        // 实测发闷就改一个数字。
        //
        // 【★ 两处与 PRD R-05 ① 的有意偏离，PM 已知悉】
        // sfx_player_hurt 给了 70 ms 节流、sfx_poise_break 给了 60 ms，而 PRD 的
        // 高频清单里没有它们。理由：A-10 的验收场景是「引一群敌人围攻、持续挨打
        // 10 秒」—— 在那个场景里玩家受击音**就是**高频音，而它长达 185 ms，
        // 不节流时叠三四层就是一堵墙。若 PM 认为「我挨打」必须每次都响
        // （可读性优先于听感），把这两个值改成 0f 即可，一个数字。

        /// <summary>
        /// 全部 13 条音效规格。索引由 <see cref="TryGetSpec"/> 惰性建立。
        ///
        /// 【为什么是数组而不是直接写成 Dictionary 初始化器】
        /// 数组能保证「一行一个音效、列对齐」的可读性 —— 而这张表存在的全部意义
        /// 就是能一眼横向对比。Dictionary 初始化器的花括号会把每一行撑成两行。
        /// </summary>
        public static readonly SfxSpec[] Table = new SfxSpec[]
        {
            // ---- 出口 ①：CombatEventsUnity.PlaySfx（8 个，内核里是裸字面量，已逐字符核对）----
            new SfxSpec("sfx_enemy_hit",      RecipeKind.EnemyHit,      44100, 0.100f, 0.85f, 0.050f, false, 0.89f),
            new SfxSpec("sfx_player_hurt",    RecipeKind.PlayerHurt,    22050, 0.185f, 1.00f, 0.070f, false, 0.89f),
            new SfxSpec("sfx_enemy_death",    RecipeKind.EnemyDeath,    22050, 0.300f, 0.80f, 0.000f, false, 0.85f),
            new SfxSpec("sfx_boss_death",     RecipeKind.BossDeath,     22050, 1.150f, 1.00f, 0.000f, true,  0.95f),
            new SfxSpec("sfx_boss_phase",     RecipeKind.BossPhase,     22050, 0.850f, 0.90f, 0.000f, false, 0.89f),
            new SfxSpec("sfx_boss_summon",    RecipeKind.BossSummon,    22050, 0.500f, 0.75f, 0.000f, false, 0.89f),
            new SfxSpec("sfx_boss_shockwave", RecipeKind.BossShockwave, 22050, 0.650f, 1.00f, 0.000f, true,  0.95f),
            new SfxSpec("sfx_poise_break",    RecipeKind.PoiseBreak,    44100, 0.220f, 0.90f, 0.060f, false, 0.90f),

            // ---- 出口 ②：CombatEventsT3Unity.PlaySfx（4 个，★ 必须引常量，禁止字面量）----
            // 内核改了常量值这里跟着变；内核删了常量这里编译失败。
            // 注意 SKILL_DODGE_ROLL 的实际值是 "dodge_roll"，**没有 skill_ 前缀** ——
            // 这是既成事实，正因为这里引的是常量，才不会有人「顺手统一」出事。
            new SfxSpec(Xianxia.Combat.SkillConfig.SKILL_BASIC_SLASH,  RecipeKind.BasicSlash,  44100, 0.150f, 0.55f, 0.050f, false, 0.72f),
            new SfxSpec(Xianxia.Combat.SkillConfig.SKILL_CIRCLE_BURST, RecipeKind.CircleBurst, 22050, 0.425f, 0.80f, 0.000f, false, 0.86f),
            new SfxSpec(Xianxia.Combat.SkillConfig.SKILL_BLOOD_LOTUS,  RecipeKind.BloodLotus,  22050, 0.600f, 0.85f, 0.000f, false, 0.88f),
            new SfxSpec(Xianxia.Combat.SkillConfig.SKILL_DODGE_ROLL,   RecipeKind.DodgeRoll,   22050, 0.250f, 0.45f, 0.050f, false, 0.75f),

            // ---- P1 追加：环境衬底。全表唯一一行形状不同的，它应该被一眼看见 ----
            // 它是唯一走 Rms 归一化、唯一 Loop、唯一走 Bgm 通道的一条。
            new SfxSpec(AmbienceKey, RecipeKind.WindLoop, 22050, AmbienceLoopSec, 1.00f, 0.000f, true, AmbienceRmsTarget,
                        NormalizeMode.Rms, true, Channel.Bgm)
        };

        /// <summary>
        /// key → <see cref="Table"/> 下标。惰性建立一次。
        ///
        /// 【为什么不参与 ResetStatics】
        /// 它是从 readonly 且**永不变**的 Table 派生出来的纯缓存。即使在
        /// 「关闭 Domain Reload」的情况下跨 PlayMode 存活，内容也一定是正确的。
        /// 复位它没有收益，只会让「哪些 static 需要复位」这份清单变长。
        /// </summary>
        private static Dictionary<string, int> _index;

        /// <summary>全部 key 的缓存数组，供预合成遍历。惰性建立一次，理由同 <see cref="_index"/>。</summary>
        private static string[] _allKeys;

        /// <summary>
        /// 按 key 查规格。未注册的 key 返回 false，由调用方静默降级并只警告一次。
        /// </summary>
        /// <param name="key">内核传出的音效 key。</param>
        /// <param name="spec">查到的规格；未查到时为 default。</param>
        /// <returns>是否查到。</returns>
        public static bool TryGetSpec(string key, out SfxSpec spec)
        {
            spec = default(SfxSpec);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            EnsureIndex();

            int i;
            if (!_index.TryGetValue(key, out i))
            {
                return false;
            }

            spec = Table[i];
            return true;
        }

        /// <summary>
        /// 全部已注册的 key（含环境衬底），顺序与 <see cref="Table"/> 一致。
        /// 供 AudioClipFactory.Prewarm 遍历。返回的是内部缓存数组本身，
        /// **调用方不得修改**（本工程内只读遍历，不额外拷贝以免每次预热多一次分配）。
        /// </summary>
        /// <returns>key 数组。</returns>
        public static string[] AllKeys()
        {
            EnsureIndex();
            return _allKeys;
        }

        /// <summary>
        /// 取某个通道的当前音量（尚未乘 Master）。
        /// </summary>
        /// <param name="bus">通道。</param>
        /// <returns>线性音量 [0,1]。</returns>
        public static float ChannelVolume(Channel bus)
        {
            return bus == Channel.Bgm ? BgmVolume : SfxVolume;
        }

        /// <summary>
        /// 惰性建立 key 索引与 key 数组。
        ///
        /// 【为什么在这里就把重复 key 拦下来】
        /// 表里出现两行同 key 时，Dictionary.Add 会抛异常。用 Add 而不是索引器
        /// 赋值是**故意**的：重复 key 意味着有人复制粘贴时忘了改，而后一行会
        /// 静默盖掉前一行 —— 那正是「不报错、不崩溃、只是某个音效变成了另一个」
        /// 的事故形态。宁可在预合成期当场炸掉。
        /// </summary>
        private static void EnsureIndex()
        {
            if (_index != null)
            {
                return;
            }

            var map = new Dictionary<string, int>(Table.Length);
            var keys = new string[Table.Length];
            for (int i = 0; i < Table.Length; i++)
            {
                map.Add(Table[i].Key, i);
                keys[i] = Table[i].Key;
            }

            _index = map;
            _allKeys = keys;
        }

        // =====================================================================
        // ★ 时间源使用规约（架构 §7.4，code review 检查项，不是建议）
        // =====================================================================
        //
        // 音频系统的全部内部计时**一律吃 Time.deltaTime**，
        // Assets/_Project/Scripts/Runtime/ 下 Audio*.cs 与 Sfx*.cs 中
        // FeedbackClock 的 grep 命中数必须为 0。
        //
        // 这不是疏漏，是设计。理由有三条，第三条是一个会稳定复现的 bug：
        //
        // ① 顿帧的时候，声音恰恰是唯一还在动的东西 —— 这是它的作用，不是它的 bug。
        //    hitstop 之所以成立，是因为画面定住的那 50~120 ms 里听觉在继续推进。
        //    「没有咔的那一声，顿帧就只是画面卡了一下。」
        //
        // ② 技术上也停不了，硬停会更难听。sfx_enemy_hit 全长只有 100 ms，顿帧一次
        //    就把它整个盖住；在瞬态正中间 Pause/UnPause，波形出现阶跃 → 一声咔哒，
        //    正是 A-10 明令禁止的那种声音。
        //
        // ③ 内部记账用冻结时钟会导致**连击静音**：
        //    t=0    命中 → 播音（节流窗口 50 ms 起算）→ 同一帧顿帧 90 ms
        //    t=0~90 Frozen ⇒ Delta ≡ 0 ⇒ SinceLastPlay 一动不动，永远停在 0
        //    t=90   解冻，连击第二下到了 ⇒ SinceLastPlay 仍是 0 < 0.05 ⇒ 判定
        //           「仍在节流窗口内」⇒ 静音丢弃
        //    顿帧时长（0.05~0.12 s）系统性地长于节流窗口（0.05 s），所以这不是
        //    偶发竞态，而是**每一次连击都会稳定复现**：第二下必然被吃掉。
        //    症状是「连击时声音断断续续」，而排查的人会去看 voice 池、去看节流值，
        //    很难想到是时钟源选错了。
    }
}
