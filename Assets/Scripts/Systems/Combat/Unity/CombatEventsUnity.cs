// -----------------------------------------------------------------------------
// Unity/CombatEventsUnity.cs —— ICombatEvents 的 Unity 实现（FX / 音效 / UI 出口）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【为什么内核抛事件而不是直接播特效】
// 内核必须能在没有引擎的情况下跑完一整场战斗（这是 t1_selfcheck.py 的前提）。
// 只要内核里出现一句 AudioSource.Play()，对拍就再也跑不起来了。所以内核只说
// "发生了什么"，本文件决定"看起来/听起来是什么样"。
//
// 【本类不是 MonoBehaviour】
// 事件出口需要在 Encounter 构造时就存在，而 MonoBehaviour 的生命周期由引擎决定。
// 做成普通类由 CombatController 持有，顺序完全可控；需要协程/延迟时，
// 通过注入的 host 转发即可。
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace Xianxia.Combat.UnityBridge
{
    /// <summary>
    /// 把内核事件翻译成 Unity 表现。所有回调都做了 null 保护——
    /// 表现层缺一个预制体不该让整场战斗崩掉。
    /// </summary>
    public sealed class CombatEventsUnity : ICombatEvents
    {
        /// <summary>视图查找委托，由 <see cref="CombatController"/> 注入。</summary>
        public Func<int, CombatView> ViewOf;

        /// <summary>命中特效预制体。</summary>
        public GameObject HitFxPrefab;

        /// <summary>击退特效预制体。</summary>
        public GameObject KnockbackFxPrefab;

        /// <summary>冲击波特效预制体（会按半径缩放）。</summary>
        public GameObject ShockwaveFxPrefab;

        /// <summary>特效挂载父节点。</summary>
        public Transform FxRoot;

        /// <summary>音效播放出口（key = 音效名）。留空则不播。</summary>
        public Action<string> PlaySfx;

        /// <summary>
        /// 受击反馈总出口（攻击方 / 受击方 / 本次实际伤害）。留空则退化为默认闪白。
        ///
        /// 【为什么是"一个总出口"而不是 hitstop / 屏震 / 飘字 / 闪白 各一个委托】
        /// 四件套是同一次命中的四种表达，强度分档（轻/重、谁挨打、伤害占比）
        /// 只应该算**一次**。拆成四个委托，就得把 defender.HpMax 的分档逻辑抄四遍，
        /// 或者让四个订阅者各算各的、算出不一致的档位。所以这里只抛"发生了什么"，
        /// 由 T2 的 HitFeedbackDirector 统一裁决"表现成什么样"。
        ///
        /// 【为什么传 Combatant 而不是已经算好的位置和整数伤害】
        /// 分档需要 defender.HpMax 和 defender.Faction，只给位置就不够用了。
        /// 本类属于表现层，直接引用内核实体没有依赖问题。
        ///
        /// 【它是 PopupText 原地演进来的】
        /// 原 <c>Action&lt;Vector2, string&gt; PopupText</c> 全仓零订阅（已 grep 实证），
        /// 改签名的破坏面为 0，所以没有保留旧委托的必要——留着只会让人以为飘字
        /// 有两个入口。
        /// </summary>
        public Action<Combatant, Combatant, float> HitFeedback;

        /// <summary>BOSS 阶段变化，供 UI 播阶段横幅。</summary>
        public Action<BossPhase> BossPhaseChanged;

        /// <summary>敌人死亡，供掉落 / 经验结算挂钩。</summary>
        public Action<Combatant> EnemyDied;

        /// <summary>输出调试日志。上线前关掉，OnHit 每秒会打好几条。</summary>
        public bool VerboseLog;

        // ---------------------------------------------------------------------
        // ICombatEvents
        // ---------------------------------------------------------------------

        /// <inheritdoc />
        public void OnHit(Combatant attacker, Combatant defender, float dmg, bool applied)
        {
            // applied=false 表示被 W-CORE 闸门拦下了。这时**必须什么都不播**：
            // 播了就等于告诉玩家"我挨打了"，但血没掉，反馈与结果不一致，
            // 比没有反馈更糟。
            if (!applied || defender == null)
            {
                return;
            }

            Vector2 at = new Vector2(defender.Position.X, defender.Position.Y);
            SpawnFx(HitFxPrefab, at, 1.0f);

            // 受击反馈：接了 Director 就全权交给它（它会自己算档位并派发闪白 /
            // 顿帧 / 屏震 / 飘字四件套）；没接就退回原来的默认闪白。
            //
            // 【为什么是 else 而不是两个都走】
            // Director 派发的闪白是分阵营、分轻重的（玩家朱砂红 0.80 峰值 0.16s，
            // 敌人纯白 1.00 峰值）。这里再补一发无参默认闪白会**覆盖重置计时器**，
            // 把刚设好的档位冲掉，表现为"玩家挨打闪的是白光不是红光"。
            // 二选一，不叠加。
            //
            // 【为什么保留 fallback 而不是直接依赖 Director】
            // 本类住在 Xianxia.Combat.Unity，Director 住在 Xianxia.Unity.T2，
            // 依赖方向决定了本类**永远不可能**知道 Director 是否存在。
            // 单元测试、Editor 下的裸场景、以及 CombatBridge 拆线之后都属于
            // "没有 Director"的合法状态，这时候不该连闪白都没有了。
            if (HitFeedback != null)
            {
                HitFeedback(attacker, defender, dmg);
            }
            else
            {
                CombatView view = ViewOf != null ? ViewOf(defender.Id) : null;
                if (view != null)
                {
                    view.PlayHitFlash();
                }
            }

            if (PlaySfx != null)
            {
                PlaySfx(defender.Faction == Faction.Player ? "sfx_player_hurt" : "sfx_enemy_hit");
            }

            if (VerboseLog)
            {
                Debug.LogFormat("[Combat] {0} -> {1} dmg={2:F1}",
                    attacker != null ? attacker.Id : -1, defender.Id, dmg);
            }
        }

        /// <inheritdoc />
        public void OnEnemyDeath(Combatant e)
        {
            if (e == null)
            {
                return;
            }
            if (PlaySfx != null)
            {
                PlaySfx(e.IsBoss ? "sfx_boss_death" : "sfx_enemy_death");
            }
            if (EnemyDied != null)
            {
                EnemyDied(e);
            }
            if (VerboseLog)
            {
                Debug.LogFormat("[Combat] death #{0} {1} exp={2:F0}", e.Id, e.DisplayName, e.ExpValue);
            }
        }

        /// <inheritdoc />
        public void OnBossPhase(BossPhase phase)
        {
            if (PlaySfx != null)
            {
                PlaySfx("sfx_boss_phase");
            }
            if (BossPhaseChanged != null)
            {
                BossPhaseChanged(phase);
            }
            if (VerboseLog)
            {
                Debug.LogFormat("[Combat] boss phase -> {0}", phase);
            }
        }

        /// <inheritdoc />
        public void OnSummon(int n)
        {
            if (PlaySfx != null)
            {
                PlaySfx("sfx_boss_summon");
            }
            if (VerboseLog)
            {
                Debug.LogFormat("[Combat] boss summon x{0}", n);
            }
        }

        /// <inheritdoc />
        public void OnShockwave(float radius, Vec2 center)
        {
            Vector2 at = new Vector2(center.X, center.Y);

            // 特效按半径等比放大：预制体建模半径约定为 1 单位，
            // 这样改 BOSS_P3_SHOCK_RADIUS 不需要同步改美术资源。
            SpawnFx(ShockwaveFxPrefab, at, radius);

            if (PlaySfx != null)
            {
                PlaySfx("sfx_boss_shockwave");
            }
            if (VerboseLog)
            {
                Debug.LogFormat("[Combat] shockwave r={0:F0} at ({1:F0},{2:F0})", radius, center.X, center.Y);
            }
        }

        /// <inheritdoc />
        public void OnKnockback(Combatant e, Vec2 impulse)
        {
            if (e == null)
            {
                return;
            }
            Vector2 at = new Vector2(e.Position.X, e.Position.Y);
            SpawnFx(KnockbackFxPrefab, at, 1.0f);

            if (PlaySfx != null)
            {
                PlaySfx("sfx_poise_break");
            }
            if (VerboseLog)
            {
                Debug.LogFormat("[Combat] poise break #{0} impulse=({1:F0},{2:F0})", e.Id, impulse.X, impulse.Y);
            }
        }

        // ---------------------------------------------------------------------
        // 内部
        // ---------------------------------------------------------------------

        /// <summary>实例化一个一次性特效。prefab 为空时静默跳过。</summary>
        private void SpawnFx(GameObject prefab, Vector2 at, float scale)
        {
            if (prefab == null)
            {
                return;
            }
            GameObject go = UnityEngine.Object.Instantiate(
                prefab, new Vector3(at.x, at.y, 0.0f), Quaternion.identity, FxRoot);
            if (scale != 1.0f)
            {
                go.transform.localScale = Vector3.one * scale;
            }
        }
    }
}
