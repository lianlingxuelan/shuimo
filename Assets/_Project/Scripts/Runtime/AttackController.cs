// -----------------------------------------------------------------------------
// AttackController.cs —— 玩家普攻（asmdef: Xianxia.Unity.T2）
//
// 【判定形状：90° 扇形，半径 70】
// 判定与表现刻意分开：判定是扇形（好算、可预测、玩家能学会），
// 表现是弧形剑气（好看）。两者共用同一个朝向与张角，所以看到的和打到的一致，
// 但没人需要为了"让特效看起来像判定"而把特效画成一个手电筒光锥。
//
// 【raw = 12 的来历（PRD Q5）】
// zone_youhuang 的怪 hp 在 30~45 之间、护甲 1~3。减法承伤下
// real = max(1, 12 − armor) ≈ 9~11，三到四刀一只。
// PM 原本推荐 25，那是按"敌人百级血量"估的；实算下来两刀一只，
// 打击感直接塌成割草，所以下调到 12。
//
// ★【P1-6 补注】12 现在是**1 级基础值**，不再是运行时的最终值。
//   接入玩家成长曲线后，实际 raw = 12 + AtkBonus，AtkBonus = 1 × (等级 − 1)，
//   10 级时是 21。常量 AttackRaw 本身**一字未改**，1 级加成恒为 0，
//   所以对拍基线严格位等价（见 ResolveHits 里 effectiveRaw 的注释）。
//   加成是**加法项**，不是乘区 —— 不要改成百分比，那会引入新乘区，撞数值红线。
//
// 【为什么不用 Physics2D.OverlapCircle】
// 敌人身上没有 Collider2D（P0 不做碰撞），而且内核里的位置才是权威——
// 直接遍历 Encounter.AliveEnemies() 拿内核坐标算距离，
// 与伤害结算的坐标系严格同源，不存在"看起来打到了但判定说没有"。
// 6 只怪的线性遍历，性能上完全不值得优化。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Xianxia.Combat;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>玩家普攻。挂在 Player 上。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class AttackController : MonoBehaviour
    {
        /// <summary>单次普攻的原始输出（PRD Q5 定案值）。</summary>
        public const float AttackRaw = 12.0f;

        /// <summary>攻击冷却（秒）。0.4s ⇒ 2.5 刀/秒。</summary>
        public const float AttackCooldown = 0.4f;

        /// <summary>扇形判定半径（世界单位）。</summary>
        public const float AttackRadius = 70.0f;

        /// <summary>扇形总张角（度）。判定用半角 45°。</summary>
        public const float AttackArcDeg = 90.0f;

        [Header("数值")]
        [SerializeField] private float raw = AttackRaw;
        [SerializeField] private float cooldown = AttackCooldown;
        [SerializeField] private float radius = AttackRadius;
        [SerializeField] private float arcDeg = AttackArcDeg;

        private PlayerController _player;
        private CombatBridge _bridge;
        private float _cdTimer;

        /// <summary>剩余冷却（秒）。HUD 用它画冷却条。</summary>
        public float CooldownRemain
        {
            get { return _cdTimer; }
        }

        /// <summary>
        /// 冷却进度 [0,1]，1 = 可攻击。
        /// T3 接管后以内核的技能 CD 为准（两者同为 0.4s / 24 帧，口径一致）。
        /// </summary>
        public float CooldownRatio
        {
            get
            {
                CombatBridge b = _bridge;
                if (b != null && b.IsReady && b.T3Enabled)
                {
                    return 1.0f - Mathf.Clamp01(b.SkillCdRatio(IntentSlot.Basic));
                }
                return cooldown > 0.0f ? Mathf.Clamp01(1.0f - _cdTimer / cooldown) : 1.0f;
            }
        }

        /// <summary>累计挥砍次数（T2 内联路径），调试面板用。</summary>
        public int SwingCount { get; private set; }

        /// <summary>累计投递的普攻意图次数（T3 路径），调试面板用。</summary>
        public int BasicRequestCount { get; private set; }

        /// <summary>最近一刀命中的敌人数，调试面板用。</summary>
        public int LastHitCount { get; private set; }

        /// <summary>本帧是否走 T3 技能路径（供调试面板显示）。</summary>
        public bool UsingSkillPath { get; private set; }

        private void Awake()
        {
            _player = GetComponent<PlayerController>();
        }

        private void Update()
        {
            if (_cdTimer > 0.0f)
            {
                _cdTimer -= Time.deltaTime;
                if (_cdTimer < 0.0f)
                {
                    _cdTimer = 0.0f;
                }
            }

            CombatBridge bridge = ResolveBridge();
            bool t3 = bridge != null && bridge.IsReady && bridge.T3Enabled && !bridge.BaselineMode;
            UsingSkillPath = t3;

            // 菜单打开或本局已终结时不接受攻击输入（与 PlayerController/SkillController 同一道闸门）
            if (bridge != null && bridge.IsGameplayBlocked)
            {
                return;
            }

            if (!WantAttack())
            {
                return;
            }

            if (t3)
            {
                // ★ T3：降级为技能系统的调用方。CD / 判定 / 伤害 / 特效全部由内核
                //   与 CombatEventsT3Unity 负责，这里只投递意图。
                //   Request 是幂等的，按住不放每帧投递一次完全无副作用 ——
                //   节奏由内核的 24 帧 CD 决定，与帧率无关。
                BasicRequestCount++;
                bridge.RequestCast(IntentSlot.Basic, ResolveFacing());
                return;
            }

            if (_cdTimer <= 0.0f)
            {
                Swing();
            }
        }

        private static bool WantAttack()
        {
            // T3：三路输入收敛为 InputBinder 的 Attack 动作（鼠标左键 / J / 手柄 A）。
            // **空格已摘除**（用户 Q1 拍板：空格改作闪避备用键）。
            // 仍用"按住"语义而非"按下"：按住不放应当按冷却连续挥砍。
            return InputBinder.Held(GameAction.Attack);
        }

        private Vector2 ResolveFacing()
        {
            Vector2 facing = _player != null ? _player.LastFacing : Vector2.right;
            if (facing.sqrMagnitude <= 0.0f)
            {
                facing = Vector2.right;   // 架构文档 U4 的兜底，避免 Atan2(0,0) 之后的 NaN
            }
            return facing;
        }

        /// <summary>
        /// 挥一刀（T2 原路径）：先出特效（手感靠它），再结算伤害。
        /// baselineMode / T3 关闭时走这里，数值与判定与 T2 逐字一致。
        /// </summary>
        public void Swing()
        {
            _cdTimer = cooldown;
            SwingCount++;

            Vector2 facing = ResolveFacing();

            VfxSlash.Play(transform.position, facing, radius, arcDeg);
            LastHitCount = ResolveHits(facing);
        }

        /// <summary>
        /// 扇形判定 + 伤害结算。
        /// 一次挥砍对扇形内**每只**敌人各结算一次（AOE 语义），
        /// 敌人侧没有 W-CORE 闸门，所以不存在"同帧多次命中被吞"的问题。
        /// </summary>
        /// <returns>本次命中的敌人数。</returns>
        private int ResolveHits(Vector2 facing)
        {
            CombatBridge bridge = ResolveBridge();
            if (bridge == null || !bridge.IsReady)
            {
                return 0;
            }

            Combatant player = bridge.Player;
            Encounter enc = bridge.Encounter;
            if (player == null || enc == null)
            {
                return 0;
            }

            Vector2 origin = new Vector2(transform.position.x, transform.position.y);
            float halfArc = arcDeg * 0.5f;
            float r2 = radius * radius;

            // ★ P1-6：普攻是**读时注入**——每次挥砍都现读一次等级加成，
            //   所以升级之后紧接着的下一刀立刻生效，不需要任何刷新步骤。
            //   （技能走的是另一条路：写时注入，见 CombatBridge.RefreshSkillRawFromLevel。）
            //
            //   在循环外只读一次，理由有两条：一是同一次挥砍对所有目标必须同伤害，
            //   二是避免把属性读取放进热循环。
            //   1 级时 PlayerAtkBonus 恒为 0.0f，effectiveRaw = 12 + 0.0f ≡ 12，
            //   与接入成长系统之前**逐位等价**，对拍基线不漂移。
            float effectiveRaw = raw + bridge.PlayerAtkBonus;

            // AliveEnemies() 返回的是**快照列表**，所以下面击杀目标不会破坏遍历
            // （Encounter.AliveEnemies 的注释专门交代过这件事）。
            List<Combatant> enemies = enc.AliveEnemies();
            int hits = 0;

            for (int i = 0; i < enemies.Count; i++)
            {
                Combatant e = enemies[i];
                Vector2 to = new Vector2(e.Position.X - origin.x, e.Position.Y - origin.y);

                float d2 = to.sqrMagnitude;
                if (d2 > r2)
                {
                    continue;
                }

                // 距离≈0 时方向无意义，判定为命中（人贴在脸上不该砍空）。
                if (d2 > 1e-6f && Vector2.Angle(facing, to) > halfArc)
                {
                    continue;
                }

                DamageResolver.ResolvePlayerAttack(player, e, effectiveRaw, enc.Events);
                hits++;
            }

            return hits;
        }

        /// <summary>
        /// 找到场景里的 CombatBridge 并缓存。
        /// 玩家与 Combat 是两棵不同的子树，玩家身上拿不到引用；
        /// 又因为世界会被反复重建，缓存必须能在失效后自愈，所以每次都校验 null。
        /// </summary>
        private CombatBridge ResolveBridge()
        {
            if (_bridge != null)
            {
                return _bridge;
            }
#if UNITY_2023_1_OR_NEWER
            _bridge = Object.FindFirstObjectByType<CombatBridge>();
#else
            _bridge = Object.FindObjectOfType<CombatBridge>();
#endif
            return _bridge;
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 o = transform.position;
            Vector2 facing = _player != null ? _player.LastFacing : Vector2.right;
            float half = arcDeg * 0.5f;

            Gizmos.color = new Color(0.95f, 0.85f, 0.45f, 0.9f);
            Gizmos.DrawWireSphere(o, radius);

            Vector3 a = Quaternion.Euler(0, 0, half) * new Vector3(facing.x, facing.y, 0) * radius;
            Vector3 b = Quaternion.Euler(0, 0, -half) * new Vector3(facing.x, facing.y, 0) * radius;
            Gizmos.DrawLine(o, o + a);
            Gizmos.DrawLine(o, o + b);
        }
    }
}
