// -----------------------------------------------------------------------------
// SkillDef.cs —— 技能静态定义与技能表（引擎无关）
//
// 【为什么技能是"数据"而不是"代码"】
// P0-02 验收①要求「技能定义全部数据化，不得在代码里硬编码任何数值」。
// 一旦某个技能的半径写在 if 分支里，第二个技能就必然复制那段 if —— 半年后
// 「改一个数要动三处、漏一处就 bug」。这里把技能压成一张只读表：
// 判定形状是枚举、数值是字段、附着状态是数组，运行时只有一条通用结算路径。
//
// 【为什么 LoadFrom 现在就要留出来】
// T4 会把技能表搬到 JSON。届时若 SkillTable 只有 Register，就得改所有装配点。
// 现在留一个 LoadFrom(IEnumerable<SkillDef>)，T4 只需实现一个 JSON → SkillDef[]
// 的解析器，上层零改动（架构 §7.2 的硬性要求）。
//
// 【为什么槽位是定长数组而不是字典】
// 确定性红线（§7.3-5）：绝不引入字典遍历顺序依赖。槽位用定长数组按 IntentSlot
// 索引，零动态分配、零遍历顺序歧义。id → def 的字典只用于按键查找，不产出伤害顺序。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是游戏的「技能说明书总目录」。每个技能长什么样、打多远、扣多少蓝，
//         全都写成数据存在这里，而不是散落在代码的 if 分支里。
//
// · 为什么技能要做成"数据"而不是"代码"？
//   假设你把水剑斩的半径 70 直接写死在攻击代码里。等策划要加第二个技能时，
//   最省事的做法就是把那段代码复制一份改成 140 —— 于是同一套判定逻辑有了两份。
//   再加第三个技能就是三份。半年后策划说"所有技能半径 +10%"，
//   你就得改三处，而且一定会漏掉一处。
//   正确做法是：判定逻辑只写一份，半径是传进去的参数。
//   这就是所谓"数据驱动"，也是 PRD P0-02 ① 的硬性验收标准。
//
// · 这里定义了什么？
//     SkillShape        —— 攻击范围的形状：扇形（像挥剑）或圆形（像法阵爆炸）
//     StatusApplication —— "这一招命中后给敌人挂什么 Debuff、多大概率"
//     SkillDef          —— 一个技能的完整说明书（伤害、范围、冷却、耗蓝、帧数据……）
//     SkillTable        —— 所有技能说明书的册子，支持按名字查、按技能栏格子查
//
// · 名词解释
//     Raw       —— 原始伤害。还没减去敌人护甲之前的数字。
//     Poise     —— 韧性伤害。敌人有一条看不见的"抗打断条"，
//                  打空了它才会被打得踉跄+击退。高韧伤 = 更容易把怪震开。
//     Cooldown  —— 冷却。放完这一招后要等多久才能再放，我们用"多少帧"来记。
//     ForwardOffset —— 圆形范围的圆心往身前推多远。法阵冲击推 48 像素，
//                      这样爆炸中心在你面前而不是脚下。
// =============================================================================
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Xianxia.Combat
{
    /// <summary>
    /// 技能命中形状。
    ///
    /// 【新手解释】决定"这一招能打到哪一片区域"。目前只有两种：
    /// 扇形（以自己为顶点朝面前张开一个角度，像挥剑）
    /// 和圆形（以身前某点为圆心的一整圈，像脚下炸开一个法阵）。
    /// </summary>
    public enum SkillShape
    {
        /// <summary>扇形：以施法者为顶点，朝 facing 张开 ArcDeg 度、半径 Range。</summary>
        Sector = 0,

        /// <summary>圆形：以施法者前推 ForwardOffset 的位置为圆心，半径 Range。</summary>
        Circle = 1
    }

    /// <summary>
    /// 受击反应等级。**P0 阶段仅定义不使用**（字段已在 <see cref="SkillDef.Reaction"/>
    /// 中预留），P1-07 落地时才接线，届时需重跑平衡回归。
    /// </summary>
    public enum HitReaction
    {
        /// <summary>无额外反应，只走既有的"破韧才硬直/击退"二元逻辑。</summary>
        None = 0,

        /// <summary>强制硬直。</summary>
        HitStun = 1,

        /// <summary>强制击退。</summary>
        Knockback = 2,

        /// <summary>击倒（含倒地保护）。</summary>
        Knockdown = 3
    }

    /// <summary>
    /// 一次状态附着的描述。<see cref="Chance"/> &gt;= 1 时**不摇骰**
    /// （§7.3-4：100% 附着的技能连 SkillRng 都不碰，抽样序列只被真正的随机事件推进）。
    /// </summary>
    public struct StatusApplication
    {
        /// <summary>状态 id（对应 <see cref="StatusTable"/> 的 key）。</summary>
        public string StatusId;

        /// <summary>附着概率，[0,1]。&gt;= 1 表示必定附着且不消耗随机流。</summary>
        public float Chance;

        /// <summary>状态等级（P0 恒为 1，为 T4 的等级化留位）。</summary>
        public int Level;

        /// <summary>构造。</summary>
        /// <param name="statusId">状态 id。</param>
        /// <param name="chance">附着概率。</param>
        /// <param name="level">状态等级。</param>
        public StatusApplication(string statusId, float chance, int level)
        {
            StatusId = statusId;
            Chance = chance;
            Level = level < 1 ? 1 : level;
        }
    }

    /// <summary>
    /// 一个技能的静态定义。只读数据，运行时状态住在 <see cref="SkillRuntime"/>。
    ///
    /// 【新手解释】一张"技能说明书"。注意它是**只读**的：
    /// 说明书上写着"冷却 24 帧"，但"这一招现在还剩几帧冷却"不写在这儿 ——
    /// 那属于每个角色各自的运行时状态，存在 SkillRuntime 里。
    /// 把"设定"和"当前状态"分开存，是为了让同一份说明书能被无数个角色共用。
    /// </summary>
    public sealed class SkillDef
    {
        private static readonly StatusApplication[] NoEffects = new StatusApplication[0];

        /// <summary>技能 id（唯一）。</summary>
        public string Id = string.Empty;

        /// <summary>中文显示名。</summary>
        public string DisplayName = string.Empty;

        /// <summary>命中形状。</summary>
        public SkillShape Shape = SkillShape.Sector;

        /// <summary>作用半径（像素）。</summary>
        public float Range;

        /// <summary>扇形张角（度）。<see cref="SkillShape.Circle"/> 时忽略。</summary>
        public float ArcDeg = 90.0f;

        /// <summary>圆心前推距离（像素）。<see cref="SkillShape.Sector"/> 时通常为 0。</summary>
        public float ForwardOffset;

        /// <summary>原始伤害。0 表示无伤害技能（如闪避）。</summary>
        public float Raw;

        /// <summary>
        /// 韧性伤害。与 <see cref="Raw"/> 分离：PRD §4.4 给三个技能配了独立的 poise
        /// （10 / 30 / 5），法阵冲击的"群体震开"正是靠高韧伤而非高血伤实现的。
        /// </summary>
        public float PoiseDamage;

        /// <summary>冷却帧数（60Hz）。</summary>
        public int CooldownFrames;

        /// <summary>灵力开销。</summary>
        public float QiCost;

        /// <summary>体力开销。</summary>
        public float StaminaCost;

        /// <summary>帧数据。</summary>
        public FrameData Frames = new FrameData(0, 1, 0);

        /// <summary>命中时附着的状态列表。空数组表示无附着。</summary>
        public StatusApplication[] Effects = NoEffects;

        /// <summary>受击反应等级。**P0 不使用**，P1-07 接线。</summary>
        public HitReaction Reaction = HitReaction.None;

        /// <summary>
        /// 本技能进入的动作大类。普攻走 <see cref="ActionKind.Attack"/>（可被闪避全程取消），
        /// 技能走 <see cref="ActionKind.Cast"/>，闪避走 <see cref="ActionKind.Dodge"/>。
        /// </summary>
        public ActionKind Action = ActionKind.Cast;

        /// <summary>是否为产出伤害的技能（闪避为 false）。</summary>
        public bool DealsDamage
        {
            get { return Raw > 0.0f; }
        }

        /// <summary>扇形半角（度）。</summary>
        public float HalfArcDeg
        {
            get { return ArcDeg * 0.5f; }
        }

        /// <summary>冷却时长（秒）。仅供 UI 展示。</summary>
        public float CooldownSeconds
        {
            get { return CooldownFrames * CombatScheduler.FixedStep; }
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("[{0} {1} raw={2:F1} cd={3}f qi={4:F0} stam={5:F0}]",
                Id, Shape, Raw, CooldownFrames, QiCost, StaminaCost);
        }
    }

    /// <summary>
    /// 技能表：id → 定义 的查找 + 槽位 → 定义 的定长绑定。
    ///
    /// 【新手解释】装着所有技能说明书的册子，提供两种查法：
    ///   ① 按技能名字查（"skill_basic_slash" → 水剑斩的说明书）
    ///   ② 按技能栏第几格查（第 0 格 → 水剑斩）
    /// 第 ② 种是游戏里实际用的：你按 J 键 = 用第 0 格，按 K 键 = 用第 1 格。
    /// 这样将来做"技能自定义装配"时，只要改格子的绑定就行，代码零改动。
    /// </summary>
    public sealed class SkillTable
    {
        /// <summary>槽位数量（PRD Q4 拍板：3 技能 + 1 闪避）。</summary>
        public const int SlotCount = PlayerIntent.SlotCount;

        private readonly Dictionary<string, SkillDef> _byId = new Dictionary<string, SkillDef>(8);
        private readonly SkillDef[] _bySlot = new SkillDef[SlotCount];

        /// <summary>已注册的技能数量。</summary>
        public int Count
        {
            get { return _byId.Count; }
        }

        /// <summary>
        /// 注册一个技能定义。同 id 重复注册时覆盖（便于 T4 的热重载）。
        /// </summary>
        /// <param name="def">技能定义。null 或空 id 时忽略。</param>
        public void Register(SkillDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id))
            {
                return;
            }
            _byId[def.Id] = def;
        }

        /// <summary>
        /// 批量装载。**T4 的 JSON 驱动接入点**：届时只需实现 JSON → SkillDef 的解析，
        /// 上层装配代码零改动（架构 §7.2）。
        /// </summary>
        /// <param name="defs">技能定义集合。null 时忽略。</param>
        public void LoadFrom(IEnumerable<SkillDef> defs)
        {
            if (defs == null)
            {
                return;
            }
            foreach (SkillDef d in defs)
            {
                Register(d);
            }
        }

        /// <summary>把某个已注册的技能绑定到槽位。</summary>
        /// <param name="slot">槽位。</param>
        /// <param name="id">技能 id。未注册时静默忽略（装配错误会在 CanCast 时暴露）。</param>
        public void AssignSlot(IntentSlot slot, string id)
        {
            int i = (int)slot;
            if (i < 0 || i >= SlotCount)
            {
                return;
            }
            SkillDef def = Get(id);
            if (def == null)
            {
                return;
            }
            _bySlot[i] = def;
        }

        /// <summary>按 id 取技能定义。</summary>
        /// <param name="id">技能 id。</param>
        /// <returns>技能定义；不存在时返回 null。</returns>
        public SkillDef Get(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            SkillDef def;
            return _byId.TryGetValue(id, out def) ? def : null;
        }

        /// <summary>按槽位取技能定义。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>技能定义；未绑定时返回 null。</returns>
        public SkillDef GetSlot(IntentSlot slot)
        {
            int i = (int)slot;
            return i >= 0 && i < SlotCount ? _bySlot[i] : null;
        }

        /// <summary>按槽位序号取技能定义（供 <see cref="ActionState.SkillSlot"/> 直接查表）。</summary>
        /// <param name="slotIndex">槽位序号。</param>
        /// <returns>技能定义；越界或未绑定时返回 null。</returns>
        public SkillDef GetSlot(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < SlotCount ? _bySlot[slotIndex] : null;
        }

        /// <summary>清空全表。</summary>
        public void Clear()
        {
            _byId.Clear();
            for (int i = 0; i < SlotCount; i++)
            {
                _bySlot[i] = null;
            }
        }
    }
}
