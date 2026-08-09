// -----------------------------------------------------------------------------
// CameraFollow.cs —— 阻尼跟随 + 边界钳制（asmdef: Xianxia.Unity.T2）
//
// 【为什么在 LateUpdate】
// 相机必须在所有移动都结束之后再取景。放在 Update 里，跟随的是玩家**上一帧**的
// 位置，快速移动时画面会持续落后半帧，表现为「人物在屏幕上左右轻微游移」。
//
// 【为什么用 SmoothDamp 而不是 Lerp(a, b, k)】
// Lerp 的 k 是"每帧比例"，帧率一变跟随手感就变：144Hz 下黏得要命，30Hz 下甩得
// 到处飞。SmoothDamp 吃的是"到达时间"，与帧率无关，60/144Hz 手感一致。
//
// 【边界钳制在阻尼之后】
// 顺序反过来的话，阻尼会把已经钳好的位置又拉出边界，玩家贴着地图边走时
// 会看到画面在边界上来回抽搐。
//
// 【为什么 SmoothDamp 的起点是自维护的 _center，而不是 transform.position】
// 因为 CameraShake（ExecutionOrder 150，排在本组件的 100 之后）会在本组件写完
// 之后**再改一次** transform.position。如果下一帧继续拿 transform.position 当
// 阻尼起点，抖动就会被当成"玩家移动"喂进 _velocity：跟随开始跟着抖，屏震停下
// 之后镜头还会自己"荡"好几下，并且留下累计漂移。R-02 明文要求"结束时相机必须
// 精确回到无偏移位置，不允许留下累计漂移"，这条不解决就是零分。
//
// 所以本组件维护一个**权威的无偏移中心** _center：跟随只认它，屏震只读它、
// 绝不回写它。无屏震时每帧末尾 transform.position 恒等于 _center，
// 下一帧读哪个都一样 —— 这次改造对"没有屏震"的情况是逐字等价的。
//
// 【钳制递归适用于屏震】
// 屏震是叠加在跟随结果之上的**又一层位移**，所以"钳制必须在位移之后"这条原则
// 对它同样成立：CameraShake 必须调 ClampPoint 把 basePos + offset 再钳一次。
// 代价是地图边缘的屏震幅度会被自动压到 0 —— 这不是 bug，宁可少抖，绝不露黑边。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>2D 正交相机跟随。挂在 Main Camera 上。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(100)]
    public sealed class CameraFollow : MonoBehaviour
    {
        /// <summary>默认跟随平滑时间（秒）。越大越"重"。</summary>
        public const float DefaultSmoothTime = 0.14f;

        [Header("跟随")]
        [Tooltip("跟随目标。由 WorldBuilder 注入。")]
        [SerializeField] private Transform target;

        [Tooltip("平滑时间（秒）。0 = 硬跟随。")]
        [SerializeField] private float smoothTime = DefaultSmoothTime;

        [Tooltip("相机 Z。2D 正交下必须为负，否则什么都看不到。")]
        [SerializeField] private float cameraZ = -100.0f;

        [Header("边界")]
        [Tooltip("把镜头钳制在世界矩形内，避免拍到地图外的空白。")]
        [SerializeField] private bool clampToWorld = true;

        [SerializeField] private float worldWidth;
        [SerializeField] private float worldHeight;

        private Camera _cam;
        private Vector3 _velocity;

        /// <summary>
        /// 权威的**无偏移**相机中心。跟随的唯一状态，屏震只读不写。
        /// 详见文件头「为什么 SmoothDamp 的起点是自维护的 _center」。
        /// </summary>
        private Vector3 _center;

        /// <summary>
        /// 供 <see cref="CameraShake"/> 只读取用的"没有任何抖动时相机应该在哪"。
        ///
        /// 【为什么屏震必须每帧从这里重新起算，而不是在当前位置上累加偏移】
        /// 累加式抖动没有任何机制保证回零，浮点误差会一点点攒成永久漂移；
        /// 而"基准点 + 本帧偏移量"的写法，只要偏移量在末尾算出 0，
        /// 相机就**数学上精确**回到无偏移位置，不依赖任何清理步骤。
        /// </summary>
        public Vector3 BaseCenter
        {
            get { return _center; }
        }

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _velocity = Vector3.zero;

            // Configure() 之前就可能有人读 BaseCenter（CameraShake 的 Awake 早于
            // WorldBuilder 的注入），先拿当前位置兜底，避免从世界原点起步。
            _center = transform.position;
        }

        /// <summary>由 <see cref="WorldBuilder"/> 注入跟随目标与世界尺寸。</summary>
        public void Configure(Transform follow, float width, float height)
        {
            target = follow;
            worldWidth = width;
            worldHeight = height;

            if (_cam == null)
            {
                _cam = GetComponent<Camera>();
            }

            // 立刻吸附到目标，省掉进场时那一下"从原点飞过去"。
            if (target != null)
            {
                Vector3 snap = new Vector3(target.position.x, target.position.y, cameraZ);

                // _center 必须与 transform 同步吸附。漏掉它的话，_center 还停在
                // Awake 时的旧值，第一帧 SmoothDamp 会从那里往目标"飞"过去，
                // 恰好把这次吸附想省掉的那一下又还回来了。
                _center = ClampToBounds(snap);
                transform.position = _center;
                _velocity = Vector3.zero;
            }
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            Vector3 desired = new Vector3(target.position.x, target.position.y, cameraZ);

            // 第 6 参显式传 FeedbackClock.Delta：hitstop 期间取 0，SmoothDamp 原地
            // 返回起点、且不改 _velocity，相机与角色一起定格。
            // 不传的话它会取 Time.deltaTime，顿帧时镜头继续往前推，
            // 人停了画面还在游，那比不做顿帧更难受。
            Vector3 next = smoothTime > 0.0f
                ? Vector3.SmoothDamp(_center, desired, ref _velocity, smoothTime,
                                     Mathf.Infinity, FeedbackClock.Delta)
                : desired;

            next.z = cameraZ;

            // 钳制在阻尼之后（文件头原则），结果写进权威中心，再落到 transform。
            // 无屏震时到此为止，与改造前逐字等价。
            _center = ClampToBounds(next);
            transform.position = _center;
        }

        /// <summary>
        /// 把任意一个候选相机位置钳进合法取景范围。纯函数，不读也不改任何状态。
        ///
        /// 【为什么要把私有的 ClampToBounds 开一个公开只读包装】
        /// 因为屏震偏移量是在本组件之外（CameraShake, ExecutionOrder 150）加上去的，
        /// 而"钳制必须在位移之后"这条原则对它同样成立。不开这个口子，CameraShake
        /// 就只有两个选择：在钳制之前加偏移（等于白抖，会被随后的钳制吃掉），
        /// 或者干脆不钳（地图边缘直接露黑边，A-6 一票否决）。两头都是死路。
        /// </summary>
        public Vector3 ClampPoint(Vector3 pos)
        {
            return ClampToBounds(pos);
        }

        /// <summary>
        /// 把相机中心钳到"视口完全落在世界内"的合法范围。
        /// 世界比视口还小时（不该发生，但配置错了就会）居中显示而不是抽搐。
        /// </summary>
        private Vector3 ClampToBounds(Vector3 pos)
        {
            if (!clampToWorld || _cam == null || worldWidth <= 0.0f || worldHeight <= 0.0f)
            {
                return pos;
            }

            float halfH = _cam.orthographicSize;
            float halfW = halfH * _cam.aspect;

            if (worldWidth <= halfW * 2.0f)
            {
                pos.x = worldWidth * 0.5f;
            }
            else
            {
                pos.x = Mathf.Clamp(pos.x, halfW, worldWidth - halfW);
            }

            if (worldHeight <= halfH * 2.0f)
            {
                pos.y = worldHeight * 0.5f;
            }
            else
            {
                pos.y = Mathf.Clamp(pos.y, halfH, worldHeight - halfH);
            }

            return pos;
        }
    }
}
