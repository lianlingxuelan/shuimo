// -----------------------------------------------------------------------------
// BossFlowConfig.cs —— P2-1「BOSS 战接线」全部魔数的唯一出处（asmdef: Xianxia.Unity.T2）
//
// 【为什么单开一个文件】
// BOSS 接线横跨 CombatBridge（编排）、WorldBuilder（建模板）、EnemySpawner（上色）、
// HudBossBar（血条）四个文件。这些常量只要散落一次，就会出现"改了一处没改另一处"的
// 经典事故 —— 血条画在距顶 72，兜底超时却按 40 去算，两边谁都不知道对方存在。
// 一个常量都不许散落在别处：新增数字先加在这里，再去引用。
//
// 【这里不放什么】
//   1. 战斗数值（HP/ATK/韧性/经验倍率）—— 那是 DifficultyBridge 与 CombatConfig 的地盘，
//      属于**数值冻结区**，围攻倍率 2.5294x 的生命线就压在上面，一个字符都不许碰；
//   2. BOSS 阶段阈值（0.65 / 0.30）—— 真源在 CombatConfig.BOSS_PHASE_P2/P3_THRESHOLD，
//      血条刻度线直接引用那两个常量。在这里再抄一份 = 两处定义早晚对不上；
//   3. 冲击波半径 200 —— 真源在 CombatConfig.BOSS_P3_SHOCK_RADIUS，
//      而且它被 t1_selfcheck.py 与 Python 镜像双向锁死。
//
// 【超时三档的取值依据】详见架构文档 §6 Q-1。一句话：
// 判据必须是「欠着 BOSS 债 **且** 场上零敌人」的**连续**滞留时长
// （Encounter.BossPendingIdleSeconds），而不是"自置位起的总时长"。
// 后者在"建场即置位"的语义下必然误触发 —— 玩家正常清杂兵就要几十秒，
// 照抄 PRD 的 10s 兜底，结果不是防住软锁，而是 100% 复现"BOSS 永远不出场"。
// -----------------------------------------------------------------------------

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// BOSS 出场编排状态（防线 R-2）。
    ///
    /// **只进不退**：Disabled 是独立的死胡同，其余四态严格按
    /// Pending → Entering → Fighting → Done 单向推进。
    ///
    /// 为什么要单向：BOSS 债的置位者与清位者都只有 CombatBridge 一家，
    /// 如果状态可以回退，就会出现"清位 → 又被置位 → 又清位"的抖动 ——
    /// 表现为屏幕上 BOSS 血条一闪一闪、或者兜底日志每秒刷一屏。
    /// </summary>
    public enum BossFlowState
    {
        /// <summary>本区没有 BOSS 配置（或战场未就绪）。BossPending 全程为 false，行为退化为无 BOSS 关卡。</summary>
        Disabled = 0,

        /// <summary>已置位 BOSS 债，正在等玩家清完杂兵。</summary>
        Pending = 1,

        /// <summary>杂兵已清空，入场倒计时中 / 正在尝试生成。</summary>
        Entering = 2,

        /// <summary>BOSS 已出场并存活，战斗进行中。</summary>
        Fighting = 3,

        /// <summary>BOSS 已阵亡，或兜底放弃了本局 BOSS 战。终态。</summary>
        Done = 4
    }

    /// <summary>
    /// BOSS 出场编排 / 血条 / 视图外观的全部常量。
    /// 纯静态常量容器，不可实例化。
    /// </summary>
    public static class BossFlowConfig
    {
        // ---------------------------------------------------------------------
        // 一、出场节奏与软锁兜底（判据：Encounter.BossPendingIdleSeconds）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 清场后到 BOSS 现身的延迟（秒）。给玩家一个"喘口气 + 意识到有事要发生"的窗口。
        ///
        /// ⚠️ 这段时间**不冻结玩法**。冻结只能走 <c>CombatBridge.SetMenuPaused(true)</c>
        /// 这一个闸门，而它会连带冻结 <c>StepFixed</c>，导致内核的 BossPendingIdleSeconds
        /// 停止推进 —— 软锁兜底当场失效。要仪式感请用血条淡入/镜头抖动，不要碰暂停。
        /// </summary>
        public const float EntryDelaySeconds = 1.5f;

        /// <summary>
        /// 一级兜底：滞留超过这个秒数就 <c>LogWarning</c> + 重试一次生成。
        ///
        /// 取值 4.0s = <see cref="EntryDelaySeconds"/> × 2 + 1.0s 余量，覆盖：
        /// ① 一次 GC 尖峰（低端机典型 200~500ms）；
        /// ② SpriteFactory.Circle 首次生成 BOSS 贴图的同步开销（64×64 逐像素）；
        /// ③ FindSpawnableNear 螺旋搜索的最坏路径。
        /// 4s 也正好落在人类"等待异常"的感知阈值 3~5s 内 ——
        /// 玩家会觉得"卡了一下"，而不会觉得"游戏坏了"。
        /// </summary>
        public const float EntryTimeoutSeconds = 4.0f;

        /// <summary>
        /// 二级兜底（最后防线）：滞留超过这个秒数就强制 <c>ClearBossPending()</c> + <c>LogError</c>。
        ///
        /// 取值 12.0s = <see cref="EntryTimeoutSeconds"/> × 3（首次 + 两次重试全部失败）。
        /// 到这里认定 BOSS 系统不可用，**主动放弃 BOSS 战、放行胜利判定**。
        /// 宁可这局没打到 BOSS，也绝不让玩家卡在空地图上 —— 软锁是唯一不可接受的结局。
        /// </summary>
        public const float HardTimeoutSeconds = 12.0f;

        /// <summary>
        /// 诊断留痕阈值（秒），判据是 <c>BossPendingTotalSeconds</c>（**不是** Idle）。
        /// 超过就打一条 <c>LogWarning</c>（去重，一局只打一次），**不改任何状态**。
        /// 用途：让"玩家挂机 10 分钟"这类非缺陷场景在日志里可分辨。
        /// </summary>
        public const float DiagTimeoutSeconds = 600.0f;

        /// <summary>
        /// 生成重试次数上限。首次失败后最多再试 2 次，加起来 3 次机会，
        /// 恰好对应 <see cref="HardTimeoutSeconds"/> = <see cref="EntryTimeoutSeconds"/> × 3。
        /// </summary>
        public const int MaxSpawnRetries = 2;

        // ---------------------------------------------------------------------
        // 二、BOSS 视图外观（EnemySpawner.DressBoss / WorldBuilder.BuildBossTemplate）
        // ---------------------------------------------------------------------

        /// <summary>
        /// BOSS 体型放大倍率。必须显著大于精英的 <c>EnemySpawner.EliteScale</c>(1.45)，
        /// 否则玩家分不清"这是个大号精英"还是"这就是 BOSS"。
        /// </summary>
        public const float BossViewScale = 1.9f;

        /// <summary>BOSS 描边宽度（贴图像素）。比精英的 5px 更粗，远处也能认出来。</summary>
        public const float BossOutlinePx = 7.0f;

        /// <summary>BOSS 描边色 R 分量（深红）。与精英的金色 (0.95,0.82,0.35) 明确区分。</summary>
        public const float BossOutlineR = 0.86f;

        /// <summary>BOSS 描边色 G 分量。</summary>
        public const float BossOutlineG = 0.20f;

        /// <summary>BOSS 描边色 B 分量。</summary>
        public const float BossOutlineB = 0.22f;

        // ---------------------------------------------------------------------
        // 三、BOSS 血条布局（HudBossBar，1920×1080 参考分辨率）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 血条根距屏幕顶边的距离（像素）。
        ///
        /// ⚠️ **是 72，不是 PRD 写的 40。** 原因：HudStatusIcons 的「目标状态行」
        /// 占据距顶 28~62、x∈[−147,+147]（HudStatusIcons.cs:105-106）。
        /// 血条若放在 40，纵向 [40,68] 与状态行 [28,62] 重叠 22px，横向还完全包住它 ——
        /// 结果就是"打 BOSS 时目标 DEBUFF 图标被血条盖住"，而打 BOSS 恰恰是最需要
        /// 看目标 DEBUFF 的时候。这不是审美问题，是功能问题。
        /// 下移到 72 后留 10px 呼吸间隙，且既有 HUD **零布局改动**。
        /// </summary>
        public const float BarTopMargin = 72.0f;

        /// <summary>血条（含名字行）根节点宽度。</summary>
        public const float BarWidth = 720.0f;

        /// <summary>血条根节点总高 = 名字行 24 + 血槽 28 + 下留白 12。</summary>
        public const float BarRootHeight = 64.0f;

        /// <summary>名字行高度。</summary>
        public const float BarNameHeight = 24.0f;

        /// <summary>
        /// 血槽相对血条根**顶边**的下沉量（像素）。
        /// = 名字行 24 + 4px 呼吸间隙，与架构文档 §5.3 布局表的 (0, −28) 一致。
        /// </summary>
        public const float BarSlotOffsetY = 28.0f;

        /// <summary>血槽高度。</summary>
        public const float BarHeight = 28.0f;

        /// <summary>阶段刻度线宽度（像素）。</summary>
        public const float TickWidth = 2.0f;

        /// <summary>名字行字号。</summary>
        public const int BarNameFontSize = 18;

        // ---------------------------------------------------------------------
        // 四、冲击波特效（WorldBuilder.BuildShockwaveFxTemplate / FxAutoDespawn）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 单个冲击波特效实例的存活时长（秒）。到点自毁。
        /// 略长于 <c>CombatConfig.BOSS_P3_SHOCK_CD</c>(6.0s) 的零头即可 ——
        /// 这是"一次性特效"，不是持续场，留太久会在 FxRoot 下堆积。
        /// </summary>
        public const float FxLifeSeconds = 0.45f;

        /// <summary>冲击波特效环的描边宽度（贴图像素）。</summary>
        public const float FxOutlinePx = 3.0f;

        /// <summary>冲击波特效颜色 R 分量（暖橙，与 BOSS 深红区分开，免得看不清波前）。</summary>
        public const float FxColorR = 0.98f;

        /// <summary>冲击波特效颜色 G 分量。</summary>
        public const float FxColorG = 0.62f;

        /// <summary>冲击波特效颜色 B 分量。</summary>
        public const float FxColorB = 0.24f;

        /// <summary>冲击波特效不透明度。</summary>
        public const float FxColorA = 0.55f;

        /// <summary>
        /// 装冲击波模板的容器节点名。该容器 <c>SetActive(false)</c>，
        /// 而**模板根自身 activeSelf 必须为 true** —— 详见 <c>WorldBuilder.BuildShockwaveFxTemplate</c>
        /// 的注释与架构文档 §7-C4。
        /// </summary>
        public const string FxTemplateRootName = "FxTemplates";
    }
}
