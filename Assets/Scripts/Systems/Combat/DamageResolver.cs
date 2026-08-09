// -----------------------------------------------------------------------------
// DamageResolver.cs —— 伤害路由（引擎无关）
//
// 【为什么要一个"路由器"】
// 玩家与敌人的承伤模型刻意不同构：
//   · 玩家 → W-CORE 两层闸门 + 除法软上限减伤（Difficulty.DamageTaken）
//   · 敌人 → 减法模型 real = max(1, raw − max(0, armor − breakDef)) + 霸体/击退
// 如果让调用点自己判断"我该走哪套"，这个 if 会散布到 AI、技能、陷阱、DOT……
// 每漏一处就是一个数值漏洞。全部收敛到这里，调用点只说"A 打 B"。
//
// 【为什么它是 static】
// 伤害结算是纯函数式的：输入 (攻击方, 承伤方, 数值) → 输出 (是否生效, 实伤)。
// 它不该有任何跨调用的状态——所有状态（冷却、韧性、血量）都住在 Combatant 里。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using Xianxia.Core;

namespace Xianxia.Combat
{
    /// <summary>伤害路由与两套承伤模型的落地实现。</summary>
    public static class DamageResolver
    {
        /// <summary>
        /// 接触伤害结算（敌人 → 玩家）。
        /// 距离判定用纯几何 <c>Vec2.DistanceTo &lt;= range</c>，不依赖任何碰撞体，
        /// 因此内核可在无引擎环境下逐位复现（这是全部对拍用例的地基）。
        /// </summary>
        /// <param name="attacker">攻击方（敌人）。</param>
        /// <param name="defender">承伤方（玩家）。</param>
        /// <param name="range">判定半径，通常传 <c>CombatConfig.TOUCH_RANGE</c>(44)。</param>
        /// <param name="ev">事件出口，可为 null。</param>
        /// <returns>true = 本次命中真正生效（未被 W-CORE 闸门拦下）。</returns>
        public static bool ResolveContact(Combatant attacker, Combatant defender, float range, ICombatEvents ev)
        {
            if (attacker == null || defender == null)
            {
                return false;
            }
            if (!attacker.IsAlive || !defender.IsAlive)
            {
                return false;
            }

            if (attacker.Position.DistanceTo(defender.Position) > range)
            {
                return false;
            }

            float mult = attacker.AI != null ? attacker.AI.CurrentDamageMult : 1.0f;
            float raw = attacker.ContactDamage * mult;

            return ApplyToPlayer(defender, attacker.Id, raw, attacker, ev);
        }

        /// <summary>
        /// 冲击波 AoE 结算（BOSS P3）。中心点到玩家的距离 ≤ radius 即命中。
        /// 玩家仍受 W-CORE 两层闸门约束，也能被闪避无敌（<c>WCore.Iframe</c>）规避。
        /// </summary>
        /// <param name="source">波源（BOSS），用于取 source id 与伤害基数。</param>
        /// <param name="defender">玩家。</param>
        /// <param name="center">冲击波中心。</param>
        /// <param name="radius">冲击波半径。</param>
        /// <param name="ev">事件出口，可为 null。</param>
        /// <returns>true = 命中且生效。</returns>
        public static bool ResolveShockwave(Combatant source, Combatant defender, Vec2 center, float radius, ICombatEvents ev)
        {
            if (source == null || defender == null || !defender.IsAlive)
            {
                return false;
            }
            if (center.DistanceTo(defender.Position) > radius)
            {
                return false;
            }

            float mult = source.AI != null ? source.AI.CurrentDamageMult : 1.0f;
            float raw = source.ContactDamage * mult;
            return ApplyToPlayer(defender, source.Id, raw, source, ev);
        }

        /// <summary>
        /// 【R1 钩子】玩家攻击敌人。
        ///
        /// T1 只交付路由与减法承伤这一段；**具体的技能伤害公式（暴击 / 属性 / 倍率）
        /// 属于 T2 的数值层**，此处的 <paramref name="raw"/> 由上层算好后传入。
        /// <c>player.BreakDef</c> 默认 0，穿甲生效后无需改动本函数。
        /// </summary>
        /// <param name="player">攻击方（玩家）。</param>
        /// <param name="target">承伤方（敌人）。</param>
        /// <param name="raw">已算好的技能输出（不再二次乘攻击力或暴击）。</param>
        /// <param name="ev">事件出口，可为 null。</param>
        /// <returns>实际扣除的血量；未命中或无效时返回 0。</returns>
        public static float ResolvePlayerAttack(Combatant player, Combatant target, float raw, ICombatEvents ev)
        {
            if (player == null || target == null || !target.IsAlive)
            {
                return 0.0f;
            }
            return ApplyToEnemy(target, raw, player, ev);
        }

        // ---------------------------------------------------------------------
        // 敌人侧：减法模型 + 霸体 + 击退
        // ---------------------------------------------------------------------

        /// <summary>
        /// 对敌人落实伤害：减法承伤 → 扣韧性 → 破韧则施加硬直 + 击退脉冲 → 死亡事件。
        /// </summary>
        /// <param name="target">承伤敌人。</param>
        /// <param name="raw">攻击方输出。</param>
        /// <param name="attacker">攻击方（取 BreakDef 与击退方向），可为 null。</param>
        /// <param name="ev">事件出口，可为 null。</param>
        /// <returns>实际扣除的血量。</returns>
        public static float ApplyToEnemy(Combatant target, float raw, Combatant attacker, ICombatEvents ev)
        {
            ICombatEvents evt = ev ?? NullCombatEvents.Instance;

            float breakDef = attacker != null ? attacker.BreakDef : 0.0f;
            float real = target.ApplyEnemyDamage(raw, breakDef);

            evt.OnHit(attacker, target, real, true);

            // 韧性：按实伤扣。破韧才给硬直与击退，平时挨打不退——这正是霸体的意义。
            if (target.DamagePoise(real))
            {
                target.ApplyHitStun(CombatConfig.POISE_BREAK_HITSTUN);

                Vec2 dir = attacker != null
                    ? attacker.Position.DirectionTo(target.Position)
                    : Vec2.Right;
                Vec2 impulse = Combatant.KnockbackImpulse(dir, CombatConfig.POISE_BREAK_KNOCKBACK_DIST);
                target.TakeKnockback(impulse);
                evt.OnKnockback(target, impulse);
            }

            if (!target.IsAlive)
            {
                evt.OnEnemyDeath(target);
            }
            return real;
        }

        // ---------------------------------------------------------------------
        // 玩家侧：W-CORE 两层闸门
        // ---------------------------------------------------------------------

        /// <summary>
        /// 对玩家落实伤害：全部走 <c>WCoreState.TakeDamageFrom</c>，由它裁决两层闸门。
        ///
        /// 【R6 说明】<see cref="Faction.Companion"/> 的 0.5x 减伤本次**不实现**。
        /// 枚举位已保留；实装时只需在此处按 <c>target.Faction</c> 分流一次乘法。
        /// 现在就加，会让「围攻频率 4.300 / 倍率 2.53x」这条基线掺进未验收的数值分支。
        /// </summary>
        /// <param name="target">玩家。</param>
        /// <param name="srcId">伤害来源实例 id（W-CORE 每来源冷却表的 key）。</param>
        /// <param name="raw">原始伤害。减伤由 <c>WCore.DamageFilter</c> 注入。</param>
        /// <param name="attacker">攻击方，仅用于事件回传，可为 null。</param>
        /// <param name="ev">事件出口，可为 null。</param>
        /// <returns>true = 本次真正生效（扣血或触发闪避）。</returns>
        public static bool ApplyToPlayer(Combatant target, int srcId, float raw, Combatant attacker, ICombatEvents ev)
        {
            ICombatEvents evt = ev ?? NullCombatEvents.Instance;

            WCoreState core = target.WCore;
            if (core == null)
            {
                // 没有 W-CORE 的"玩家"只可能是配置错误。退化为减法模型而不是静默丢弃，
                // 让问题在 QA 阶段以"掉血异常"的形式暴露，而不是变成打不动的幽灵。
                float fallback = target.ApplyEnemyDamage(raw, 0.0f);
                evt.OnHit(attacker, target, fallback, true);
                return true;
            }

            float hpBefore = core.Hp;
            bool applied = core.TakeDamageFrom(srcId, raw);
            float dealt = applied ? hpBefore - core.Hp : 0.0f;

            evt.OnHit(attacker, target, dealt, applied);
            return applied;
        }

        /// <summary>
        /// 便捷重载：仅按减法模型计算「实伤」而不落地，供 UI 预览 / 单元测试使用。
        /// <c>real = max(1, raw − max(0, armor − breakDef))</c>
        /// </summary>
        public static float PreviewSubtractive(float raw, float armor, float breakDef)
        {
            float eff = armor - breakDef;
            if (eff < 0.0f)
            {
                eff = 0.0f;
            }
            float real = raw - eff;
            return real < CombatConfig.ENEMY_DAMAGE_FLOOR ? CombatConfig.ENEMY_DAMAGE_FLOOR : real;
        }

        // =====================================================================
        // T3 增量：技能命中 / DOT 跳伤
        //
        // 【上面六个方法一行未动】
        // ResolveContact / ResolveShockwave / ResolvePlayerAttack / ApplyToEnemy /
        // ApplyToPlayer / PreviewSubtractive 保持字节级不变。下面全部是新增入口，
        // 未装配 T3 组件时没有任何调用点会走到这里。
        // =====================================================================

        /// <summary>
        /// 对敌人落实伤害的 T3 通用版：<b>可指定破防值与韧性伤害</b>。
        ///
        /// 【为什么不直接改 <see cref="ApplyToEnemy"/> 加两个参数】
        /// 那个方法是 U1/T2 验收路径上的既有实现，红线要求签名与逻辑不动。
        /// 这里另开一个入口，代价是两段结构相似的代码。
        /// ⚠️ <b>它与 ApplyToEnemy 必须保持行为同构：改一处必须改另一处。</b>
        /// 为了让"忘了改另一处"从静默漂移变成红灯，
        /// <c>T3CombatDepthTests.ApplyToEnemyWithPoise_EquivalentToLegacy</c>
        /// 用一组输入矩阵断言二者结果逐位相同（poiseDamage &lt; 0 时）。
        /// T4 合并两条路径时删掉旧的即可。
        ///
        /// 【为什么韧性伤害要与血量伤害分离】
        /// PRD §4.4 给三个技能配了独立的 poise（10 / 30 / 5）。
        /// 法阵冲击的"群体震开"正是靠 poise 30 == <c>POISE_MAX_DEFAULT</c> 一击破韧，
        /// 而它的实伤只有约 18 —— 如果沿用"按实伤扣韧性"，这个设计意图直接落空。
        /// </summary>
        /// <param name="target">承伤敌人。</param>
        /// <param name="raw">攻击方输出。</param>
        /// <param name="breakDef">攻击方破防值（DOT 走施加者快照，因此不从 attacker 取）。</param>
        /// <param name="poiseDamage">
        /// 韧性伤害。<b>负值表示"沿用实伤"</b>（与 <see cref="ApplyToEnemy"/> 同语义）；
        /// 0 表示完全不扣韧性（DOT 跳伤用，毒不该把怪震开）。
        /// </param>
        /// <param name="attacker">攻击方，用于击退方向与事件回传，可为 null。</param>
        /// <param name="ev">事件出口，可为 null。</param>
        /// <returns>实际扣除的血量。</returns>
        public static float ApplyToEnemyWithPoise(
            Combatant target,
            float raw,
            float breakDef,
            float poiseDamage,
            Combatant attacker,
            ICombatEvents ev)
        {
            if (target == null)
            {
                return 0.0f;
            }
            ICombatEvents evt = ev ?? NullCombatEvents.Instance;

            float real = target.ApplyEnemyDamage(raw, breakDef);

            evt.OnHit(attacker, target, real, true);

            float poise = poiseDamage < 0.0f ? real : poiseDamage;
            if (poise > 0.0f && target.DamagePoise(poise))
            {
                target.ApplyHitStun(CombatConfig.POISE_BREAK_HITSTUN);

                Vec2 dir = attacker != null
                    ? attacker.Position.DirectionTo(target.Position)
                    : Vec2.Right;
                Vec2 impulse = Combatant.KnockbackImpulse(dir, CombatConfig.POISE_BREAK_KNOCKBACK_DIST);
                target.TakeKnockback(impulse);
                evt.OnKnockback(target, impulse);
            }

            if (!target.IsAlive)
            {
                evt.OnEnemyDeath(target);
            }
            return real;
        }

        /// <summary>
        /// 技能命中单个目标：扣血 → 扣韧性 → 附着状态 → 发事件。
        ///
        /// 【为什么状态附着在扣血之后】
        /// 法阵冲击附着的是破防（护甲 −40%）。若先附着再扣血，这一击自己就吃到了减甲，
        /// 于是"破防"这个后效会凭空多出一次即时收益，与"后效"的语义矛盾，
        /// 也让技能的实际伤害无法从数值表上算出来。
        ///
        /// 【为什么 Chance &gt;= 1 时连 rng 都不碰】（§7.3-4）
        /// 抽样序列只应被真正的随机事件推进。若 100% 附着也去摇一次骰，
        /// 那么"这个技能命中了几个目标"就会改变后续所有随机数 ——
        /// 同种子对拍会因为玩家多打中一只怪而整条流错位。
        ///
        /// 【目标已死时不附着】
        /// 尸体上挂毒没有意义（⑥ 阶段就会被移除），但会真的生成一次 VFX 与图标。
        /// </summary>
        /// <param name="caster">施法者。</param>
        /// <param name="target">被命中者。</param>
        /// <param name="def">技能定义。</param>
        /// <param name="comboMult">连击增伤倍率（P1-06；P0 固定传 1.0f）。</param>
        /// <param name="rng">技能随机流。可为 null（此时只有 Chance &gt;= 1 的附着会生效）。</param>
        /// <param name="evT3">T3 事件出口，可为 null。</param>
        /// <param name="ev">既有事件出口，可为 null。</param>
        /// <returns>实际扣除的血量；无伤害技能返回 0。</returns>
        public static float ResolveSkillHit(
            Combatant caster,
            Combatant target,
            SkillDef def,
            float comboMult,
            PCG32 rng,
            ICombatEventsT3 evT3,
            ICombatEvents ev)
        {
            if (target == null || def == null || !target.IsAlive)
            {
                return 0.0f;
            }
            ICombatEventsT3 evtT3 = evT3 ?? NullCombatEventsT3.Instance;

            float mult = comboMult > 0.0f ? comboMult : 1.0f;
            float real = 0.0f;

            if (def.DealsDamage)
            {
                float breakDef = caster != null ? caster.BreakDef : 0.0f;
                real = ApplyToEnemyWithPoise(target, def.Raw * mult, breakDef, def.PoiseDamage, caster, ev);
            }

            ApplySkillEffects(caster, target, def, rng, evtT3);

            evtT3.OnSkillHit(caster, target, def, real);
            return real;
        }

        /// <summary>
        /// 把技能的 <see cref="SkillDef.Effects"/> 逐条尝试附着到目标身上。
        /// 抽出成独立方法，是为了让"附着规则"只有一处实现 ——
        /// P1 的敌方技能、P2 的蓄力重攻击都会复用它。
        /// </summary>
        /// <param name="caster">施法者（用于生成快照），可为 null。</param>
        /// <param name="target">目标。</param>
        /// <param name="def">技能定义。</param>
        /// <param name="rng">技能随机流，可为 null。</param>
        /// <param name="evT3">T3 事件出口（调用方保证非 null）。</param>
        private static void ApplySkillEffects(
            Combatant caster,
            Combatant target,
            SkillDef def,
            PCG32 rng,
            ICombatEventsT3 evT3)
        {
            StatusApplication[] effects = def.Effects;
            if (effects == null || effects.Length == 0)
            {
                return;
            }

            StatusComponent sc = target.Status;
            if (sc == null || sc.Table == null || !target.IsAlive)
            {
                return;
            }

            SourceSnapshot snap = caster != null
                ? new SourceSnapshot(caster.Id, caster.BreakDef, 1.0f)
                : SourceSnapshot.None();

            for (int i = 0; i < effects.Length; i++)
            {
                StatusApplication app = effects[i];
                StatusEffectDef sdef = sc.Table.Get(app.StatusId);
                if (sdef == null)
                {
                    continue;
                }

                if (app.Chance < 1.0f)
                {
                    if (rng == null || !rng.Chance(app.Chance))
                    {
                        continue;
                    }
                }

                sc.Apply(sdef, snap, evT3);
            }
        }

        /// <summary>
        /// 结算一条 DOT 跳伤（P0-06 验收①：与直接命中共用同一个伤害入口）。
        ///
        /// 【为什么 DOT 的韧性伤害恒为 0】
        /// 中毒的怪不该被自己的毒震开。若沿用"按实伤扣韧性"，蛊毒 5 层每 0.5s 跳 10 点，
        /// 三跳就能破一次韧 —— 怪会被毒推着走，走位与围攻节奏彻底失控。
        ///
        /// 【为什么破防取自快照而不是当前施加者】
        /// 见 ActiveStatus.cs 的 SourceSnapshot 注释：施加者可能已死亡并被移出列表。
        ///
        /// 【为什么按值传 DotTick 而不是 in】
        /// <c>in</c> 参数要求 C# 7.2+。这个 struct 只有四个字段（一引用 + 一 float +
        /// 一 12 字节快照 + 一引用），拷贝成本远低于为了省几个字节而给工程加一条
        /// 语言版本约束。
        ///
        /// 【为什么 evT3 收了却一次都没用 —— 这是刻意的，不是漏写】
        /// 初学者读到这里最容易以为"作者忘了调 evT3"，于是好心补一行
        /// <c>evT3.OnDamage(...)</c>，那会直接破坏两条既定设计：
        ///   1. DOT 跳伤刻意不进 T3 事件流。T3 事件流驱动的是飘字、受击闪白、
        ///      连击数这些"打击反馈"表现；而 DOT 是每 0.5~0.6s 自动跳一次的背景伤害，
        ///      一旦接进去，挂满蛊毒+灼烧时屏幕会被跳字刷屏，真正的直接命中反而被淹没。
        ///   2. 这与 <c>Encounter.AddCombo</c> 的口径是同一条契约：DOT 同样不计连击
        ///      （见 Encounter.cs "为什么 DOT 跳伤不计入连击"）。两处必须同进同退，
        ///      只在这里单方面接上事件流，就会出现"飘字狂跳但连击数不动"的表现割裂。
        /// 那为什么还要把这个形参留着？因为签名是给 P1 预留的锚点：将来做
        /// "跳伤飘字分色"（毒=紫 / 灼=橙）时，改动只发生在方法体内部，
        /// 调用方 <c>Encounter.FlushDotTicks</c> 一行都不用动。
        /// 保留未使用形参的代价是一条编译器提示，收益是接口稳定 —— 这笔账划算。
        /// </summary>
        /// <param name="tick">跳伤描述。</param>
        /// <param name="ev">既有事件出口，可为 null。</param>
        /// <param name="evT3">T3 事件出口，可为 null（本方法当前不用，留给 P1 的跳伤飘字分色）。</param>
        /// <returns>实际扣除的血量；目标已死或伤害非正时返回 0。</returns>
        public static float ApplyDotTick(DotTick tick, ICombatEvents ev, ICombatEventsT3 evT3)
        {
            Combatant target = tick.Target;
            if (target == null || !target.IsAlive || tick.Damage <= 0.0f)
            {
                return 0.0f;
            }
            // 逐个参数看懂这一行（对照 ApplyToEnemyWithPoise 的签名）：
            //   tick.Damage          —— 跳伤数值，施加状态那一刻就算好了，这里只是照搬。
            //   tick.Source.BreakDef —— 破防取自"快照"，不是取自当前施加者（施加者可能已死）。
            //   0.0f (poiseDamage)   —— 韧性伤害恒为 0，理由见上面的文档注释。
            //   null (attacker)      —— 刻意不传攻击者。同理由：DOT 结算时施加者可能已经死了、
            //                           已经被移出战斗列表，硬传一个悬空引用只会招来空引用崩溃；
            //                           而 DOT 结算本身也用不到攻击者（伤害已由快照定死）。
            //   ev (不是 evT3)       —— 只走既有事件出口，刻意绕开 T3 事件流，理由见上。
            return ApplyToEnemyWithPoise(target, tick.Damage, tick.Source.BreakDef, 0.0f, null, ev);
        }
    }
}
