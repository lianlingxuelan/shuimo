// -----------------------------------------------------------------------------
// ICombatEventsT3.cs —— T3 战斗事件出口（引擎无关）
//
// 【为什么新开一个接口，而不是往 ICombatEvents 上加方法】（架构 §1.6d · 偏离 PRD D-1）
// ICombatEvents 当前有 3 个实现，其中一个是
//   Assets/Scripts/Systems/Combat/Tests/CombatKernelTests.cs 的 RecorderEvents。
// 而那个文件正是「88 条断言一字不改」的红线文件。往接口上加方法 = 必须动它。
// 即使只是补几个空实现、不碰任何断言，它也会让 QA 在 diff 里看到测试文件被修改，
// 凭空制造一次信任成本 —— 这个成本远大于多开一个接口的成本。
//
// 于是：ICombatEvents.cs / NullCombatEvents / CombatEventsUnity / RecorderEvents
// **四个既有文件零改动**。T4 若想合并，让 ICombatEventsT3 : ICombatEvents 即可，
// 无迁移成本。
//
// 【调用频率约束】
// 本接口会被 60Hz × 敌人数 高频调用。实现方（CombatEventsT3Unity）里不许做
// Instantiate 以外的重活，更不许 FindObjectOfType —— 引用一律在装配阶段注入。
// 参数刻意不用 params 数组、不用闭包，避免高频路径产生 GC。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是战斗内核的「广播喇叭」，用来通知外面"刚刚发生了什么"。
//
// · 为什么需要它？
//   战斗计算的部分（内核）是纯数学代码，它**完全不认识** Unity，
//   不知道什么是特效、什么是 UI、什么是音效。这是故意的：
//   只有这样，我们才能在没有 Unity 的环境下用 Python 脚本把战斗跑一遍来验证数值。
//   但玩家需要看到特效、听到音效。怎么办？
//   内核在关键时刻喊一嗓子："我放技能了！""我中毒了！"
//   至于听到之后播什么特效，是 Unity 那边的事，内核不管也不需要知道。
//   这种设计叫"接口隔离"或"观察者模式"，是解耦的经典手段。
//
// · 什么是 interface（接口）？
//   一份"能力清单"，只写方法名不写具体实现。谁想当听众，
//   就得把清单上的方法全部实现一遍。好处是内核只认清单、不认具体是谁在听 ——
//   游戏里是 Unity 在听（放特效），跑测试时是空实现在听（什么都不做）。
//
// · 什么是 NullCombatEventsT3（空实现）？
//   一个"假装在听但其实什么都不做"的听众。为什么要它？
//   因为如果允许听众为 null，那内核里每一次喊话前都得写 if (听众 != null)，
//   全项目几十处，漏写一处就是空引用崩溃。
//   准备一个永远不出错的假听众当默认值，这些 if 就一个都不需要了。
//   这个技巧叫「空对象模式」。
//
// · 为什么不直接在旧的 ICombatEvents 上加方法？
//   因为那个接口有 3 个实现类，其中一个在测试文件里，
//   而那个测试文件是"一个字都不许改"的红线文件（它锁着 88 条验收断言）。
//   加方法就必须动它。虽然只是补几个空方法、不碰任何断言，
//   但 QA 在代码差异里看到测试文件被改过，就得额外花时间确认 ——
//   这笔信任成本比多写一个接口贵得多。
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// T3 新增的战斗事件回调。与既有 <see cref="ICombatEvents"/> 并列而非继承，
    /// 使既有实现全部零改动。内核内部不做 null 判断，统一回落到
    /// <see cref="NullCombatEventsT3.Instance"/>。
    ///
    /// 【新手解释】战斗内核的"广播喇叭清单"：里面每一个方法都是一种通知。
    /// Unity 那边实现这份清单后，就能在对应时刻放特效、刷 UI、播音效。
    /// </summary>
    public interface ICombatEventsT3
    {
        /// <summary>技能起手成功（已扣资源、已进 CD、已进前摇）。</summary>
        /// <param name="caster">施法者。</param>
        /// <param name="def">技能定义。</param>
        void OnSkillCast(Combatant caster, SkillDef def);

        /// <summary>技能命中单个目标（AOE 时每个目标各触发一次）。</summary>
        /// <param name="caster">施法者。</param>
        /// <param name="target">被命中者。</param>
        /// <param name="def">技能定义。</param>
        /// <param name="dmg">实际扣除的血量。</param>
        void OnSkillHit(Combatant caster, Combatant target, SkillDef def, float dmg);

        /// <summary>闪避起手成功。</summary>
        /// <param name="who">闪避者。</param>
        /// <param name="dir">闪避方向（已归一化）。</param>
        void OnDodgeStart(Combatant who, Vec2 dir);

        /// <summary>状态首次附着到目标身上。</summary>
        /// <param name="target">目标。</param>
        /// <param name="status">在体状态实例。</param>
        void OnStatusApplied(Combatant target, ActiveStatus status);

        /// <summary>已有状态的层数或剩余时长发生变化（叠层 / 刷新）。</summary>
        /// <param name="target">目标。</param>
        /// <param name="status">在体状态实例。</param>
        void OnStatusStackChanged(Combatant target, ActiveStatus status);

        /// <summary>状态到期并已从目标身上移除。</summary>
        /// <param name="target">目标。</param>
        /// <param name="statusId">状态 id。</param>
        void OnStatusExpired(Combatant target, string statusId);

        /// <summary>
        /// 资源发生变化（消耗 / 因不足而被拒绝）。HUD 据此播放"灵力条闪红"。
        /// </summary>
        /// <param name="who">资源持有者。</param>
        /// <param name="isQi">true = 灵力，false = 体力。</param>
        /// <param name="current">当前值。</param>
        /// <param name="max">上限。</param>
        void OnResourceChanged(Combatant who, bool isQi, float current, float max);

        /// <summary>软索敌把朝向吸附到了某个敌人身上（P0-07）。</summary>
        /// <param name="who">施法者。</param>
        /// <param name="facing">吸附后的朝向（已归一化）。</param>
        void OnFacingSnapped(Combatant who, Vec2 facing);

        /// <summary>连击数变化（P1-06；P0 阶段恒为 0 / 1.0）。</summary>
        /// <param name="combo">当前连击数。</param>
        /// <param name="mult">当前增伤倍率。</param>
        void OnComboChanged(int combo, float mult);
    }

    /// <summary>
    /// 空实现单例。内核默认持有它，于是所有调用点都不需要写 null 判断
    /// （与 <see cref="NullCombatEvents"/> 同一范式）。
    ///
    /// 【新手解释】"假装在听、其实什么都不做"的默认听众。
    /// 有了它，内核里所有喊话的地方都能直接喊，不用先检查有没有人听。
    /// "单例"是指全局只造一个、大家共用 —— 反正它没有任何状态，造一万个也一样。
    /// 构造函数写成 private 就是为了禁止外面 new 出第二个。
    /// </summary>
    public sealed class NullCombatEventsT3 : ICombatEventsT3
    {
        /// <summary>全局唯一实例。</summary>
        public static readonly NullCombatEventsT3 Instance = new NullCombatEventsT3();

        private NullCombatEventsT3()
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="caster">施法者。</param>
        /// <param name="def">技能定义。</param>
        public void OnSkillCast(Combatant caster, SkillDef def)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="caster">施法者。</param>
        /// <param name="target">被命中者。</param>
        /// <param name="def">技能定义。</param>
        /// <param name="dmg">实伤。</param>
        public void OnSkillHit(Combatant caster, Combatant target, SkillDef def, float dmg)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="who">闪避者。</param>
        /// <param name="dir">方向。</param>
        public void OnDodgeStart(Combatant who, Vec2 dir)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="target">目标。</param>
        /// <param name="status">状态实例。</param>
        public void OnStatusApplied(Combatant target, ActiveStatus status)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="target">目标。</param>
        /// <param name="status">状态实例。</param>
        public void OnStatusStackChanged(Combatant target, ActiveStatus status)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="target">目标。</param>
        /// <param name="statusId">状态 id。</param>
        public void OnStatusExpired(Combatant target, string statusId)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="who">持有者。</param>
        /// <param name="isQi">是否灵力。</param>
        /// <param name="current">当前值。</param>
        /// <param name="max">上限。</param>
        public void OnResourceChanged(Combatant who, bool isQi, float current, float max)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="who">施法者。</param>
        /// <param name="facing">朝向。</param>
        public void OnFacingSnapped(Combatant who, Vec2 facing)
        {
        }

        /// <summary>空实现。</summary>
        /// <param name="combo">连击数。</param>
        /// <param name="mult">倍率。</param>
        public void OnComboChanged(int combo, float mult)
        {
        }
    }
}
