// -----------------------------------------------------------------------------
// PlayerController.cs —— 自由 8 向移动（asmdef: Xianxia.Unity.T2）
//
// 【玩家是唯一由 Unity 侧写位置的实体】
// 内核（Encounter.StepFixed）会积分敌人的位移，但**绝不回写玩家**——
// CombatController.SyncKernelIntoViews 里那个 `continue` 就是为此存在的。
// 顺序上，本组件必须在 CombatController 之前跑完，否则内核读到的是上一帧的
// 玩家位置，接触判定整体延迟一帧。所以打了 DefaultExecutionOrder(-100)。
//
// 【斜向归一化：这不是细节，是手感的分水岭】
// 直接 (x, y) 当速度用，右上方向的模长是 √2 ≈ 1.414 —— 玩家会发现斜着走
// 快 41%，于是所有人全程斜着走，你精心设计的横向走位从此不存在。
// 一行 .normalized 就消灭了这个问题。
//
// 【为什么不用 Rigidbody2D】
// P0 不做地形碰撞（P1-05）。上刚体等于引入一套需要调 drag/mass/interpolation 的
// 物理时序，而它现在唯一的作用是把 Transform 挪一挪。等真要做墙体时再上不迟。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>玩家移动与朝向。挂在 Player 上。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed class PlayerController : MonoBehaviour, IFacingProvider
    {
        /// <summary>移动速度（世界单位/秒）。200 ≈ 6.25 tile/s。</summary>
        public const float MoveSpeed = 200.0f;

        /// <summary>
        /// 判定"有输入"的死区。手柄摇杆静止时不会精确回零，
        /// 没有死区就会让 lastFacing 被噪声抖成随机方向。
        /// </summary>
        public const float InputDeadzone = 0.01f;

        [Header("移动")]
        [Tooltip("移动速度（世界单位/秒）。")]
        [SerializeField] private float moveSpeed = MoveSpeed;

        [Tooltip("把玩家钳制在世界边界内。关掉可自由出图，方便调试。")]
        [SerializeField] private bool clampToWorld = true;

        [Header("朝向")]
        [Tooltip("朝向指示条。由 WorldBuilder 注入，可留空。")]
        [SerializeField] private Transform facingMarker;

        /// <summary>
        /// 最近一次的有效朝向（归一化）。松开方向键后**保持不变**——
        /// 攻击扇形吃这个值，若在停手瞬间清零，站桩挥砍就会打向 (0,0) 的 NaN 方向。
        /// 初值向右（架构文档 U4）。
        /// </summary>
        public Vector2 LastFacing { get; private set; }

        /// <summary>本帧的移动方向（归一化，无输入时为零）。</summary>
        public Vector2 MoveDir { get; private set; }

        /// <summary>本帧是否有移动输入。</summary>
        public bool IsMoving
        {
            get { return MoveDir.sqrMagnitude > 0.0f; }
        }

        /// <summary>
        /// 外部接管的速度（世界单位/秒）。非 null 时本组件**不再**用输入驱动位移，
        /// 由接管方（<see cref="DodgeController"/>）负责积分，避免同一帧被推两次。
        /// </summary>
        public Vector2? ExternalVelocity { get; private set; }

        /// <summary>移动是否已被外部接管。</summary>
        public bool IsExternallyDriven
        {
            get { return ExternalVelocity.HasValue; }
        }

        /// <summary><see cref="IFacingProvider"/> 实现：当前朝向。</summary>
        public Vector2 CurrentFacing
        {
            get { return LastFacing; }
        }

        // 战斗桥缓存（惰性查找一次，之后复用）。P0-2 闸门每帧读 b.IsRunOver 要用。
        private CombatBridge _bridge;

        // P2_3 诊断：Update 末尾（Move 写完）记下的位置；LateUpdate 时若被外部
        // 偷偷改写（其他脚本的 LateUpdate/OnTrigger/外部 AI 拽回等），直接夺回。
        private Vector3 _posAfterUpdate;

        // 已确认玩家为场景根节点，避免每帧重复调用 native SetParent。
        private bool _isRootConfirmed;

        /// <summary>
        /// 取得战斗桥引用：优先用缓存，没有就自己找一次。
        ///
        /// 【和 SkillController.ResolveBridge 同款写法（含 UNITY_2023_1_OR_NEWER）】
        /// FindFirstObjectByType 会遍历整个场景、相当昂贵，绝不能每帧搜。
        /// 找到之后写回 _bridge 字段，后续帧第一个 if 就直接返回——
        /// 于是全场景搜索一辈子只发生一次（"惰性初始化 + 缓存"）。
        ///
        /// 【不需要 using Xianxia.Combat】本组件只关心 CombatBridge 这个 Unity 脚本
        /// （同处 Xianxia.Unity.T2 命名空间），读的是它转发的 bool 型 IsRunOver，
        /// 不接触内核的 RunPhase 枚举，所以现有 using 足够。
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

        private void Awake()
        {
            LastFacing = Vector2.right;
            MoveDir = Vector2.zero;
            ExternalVelocity = null;

            // P0-移动 bug 保险修复：玩家必须是场景根节点，不能被任何世界生成根（如
            // Shuimo_T2World）挂为子物体。父物体的 transform 每帧被 native 代码/相机
            // 系统改写时，会把玩家的 world position 一起拽走，表现为"动一下回到原点/
            // 抽搐"。WorldBuilder 里已经改成 SetParent(null)，但为确保任何编译/缓存
            // 状态下都生效，Player 自己 Awake 时再做一次强制解绑并保持世界坐标。
            if (transform.parent != null)
            {
                Debug.LogWarning("[PlayerDiag] 玩家被挂在 " + transform.parent.name
                    + " 下，强制提升为根节点以保持移动独立。");
                transform.SetParent(null, true);
            }
            else
            {
                _isRootConfirmed = true;
            }
        }

        /// <summary>由 <see cref="WorldBuilder"/> 注入朝向指示条。</summary>
        public void SetFacingMarker(Transform marker)
        {
            facingMarker = marker;
        }

        private void Update()
        {
            // ★ P0-2 移动 bug 最终保险：把玩家提到场景根节点。
            // 已确认过根节点后跳过，避免每帧无意义地调用 native SetParent。
            // 任何世界生成根（Shuimo_T2World 等）或 prefab 实例化时挂的父物体，
            // 都会把玩家的 world position 一起拖拽，表现为"动一下回到原点/抽搐"。
            // 这里用 SetParent(null, true) 保持当前世界坐标不变，只做结构解绑。
            if (!_isRootConfirmed && transform.parent != null)
            {
                Vector3 worldPosBeforeUnparent = transform.position;
                transform.SetParent(null, true);
                // 二次保险：SetParent(true) 在极端情况下仍可能被 native 层改坐标，
                // 直接把我们刚记录的世界坐标写回去，夺回绝对控制权。
                if ((transform.position - worldPosBeforeUnparent).sqrMagnitude > 0.0001f)
                {
                    transform.position = worldPosBeforeUnparent;
                }
                else
                {
                    _isRootConfirmed = true;
                }
            }

            // ★ P0-2 闸门：对局结束后（玩家死亡 / 通关）不再接受任何输入、不再移动。
            // 读 b.IsRunOver（CombatBridge 转发的 Encounter.IsRunOver，内核只读状态）。
            // 不放行的话，玩家死了还能用 WASD 乱走、攻击键还能打空挥——
            // 这正是用户实测到的"死了还能移动、游戏毫无反应"。
            // 放在最开头，先于 ReadInput / Move，确保死亡当帧起就彻底冻结玩家。
            // P0-5 起改读 IsGameplayBlocked：它把"终局"与"菜单打开"合成同一道闸门，
            // 否则主菜单/暂停面板盖在屏幕上时玩家仍能用 WASD 在幕后乱走。
            CombatBridge b = ResolveBridge();

            if (b != null && b.IsGameplayBlocked)
            {
                return;
            }

            ReadInput();
            Move(Time.deltaTime);
            UpdateFacingMarker();
            _posAfterUpdate = transform.position;
        }

        /// <summary>
        /// P2_3 诊断 LateUpdate：检测 Update 之后是否还有别的代码改写玩家位置。
        /// 间距 > 0.1 单位认为「被外力拽走」，立即报「谁在改 + 改了多大 + 完整调用栈」。
        ///
        /// 【为什么抓调用栈】"动一下回到原点/抽搐"这类症状，本质是 Update 刚把玩家挪到
        /// 新位置，随后某个其它系统（内核回写 / CombatView / 击退 / 场景重建）又把
        /// transform.position 覆写回旧值。光看差值只能确认"被改了"，看不出"谁改的"。
        /// 这里在发现被改的当帧直接 capture 托管调用栈，一次 PlayMode 就能点名元凶，
        /// 不再需要来回试。
        /// </summary>
        private void LateUpdate()
        {
            // 最终保险：任何在 Update 之后把玩家重新挂回父物体、或 native 层改坐标的
            // 行为，都在这里被直接抵消——把玩家拉回 Update 结束时我们记录的位置。
            // 已确认根节点后仍每帧检查一次 parent：被外部重新挂回是小概率事件，
            // 一旦检测到就重置标志，下帧 Update 会重新解绑。
            if (transform.parent != null)
            {
                _isRootConfirmed = false;
            }

            Vector3 now = transform.position;
            Vector3 diff = now - _posAfterUpdate;
            if (diff.sqrMagnitude > 0.01f)
            {
                // 被外力拽走：直接夺回控制权，写回 Update 结束时的位置。
                // 日志已关闭——移动 bug 已定位并修复（父物体拖拽），保留保险即可。
                transform.position = _posAfterUpdate;
            }
        }

        /// <summary>
        /// 读输入。用内置 Input Manager 的 Horizontal / Vertical 轴：
        /// 它天然同时吃 WASD、方向键和手柄，而 T2 没有引入 New Input System
        /// （架构文档 §6 明确不新增依赖包）。
        /// </summary>
        private void ReadInput()
        {
            // T3：改走 InputBinder（键鼠 + 手柄统一出口）。无 InputBinder 组件时
            // 其静态门面会回退到 Input.GetAxisRaw("Horizontal"/"Vertical")，
            // 与 T2 原路径逐字节等价。
            Vector2 raw = InputBinder.MoveAxis();
            if (raw.sqrMagnitude <= InputDeadzone)
            {
                MoveDir = Vector2.zero;
                return;
            }

            // ★ 斜向归一化：8 个方向的速度模长必须完全相等。
            MoveDir = raw.normalized;
            LastFacing = MoveDir;
        }

        private void Move(float dt)
        {
            // 外部接管期间（翻滚位移）不吃输入：接管方自己积分，这里再动一次就会翻倍。
            if (ExternalVelocity.HasValue)
            {
                return;
            }
            if (dt <= 0.0f || !IsMoving)
            {
                return;
            }

            ApplyDisplacement(MoveDir * moveSpeed, dt);
        }

        /// <summary>
        /// 设置 / 清除外部接管速度。传 null 交还控制权。
        /// </summary>
        /// <param name="velocity">世界单位/秒；null = 解除接管。</param>
        public void SetExternalVelocity(Vector2? velocity)
        {
            ExternalVelocity = velocity;
        }

        /// <summary>
        /// 由接管方调用，按给定速度推进一帧位移（复用同一套世界边界钳制）。
        /// </summary>
        /// <param name="velocity">世界单位/秒。</param>
        /// <param name="dt">本帧时长（秒）。</param>
        public void MoveExternal(Vector2 velocity, float dt)
        {
            if (dt <= 0.0f || velocity.sqrMagnitude <= 0.0f)
            {
                return;
            }
            ApplyDisplacement(velocity, dt);
            // ★ 闪避「原地闪一下」修复（阶段 66）：
            // 外部接管位移（DodgeController 翻滚）发生在 PlayerController.Update(-100) 之后、
            // LateUpdate 之前（DodgeController 为 -90，晚于 PlayerController）。若不同步刷新
            // _posAfterUpdate 快照，LateUpdate 的「外力回拽」护栏会把这次合法位移误判为
            // 异常改写并回退到快照位置，表现为「有特效、人物却原地不动」。
            // 这里把快照抬高到外部位移之后：合法位移被护栏正确放行，真·外力（父物体拖拽）
            // 仍照常拦截。_posAfterUpdate 为字段，在 Update/LateUpdate 同处赋值，可直接写。
            _posAfterUpdate = transform.position;
        }

        /// <summary>
        /// 强制覆写朝向（软索敌吸附 / 翻滚方向）。零向量忽略。
        /// </summary>
        /// <param name="facing">目标朝向，内部归一化。</param>
        public void SetFacingOverride(Vector2 facing)
        {
            if (facing.sqrMagnitude <= 0.0f)
            {
                return;
            }
            LastFacing = facing.normalized;
        }

        private void ApplyDisplacement(Vector2 velocity, float dt)
        {
            Vector3 pos = transform.position;
            pos.x += velocity.x * dt;
            pos.y += velocity.y * dt;

            if (clampToWorld && WorldBuilder.Grid != null)
            {
                // 留半格边距，让玩家贴边时身体不会有一半跑到边界岩石外面去。
                float margin = WorldBuilder.TileUnit * 0.5f;
                pos.x = Mathf.Clamp(pos.x, margin, WorldBuilder.WorldWidth - margin);
                pos.y = Mathf.Clamp(pos.y, margin, WorldBuilder.WorldHeight - margin);
            }

            transform.position = pos;
        }

        /// <summary>把朝向条摆到身体外缘，指向 LastFacing。</summary>
        private void UpdateFacingMarker()
        {
            if (facingMarker == null)
            {
                return;
            }
            float dist = WorldBuilder.PlayerBodySize * 0.62f;
            facingMarker.localPosition = new Vector3(LastFacing.x * dist, LastFacing.y * dist, 0.0f);
            facingMarker.localRotation = Quaternion.Euler(0.0f, 0.0f, FacingDegrees);
        }

        /// <summary>朝向角（度）。0° 为 +X，逆时针为正，与 <c>Vec2.Angle</c> 同约定。</summary>
        public float FacingDegrees
        {
            get { return Mathf.Atan2(LastFacing.y, LastFacing.x) * Mathf.Rad2Deg; }
        }
    }
}
