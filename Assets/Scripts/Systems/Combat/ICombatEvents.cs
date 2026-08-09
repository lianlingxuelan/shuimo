// -----------------------------------------------------------------------------
// ICombatEvents.cs —— 战斗事件回调接口（引擎无关）
//
// 【它解决什么问题】
// 内核算得出「这一击生效了」「BOSS 进 P3 了」「该放冲击波了」，但它不能也不该
// 知道怎么播特效、怎么放音效、怎么 Instantiate 一个 GameObject。所有需要引擎
// 能力的动作都从这个接口出去，由 Combat/Unity/CombatEventsUnity.cs 落地。
// 反过来，单元测试与 Python 对拍注入一个记录型实现，就能断言「事件按时触发、
// 参数正确」——这正是 T1-09 / T1-10 / T1-11 的验收方式。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// 战斗事件出口。内核只调用，不实现；实现由 Unity 壳或测试桩提供。
    /// 所有方法都必须能被高频调用（60Hz × 敌人数），实现方不得在里面做重活。
    /// </summary>
    public interface ICombatEvents
    {
        /// <summary>
        /// 一次命中尝试的结果。
        /// </summary>
        /// <param name="attacker">攻击方。</param>
        /// <param name="defender">承伤方。</param>
        /// <param name="dmg">实际生效伤害（被闸门拦下时为 0）。</param>
        /// <param name="applied">
        /// true = 真正生效（扣了血或触发闪避）；false = 被 W-CORE 两层闸门拦下。
        /// 只有 applied 才该放特效/音效，否则围攻时每帧都在闪。
        /// </param>
        void OnHit(Combatant attacker, Combatant defender, float dmg, bool applied);

        /// <summary>敌人血量归零。掉落 / 经验 / 尸体特效在这里接。</summary>
        void OnEnemyDeath(Combatant e);

        /// <summary>BOSS 跨阶段。同一阶段只会触发一次（向下跨阈值时）。</summary>
        void OnBossPhase(BossPhase phase);

        /// <summary>BOSS 召唤小怪。n = 本次召唤数量。</summary>
        void OnSummon(int n);

        /// <summary>BOSS 冲击波。壳据半径做视觉；命中判定已在内核用纯几何完成。</summary>
        void OnShockwave(float radius, Vec2 center);

        /// <summary>击退脉冲已写入目标速度。壳可据此播放踉跄动画。</summary>
        void OnKnockback(Combatant e, Vec2 impulse);
    }

    /// <summary>
    /// 空实现。内核内部从不对 <see cref="ICombatEvents"/> 做 null 判断，
    /// 而是统一回落到本单例——少写几十处 `if (ev != null)`，也杜绝漏判空。
    /// </summary>
    public sealed class NullCombatEvents : ICombatEvents
    {
        /// <summary>全局唯一实例（无状态，线程安全）。</summary>
        public static readonly NullCombatEvents Instance = new NullCombatEvents();

        private NullCombatEvents() { }

        /// <inheritdoc />
        public void OnHit(Combatant attacker, Combatant defender, float dmg, bool applied) { }

        /// <inheritdoc />
        public void OnEnemyDeath(Combatant e) { }

        /// <inheritdoc />
        public void OnBossPhase(BossPhase phase) { }

        /// <inheritdoc />
        public void OnSummon(int n) { }

        /// <inheritdoc />
        public void OnShockwave(float radius, Vec2 center) { }

        /// <inheritdoc />
        public void OnKnockback(Combatant e, Vec2 impulse) { }
    }
}
