// -----------------------------------------------------------------------------
// CombatEventsT3Unity.cs —— T3 事件的 Unity 落地实现：内核喊一声，这里放特效
//
// 【先理解这个类存在的意义：它是"内核不认识 Unity"这条铁律的兑现方式】
// 战斗内核（Xianxia.Combat 程序集）被强制要求不引用 UnityEngine —— 那是护栏脚本
// 每次都会检查的红线。可是"技能命中时要闪白、要播音效"这类需求确实存在，怎么办？
//
// 答案是把它反过来：内核不去"调用"Unity，而是"广播"事件。
//   · 内核只认识 ICombatEventsT3 这个纯 C# 接口，命中时调 ev.OnSkillHit(...)；
//   · 本类实现该接口，在方法体里干 Unity 的活（播特效、闪白、打日志）；
//   · 装配时把本类的实例交给内核。内核全程不知道自己手里这个对象是 Unity 实现，
//     它可能是控制台版本、可能是单元测试里的计数器、也可能是什么都不做的空实现。
//
// 这就是"依赖倒置"+"观察者模式"的组合：
//   依赖方向从 [内核 → Unity] 反转成 [内核 → 接口 ← Unity]，
//   于是内核可以脱离 Unity 独立编译、独立测试、独立跑一万帧对拍。
// 读懂这个类，就读懂了本工程为什么能做到"逻辑与表现彻底分离"。
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>T3 战斗事件的 Unity 侧实现：把内核事件翻译成特效、音效与 HUD 刷新。</summary>
    public sealed class CombatEventsT3Unity : ICombatEventsT3
    {
        // 【下面这几个 Func/Action 字段是"回调注入"，比直接引用具体类更灵活】
        // 本类需要"根据战斗单位 id 找到它的显示对象"，但它不该自己去管理这张表
        // （那是 CombatBridge 的职责）。于是留一个函数指针，由装配方填进来。
        // 好处：本类不依赖任何具体的管理器，写测试时随便传个 lambda 就能跑。

        /// <summary>按战斗单位 id 查找其视图组件。由 CombatBridge 装配时注入。</summary>
        public Func<int, CombatView> ViewOf;

        /// <summary>玩家的 Transform。玩家没有 CombatView，需单独持有。</summary>
        public Transform PlayerTransform;

        /// <summary>播放音效的回调，可为 null（没有音频系统时静默跳过）。</summary>
        public Action<string> PlaySfx;

        /// <summary>资源变化回调，供 HUD 订阅。</summary>
        public Action<bool, float, float> ResourceChanged;

        /// <summary>连击变化回调，供 HUD 订阅。</summary>
        public Action<int, float> ComboChanged;

        /// <summary>
        /// 技能施放事件。参数：(caster, 技能定义, 施放朝向)。
        /// 供外部模块订阅做表现联动（如竹林砍竹检测），不用于战斗规则。
        /// </summary>
        public event Action<Combatant, SkillDef, Vector2> SkillCast;

        /// <summary>是否输出详细日志。默认关闭 —— 战斗中每次命中都打日志会严重拖慢帧率。</summary>
        public bool VerboseLog;

        // 下面这一批计数器/最近值属性，是给自动化测试和调试面板用的"探针"。
        // 测试可以断言"跑完这段脚本，SkillCastCount 应该等于 3"，
        // 从而在不打开 Unity 的情况下验证事件链路是否通畅。
        /// <summary>技能施放次数。</summary>
        public int SkillCastCount { get; private set; }

        public int SkillHitCount { get; private set; }

        public int DodgeStartCount { get; private set; }

        public int StatusAppliedCount { get; private set; }

        public int StatusExpiredCount { get; private set; }

        public int FacingSnapCount { get; private set; }

        public float LastSkillDamage { get; private set; }

        public string LastSkillId { get; private set; } = string.Empty;

        public Vector2 LastSnapFacing { get; private set; } = Vector2.right;

        public float QiCurrent { get; private set; }

        public float QiMax { get; private set; } = SkillConfig.QI_MAX;

        public float StaminaCurrent { get; private set; }

        public float StaminaMax { get; private set; } = SkillConfig.STAM_MAX;

        public int Combo { get; private set; }

        public float ComboMult { get; private set; } = 1.0f;

        public void ResetCounters()
        {
            SkillCastCount = 0;
            SkillHitCount = 0;
            DodgeStartCount = 0;
            StatusAppliedCount = 0;
            StatusExpiredCount = 0;
            FacingSnapCount = 0;
            LastSkillDamage = 0.0f;
            LastSkillId = string.Empty;
        }

        /// <summary>
        /// 内核通知：某人放了个技能。这里负责播放对应特效与音效。
        ///
        /// 【注意本方法没有任何"技能能不能放"的判断】
        /// 能否施放（灵力够不够、在不在冷却）早已在内核里判完了，
        /// 内核只在**真的放出来了**的时候才调用本方法。
        /// 事件处理器的职责边界就该这么清楚：只管"发生了，去表现"，不管"该不该发生"。
        /// 一旦事件处理器里出现业务判断，就意味着规则被写在了两个地方，迟早不一致。
        /// </summary>
        public void OnSkillCast(Combatant caster, SkillDef def)
        {
            if (caster == null || def == null)
            {
                return;
            }

            SkillCastCount++;
            LastSkillId = def.Id;

            // 把内核的纯数学坐标 Vec2 翻译成 Unity 的 Vector3 —— 这类"翻译"正是本类的日常。
            Vector3 origin = WorldPos(caster);
            Vector2 facing = FacingOf(caster);
            VfxSkill.PlayForSkill(def, origin, facing);

            // 回调判空后再调用：没接音频系统时安静跳过，不报错。
            // 又一次的"空即原路径"—— 缺失的能力只是不发生，绝不让整条链路崩掉。
            if (PlaySfx != null)
            {
                PlaySfx(def.Id);
            }

            if (SkillCast != null)
            {
                SkillCast(caster, def, FacingOf(caster));
            }

            if (VerboseLog)
            {
                Debug.Log("[T3] cast " + def.Id);
            }
        }

        public void OnSkillHit(Combatant caster, Combatant target, SkillDef def, float dmg)
        {
            if (target == null || def == null)
            {
                return;
            }

            // 探针：测试在断言这两个字段，别顺手删。
            SkillHitCount++;
            LastSkillDamage = dmg;

            // 【这里原来还调了一次 view.PlayHitFlash()，已删除——它是重复触发源】
            // 实证链路：SkillRuntime → DamageResolver.ResolveSkillHit(:297)
            //          → ApplyToEnemyWithPoise(:318) → evt.OnHit(...)(:251)   ← 第一条链
            //          → evtT3.OnSkillHit(...)(:323)                          ← 第二条链
            // 也就是说一次技能命中，内核在**同一步内先后**走了两条链，两条都调
            // PlayHitFlash() ⇒ 第二次把计时器重置一遍，闪白时长被无意延长，
            // 且分档信息被后来的无参默认档冲掉。
            // 删掉这一句之后，闪白在架构上只剩 OnHit 一个入口（OnHit 覆盖普攻 /
            // 技能 / BOSS 全部伤害路径，而 OnSkillHit 只覆盖技能），
            // 根本不需要跨链路去重。
            //
            // 【已知且可接受的行为变化】
            // DealsDamage == false 的纯 debuff 技能，过去命中时会闪一下白，现在不闪了。
            // 这是对的：闪白的语义是"挨了伤害"，它不该用来表示"被附着了状态"。
            // 状态附着有 VfxStatus 自己的表现（见 OnStatusApplied），语义不重叠。

            if (VerboseLog)
            {
                Debug.Log("[T3] hit " + def.Id + " dmg=" + dmg.ToString("F2"));
            }
        }

        public void OnDodgeStart(Combatant who, Vec2 dir)
        {
            if (who == null)
            {
                return;
            }

            DodgeStartCount++;
            Vector2 d = dir.IsZero() ? Vector2.right : new Vector2(dir.X, dir.Y);
            VfxSkill.PlayDodge(WorldPos(who), d, SkillConfig.DODGE_DISTANCE);

            if (PlaySfx != null)
            {
                PlaySfx(SkillConfig.SKILL_DODGE_ROLL);
            }
        }

        public void OnStatusApplied(Combatant target, ActiveStatus status)
        {
            if (target == null || status == null || status.Def == null)
            {
                return;
            }

            StatusAppliedCount++;
            VfxStatus vfx = ResolveStatusVfx(target);
            if (vfx != null)
            {
                vfx.Apply(status);
            }
        }

        public void OnStatusStackChanged(Combatant target, ActiveStatus status)
        {
            if (target == null || status == null || status.Def == null)
            {
                return;
            }

            VfxStatus vfx = ResolveStatusVfx(target);
            if (vfx != null)
            {
                vfx.Apply(status);
            }
        }

        public void OnStatusExpired(Combatant target, string statusId)
        {
            if (target == null || string.IsNullOrEmpty(statusId))
            {
                return;
            }

            StatusExpiredCount++;
            VfxStatus vfx = ResolveStatusVfx(target);
            if (vfx != null)
            {
                vfx.Remove(statusId);
            }
        }

        /// <summary>
        /// 内核通知：灵力或体力发生了变化。
        ///
        /// 【为什么用一个 bool 参数区分两种资源，而不是拆成两个方法】
        /// 灵力(Qi)和体力(Stamina)是本作的双资源池：技能耗灵力、闪避耗体力
        /// （拆成两池的设计理由见 HudSkillBar.CostLabel 的注释）。
        /// 两者的处理逻辑完全对称 —— 都是"存下当前值和上限、然后通知 HUD"，
        /// 用一个 isQi 开关比复制两份几乎相同的方法更好维护。
        /// 反过来说，如果将来两种资源的处理开始出现实质差异，就该拆开了。
        /// </summary>
        public void OnResourceChanged(Combatant who, bool isQi, float current, float max)
        {
            if (isQi)
            {
                QiCurrent = current;
                // max 非正时回落到配置常量：上限为 0 会让 HUD 算百分比时发生除零，
                // 血条宽度变成 NaN，UI 直接消失。在入口处挡掉比事后排查便宜得多。
                QiMax = max > 0.0f ? max : SkillConfig.QI_MAX;
            }
            else
            {
                StaminaCurrent = current;
                StaminaMax = max > 0.0f ? max : SkillConfig.STAM_MAX;
            }

            if (ResourceChanged != null)
            {
                ResourceChanged(isQi, current, max);
            }
        }

        public void OnFacingSnapped(Combatant who, Vec2 facing)
        {
            FacingSnapCount++;
            if (!facing.IsZero())
            {
                LastSnapFacing = new Vector2(facing.X, facing.Y);
            }
        }

        public void OnComboChanged(int combo, float mult)
        {
            Combo = combo;
            ComboMult = mult;
            if (ComboChanged != null)
            {
                ComboChanged(combo, mult);
            }
        }

        // ---------------------------------------------------------------------
        // 内部解析
        // ---------------------------------------------------------------------

        private static Vector3 WorldPos(Combatant c)
        {
            return c != null ? new Vector3(c.Position.X, c.Position.Y, 0.0f) : Vector3.zero;
        }

        private static Vector2 FacingOf(Combatant c)
        {
            if (c == null || c.Action == null)
            {
                return Vector2.right;
            }
            Vec2 f = c.Action.LockedFacing;
            if (f.IsZero())
            {
                return Vector2.right;
            }
            return new Vector2(f.X, f.Y);
        }

        private CombatView ResolveView(Combatant c)
        {
            if (c == null || ViewOf == null)
            {
                return null;
            }
            return ViewOf(c.Id);
        }

        /// <summary>
        /// 找到某个战斗单位对应的状态特效组件。
        ///
        /// 【为什么玩家和敌人要走两条不同的路径】
        /// 敌人是运行时批量生成的，每个都配有一个 CombatView 组件来管理显示；
        /// 而玩家是场景里预先存在的特殊对象，不走那套视图管理，只有一个 Transform。
        /// 这个不对称是历史与实现现实造成的，不是设计上的优雅之处 ——
        /// 如果哪天要重构，让玩家也统一挂 CombatView 就能消掉这个分支。
        /// 在此之前，这里必须老实地分两路处理。
        /// </summary>
        private VfxStatus ResolveStatusVfx(Combatant c)
        {
            if (c == null)
            {
                return null;
            }

            if (c.Faction == Faction.Player)
            {
                return PlayerTransform != null ? VfxStatus.For(PlayerTransform.gameObject) : null;
            }

            CombatView view = ResolveView(c);
            if (view == null)
            {
                return null;
            }

            VfxStatus vfx = VfxStatus.For(view.gameObject);
            if (vfx != null && vfx.Anchor == null)
            {
                vfx.Anchor = view.StatusAnchor;
            }
            return vfx;
        }
    }
}
