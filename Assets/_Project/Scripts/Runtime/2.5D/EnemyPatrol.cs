// -----------------------------------------------------------------------------
// 2.5D/EnemyPatrol.cs —— 敌人巡逻 + 感知追击 AI（feature/2.5d，地图升级 选项A 续）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过。状态转移的正确性由 EditMode 单测
// P2_2_EnemyAiBrainTests 覆盖（测的是 EnemyAiBrain 决策核，不需要进 PlayMode）。
//
// 【职责】
//   · 巡逻（原有行为，完全保留）：在所分配的圆形区域内随机游走，到达后停顿再选新目标；
//   · 感知追击（本次新增）：玩家进入警戒半径 → 追击；追丢/被拖太远 → 返巢；
//     贴身 → 停步并起手攻击**表现**。
//   驱动 CharacterView 的 Idle/Walk/Attack 与朝向翻转。
//
// 【状态机在哪】本类只负责「执行」：位移、朝向、动画、冷却计时。
//   「决定该干什么」全部交给纯函数 EnemyAiBrain.Decide —— 这样状态转移可被单测钉死，
//   不必靠肉眼在 PlayMode 里找抖动 bug。
//
// 【推进闸门】由 EnemyNpcSpawner 在 !IsGameplayBlocked 帧用 FeedbackClock.Delta 驱动，
// 顿帧/暂停时与全场景同步冻结。本类**不自己读 Time.deltaTime**，时钟唯一来源是调用方传入的 dt。
// 攻击冷却同样只按传入 dt 推进，因此暂停期间冷却不会偷跑。
//
// 【确定性】巡逻轨迹用 xorshift32（种子由 Spawner 按区域+序号给定），同一关卡可复现。
// 追击是对玩家位置的确定性响应，不引入新的随机源。
//
// 【红线】（与 EnemyNpcSpawner 同）
//   1. 不进战斗内核、不调 DamageResolver、不引用 CombatScheduler / RunPhase /
//      RequestHitstop / KickHitstop，不改任何内核类型；
//   2. 不写 Time.timeScale、不写 FeedbackClock.Frozen；
//   3. **不直调内核**：贴身攻击经注入的 IDamageRequester 把伤害请求抛出
//      （damageRequester 由 CombatBridge 实现并注入），本类**不引用任何内核战斗类型**；
//      真正结算在内核侧（模型 B 减伤 / W-CORE 闸门），推进权唯一。
//      未注入 damageRequester 时只播攻击表现、零伤害（纯表现模式，便于先验收 AI 不崩）。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge; // FeedbackClock（仅用于类型认知，实际 dt 由调用方传入）

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 敌人巡逻 + 感知追击 AI：在圆形区域内随机游走，玩家靠近则追击。
    /// 由 EnemyNpcSpawner 每帧调用 <see cref="Tick"/> 驱动。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyPatrol : MonoBehaviour
    {
        [Header("巡逻区域（圆形，世界 XY）")]
        [Tooltip("区域圆心（世界 XY）")]
        public Vector2 center;

        [Tooltip("区域半径（世界单位）")]
        public float radius = 300.0f;

        [Header("运动参数")]
        [Tooltip("巡逻移动速度（世界单位/秒）")]
        public float moveSpeed = 55.0f;

        [Tooltip("到达目标后最短停顿（秒）")]
        public float waitMin = 0.6f;

        [Tooltip("到达目标后最长停顿（秒）")]
        public float waitMax = 1.8f;

        [Tooltip("视为「到达」的距离阈值（世界单位）")]
        public float arriveEps = 8.0f;

        [Header("感知 / 追击（新增；关掉即退回纯巡逻）")]
        [Tooltip("是否启用感知追击。false 时行为与纯巡逻版本完全一致。")]
        public bool aiEnabled = true;

        [Tooltip("警戒半径：玩家进入此距离则起警转追击")]
        public float alertRadius = 220.0f;

        [Tooltip("脱战半径：追击中玩家超出此距离则返巢。应大于警戒半径以形成滞回，防边界抖动。")]
        public float loseRadius = 340.0f;

        [Tooltip("拴绳半径：离圆心超此距离强制返巢，避免被玩家拖走整张地图的怪")]
        public float leashRadius = 520.0f;

        [Tooltip("攻击距离：追至此距离内停步并起手攻击表现（不造成伤害）")]
        public float attackRange = 46.0f;

        [Tooltip("追击移动速度（世界单位/秒），一般略快于巡逻速度")]
        public float chaseSpeed = 85.0f;

        [Tooltip("攻击表现最小间隔（秒）。必须节流：每帧播 Attack 会不断重置攻击脉冲。")]
        public float attackInterval = 1.1f;

        [Tooltip("找不到玩家时的重新搜索间隔（秒），避免每帧 FindObjectOfType 开销")]
        public float playerRescanInterval = 1.0f;

        [Header("贴身伤害（经 CombatBridge 投递内核；本类不直调内核）")]
        [Tooltip("贴身一次接触伤害的原始值，真减伤由内核套。0 = 不掉血（纯表现）。")]
        public float contactDamage = 8.0f;

        [Tooltip("伤害请求出口（解耦接口）。由 CombatBridge 注入实现；为空则只播表现不掉血。")]
        public IDamageRequester damageRequester;

        [Tooltip("是否允许本敌人经 damageRequester 真正造成玩家伤害。false = 永不掉血（NPC 用）。")]
        public bool damageEnabled = true;

        [Header("确定性种子")]
        [Tooltip("巡逻随机种子（由 Spawner 按 区域+序号 给定，保证同关卡可复现）")]
        public uint seed;

        private CharacterView _view;
        private Vector2 _target;
        private float _waitRemain;
        private bool _waiting;
        private uint _s;

        private EnemyAiState _state = EnemyAiState.Patrol;
        private Transform _playerTf;
        private float _rescanRemain;
        private float _attackCooldownRemain;

        /// <summary>当前 AI 状态（只读，便于调试与外部观察）。</summary>
        public EnemyAiState CurrentState
        {
            get { return _state; }
        }

        /// <summary>
        /// 由 Spawner 在挂载后调用，设定巡逻区域与种子并立即选好第一个目标。
        /// </summary>
        /// <param name="c">区域圆心（世界 XY）。</param>
        /// <param name="r">区域半径。</param>
        /// <param name="s">确定性种子（0 会被替换为一个固定非零值）。</param>
        public void Configure(Vector2 c, float r, uint s)
        {
            center = c;
            radius = Mathf.Max(1.0f, r);
            seed = s;
            _s = s == 0u ? 0x9E3779B9u : s;
            _state = EnemyAiState.Patrol;

            // 拴绳按区域尺寸派生：至少留出 25% 的追击余量。
            // EnemyAiBrain 内部有「leash >= patrolRadius」的硬约束（防永久返巢），
            // 若区域半径大于 leashRadius 的默认值，该约束会把拴绳压成恰好等于巡逻半径，
            // 表现为「敌人刚出圈就被拽回去」，追击形同虚设。这里主动抬一档。
            float minLeash = radius * 1.25f;
            if (leashRadius < minLeash)
            {
                leashRadius = minLeash;
            }

            PickNewTarget();
        }

        private float Rand01()
        {
            // xorshift32：纯确定性伪随机，不依赖 UnityEngine.Random。
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return (_s & 0x7FFFFFFFu) / (float)0x7FFFFFFF;
        }

        private void PickNewTarget()
        {
            float ang = Rand01() * Mathf.PI * 2.0f;
            float rad = Mathf.Sqrt(Rand01()) * radius; // 圆内均匀采样
            _target = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rad;
            _waiting = false;
        }

        private void Awake()
        {
            _view = GetComponent<CharacterView>();
        }

        /// <summary>
        /// 每帧推进 AI。由 EnemyNpcSpawner 在 !IsGameplayBlocked 且 FeedbackClock.Delta &gt; 0 时调用。
        /// </summary>
        /// <param name="dt">表现层应推进的秒数（= FeedbackClock.Delta）。</param>
        public void Tick(float dt)
        {
            if (_view == null)
            {
                _view = GetComponent<CharacterView>();
            }
            if (_view == null || dt <= 0.0f)
            {
                return;
            }

            // 攻击冷却只按传入 dt 推进：暂停/顿帧期间不偷跑。
            if (_attackCooldownRemain > 0.0f)
            {
                _attackCooldownRemain -= dt;
            }

            Vector2 pos = new Vector2(transform.position.x, transform.position.y);

            if (!aiEnabled)
            {
                // 退回纯巡逻（与新增感知前的行为逐字等价）。
                TickPatrol(dt, pos);
                return;
            }

            Transform player = ResolvePlayer(dt);
            bool playerValid = player != null;
            Vector2 playerPos = playerValid
                ? new Vector2(player.position.x, player.position.y)
                : Vector2.zero;

            EnemyAiInput input = new EnemyAiInput();
            input.State = _state;
            input.SelfPos = pos;
            input.PlayerPos = playerPos;
            input.PlayerValid = playerValid;
            input.PatrolCenter = center;
            input.PatrolRadius = radius;
            input.PatrolTarget = _target;
            input.AlertRadius = alertRadius;
            input.LoseRadius = loseRadius;
            input.LeashRadius = leashRadius;
            input.AttackRange = attackRange;

            EnemyAiDecision d = EnemyAiBrain.Decide(input);

            if (d.State != _state)
            {
                OnStateChanged(_state, d.State);
                _state = d.State;
            }

            if (_state == EnemyAiState.Patrol)
            {
                // 巡逻交给原有逻辑（含到达判定 / 停顿 / 选新目标）。
                TickPatrol(dt, pos);
                return;
            }

            if (d.ShouldAttack)
            {
                TickAttack(playerValid, playerPos, pos);
                return;
            }

            if (!d.Moving)
            {
                _view.PlayState(CharacterAnimState.Idle);
                return;
            }

            float speed = _state == EnemyAiState.Chase ? chaseSpeed : moveSpeed;
            MoveToward(d.MoveTarget, speed, dt, pos);
        }

        /// <summary>状态迁移副作用：清理上一状态残留、为新状态做准备。</summary>
        /// <param name="from">迁出状态。</param>
        /// <param name="to">迁入状态。</param>
        private void OnStateChanged(EnemyAiState from, EnemyAiState to)
        {
            if (to == EnemyAiState.Chase)
            {
                // 起警：清掉巡逻等待，立刻动身。
                _waiting = false;
                return;
            }

            if (to == EnemyAiState.Patrol && from != EnemyAiState.Patrol)
            {
                // 返巢完成后恢复巡逻：位置已变，旧目标可能远在圈外，重挑一个。
                PickNewTarget();
            }
        }

        /// <summary>
        /// 贴身攻击：面朝玩家 + 经决策核判定「本帧是否该结算一次接触伤害」。
        /// 决策核消费"已按 dt 推进过的剩余冷却"，开火则回填 attackInterval。
        /// 真正掉血不在此处发生：若 <see cref="damageRequester"/> 已注入，则把伤害请求抛给
        /// CombatBridge，由它在内核侧结算；为空则只播表现（纯表现模式，零伤害）。
        /// </summary>
        /// <param name="playerValid">玩家是否有效。</param>
        /// <param name="playerPos">玩家位置。</param>
        /// <param name="pos">自身位置。</param>
        private void TickAttack(bool playerValid, Vector2 playerPos, Vector2 pos)
        {
            if (playerValid)
            {
                Vector2 face = playerPos - pos;
                if (face.sqrMagnitude > 0.000001f)
                {
                    _view.SetFacing(face.normalized);
                }
            }

            float dist = playerValid ? Vector2.Distance(pos, playerPos) : float.MaxValue;
            EnemyContactDecision dec = EnemyContactAttack.Decide(new EnemyContactInput
            {
                DistanceToPlayer = dist,
                AttackRange = attackRange,
                CooldownRemaining = _attackCooldownRemain,
                AttackInterval = attackInterval,
            });
            _attackCooldownRemain = dec.CooldownRemaining;

            if (dec.ShouldFire)
            {
                _view.PlayState(CharacterAnimState.Attack);
                if (damageRequester != null)
                {
                    damageRequester.RequestContactDamage(contactDamage);
                }
            }
            else
            {
                // 未到开火时机：Idle 幂等，且不会打断 Attack 脉冲的自然衰减。
                _view.PlayState(CharacterAnimState.Idle);
            }
        }

        /// <summary>原有巡逻推进（行为与新增感知前逐字等价）。</summary>
        /// <param name="dt">本帧推进秒数。</param>
        /// <param name="pos">自身当前位置。</param>
        private void TickPatrol(float dt, Vector2 pos)
        {
            if (_waiting)
            {
                _waitRemain -= dt;
                if (_waitRemain <= 0.0f)
                {
                    PickNewTarget();
                }
                return;
            }

            Vector2 toT = _target - pos;
            float dist = toT.magnitude;
            if (dist <= arriveEps)
            {
                _waiting = true;
                _waitRemain = waitMin + Rand01() * (waitMax - waitMin);
                _view.PlayState(CharacterAnimState.Idle);
                return;
            }

            Vector2 dir = toT / dist;
            Vector2 step = dir * moveSpeed * dt;
            if (step.magnitude > dist)
            {
                step = toT;
            }
            ApplyPosition(pos + step);

            _view.SetFacing(dir);
            _view.PlayState(CharacterAnimState.Walk);
        }

        /// <summary>朝目标点移动一步（不越过目标），并驱动朝向与 Walk。</summary>
        /// <param name="target">目标点。</param>
        /// <param name="speed">移动速度。</param>
        /// <param name="dt">本帧推进秒数。</param>
        /// <param name="pos">自身当前位置。</param>
        private void MoveToward(Vector2 target, float speed, float dt, Vector2 pos)
        {
            Vector2 to = target - pos;
            float dist = to.magnitude;
            if (dist <= 0.0001f)
            {
                _view.PlayState(CharacterAnimState.Idle);
                return;
            }

            Vector2 dir = to / dist;
            Vector2 step = dir * Mathf.Max(0.0f, speed) * dt;
            if (step.magnitude > dist)
            {
                step = to;
            }
            ApplyPosition(pos + step);

            _view.SetFacing(dir);
            _view.PlayState(CharacterAnimState.Walk);
        }

        /// <summary>写回世界位置（保持原 z，深度排序由 DepthSortUtility 另行处理）。</summary>
        /// <param name="p">新的世界 XY。</param>
        private void ApplyPosition(Vector2 p)
        {
            transform.position = new Vector3(p.x, p.y, transform.position.z);
        }

        /// <summary>
        /// 惰性解析玩家 Transform。找到后缓存；找不到则按 playerRescanInterval 节流重试，
        /// 避免每帧 FindObjectOfType 的开销。
        /// </summary>
        /// <param name="dt">本帧推进秒数（用于推进重试计时）。</param>
        /// <returns>玩家 Transform；未找到返回 null。</returns>
        private Transform ResolvePlayer(float dt)
        {
            if (_playerTf != null)
            {
                return _playerTf;
            }

            _rescanRemain -= dt;
            if (_rescanRemain > 0.0f)
            {
                return null;
            }
            _rescanRemain = Mathf.Max(0.25f, playerRescanInterval);

            // 与 BambooSceneContext 既有做法一致：只读玩家 Transform，不碰其状态。
            PlayerController pc = Object.FindObjectOfType<PlayerController>();
            if (pc != null)
            {
                _playerTf = pc.transform;
            }
            return _playerTf;
        }
    }
}
