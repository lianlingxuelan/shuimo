// -----------------------------------------------------------------------------
// 2.5D/EnemyPatrol.cs —— 敌人巡逻 AI（feature/2.5d，地图升级 选项A）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过。
//
// 【职责】纯巡逻：在所分配的圆形区域内随机游走，到达目标后停顿随机时长，
// 再选新目标。驱动 CharacterView 的 Walk/Idle 状态与朝向翻转。
// 不进战斗内核、不调 DamageResolver、不改任何内核类型（红线同 EnemyNpcSpawner）。
//
// 【推进闸门】由 EnemyNpcSpawner 在 !IsGameplayBlocked 帧用 FeedbackClock.Delta 驱动，
// 顿帧/暂停时与全场景同步冻结。本类**不自己读 Time.deltaTime**，时钟唯一来源是调用方传入的 dt。
//
// 【确定性】巡逻轨迹用 xorshift32（种子由 Spawner 按区域+序号给定），同一关卡可复现。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge; // FeedbackClock（仅用于类型认知，实际 dt 由调用方传入）

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 敌人巡逻 AI：在圆形区域内随机游走。由 EnemyNpcSpawner 每帧调用 <see cref="Tick"/> 驱动。
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
        [Tooltip("移动速度（世界单位/秒）")]
        public float moveSpeed = 55.0f;

        [Tooltip("到达目标后最短停顿（秒）")]
        public float waitMin = 0.6f;

        [Tooltip("到达目标后最长停顿（秒）")]
        public float waitMax = 1.8f;

        [Tooltip("视为「到达」的距离阈值（世界单位）")]
        public float arriveEps = 8.0f;

        [Header("确定性种子")]
        [Tooltip("巡逻随机种子（由 Spawner 按 区域+序号 给定，保证同关卡可复现）")]
        public uint seed;

        private CharacterView _view;
        private Vector2 _target;
        private float _waitRemain;
        private bool _waiting;
        private uint _s;

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
        /// 每帧推进巡逻。由 EnemyNpcSpawner 在 !IsGameplayBlocked 且 FeedbackClock.Delta &gt; 0 时调用。
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

            if (_waiting)
            {
                _waitRemain -= dt;
                if (_waitRemain <= 0.0f)
                {
                    PickNewTarget();
                }
                return;
            }

            Vector2 pos = new Vector2(transform.position.x, transform.position.y);
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
            Vector2 newPos = pos + step;
            transform.position = new Vector3(newPos.x, newPos.y, transform.position.z);

            _view.SetFacing(dir);
            _view.PlayState(CharacterAnimState.Walk);
        }
    }
}
