// -----------------------------------------------------------------------------
// HitFeedbackConfig.cs —— 受击反馈四件套的全部可调数字（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【明确不要什么：ScriptableObject 配置资产】
// 手感参数确实迟早要交给策划调，但那是 P2 的事（R-13）。现在上 SO 会带来
// 三样立刻要还的债：(a) 多一个 .asset 二进制资产，破坏本工程"零资产、
// 全程序化生成"的基调；(b) 每个组件都要多一条"配置没注入"的空引用分支；
// (c) 参数散在资产里就 grep 不到了，改一个数字要开 Editor 才知道改没改对。
//
// 【明确不要什么：把数字散在各组件里当 private const】
// 屏震幅度写在 CameraShake、顿帧时长写在 Director、闪白时长写在 CombatView，
// 就再也没人能一眼看出"玩家挨打这一档整体有多重"。调手感是**跨四个表现**
// 一起调的，参数就必须摆在同一屏里。
//
// 【要什么：一张能一眼横向对比的常量表】
// 铁律：任何一个手感数字在全仓只允许出现一次。发现两处相同字面量立刻抽到这里。
// 唯一的有据例外是 CombatView.DefaultFlashDuration（0.08f）——它定义的是
// PlayHitFlash() 无参重载的**行为本身**，必须与调用方解耦，不能反向依赖 T2。
//
// 【为什么分玩家 / 敌人两套阈值——这不是过度设计】
// DifficultyBridge.cs:52 敌人基准 Hp = 22.0f，:151 玩家 PlayerHpMax = 260.0f，
// 相差 11.8 倍。若共用"8% HpMax 记重击"这一条线，敌人侧 8% × 22 = 1.76 点，
// 任何一次普攻都会越线 → 轻/重分档在敌人侧**完全退化成常量**，等于没分档。
// 所以两侧各一套线、各一套插值区间。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 受击反馈（hitstop / 屏震 / 闪白 / 飘字）的常量表。纯数据，无行为。
    ///
    /// 【为什么是 static class 而不是单例 MonoBehaviour】
    /// 这里没有一个字段需要生命周期、需要 Update、或者需要在运行时被写。
    /// 给纯常量套一个 MonoBehaviour 只会引入"实例还没 Awake 就被读"的时序风险。
    /// </summary>
    public static class HitFeedbackConfig
    {
        // =====================================================================
        // 全局总开关（R-09）
        // =====================================================================

        /// <summary>
        /// 全局反馈强度系数，统一缩放**屏震幅度**与 **hitstop 时长**。默认 1.0。
        ///
        /// 【为什么不缩放闪白与飘字】
        /// 闪白和飘字承载的是"可读性"——谁挨打了、挨了多少。调弱它们会损失信息。
        /// 屏震和顿帧承载的是"冲击力"，那才是众口难调、需要 A/B 的部分。
        ///
        /// 【为什么本期就要做】
        /// C-2 的主观验收要求当场在 0.6 / 0.8 / 1.0 三档之间比较，用眼睛而不是
        /// 用会议来决定水墨调性会不会被反馈带偏。没有这个系数就只能改代码重编译。
        /// 同时它也是将来"减弱镜头晃动"无障碍选项的唯一接口。
        ///
        /// 【为什么是 static 可写字段而不是 const】
        /// 就是为了能在运行时（Inspector 调试脚本 / 未来的设置面板）当场改。
        /// 它是**唯一**一个可写字段，其余全是只读常量。
        ///
        /// 【★ 复位责任归属 —— 与 FeedbackClock.Frozen 完全对齐】
        /// 本字段是全仓**第二个**全局可写 static，因此复位纪律必须与第一个
        /// （<c>FeedbackClock.Frozen</c>）逐条对齐，不能只靠"用完记得改回来"：
        ///   1. <see cref="ResetStatics"/>：带
        ///      [RuntimeInitializeOnLoadMethod(SubsystemRegistration)]，
        ///      专治「Enter Play Mode without Domain Reload」下静态字段跨
        ///      PlayMode 存活 —— 上一局调试时把它按到 0，下一局进游戏就是
        ///      "反馈全没了、还一条报错都没有"，排查成本极高。
        ///      本工程已经因为 MainMenuHud.SkipOnNextLoad 这个同构的 static
        ///      吃过一次亏（MENU09 假红），这里不犯第二次。
        ///   2. EditMode / PlayMode 测试的 SetUp / TearDown：
        ///      [RuntimeInitializeOnLoadMethod] **不会**在 EditMode 测试里触发，
        ///      所以测试侧必须自己无条件复位，那是测试文件的第一纪律，
        ///      不是本类的责任 —— 两层各管各的，缺一不可。
        /// 除以上两处，任何代码把它改脏之后都有义务自己还原。
        /// </summary>
        public static float FeedbackIntensity = 1.0f;

        /// <summary>
        /// <see cref="FeedbackIntensity"/> 的出厂默认值。复位钩子与"恢复默认"
        /// 按钮都取这一个来源，避免将来改默认值时漏改其中一处。
        /// </summary>
        public const float IntensityDefault = 1.0f;

        /// <summary>强度系数的合法下界。0 = 完全关闭反馈，是无障碍选项的终点。</summary>
        public const float IntensityMin = 0.0f;

        /// <summary>强度系数的合法上界。超过 1.5 屏震会甩出边界钳制，抖了也白抖。</summary>
        public const float IntensityMax = 1.5f;

        /// <summary>
        /// 把本类的可写静态状态复位到出厂值。由 Unity 在每次进入运行时自动调用，
        /// 也可以被测试显式调用来隔离用例之间的污染。
        ///
        /// 【为什么是 SubsystemRegistration 而不是 AfterSceneLoad】
        /// 与 <c>FeedbackClock.ResetStatics</c> 取同一档，理由也同一条：
        /// SubsystemRegistration 是 RuntimeInitializeLoadType 里**最早**的一档，
        /// 早于任何 Awake。放晚了就会出现"第一个 Awake 已经读到脏的
        /// FeedbackIntensity"的窗口 —— 而 HitFeedbackDirector.OnEnable 恰恰
        /// 就在那个窗口里，它一旦读到 0，开局第一批命中会静默无反馈。
        ///
        /// 【将来接玩家设置持久化时改这里】
        /// 那时本方法的语义从"恢复出厂值"变成"从设置源重新加载"，
        /// 复位点仍然是这一个，不要在别处再开第二个入口。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetStatics()
        {
            FeedbackIntensity = IntensityDefault;
        }

        // =====================================================================
        // 分档阈值（§4.2 —— 必须两套）
        // =====================================================================

        /// <summary>玩家侧重击线：单次伤害 ≥ 8% HpMax（HpMax 稳定 260 ⇒ ≈ 20.8 点）。</summary>
        public const float HeavyRatioPlayer = 0.08f;

        /// <summary>
        /// 敌人侧重击线：单次伤害 ≥ 45% HpMax（Hp ≈ 22~40 ⇒ ≈ 10 点）。
        ///
        /// 【45% 是反推出来的起调值，不是定论】
        /// 它由 DifficultyBridge.cs:52 的 Hp = 22.0f 反推而来。工程师在本地实测
        /// 普攻的实际伤害后应当校准这个数——参数外露的意义就在这里。
        /// </summary>
        public const float HeavyRatioEnemy = 0.45f;

        /// <summary>
        /// 玩家"被重击"的屏震升档线：≥ 15% HpMax（≈ 39 点）走最强那一档（US-4）。
        /// 比 <see cref="HeavyRatioPlayer"/> 更高——重击是"疼"，这一档是"要命"。
        /// </summary>
        public const float ShakeHeavyRatioPlayer = 0.15f;

        // =====================================================================
        // R-06 连续强度插值：Ratio → Scale
        // =====================================================================
        //
        // 【为什么要连续插值而不是只有轻/重两档】
        // 只有两档时，19.9 点和 20.1 点伤害的手感会出现一个台阶，而玩家感知到的
        // 伤害是连续的。连续系数让"打得越狠震得越凶"成立，两档只负责选基准值。

        /// <summary>玩家侧插值下界：Ratio ≤ 2% 时取 <see cref="ScaleMin"/>。</summary>
        public const float ScaleRatioLoPlayer = 0.02f;

        /// <summary>玩家侧插值上界：Ratio ≥ 20% 时取 <see cref="ScaleMax"/>。</summary>
        public const float ScaleRatioHiPlayer = 0.20f;

        /// <summary>敌人侧插值下界。区间整体上移，同理于两套重击线。</summary>
        public const float ScaleRatioLoEnemy = 0.15f;

        /// <summary>敌人侧插值上界。</summary>
        public const float ScaleRatioHiEnemy = 0.60f;

        /// <summary>强度系数下限。不取 0 —— 再轻的一下也得有反应，否则像打空气。</summary>
        public const float ScaleMin = 0.5f;

        /// <summary>强度系数上限。1.5 是屏震幅度在 A-6 边界钳制下仍不失真的经验上界。</summary>
        public const float ScaleMax = 1.5f;

        // =====================================================================
        // R-01 hitstop（命中顿帧）
        // =====================================================================

        /// <summary>敌人挨打·轻击的顿帧时长（秒）。</summary>
        public const float StopEnemyLight = 0.05f;

        /// <summary>敌人挨打·重击的顿帧时长（秒）。</summary>
        public const float StopEnemyHeavy = 0.09f;

        /// <summary>
        /// 玩家挨打的顿帧时长（秒）。玩家自己挨打给最强顿帧——这是"疼"的主要来源，
        /// 也是玩家唯一必须无条件察觉的事件，所以不再细分轻重。
        /// </summary>
        public const float StopPlayer = 0.12f;

        /// <summary>
        /// 两次 hitstop **起始**间隔的下限（秒）。
        /// 没有它，连击时每一下都顿一次，画面会持续卡顿，从"打击感"退化成"掉帧"。
        /// 注意这是**起始**冷却：冷却期内的命中不会延长当前顿帧，直接丢弃。
        /// </summary>
        public const float StopCooldown = 0.15f;

        /// <summary>
        /// 同目标去重窗口（秒）。服务 R-01「同一时刻多次命中只取最长的一次」——
        /// 典型场景是范围技能在一帧内对同一目标结算多段伤害。
        /// 取 0.02s（约 1.2 帧 @60fps）：足够覆盖"同一步内的多次结算"，
        /// 又短到不会吃掉玩家真正的连招第二下。
        /// </summary>
        public const float StopDedupeWindow = 0.02f;

        /// <summary>顿帧时长的硬上限（秒）。连续强度系数最高 1.5 倍，兜底防手滑改常量把画面冻死。</summary>
        public const float StopMaxSeconds = 0.30f;

        // =====================================================================
        // R-02 屏震
        // =====================================================================

        /// <summary>敌人挨打·轻击的屏震幅度（世界单位）。"点头"级别，只求有反应。</summary>
        public const float ShakeAmpEnemyLight = 0.05f;

        /// <summary>敌人挨打·轻击的屏震时长（秒）。</summary>
        public const float ShakeDurEnemyLight = 0.08f;

        /// <summary>敌人挨打·重击的屏震幅度（世界单位）。</summary>
        public const float ShakeAmpEnemyHeavy = 0.12f;

        /// <summary>敌人挨打·重击的屏震时长（秒）。</summary>
        public const float ShakeDurEnemyHeavy = 0.14f;

        /// <summary>玩家挨打的屏震幅度（世界单位）。玩家挨打是最需要被感知的事件。</summary>
        public const float ShakeAmpPlayer = 0.20f;

        /// <summary>玩家挨打的屏震时长（秒）。</summary>
        public const float ShakeDurPlayer = 0.20f;

        /// <summary>玩家被重击（≥ <see cref="ShakeHeavyRatioPlayer"/>）的屏震幅度（世界单位）。</summary>
        public const float ShakeAmpPlayerHeavy = 0.30f;

        /// <summary>玩家被重击的屏震时长（秒）。</summary>
        public const float ShakeDurPlayerHeavy = 0.26f;

        /// <summary>
        /// 抖动频率（Hz）。取 PRD 建议区间 20~30 的中值。
        /// 太低（&lt;15Hz）看起来像镜头在"荡"，太高（&gt;35Hz）在 60fps 下会因为
        /// 采样不足而出现摩尔纹式的乱跳。
        /// </summary>
        public const float ShakeFrequencyHz = 24.0f;

        /// <summary>
        /// Y 轴相对 X 轴的幅度比。&lt;1 让抖动偏横向 ——
        /// "被打了一下"是横向的，纵向为主会像"地震"，那是另一种语义。
        /// </summary>
        public const float ShakeYRatio = 0.72f;

        /// <summary>
        /// Y 路正弦相对 X 路的频率倍率。取无理数附近的 1.37 而不是整数倍，
        /// 是为了让两路永不同周期回合——同周期会退化成一条固定斜率的直线往复，
        /// 看起来像"画面在滑轨上推拉"，不像抖动。
        /// </summary>
        public const float ShakeYFreqMul = 1.37f;

        /// <summary>Y 路正弦的相位偏移（弧度）。与频率倍率一起打散两路的同步性。</summary>
        public const float ShakeYPhase = 1.7f;

        // =====================================================================
        // R-03 受击闪白（数值先落表，派发在批次 2 的 T05）
        // =====================================================================

        /// <summary>敌人闪白色：纯白。沿用现状。</summary>
        public static readonly Color EnemyTint = Color.white;

        /// <summary>
        /// 玩家闪白色：朱砂红 #C0392B。
        /// 不用正红 #FF0000 —— 正红在水墨调性里是异物，朱砂是国画本来就有的颜料色。
        /// </summary>
        public static readonly Color PlayerTint = new Color(0.7529412f, 0.2235294f, 0.1686275f, 1.0f);

        /// <summary>敌人闪白峰值混合强度。1.0 = 完全变白，沿用现状。</summary>
        public const float FlashPeakEnemy = 1.0f;

        /// <summary>
        /// 玩家闪白峰值混合强度。0.80 —— 朱砂只混到八成，保留角色本身的墨色底子。
        /// 混到 1.0 会变成一块纯色剪影，那就"塑料"了。
        /// </summary>
        public const float FlashPeakPlayer = 0.80f;

        /// <summary>敌人挨打·重击的闪白时长（秒）。轻击档沿用 CombatView.DefaultFlashDuration。</summary>
        public const float FlashDurEnemyHeavy = 0.13f;

        /// <summary>玩家挨打的闪白时长（秒）。比敌人长，因为玩家更需要时间意识到"我中招了"。</summary>
        public const float FlashDurPlayer = 0.16f;

        // =====================================================================
        // R-04 伤害飘字（数值先落表，实现在批次 2 的 T04）
        // =====================================================================

        /// <summary>同屏飘字上限。超出时环形池自动覆盖最旧的一条。</summary>
        public const int PopupCapacity = 12;

        /// <summary>同目标伤害合并窗口（秒）。窗口内累加进同一条，避免连击时数字糊成一团。</summary>
        public const float PopupMergeWindow = 0.15f;

        /// <summary>飘字总寿命（秒）。</summary>
        public const float PopupLifetime = 0.70f;

        /// <summary>弹入阶段结束时刻（秒）：0 → 0.10s 做 0.8 → 1.15 → 1.0 的缩放。</summary>
        public const float PopupPopInEnd = 0.10f;

        /// <summary>上飘阶段结束时刻（秒）：0.10 → 0.45s 匀速上飘，之后开始淡出。</summary>
        public const float PopupRiseEnd = 0.45f;

        /// <summary>弹入阶段的缩放起点。</summary>
        public const float PopupScaleFrom = 0.80f;

        /// <summary>弹入阶段的缩放过冲峰值。略微过冲才有"弹"的感觉。</summary>
        public const float PopupScaleOvershoot = 1.15f;

        /// <summary>上飘距离（世界单位）。</summary>
        public const float PopupRiseWorld = 0.60f;

        /// <summary>飘字诞生点相对目标的垂直偏移（世界单位）。</summary>
        public const float PopupSpawnOffsetY = 0.80f;

        /// <summary>飘字诞生点的水平随机抖动幅度（世界单位，±）。避免连击时完全重叠。</summary>
        public const float PopupSpawnJitterX = 0.25f;

        /// <summary>重击飘字的字号倍率。</summary>
        public const float PopupHeavyFontMul = 1.30f;

        /// <summary>
        /// 重击飘字的弹入过冲峰值。比普通档的 <see cref="PopupScaleOvershoot"/>(1.15) 更炸。
        ///
        /// 【为什么重击要单独一条过冲曲线，而不是只把字号放大】
        /// 字号倍率改变的是"这个数字有多大"，过冲改变的是"它是怎么出现的"。
        /// 只放大字号，重击和轻击的**出现方式**一模一样，眼睛在连击里分辨
        /// 大小差异要靠横向比较（而飘字是一条条错开出现的，没得比）；
        /// 加大过冲之后，重击是"砸"出来的，轻击是"浮"出来的，
        /// 这个差别不需要参照物就能读出来。
        ///
        /// 【为什么是 1.42 而不是更大】
        /// 过冲峰值发生在 PopupPopInEnd 的一半（0.05s）处，@60fps 只有 3 帧。
        /// 再大就会因为采样不足而看起来像"闪了一下"而不是"弹了一下"，
        /// 与 ShakeFrequencyHz 上限 35Hz 是同一类采样约束。
        /// </summary>
        public const float PopupHeavyScaleOvershoot = 1.42f;

        /// <summary>
        /// 飘字基准字号（设计分辨率下的磅值）。
        ///
        /// 【为什么重击档不是换一个 fontSize，而是缩放 localScale】
        /// Legacy Text 每出现一个新字号，动态字体图集就要多烘一套字形。
        /// 数字只有 10 个字符，两套图集本身不贵，但图集重建会触发 Canvas rebuild，
        /// 而重击恰恰发生在最忙的那一帧。缩放走 Transform，零重建。
        /// </summary>
        public const int PopupFontSize = 34;

        /// <summary>
        /// 飘字描边宽度（像素）。1 太细，水墨背景上会被吃掉；3 起会糊成一团黑边。
        /// </summary>
        public const float PopupOutlineDistancePx = 2.0f;

        /// <summary>敌人受伤配色：墨黑 #2B2B2B。</summary>
        public static readonly Color PopupEnemyColor = new Color(0.1686275f, 0.1686275f, 0.1686275f, 1.0f);

        /// <summary>玩家受伤配色：朱砂红 #C0392B，与 <see cref="PlayerTint"/> 同色，形成统一语言。</summary>
        public static readonly Color PopupPlayerColor = new Color(0.7529412f, 0.2235294f, 0.1686275f, 1.0f);

        /// <summary>飘字描边色：白色。保证在深浅背景上都可读。</summary>
        public static readonly Color PopupOutlineColor = Color.white;

        /// <summary>
        /// 投影落在视口外时钳到边缘的内缩距离（像素，设计分辨率下）。
        /// 不丢弃而是钳住：玩家需要知道"屏幕外那个怪挨打了"。
        /// </summary>
        public const float PopupViewportPadPx = 24.0f;

        /// <summary>
        /// 被 HUD 技能栏挡住时上抬到的安全高度（像素，设计分辨率 1920×1080 下）。
        ///
        /// 【为什么不在这里写 [34,110] 这个区间】
        /// 区间本身要直接读 HudSkillBar.BottomMargin / CellSize 算出来，抄一份
        /// 字面量的话，将来技能栏改高度，飘字避让会**静默失效**且没人会发现。
        /// 这里只放"躲到哪"，不放"躲什么"。
        /// </summary>
        public const float PopupHudSafeY = 120.0f;

        // =====================================================================
        // R-07 击杀强调
        // =====================================================================
        //
        // 【为什么击杀要单独一档，而不是"最后那一刀自然就重"】
        // 致命一击的伤害往往很小（怪只剩 2 点血时一发普攻也能杀），按伤害占比
        // 分档会把它判成最轻的一档 —— 于是"杀死一只怪"和"挠了它一下"手感完全
        // 一样，击杀这个玩家最在意的事件反而是全场最没存在感的。
        // 所以击杀不看伤害，它是一个**事件**，给一档固定的、比普通重击更强的反馈。

        /// <summary>击杀顿帧时长（秒）。比 <see cref="StopEnemyHeavy"/>(0.09) 更长，但不及玩家挨打(0.12)。</summary>
        public const float KillStopSeconds = 0.14f;

        /// <summary>击杀屏震幅度（世界单位）。介于敌人重击(0.12)与玩家挨打(0.20)之间。</summary>
        public const float KillShakeAmp = 0.22f;

        /// <summary>击杀屏震时长（秒）。</summary>
        public const float KillShakeDur = 0.22f;

        // =====================================================================
        // R-08 低血量加强
        // =====================================================================

        /// <summary>
        /// 玩家"濒死"血量线。低于它时受击反馈整体加强。
        ///
        /// 【为什么只对玩家生效】
        /// 这一档的目的是让玩家在低头看血条**之前**就用余光察觉"我快死了"。
        /// 敌人濒死不需要这个信号——敌人濒死的表达是它下一刀就会倒下。
        /// </summary>
        public const float LowHpRatio = 0.30f;

        /// <summary>濒死时闪白时长的倍率。拉长而不是加深：加深会盖住角色本体，反而看不清自己在哪。</summary>
        public const float LowHpFlashMul = 1.40f;

        /// <summary>濒死时屏震幅度的倍率。1.2 是"更慌"而不是"看不清"的上限。</summary>
        public const float LowHpShakeMul = 1.20f;

        // =====================================================================
        // R-09 命中粒子特效（HitEffects.cs 第五路通道）
        //
        // 【为什么这些数字也进常量表，而不是写在 HitEffects.cs 里当 private const】
        // 与飘字同一套理由：调"特效整体有多猛"的人不关心粒子 prefab 的内部曲线，
        // 但他会关心"重击粒子比轻击大多少""同屏最多多少个"。这些数字跨"一个 prefab
        // 多大"和"反馈整体强度"两件事，必须和屏震、顿帧摆在同一屏里一起看。
        // 而且它们已经被 HitEffects.cs 引用（Resources 兜底路径之外唯一的硬依赖），
        // 留在本文件才能 grep 到、才能被一次性复位。
        //
        // 【为什么寿命给得比常见粒子播放时长略长】
        // 强制存活时长是为了"单个实例至少播完"。本工程不控制用户 prefab 的
        // Stop Action，所以给一个保守偏大的值（0.45s），避免把用户做得稍长的
        // 火花在中途切掉。用户 prefab 自己带 Destroy 的话，看板会在 Go == null
        // 时立刻回收，不会等到寿命到期——两者取短，互不拖后腿。
        // =====================================================================

        /// <summary>命中粒子的强制存活时长（秒）。保守偏长，避免把用户稍长的火花切在半途。</summary>
        public const float HitFxLifetime = 0.45f;

        /// <summary>击杀粒子的强制存活时长（秒）。通常比命中粒子长一点，爆散更完整。</summary>
        public const float KillFxLifetime = 0.70f;

        /// <summary>同屏粒子实例上限（看板长度）。满则先销毁最老的一个，保证新命中一定有回应。</summary>
        /// <remarks>语义是"个数"，必须是 <c>int</c>：它直接喂给 <c>new LiveFx[cap]</c> 的数组长度，
        /// 声明成 float 会让 <c>HitEffects._capacity</c> 的初始化与 <c>Mathf.Clamp</c> 结果都拿不到 int（CS0266）。</remarks>
        public const int HitFxCapacity = 24;

        /// <summary><see cref="_capacity"/> 的合法下界。0 会让每击 Instantiate 又立刻 Destroy（见 HitEffects.EnsureBoard）。</summary>
        public const int HitFxCapacityMin = 1;

        /// <summary><see cref="_capacity"/> 的合法上界。再大也只会拖累回收遍历，意义不大。</summary>
        public const int HitFxCapacityMax = 128;

        /// <summary>粒子诞生点相对受击方体心的垂直抬升（世界单位）。避免从脚底冒火花。</summary>
        public const float HitFxSpawnOffsetY = 0.30f;

        /// <summary>轻击粒子的尺寸倍率（在 prefab 自身缩放基础上再乘）。1 = 不改大小。</summary>
        public const float HitFxScaleLight = 1.00f;

        /// <summary>重击粒子的尺寸倍率。比轻击明显大一圈，强化"这一下打实了"。</summary>
        public const float HitFxScaleHeavy = 1.45f;

        /// <summary>粒子尺寸倍率下限。钳到它之上是为了防 FeedbackIntensity 被调成 0 时算出零向量（见 HitEffects.ResolveScale）。</summary>
        public const float HitFxScaleMin = 0.40f;

        /// <summary>粒子尺寸倍率上限。钳到它之下是为了防止连续强度系数叠加后粒子大得糊屏。</summary>
        public const float HitFxScaleMax = 2.50f;

        /// <summary>击杀粒子的固定尺寸倍率。比重击更大的一次爆散。</summary>
        public const float HitFxScaleKill = 1.80f;

        /// <summary>预留通道（光环）在无显式时长时的最长存活上限（秒）。给一个很长但有限的值，避免 StopFx 漏调导致永久泄漏。</summary>
        public const float AuraFxMaxSeconds = 8.0f;

        // =====================================================================
        // R-04 暴击（crit）跳字 —— 颜色预留接口
        //
        // 【为什么这里只给"颜色"，不给"字号 / 过冲"】
        // 字号倍率已在重击档用 PopupHeavyFontMul(1.30) 表达，弹入过冲已在
        // PopupHeavyScaleOvershoot(1.42) 表达——它们走的是"重击"这个概念。
        // 本段只补"颜色"这一项，因为颜色是唯一一个当前**没有任何分档**的维度：
        // 飘字色目前只按阵营二分（玩家朱砂 / 敌人墨黑），临界（crit）没有专属色。
        //
        // 【★ 诚实边界：当前命中事件负载里没有 crit 标志】
        // CombatEventsUnity.HitFeedback 的委托签名是 Action&lt;Combatant,Combatant,float&gt;
        // （attacker / defender / dmg），内核的"暴击"只存在于伤害**公式**内部
        // （DamageResolver 算 raw 时已含暴击），并未作为独立字段透出到反馈通道。
        // 所以本期 CritColor 是一个**就绪但沉睡**的接口：PopupColorOf 已实现，
        // Grade() 也改为经它取色，但传入的 isCrit 恒为 false，现网行为与改动前逐字等价。
        // 将来内核在命中事件里多带一个 bool isCrit 时，只需把 HitFeedbackDirector.Grade()
        // 里那一行 PopupColorOf(..., false) 改成 PopupColorOf(..., isCrit)，
        // 暴击跳字立刻变金色，其余一行都不用动。这是把"改一处"做成"改一行"的预留。
        // =====================================================================

        /// <summary>暴击跳字色：金色 #FFD700。crit 分支的专属色，区别于阵营二分（玩家朱砂 / 敌人墨黑）。</summary>
        public static readonly Color CritColor = new Color(1.0f, 0.843f, 0.0f, 1.0f);

        /// <summary>
        /// 取飘字色。分阵营、可叠加暴击。
        ///
        /// <para>【唯一取色入口】Grade() 改走本方法后，飘字配色裁定就收敛到一处，
        /// 与 FlashTint 同理（见 HitGrade 的注释）。扩展新配色分支只改这里。</para>
        /// </summary>
        /// <param name="isPlayerVictim">受击方是否玩家。</param>
        /// <param name="isCrit">是否暴击。当前恒为 false（见本段文件头诚实边界），将来由内核命中事件接入。</param>
        public static Color PopupColorOf(bool isPlayerVictim, bool isCrit)
        {
            if (isCrit)
            {
                return CritColor;
            }
            return isPlayerVictim ? PopupPlayerColor : PopupEnemyColor;
        }

        // =====================================================================
        // 派生计算（唯一一处允许有逻辑的地方：把两套阈值的分派收敛在常量表内部）
        // =====================================================================

        /// <summary>
        /// 取重击判定线。分阵营，理由见类头注释。
        /// </summary>
        /// <param name="isPlayerVictim">受击方是否玩家。</param>
        public static float HeavyRatio(bool isPlayerVictim)
        {
            return isPlayerVictim ? HeavyRatioPlayer : HeavyRatioEnemy;
        }

        /// <summary>
        /// 取钳制后的全局强度系数。
        ///
        /// 【为什么要单独开一个函数，而不是让调用方自己 Clamp】
        /// <see cref="FeedbackIntensity"/> 是全仓唯一一个**可写**字段，
        /// 谁都能在运行时把它设成 -3 或者 99。钳制这件事只要有第二份实现，
        /// 就一定会出现"屏震被钳住了、顿帧没钳住"这种半钳制状态 —— 那时
        /// FeedbackIntensity = 99 会直接把画面冻死 StopMaxSeconds 那么久。
        ///
        /// 【谁在用】
        /// <see cref="ScaleOf"/>（走占比插值的常规命中）与 R-07 击杀强调
        /// （固定档，**不看**伤害占比，所以够不到 ScaleOf，只能直接取本函数）。
        /// </summary>
        public static float Intensity()
        {
            return Mathf.Clamp(FeedbackIntensity, IntensityMin, IntensityMax);
        }

        /// <summary>
        /// 把伤害占比映射为 R-06 的连续强度系数，并乘上全局
        /// <see cref="FeedbackIntensity"/>。结果直接用于缩放屏震幅度与顿帧时长。
        ///
        /// 【为什么钳制在两端而不是线性外推】
        /// 外推会让"一击秒杀"这种极端 Ratio 抖出边界外，被 ClampPoint 按住之后
        /// 反而看不出跟普通重击的区别，白算一场。钳死更诚实。
        /// </summary>
        /// <param name="ratio">dmg / defender.HpMax，调用方已保证非负。</param>
        /// <param name="isPlayerVictim">受击方是否玩家，决定用哪套插值区间。</param>
        public static float ScaleOf(float ratio, bool isPlayerVictim)
        {
            float lo = isPlayerVictim ? ScaleRatioLoPlayer : ScaleRatioLoEnemy;
            float hi = isPlayerVictim ? ScaleRatioHiPlayer : ScaleRatioHiEnemy;

            float k = hi > lo ? Mathf.Clamp01((ratio - lo) / (hi - lo)) : 1.0f;
            float scale = Mathf.Lerp(ScaleMin, ScaleMax, k);

            return scale * Intensity();
        }
    }
}
