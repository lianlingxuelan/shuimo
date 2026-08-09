// -----------------------------------------------------------------------------
// ResourcePool.cs —— 灵力 / 体力共用的资源池（引擎无关）
//
// 【为什么两个池共用一个类而不是各写一个】
// Q2 拍板为双池：灵力(Qi) 管技能、体力(Stamina) 管闪避。两者的曲线形状完全相同
// （线性回复 + 脱战倍率 + 空闲判定 + 消耗后回复锁），差异全在注入的常量上。
// 各写一份的必然结果是「体力那条忘了写脱战加速」这类不对称 bug —— 而且极难发现，
// 因为两条曲线单独看都"能跑"。共用一份经过单测的推进代码，不对称就无从产生。
//
// 【回复必须按逻辑帧推进，绝不允许用 Time.deltaTime】
// Current += RegenPerSec * CombatScheduler.FixedStep，在 Encounter.StepFixed 的 ①-A
// 阶段执行。若在 Unity 的 Update 里按真实时间累加，就会出现「帧率越高回蓝越多」——
// 这是最典型的"性能即数值"漏洞，且在 60fps 的开发机上永远测不出来。
//
// 【为什么体力要有"消耗后回复锁"】
// 没有它，体力回复速度（18/s）会让贴地连滚变成一种位移技：翻完立刻回够下一次的量。
// 锁 21 帧（0.35s）之后，稳态翻滚间隔约 1.39s，长于 CD 0.8s ——
// **体力才是闪避的真实约束，CD 只是防抖**，这正是 G1「攻守取舍」想要的形状。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是「蓝条」和「耐力条」共用的那一段代码。
//
// · 游戏里有两条资源条（这是产品拍板的 Q2 决定）：
//     灵力 Qi      —— 俗称"蓝"。放技能要花它。上限 100，战斗中每秒回 8 点。
//     体力 Stamina —— 俗称"耐力"。翻滚要花它。上限 100，战斗中每秒回 18 点。
//   为什么体力回得比蓝快？因为翻滚是唯一的保命手段，
//   要是因为"没资源"而被打死，玩家只会觉得系统在耍赖，而不是自己菜。
//
// · 为什么两条条共用一个类？
//   它们的规则形状完全一样（线性回复 + 脱战加速 + 消耗后短暂停回），
//   只是数字不同。要是写成两份代码，早晚会出现"体力那份忘了写脱战加速"
//   这种极难发现的 bug —— 因为两条单独看都能跑。
//   共用一份经过测试的代码，"不对称"就从根上没法产生。
//
// · 名词解释
//     脱战(OutOfCombat) —— 一段时间没打架也没挨打，就算脱离战斗，
//                           此时资源回得更快，鼓励"拉开距离喘口气再上"。
//     回复锁(RegenLock) —— 刚花掉资源后的一小段时间里禁止回复。
//                           体力有 0.35 秒的锁，专门用来防"贴着怪无限连滚"。
//                           没有它，18/秒的回速会让翻滚退化成一个位移技能。
//
// · ⚠️ 一个新手最容易犯的致命错误
//   千万不要在 Unity 的 Update() 里用 Time.deltaTime 来回蓝。
//   那样写的话，144Hz 电竞屏的玩家每秒回蓝会比 60Hz 的玩家多一倍 ——
//   "换个显示器就变强"，这在业内叫「性能即数值」漏洞。
//   本文件的 TickFrame 由每秒固定 60 次的战斗时钟驱动，从机制上杜绝了这件事。
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// 一个可消耗 / 自然回复的资源池。<see cref="Combatant"/> 上挂两个实例
    /// （<c>Qi</c> 与 <c>Stamina</c>），参数由 <see cref="SkillConfig"/> 注入。
    ///
    /// **可空组件**：未挂载时 <see cref="Encounter"/> 的 ①-A 阶段直接跳过。
    ///
    /// 【新手解释】一根"会自己慢慢回满的条"。玩家身上挂了两根：一根当蓝条，
    /// 一根当耐力条。你在 HUD 上看到的那两条彩色进度条，读的就是这里的数字。
    /// </summary>
    public sealed class ResourcePool
    {
        /// <summary>
        /// 可负担判定的浮点容差。
        /// 没有它，"回满 100 却因为 99.99999 付不起 100 的技能"会随机复现。
        /// </summary>
        public const float AffordEpsilon = 1e-4f;

        /// <summary>当前值。</summary>
        public float Current;

        /// <summary>上限。</summary>
        public float Max = 100.0f;

        /// <summary>战斗中的回复速率（每秒）。</summary>
        public float RegenPerSec;

        /// <summary>脱战倍率。脱战后回复速率 = <see cref="RegenPerSec"/> × 本值。</summary>
        public float OutOfCombatMult = 1.0f;

        /// <summary>连续多少逻辑帧无活动即判定为脱战。</summary>
        public int OutOfCombatFrames = 120;

        /// <summary>每次消耗后锁定回复的帧数（0 = 不锁）。</summary>
        public int RegenLockOnSpend;

        // 已连续空闲的帧数。上限夹在 OutOfCombatFrames，避免长时间挂机后整数溢出。
        private int _idleFrames;

        // 回复锁剩余帧数。
        private int _lockFrames;

        /// <summary>当前占比，夹在 [0,1]。HUD 直接读它。</summary>
        public float Ratio
        {
            get
            {
                if (Max <= 0.0f)
                {
                    return 0.0f;
                }
                float r = Current / Max;
                if (r < 0.0f)
                {
                    return 0.0f;
                }
                return r > 1.0f ? 1.0f : r;
            }
        }

        /// <summary>是否已进入脱战状态（回复加速中）。</summary>
        public bool IsOutOfCombat
        {
            get { return _idleFrames >= OutOfCombatFrames; }
        }

        /// <summary>回复是否被锁（刚消耗过）。</summary>
        public bool IsRegenLocked
        {
            get { return _lockFrames > 0; }
        }

        /// <summary>回复锁剩余帧数（调试 / 单测用）。</summary>
        public int RegenLockRemain
        {
            get { return _lockFrames; }
        }

        /// <summary>已连续空闲帧数（调试 / 单测用）。</summary>
        public int IdleFrames
        {
            get { return _idleFrames; }
        }

        /// <summary>是否付得起指定开销（含浮点容差）。</summary>
        /// <remarks>
        /// 【新手解释】"我这点蓝够不够放这一招？" 只查询、不扣除。
        /// 什么时候被调用：按下技能键后、真正起手之前的资格审查。
        /// 为什么要加个 0.0001 的容差？因为电脑存小数有微小误差，
        /// 蓝条回满时实际可能是 99.99999 而不是 100，
        /// 不加容差就会出现"明明满蓝却放不出 100 费技能"的灵异事件。
        /// </remarks>
        /// <param name="cost">开销。&lt;= 0 恒为 true。</param>
        /// <returns>true = 付得起。</returns>
        public bool CanAfford(float cost)
        {
            if (cost <= 0.0f)
            {
                return true;
            }
            return Current + AffordEpsilon >= cost;
        }

        /// <summary>
        /// 尝试扣除。失败时**不产生任何副作用**（不扣、不锁、不重置空闲计数）。
        /// </summary>
        /// <remarks>
        /// 【新手解释】真正"扣蓝"。什么时候被调用：技能资格审查全部通过、
        /// 确定要放这一招的那一刻。
        /// 关键：**失败时一点副作用都没有**（不扣、不锁、不重置计时）。
        /// 这是"蓝不够时技能不触发也不空耗"（PRD P0-03 ①）的实现基础。
        /// </remarks>
        /// <param name="cost">开销。&lt;= 0 时视为成功且不触发回复锁。</param>
        /// <returns>true = 扣除成功。</returns>
        public bool TrySpend(float cost)
        {
            if (cost <= 0.0f)
            {
                return true;
            }
            if (!CanAfford(cost))
            {
                return false;
            }

            Current -= cost;
            if (Current < 0.0f)
            {
                Current = 0.0f;
            }
            _lockFrames = RegenLockOnSpend > 0 ? RegenLockOnSpend : 0;
            _idleFrames = 0;
            return true;
        }

        /// <summary>
        /// 推进一个逻辑帧。<paramref name="fixedStep"/> 必须传
        /// <see cref="CombatScheduler.FixedStep"/>（由 <see cref="Encounter.StepFixed"/> 转发的 dt）。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"让条子自己回一点"。每秒被调用 60 次，由战斗时钟驱动。
        /// 内部顺序：先累加空闲计时（用来判断脱没脱战）→ 再看回复锁是否还在
        /// （在的话这一帧不回）→ 最后按 每秒回速 ÷ 60 加上去。
        /// 参数 fixedStep 永远是 1/60 秒，由 Encounter 传进来，
        /// 绝不能自己写死 0.0167 —— 那个近似值累积 13 次就会让数值跑偏。
        /// </remarks>
        /// <param name="fixedStep">固定步长（秒），恒为 1/60。</param>
        public void TickFrame(float fixedStep)
        {
            if (_idleFrames < OutOfCombatFrames)
            {
                _idleFrames++;
            }

            if (_lockFrames > 0)
            {
                _lockFrames--;
                return;
            }

            if (Current >= Max)
            {
                Current = Max;
                return;
            }

            float rate = IsOutOfCombat ? RegenPerSec * OutOfCombatMult : RegenPerSec;
            if (rate <= 0.0f)
            {
                return;
            }

            Current += rate * fixedStep;
            if (Current > Max)
            {
                Current = Max;
            }
        }

        /// <summary>
        /// 标记"有活动"（施法 / 闪避 / 受击），重新开始脱战计时。
        /// 受击侧由 <see cref="Encounter"/> 在 ①-A 阶段按血量变化调用。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"打架了！脱战计时重新数。" 一挨打或一出手就调它，
        /// 于是"脱战加速回复"这个奖励只有真正脱离战斗才拿得到。
        /// </remarks>
        public void NotifyActivity()
        {
            _idleFrames = 0;
        }

        /// <summary>直接加值（不触发回复锁）。用于 P2 的"完美闪避灵力立回"等场景。</summary>
        /// <param name="amount">增量。&lt;= 0 时忽略。</param>
        public void Add(float amount)
        {
            if (amount <= 0.0f)
            {
                return;
            }
            Current += amount;
            if (Current > Max)
            {
                Current = Max;
            }
        }

        /// <summary>回满并清空全部内部计时。</summary>
        public void Fill()
        {
            Current = Max;
            _idleFrames = OutOfCombatFrames;
            _lockFrames = 0;
        }

        /// <summary>完全复位（切区 / 重生）。</summary>
        public void Reset()
        {
            Fill();
        }

        /// <summary>
        /// 工厂：按 <see cref="SkillConfig"/> 造一个灵力池（管技能）。
        /// </summary>
        /// <returns>已回满的灵力池。</returns>
        public static ResourcePool CreateQi()
        {
            ResourcePool p = new ResourcePool();
            p.Max = SkillConfig.QI_MAX;
            p.RegenPerSec = SkillConfig.QI_REGEN;
            p.OutOfCombatMult = SkillConfig.QI_OOC_MULT;
            p.OutOfCombatFrames = SkillConfig.QI_OOC_FRAMES;
            p.RegenLockOnSpend = SkillConfig.QI_REGEN_LOCK_FRAMES;
            p.Fill();
            return p;
        }

        /// <summary>
        /// 工厂：按 <see cref="SkillConfig"/> 造一个体力池（管闪避）。
        /// </summary>
        /// <returns>已回满的体力池。</returns>
        public static ResourcePool CreateStamina()
        {
            ResourcePool p = new ResourcePool();
            p.Max = SkillConfig.STAM_MAX;
            p.RegenPerSec = SkillConfig.STAM_REGEN;
            p.OutOfCombatMult = SkillConfig.STAM_OOC_MULT;
            p.OutOfCombatFrames = SkillConfig.STAM_OOC_FRAMES;
            p.RegenLockOnSpend = SkillConfig.STAM_REGEN_LOCK_FRAMES;
            p.Fill();
            return p;
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("{0:F1}/{1:F1}{2}{3}",
                Current, Max,
                IsOutOfCombat ? " [OOC]" : string.Empty,
                IsRegenLocked ? " [LOCK" + _lockFrames + "]" : string.Empty);
        }
    }
}
