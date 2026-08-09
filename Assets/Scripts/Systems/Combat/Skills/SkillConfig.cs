// -----------------------------------------------------------------------------
// SkillConfig.cs —— T3 技能 / 双资源池 / 输入缓冲 / 随机流常量的唯一集中地
//
// 【为什么新常量一律不进 CombatConfig.cs】（架构 §7.2）
// CombatConfig.cs 是「T1 已对拍常量」的所在地 —— 它的每一个数字都被 64 条 Python
// 对拍与 88 条 NUnit 断言钉死。往里面加一个字段本身不改变行为，但它会让
// 「CombatConfig 是否被动过」这个问题从"一眼可见"退化为"要逐行 diff 才知道"。
// T3 的全部新常量集中到这里，红线自检就只剩一句话：CombatConfig.cs 零 diff。
//
// 【秒 → 帧的换算只在本文件发生一次】
// 所有时长常量都以 int 帧的形式定义，注释里写明对应秒数（@60Hz）。
// 运行时禁止再做换算，更禁止出现 0.01667f 这种字面量
// （CombatScheduler.cs 的血泪注释：13×0.01667 会让围攻频率从 4.300 变 4.65）。
//
// 【数值来源与待确认项】
//   · 水剑斩：PRD §4.4 明确要求"T2 口径原样搬运，不得改动"
//     → raw 12 / CD 0.4s / r70 / 90° / 灵力 0，与 AttackController 的四个常量逐一对齐。
//   · 双池数值：架构 §1.3 的倒推占位值，对应待明确项 N2（PM 未拍板，先跑通 + 可调）。
//   · BURST_BREAK_DEF_CHANCE：待明确项 N4。架构建议「100% 附着 + 时长减半」，
//     本实现按建议取 1.0f；改回 0.6f 只需动这一个常量，SkillRng 摇骰路径已实现并有单测。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：**所有能调的数字都在这个文件里**。想改技能强度？只改这里，别动别的。
//
// · 这个文件为什么重要？
//   游戏开发里最怕的就是"魔法数字"——某个 70 藏在攻击代码里，另一个 70 藏在特效
//   代码里，策划说"攻击距离改成 80"，你改了一个漏了一个，于是特效和判定对不上。
//   把所有数字集中到一个文件，改数就变成了"改一行"，且一眼能看全。
//
// · 怎么读这些常量名？
//     QI_    开头 = 灵力（蓝条）相关
//     STAM_  开头 = 体力（耐力条）相关
//     BASIC_ 开头 = 水剑斩（普通攻击）
//     BURST_ 开头 = 法阵冲击（范围技能）
//     LOTUS_ 开头 = 血莲侵蚀（下毒技能）
//     DODGE_ 开头 = 踏雪（翻滚闪避）
//     xxx_FRAMES 结尾 = 单位是"帧"（60 帧 = 1 秒）
//
// · 为什么时间都写成"帧"而不是"秒"？
//   因为游戏逻辑每秒精确跑 60 次，用整数帧计数是精确的，
//   而用小数秒累减会有微小误差、累积起来会让不同技能的节奏错位。
//   秒→帧的换算只在这个文件里做一次（注释里都标了对应秒数），运行时不再换算。
//
// · ⚠️ 为什么不能把这些数字加到 CombatConfig.cs 里？
//   那个文件里的每个数字都被 64 组自动对拍测试锁死了，属于"红线文件"。
//   往里面加东西本身不会改变行为，但会让"这个文件到底有没有被动过"
//   从一眼可见退化成"要逐行比对才知道"。分开放，红线检查就只剩一句话：
//   CombatConfig.cs 零改动。
//
// · 关于"待确认项 N2 / N4"
//   注释里标了 N2 / N4 的数字是我按设计意图倒推的占位值，产品经理还没最终拍板。
//   它们全都暴露成了单独的常量，改起来就是改一行数字，不会牵动任何逻辑。
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// T3 技能系统与双资源池的全部常量，以及默认技能表的构造。
    ///
    /// 【新手解释】游戏的"数值配置表"。static（静态）表示不需要 new，
    /// 直接写 <c>SkillConfig.BASIC_RAW</c> 就能取到水剑斩的伤害值。
    /// const（常量）表示编译后就固定了，运行时谁也改不了 —— 这是好事，
    /// 意味着不可能有代码在半路偷偷把技能伤害改掉。
    /// </summary>
    public static class SkillConfig
    {
        // =====================================================================
        // 灵力 Qi（管技能）
        // =====================================================================

        /// <summary>灵力上限。</summary>
        public const float QI_MAX = 100.0f;

        /// <summary>灵力战斗中回复速率（每秒）。</summary>
        public const float QI_REGEN = 8.0f;

        /// <summary>灵力脱战倍率（脱战后 = 8 × 2.0 = 16/s）。</summary>
        public const float QI_OOC_MULT = 2.0f;

        /// <summary>灵力脱战判定：连续 120 帧（2.0s）未施法且未受击。</summary>
        public const int QI_OOC_FRAMES = 120;

        /// <summary>灵力消耗后回复锁帧数。0 = 不锁（灵力靠 CD 约束，不需要额外锁）。</summary>
        public const int QI_REGEN_LOCK_FRAMES = 0;

        // =====================================================================
        // 体力 Stamina（管闪避）
        // =====================================================================

        /// <summary>体力上限。</summary>
        public const float STAM_MAX = 100.0f;

        /// <summary>
        /// 体力战斗中回复速率（每秒）。刻意高于灵力：
        /// 闪避是唯一的保命手段，不该因为"没资源"而死。
        /// </summary>
        public const float STAM_REGEN = 18.0f;

        /// <summary>体力脱战倍率（脱战后 = 18 × 1.5 = 27/s）。</summary>
        public const float STAM_OOC_MULT = 1.5f;

        /// <summary>体力脱战判定：连续 72 帧（1.2s）未闪避且未受击。</summary>
        public const int STAM_OOC_FRAMES = 72;

        /// <summary>
        /// 体力消耗后回复锁：21 帧（0.35s）。
        /// 稳态翻滚间隔 ≈ 1.39s &gt; CD 0.8s → **体力才是闪避的真实约束，CD 只是防抖**。
        /// 这是 PRD 里没有的新增机制，对应待明确项 N2。
        /// </summary>
        public const int STAM_REGEN_LOCK_FRAMES = 21;

        /// <summary>
        /// 闪避的体力开销。Q2 拍板后由"消耗灵力 20"改为"消耗体力 25"
        /// （满体力恰好连翻 4 次）。
        /// </summary>
        public const float DODGE_STAMINA_COST = 25.0f;

        // =====================================================================
        // 输入缓冲 / 软索敌 / 随机流
        // =====================================================================

        /// <summary>输入缓冲窗（帧）。6 帧 = 100ms，后摇末尾提前按下会被接住。</summary>
        public const int INPUT_BUFFER_FRAMES = 6;

        /// <summary>软索敌最大吸附角（度）。超出不吸附（P0-07 验收①）。</summary>
        public const float SOFT_AIM_MAX_DEG = 15.0f;

        /// <summary>软索敌总开关的默认值（P0-07 验收③要求可整体关闭）。</summary>
        public const bool SOFT_AIM_DEFAULT_ENABLED = true;

        // =====================================================================
        // 连击计数（P0-08 只要"显示"，P1-06 才接增伤曲线）
        // =====================================================================

        /// <summary>
        /// 连击断连的空窗帧数：240 帧 = 4.0s（PRD P1-06 原文「4s 无命中归零」）。
        ///
        /// 【为什么 P0 就要把计数做进内核】
        /// P0-08 的 HUD 要显示连击数，而 §1.7 定的规矩是「控制器不持有任何战斗状态，
        /// 唯一真源在内核」。若把计数放在 Unity 侧，它会随渲染帧率漂移（掉帧时少数几拍），
        /// 而且 P1-06 接增伤时又得整段搬回内核 —— 不如一次做对。
        /// P0 阶段 <c>Encounter.ComboMult</c> 恒返回 1.0f，**对伤害零影响**。
        /// </summary>
        public const int COMBO_RESET_FRAMES = 240;

        /// <summary>连击面板的显示门槛（PRD §5 要点④：combo ≥ 5 才出现，避免常驻噪音）。</summary>
        public const int COMBO_HUD_MIN = 5;

        /// <summary>
        /// 技能随机流的种子混淆常量（黄金比例倒数的 64 位定点，PCG 系列的惯用混淆子）。
        /// <c>SkillRng = new PCG32(zoneSeed ^ SKILL_STREAM_MIX, SKILL_STREAM_INC)</c>。
        /// </summary>
        public const ulong SKILL_STREAM_MIX = 0x9E3779B97F4A7C15UL;

        /// <summary>
        /// 技能随机流的 increment。**必须是奇数且不等于 PCG32.DefaultIncrement**
        /// —— 不同 increment 才是数学上独立的序列。
        /// ⚠️ 绝对禁止用 <c>Encounter.Rng.Fork()</c> 造这条流：
        /// PCG32.Fork 内部会调一次 NextUInt()，消耗父流一次抽样，
        /// 直接把所有敌人的 ChaseOffsetDeg 推偏，同种子 diff 当场作废。
        /// </summary>
        public const ulong SKILL_STREAM_INC = 0xDA3E39CB94B95BDBUL;

        // =====================================================================
        // 技能 id
        // =====================================================================

        /// <summary>水剑斩（普攻）。</summary>
        public const string SKILL_BASIC_SLASH = "skill_basic_slash";

        /// <summary>法阵冲击。</summary>
        public const string SKILL_CIRCLE_BURST = "skill_circle_burst";

        /// <summary>血莲侵蚀。</summary>
        public const string SKILL_BLOOD_LOTUS = "skill_blood_lotus";

        /// <summary>踏雪（闪避）。</summary>
        public const string SKILL_DODGE_ROLL = "dodge_roll";

        // =====================================================================
        // 水剑斩 —— T2 口径，一字不改（对齐 AttackController 的四个常量）
        // =====================================================================

        /// <summary>水剑斩原始伤害。对齐 <c>AttackController.AttackRaw</c>。</summary>
        public const float BASIC_RAW = 12.0f;

        /// <summary>水剑斩半径。对齐 <c>AttackController.AttackRadius</c>。</summary>
        public const float BASIC_RANGE = 70.0f;

        /// <summary>水剑斩扇形张角。对齐 <c>AttackController.AttackArcDeg</c>。</summary>
        public const float BASIC_ARC_DEG = 90.0f;

        /// <summary>水剑斩韧性伤害（PRD §4.4）。</summary>
        public const float BASIC_POISE = 10.0f;

        /// <summary>水剑斩冷却 24 帧 = 0.4s。对齐 <c>AttackController.AttackCooldown</c>。</summary>
        public const int BASIC_CD_FRAMES = 24;

        /// <summary>水剑斩灵力开销：0（PRD 硬性要求）。</summary>
        public const float BASIC_QI_COST = 0.0f;

        /// <summary>
        /// 水剑斩前摇：**必须为 0**。T2 的 <c>AttackController.Swing()</c> 是按下当帧
        /// 立即结算，加任何前摇都会改变已验收的手感。
        /// </summary>
        public const int BASIC_STARTUP_FRAMES = 0;

        /// <summary>水剑斩判定帧。</summary>
        public const int BASIC_ACTIVE_FRAMES = 1;

        /// <summary>
        /// 水剑斩后摇 5 帧（83ms）。远小于 CD 24 帧，只用来提供"后摇可被闪避取消"的
        /// 语义，不影响 2.5 刀/秒的节奏。
        /// </summary>
        public const int BASIC_RECOVERY_FRAMES = 5;

        /// <summary>水剑斩取消窗起始帧。</summary>
        public const int BASIC_CANCEL_FROM = 3;

        // =====================================================================
        // 法阵冲击
        // =====================================================================

        /// <summary>法阵冲击原始伤害。</summary>
        public const float BURST_RAW = 20.0f;

        /// <summary>法阵冲击半径。</summary>
        public const float BURST_RANGE = 140.0f;

        /// <summary>法阵冲击圆心前推距离。</summary>
        public const float BURST_FORWARD = 48.0f;

        /// <summary>法阵冲击韧性伤害（高韧伤 = 群体震开的来源）。</summary>
        public const float BURST_POISE = 30.0f;

        /// <summary>法阵冲击冷却 156 帧 = 2.6s。</summary>
        public const int BURST_CD_FRAMES = 156;

        /// <summary>法阵冲击灵力开销（连放 4 次耗尽满灵力）。</summary>
        public const float BURST_QI_COST = 25.0f;

        /// <summary>法阵冲击前摇 10 帧 = 0.167s（肉眼可辨的起手）。</summary>
        public const int BURST_STARTUP_FRAMES = 10;

        /// <summary>法阵冲击判定帧。</summary>
        public const int BURST_ACTIVE_FRAMES = 2;

        /// <summary>法阵冲击后摇。</summary>
        public const int BURST_RECOVERY_FRAMES = 16;

        /// <summary>法阵冲击取消窗起始帧。</summary>
        public const int BURST_CANCEL_FROM = 20;

        /// <summary>
        /// 法阵冲击附着破防的概率。**对应待明确项 N4**：
        /// PRD 原值 0.6；架构建议改为 1.0 + 时长减半（3.0s → 1.5s），理由是
        /// 「60% 的破防在体验上不可读，玩家无法分辨"没破防"与"破防了但伤害就这么高"」。
        /// 本实现按架构建议取 1.0f（P0 全程不摇骰，确定性更硬）。
        /// 若 PM 拍回 0.6f，只需改这一个常量 —— 摇骰路径已实现且有单测覆盖。
        /// </summary>
        public const float BURST_BREAK_DEF_CHANCE = 1.0f;

        // =====================================================================
        // 血莲侵蚀
        // =====================================================================

        /// <summary>血莲侵蚀原始伤害（低直伤）。</summary>
        public const float LOTUS_RAW = 8.0f;

        /// <summary>血莲侵蚀半径。</summary>
        public const float LOTUS_RANGE = 100.0f;

        /// <summary>血莲侵蚀扇形张角。</summary>
        public const float LOTUS_ARC_DEG = 120.0f;

        /// <summary>血莲侵蚀韧性伤害。</summary>
        public const float LOTUS_POISE = 5.0f;

        /// <summary>血莲侵蚀冷却 300 帧 = 5.0s。</summary>
        public const int LOTUS_CD_FRAMES = 300;

        /// <summary>血莲侵蚀灵力开销（连放 3 次耗尽，命中 PRD P0-03 ②）。</summary>
        public const float LOTUS_QI_COST = 30.0f;

        /// <summary>血莲侵蚀前摇 14 帧 = 0.233s（最慢最重）。</summary>
        public const int LOTUS_STARTUP_FRAMES = 14;

        /// <summary>血莲侵蚀判定帧。</summary>
        public const int LOTUS_ACTIVE_FRAMES = 2;

        /// <summary>血莲侵蚀后摇。</summary>
        public const int LOTUS_RECOVERY_FRAMES = 20;

        /// <summary>血莲侵蚀取消窗起始帧。</summary>
        public const int LOTUS_CANCEL_FROM = 24;

        /// <summary>血莲侵蚀附着蛊毒的概率（PRD §4.4：100%，可叠 5 层）。</summary>
        public const float LOTUS_POISON_CHANCE = 1.0f;

        // =====================================================================
        // 踏雪（闪避）
        // =====================================================================

        /// <summary>闪避总位移（像素）。</summary>
        public const float DODGE_DISTANCE = 130.0f;

        /// <summary>闪避冷却 48 帧 = 0.8s。</summary>
        public const int DODGE_CD_FRAMES = 48;

        /// <summary>闪避前摇。</summary>
        public const int DODGE_STARTUP_FRAMES = 2;

        /// <summary>闪避判定段（位移主体）。</summary>
        public const int DODGE_ACTIVE_FRAMES = 12;

        /// <summary>闪避后摇。总 15 帧 = 0.25s ≈ PRD 的 0.24s。</summary>
        public const int DODGE_RECOVERY_FRAMES = 1;

        /// <summary>闪避取消窗起始帧。</summary>
        public const int DODGE_CANCEL_FROM = 12;

        /// <summary>i-frame 起始帧（PRD：起始第 3 帧生效）。</summary>
        public const int DODGE_IFRAME_START = 3;

        /// <summary>i-frame 持续 12 帧 = <b>0.20s</b>，精确命中 PRD。</summary>
        public const int DODGE_IFRAME_LEN = 12;

        // =====================================================================
        // 构表
        // =====================================================================

        /// <summary>
        /// 构造默认技能表（首发 3 技能 + 闪避），并绑定到四个槽位。
        ///
        /// 【为什么构表要与常量定义分离】
        /// 常量在上面，装配在这里。改数只需改常量、不碰这段装配逻辑；
        /// T4 换成 JSON 驱动时只需替换本方法体，常量区可以整体删除。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"把上面那堆数字组装成 4 张技能说明书，并塞进技能栏的 4 个格子"。
        /// 什么时候被调用：游戏开始、战斗系统初始化的时候，只调一次。
        /// 组装结果：
        ///   第 0 格 = 水剑斩   （左键 / J / 手柄A）
        ///   第 1 格 = 法阵冲击 （K / 右键 / 手柄Y）
        ///   第 2 格 = 血莲侵蚀 （L / 手柄X）
        ///   第 3 格 = 踏雪闪避 （Shift / 空格 / 手柄B）
        /// 注意闪避也被当成一个"技能"来管理，这样冷却和资源消耗就能共用同一套代码，
        /// 不用为闪避单独写一份计时器。
        /// </remarks>
        /// <returns>已注册并完成槽位绑定的技能表。</returns>
        public static SkillTable BuildDefaultTable()
        {
            SkillTable table = new SkillTable();

            // ---- 槽 0：水剑斩（T2 口径原样搬运）----
            SkillDef basic = new SkillDef();
            basic.Id = SKILL_BASIC_SLASH;
            basic.DisplayName = "水剑斩";
            basic.Shape = SkillShape.Sector;
            basic.Range = BASIC_RANGE;
            basic.ArcDeg = BASIC_ARC_DEG;
            basic.ForwardOffset = 0.0f;
            basic.Raw = BASIC_RAW;
            basic.PoiseDamage = BASIC_POISE;
            basic.CooldownFrames = BASIC_CD_FRAMES;
            basic.QiCost = BASIC_QI_COST;
            basic.StaminaCost = 0.0f;
            basic.Action = ActionKind.Attack;
            basic.Frames = new FrameData(
                BASIC_STARTUP_FRAMES, BASIC_ACTIVE_FRAMES, BASIC_RECOVERY_FRAMES,
                BASIC_CANCEL_FROM, 0, 0);
            table.Register(basic);

            // ---- 槽 1：法阵冲击（群体震开 + 破甲）----
            SkillDef burst = new SkillDef();
            burst.Id = SKILL_CIRCLE_BURST;
            burst.DisplayName = "法阵冲击";
            burst.Shape = SkillShape.Circle;
            burst.Range = BURST_RANGE;
            burst.ArcDeg = 360.0f;
            burst.ForwardOffset = BURST_FORWARD;
            burst.Raw = BURST_RAW;
            burst.PoiseDamage = BURST_POISE;
            burst.CooldownFrames = BURST_CD_FRAMES;
            burst.QiCost = BURST_QI_COST;
            burst.StaminaCost = 0.0f;
            burst.Action = ActionKind.Cast;
            burst.Frames = new FrameData(
                BURST_STARTUP_FRAMES, BURST_ACTIVE_FRAMES, BURST_RECOVERY_FRAMES,
                BURST_CANCEL_FROM, 0, 0);
            burst.Effects = new StatusApplication[]
            {
                new StatusApplication(StatusConfig.SE_BREAK_DEF, BURST_BREAK_DEF_CHANCE, 1)
            };
            table.Register(burst);

            // ---- 槽 2：血莲侵蚀（低直伤 + 高后效）----
            SkillDef lotus = new SkillDef();
            lotus.Id = SKILL_BLOOD_LOTUS;
            lotus.DisplayName = "血莲侵蚀";
            lotus.Shape = SkillShape.Sector;
            lotus.Range = LOTUS_RANGE;
            lotus.ArcDeg = LOTUS_ARC_DEG;
            lotus.ForwardOffset = 0.0f;
            lotus.Raw = LOTUS_RAW;
            lotus.PoiseDamage = LOTUS_POISE;
            lotus.CooldownFrames = LOTUS_CD_FRAMES;
            lotus.QiCost = LOTUS_QI_COST;
            lotus.StaminaCost = 0.0f;
            lotus.Action = ActionKind.Cast;
            lotus.Frames = new FrameData(
                LOTUS_STARTUP_FRAMES, LOTUS_ACTIVE_FRAMES, LOTUS_RECOVERY_FRAMES,
                LOTUS_CANCEL_FROM, 0, 0);
            lotus.Effects = new StatusApplication[]
            {
                new StatusApplication(StatusConfig.SE_POISON, LOTUS_POISON_CHANCE, 1)
            };
            table.Register(lotus);

            // ---- 槽 3：踏雪（闪避，无伤害）----
            SkillDef dodge = new SkillDef();
            dodge.Id = SKILL_DODGE_ROLL;
            dodge.DisplayName = "踏雪";
            dodge.Shape = SkillShape.Sector;
            dodge.Range = 0.0f;
            dodge.ArcDeg = 0.0f;
            dodge.ForwardOffset = 0.0f;
            dodge.Raw = 0.0f;
            dodge.PoiseDamage = 0.0f;
            dodge.CooldownFrames = DODGE_CD_FRAMES;
            dodge.QiCost = 0.0f;
            dodge.StaminaCost = DODGE_STAMINA_COST;
            dodge.Action = ActionKind.Dodge;
            dodge.Frames = DodgeAction.Frames;
            table.Register(dodge);

            table.AssignSlot(IntentSlot.Basic, SKILL_BASIC_SLASH);
            table.AssignSlot(IntentSlot.Skill1, SKILL_CIRCLE_BURST);
            table.AssignSlot(IntentSlot.Skill2, SKILL_BLOOD_LOTUS);
            table.AssignSlot(IntentSlot.Dodge, SKILL_DODGE_ROLL);

            return table;
        }
    }
}
