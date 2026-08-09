// -----------------------------------------------------------------------------
// SkillRuntime.cs —— 每单位的技能冷却与施法校验（引擎无关）
//
// 【为什么 CanCast 要返回"为什么不能"而不是 bool】
// PRD §5 要点② 是一条硬性 UI 要求：「技能格用扇形遮罩表达 CD、用置灰表达灵力不足，
// 两种不可用状态**视觉必须可区分**」。如果 CanCast 只返回 bool，HUD 就得自己再判一遍
// CD 和灵力 —— 于是"能不能放"这件事有了两个真源，某天改了施法条件却忘了改 HUD，
// 界面就开始说谎。这里把判定收敛成唯一入口，HUD 只负责把 CastReject 翻译成颜色。
//
// 【为什么 CD 与资源是"检查"和"扣除"两步而不是一步】
// 起手可能被 ActionState.TryBegin 拒绝（当前动作不在取消窗内）。若在检查阶段就扣蓝，
// 就会出现"蓝没了但技能没放出来"。所以顺序严格是：
//     CanCast（只读） → ActionState.TryBegin（可能失败，失败即返回，无副作用）
//     → TrySpend（扣资源） → BeginCast（进 CD） → 发事件
// 这个顺序写在 Encounter 的 ①-A 阶段，本类只提供其中的只读与写入两半。
//
// 【为什么 CD 用整数帧数组而不是 float 字典】
// 整数帧：与 ActionState 同一时间基，30/60/144fps 下冷却帧数完全一致（P0-01 ①）。
// 定长数组：零 GC、零遍历顺序歧义（§7.3-5 确定性红线）。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// 施法被拒绝的原因。<see cref="Ok"/> 之外的每一项都对应 HUD 上一种不同的表现。
    /// </summary>
    public enum CastReject
    {
        /// <summary>可以施放。</summary>
        Ok = 0,

        /// <summary>该槽位没有绑定技能（装配缺失）。</summary>
        NoSkill = 1,

        /// <summary>冷却中。HUD 画扇形遮罩。</summary>
        OnCooldown = 2,

        /// <summary>灵力不足。HUD 置灰 + 灵力条闪红。</summary>
        NotEnoughQi = 3,

        /// <summary>体力不足。HUD 置灰 + 体力条闪红。</summary>
        NotEnoughStamina = 4,

        /// <summary>当前动作不可被取消（前摇 / 判定 / 取消窗之前 / 硬直中）。</summary>
        ActionLocked = 5
    }

    /// <summary>
    /// 每单位一个的技能运行时状态。**可空组件**：未挂载时
    /// <see cref="Encounter"/> 的 ①-A 阶段整段跳过。
    /// </summary>
    public sealed class SkillRuntime
    {
        private readonly int[] _cd = new int[SkillTable.SlotCount];

        /// <summary>技能定义表。装配阶段注入；为 null 时任何槽位都返回 <see cref="CastReject.NoSkill"/>。</summary>
        public SkillTable Table;

        /// <summary>槽位数量。</summary>
        public int SlotCount
        {
            get { return SkillTable.SlotCount; }
        }

        /// <summary>取某槽位的技能定义。</summary>
        /// <param name="slot">槽位序号。</param>
        /// <returns>技能定义；未绑定或表为 null 时返回 null。</returns>
        public SkillDef GetDef(int slot)
        {
            return Table != null ? Table.GetSlot(slot) : null;
        }

        /// <summary>取某槽位的技能定义。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>技能定义；未绑定或表为 null 时返回 null。</returns>
        public SkillDef GetDef(IntentSlot slot)
        {
            return GetDef((int)slot);
        }

        /// <summary>剩余冷却帧数。越界返回 0。</summary>
        /// <param name="slot">槽位序号。</param>
        /// <returns>剩余帧数。</returns>
        public int CdRemain(int slot)
        {
            if (slot < 0 || slot >= _cd.Length)
            {
                return 0;
            }
            return _cd[slot];
        }

        /// <summary>剩余冷却秒数（HUD 文本用）。</summary>
        /// <param name="slot">槽位序号。</param>
        /// <returns>剩余秒数。</returns>
        public float CdRemainSeconds(int slot)
        {
            return CdRemain(slot) * CombatScheduler.FixedStep;
        }

        /// <summary>
        /// 冷却剩余比例：1.0 = 刚进 CD，0.0 = 就绪。
        /// **HUD 的扇形遮罩直接用它当填充量**，方向与直觉一致（遮罩越满 = 越久才好）。
        /// </summary>
        /// <param name="slot">槽位序号。</param>
        /// <returns>[0,1] 的比例。</returns>
        public float CdRatio(int slot)
        {
            SkillDef def = GetDef(slot);
            if (def == null || def.CooldownFrames <= 0)
            {
                return 0.0f;
            }
            float r = (float)CdRemain(slot) / def.CooldownFrames;
            if (r < 0.0f)
            {
                return 0.0f;
            }
            return r > 1.0f ? 1.0f : r;
        }

        /// <summary>该槽位是否已冷却完毕（只看 CD，不看资源与动作）。</summary>
        /// <param name="slot">槽位序号。</param>
        /// <returns>true = CD 已好。</returns>
        public bool IsReady(int slot)
        {
            return CdRemain(slot) <= 0;
        }

        /// <summary>
        /// 施法校验。**纯只读，不产生任何副作用。**
        /// 判定顺序刻意是「有没有技能 → CD → 资源 → 动作」：
        /// 前三项是玩家能主动改善的（等 CD、攒蓝），最后一项是"手速问题"，
        /// 把它放最后，HUD 优先展示更有信息量的那个理由。
        /// </summary>
        /// <param name="slot">槽位序号。</param>
        /// <param name="action">动作帧机。null 时跳过动作检查（headless 单测用）。</param>
        /// <param name="qi">灵力池。null 时视为灵力无限。</param>
        /// <param name="stamina">体力池。null 时视为体力无限。</param>
        /// <param name="def">输出：该槽位的技能定义（失败时可能为 null）。</param>
        /// <returns>拒绝原因；<see cref="CastReject.Ok"/> 表示可以施放。</returns>
        public CastReject CanCast(int slot, ActionState action, ResourcePool qi, ResourcePool stamina, out SkillDef def)
        {
            def = GetDef(slot);
            if (def == null)
            {
                return CastReject.NoSkill;
            }
            if (CdRemain(slot) > 0)
            {
                return CastReject.OnCooldown;
            }
            if (qi != null && !qi.CanAfford(def.QiCost))
            {
                return CastReject.NotEnoughQi;
            }
            if (stamina != null && !stamina.CanAfford(def.StaminaCost))
            {
                return CastReject.NotEnoughStamina;
            }
            if (action != null && !action.CanCancelInto(def.Action))
            {
                return CastReject.ActionLocked;
            }
            return CastReject.Ok;
        }

        /// <summary>
        /// 施法校验（不关心技能定义时的重载）。
        /// </summary>
        /// <param name="slot">槽位序号。</param>
        /// <param name="action">动作帧机，可为 null。</param>
        /// <param name="qi">灵力池，可为 null。</param>
        /// <param name="stamina">体力池，可为 null。</param>
        /// <returns>拒绝原因。</returns>
        public CastReject CanCast(int slot, ActionState action, ResourcePool qi, ResourcePool stamina)
        {
            SkillDef ignored;
            return CanCast(slot, action, qi, stamina, out ignored);
        }

        /// <summary>
        /// 进入冷却。**只在 <see cref="ActionState.TryBegin"/> 返回 true 之后调用**，
        /// 否则会出现"技能没放出来却进了 CD"。
        /// </summary>
        /// <param name="slot">槽位序号。</param>
        /// <param name="def">技能定义。null 时忽略。</param>
        public void BeginCast(int slot, SkillDef def)
        {
            if (slot < 0 || slot >= _cd.Length || def == null)
            {
                return;
            }
            _cd[slot] = def.CooldownFrames < 0 ? 0 : def.CooldownFrames;
        }

        /// <summary>推进一个逻辑帧：所有槽位的 CD 各减 1（夹在 0）。</summary>
        public void TickFrame()
        {
            for (int i = 0; i < _cd.Length; i++)
            {
                if (_cd[i] > 0)
                {
                    _cd[i]--;
                }
            }
        }

        /// <summary>
        /// 直接改写某槽位的剩余 CD（P2 的"完美闪避缩减 CD"与调试用）。
        /// </summary>
        /// <param name="slot">槽位序号。</param>
        /// <param name="frames">剩余帧数，负值夹为 0。</param>
        public void SetCd(int slot, int frames)
        {
            if (slot < 0 || slot >= _cd.Length)
            {
                return;
            }
            _cd[slot] = frames < 0 ? 0 : frames;
        }

        /// <summary>清空全部 CD（切区 / 重生）。</summary>
        public void Reset()
        {
            for (int i = 0; i < _cd.Length; i++)
            {
                _cd[i] = 0;
            }
        }
    }
}
