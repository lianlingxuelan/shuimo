// -----------------------------------------------------------------------------
// 2.5D/BambooVfx.cs —— 砍竹特效（feature/2.5d，轮次 B）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。请在本地 Unity 2022.3
// 打开工程后确认编译通过，并按 docs/bamboo-2_5d-roundB-design.md §1.3 做 PlayMode
// 验收（晃动、断裂、粒子层次、顿帧期间竹子动画同步冻结）。
//
// 【职责】挂在每根竹子的父节点上。BambooSceneContext.DetectHarvest 检测到扇形
// 命中时调 OnHit，本类负责：阻尼晃动 → 累计伤害 → 断裂倾倒 → 墨迹/竹叶粒子。
//
// 【红线（对应 docs/bamboo-2_5d-roundB-design.md §4）】
//   1. 所有动画推进**只用 FeedbackClock.Delta**，一处 Time.deltaTime 都没有。
//      顿帧期间 Delta 恒为 0 → 竹子晃动/倾倒与全场景同步冻结，这正是
//      「复用 A-1 裁定、永久禁用 timeScale」的落地方式。
//   2. **绝不直写 FeedbackClock.Frozen** —— 它的唯一写入者是 HitFeedbackDirector。
//      本文件只读 Delta，一个字都不写。
//   3. 按已拍板的 **D6：竹子只冻自身动画**，本类**不扩 HitFeedbackDirector**、
//      不调 RequestHitstop / KickHitstop（KickHitstop 本就是 private）。
//      攻击判定本身走现有全局顿帧管线，无需竹子触发。
//   4. **绝不调 DamageResolver**、不进战斗内核。竹子不是 Combatant，
//      不占 AliveEnemyCount，伤害只用来算「砍几刀断」这个纯表现节奏。
//   5. 不写 Time.timeScale / Scheduler / RunPhase / CombatScheduler。
//
// 【粒子 prefab 的落地方式（对应任务书第 3 项的兜底分支）】
// 目标资产是 Assets/_Project/Scripts/Runtime/2.5D/Prefabs/BambooHitFx.prefab。
// 本环境没有 Unity，无法生成合法的 .prefab —— ParticleSystem 的 YAML 序列化包含
// 几十个模块子结构（InitialModule / ShapeModule / EmissionModule / ...）和
// 版本化字段，手写出来无法校验，一旦格式不合法 Unity 会直接报导入错误、
// 甚至污染 Library。因此按任务书授权的兜底分支执行：
//   → 在本文件内用代码运行时构造等效的占位粒子（BuildRuntimeFx）。
//   → **prefab 待用户在 Unity 内转为正式 prefab**：运行时选中自动生成的
//     "BambooHitFx_Runtime" 对象，拖到 Assets/_Project/Scripts/Runtime/2.5D/Prefabs/
//     存成 BambooHitFx.prefab，再把它拖回 BambooSceneContext.inkLeafPrefab
//     （或 BambooVfx.inkLeafPrefab）字段即可切到资产路径，代码无需改动。
// 三级取用优先级：Inspector 引用 → Resources.Load("2.5D/BambooHitFx") → 运行时构造。
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 单根竹子的受击表现：阻尼晃动 + 累计伤害断裂 + 墨迹/竹叶粒子。
    /// 纯表现层，不参与战斗内核，不写任何全局时钟状态。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BambooVfx : MonoBehaviour
    {
        // =====================================================================
        // 常量
        // =====================================================================

        /// <summary>Resources 兜底路径（相对任一 Resources 目录）。</summary>
        public const string FxResourcePath = "2.5D/BambooHitFx";

        /// <summary>运行时构造的占位粒子对象名（用户可据此在 Unity 内转正式 prefab）。</summary>
        public const string RuntimeFxName = "BambooHitFx_Runtime";

        // =====================================================================
        // Inspector 参数
        // =====================================================================

        [Header("断裂 / 晃动")]
        [Tooltip("单次受击的晃动时长（秒）")]
        public float shakeDuration = 0.35f;

        [Tooltip("晃动角度幅度（度），沿受击方向摆动")]
        public float shakeAmplitude = 7.0f;

        [Tooltip("晃动频率（Hz）")]
        public float shakeFrequency = 9.0f;

        [Tooltip("累计伤害达到此值即断裂。默认 30 ≈ 3 刀普攻（AttackRaw=12 起）")]
        public float breakThreshold = 30.0f;

        [Tooltip("命中次数达到此值也强制断裂（保底，避免加成为 0 时砍不倒）")]
        public int breakHitCount = 3;

        [Tooltip("断裂倾倒动画时长（秒）")]
        public float fallDuration = 0.9f;

        [Tooltip("断裂倾倒的最终角度（度）")]
        public float fallAngle = 82.0f;

        [Tooltip("断口高度占全竹高的比例，其下留残桩")]
        [Range(0.05f, 0.9f)]
        public float breakHeightRatio = 0.22f;

        [Header("粒子（优先 Inspector 引用，其次 Resources，最后运行时构造）")]
        [Tooltip("墨迹 / 竹叶粒子 prefab。留空则走 Resources 兜底或运行时构造")]
        public GameObject inkLeafPrefab;

        [Tooltip("粒子实例的存活时间（秒，按 FeedbackClock.Delta 计）")]
        public float fxLifetime = 1.6f;

        [Tooltip("是否允许在 prefab 缺失时用代码构造占位粒子")]
        public bool allowRuntimeFxFallback = true;

        // =====================================================================
        // 只读状态
        // =====================================================================

        /// <summary>是否已断裂。断裂后不再接受命中，也不参与扇形检测。</summary>
        public bool IsBroken
        {
            get { return _broken; }
        }

        /// <summary>累计承受的伤害（纯表现用，不进内核）。</summary>
        public float AccumulatedDamage
        {
            get { return _accumDmg; }
        }

        /// <summary>累计命中次数。</summary>
        public int HitCount
        {
            get { return _hits; }
        }

        // =====================================================================
        // 私有状态
        // =====================================================================

        private Transform _trunk;
        // 本根竹子的「生长轴」：由 BambooSceneContext 传入，现在是世界 +Y（屏幕「上」）
        // 带随机自然倾斜。铰链位置、断口高度、叶子筛选、粒子飘落全部沿这个轴。
        private Vector3 _depthAxis = new Vector3(0.0f, 0.0f, -1.0f);
        private float _height = 200.0f;
        private float _radius = 14.0f;
        private Material _leafMaterial;

        private int _hits;
        private float _accumDmg;
        private bool _broken;

        // ---- 砍竹内容闭环（掉落 + 重生，feature/2.5d 内容轮次）----
        /// <summary>竹子被砍断时触发一次（_broken 由 false→true 的瞬间）。场景层订阅它来掉材料。</summary>
        public event Action<BambooVfx> OnBroken;

        [Header("重生")]
        [Tooltip("断后多久重新长出来（秒，按 FeedbackClock.Delta 计，顿帧同步冻结）")]
        public float regrowSeconds = 12.0f;

        [Tooltip("重新生长动画时长（秒）")]
        public float regrowGrowDuration = 0.8f;

        // 整根竹子（含叶）复位所需的关键变换。叶子是 trunk 的子节点，
        // 因此只要把 trunk 移回原位、复位变换，叶子会一并归位。
        private Transform _trunkHomeParent;
        private Vector3 _trunkHomePos;
        private Quaternion _trunkHomeRot;
        private Vector3 _trunkHomeScale;
        private bool _homeCaptured;

        private Transform _hinge;
        private float _regrowRemain;
        private float _growElapsed;
        private bool _growing;

        private float _shakeRemain;
        private Vector3 _shakeAxis = Vector3.right;
        private Quaternion _trunkRestRotation = Quaternion.identity;
        private bool _restCaptured;

        private float _fallElapsed;
        private Transform _fallingPart;
        private Quaternion _fallFrom = Quaternion.identity;
        private Quaternion _fallTo = Quaternion.identity;

        // 运行时粒子实例的寿命管理（用 FeedbackClock.Delta 计时，顿帧同步冻结）
        private readonly System.Collections.Generic.List<FxInstance> _liveFx =
            new System.Collections.Generic.List<FxInstance>();

        private bool _fxPaused;

        // 砍竹音效：程序化合成（WoodSfx），无需外部 SFX 包。
        private AudioSource _sfx;

        /// <summary>一个存活中的粒子实例及其剩余寿命。</summary>
        private struct FxInstance
        {
            public GameObject Go;
            public float Remain;
            public ParticleSystem[] Systems;
        }

        // =====================================================================
        // 装配
        // =====================================================================

        /// <summary>
        /// 由 <see cref="BambooSceneContext"/> 在生成竹子时调用，注入几何信息与共享资源。
        /// </summary>
        /// <param name="trunk">竹竿 Transform（晃动/倾倒的作用对象）。</param>
        /// <param name="depthAxis">本根竹子的实际生长方向（由 BambooSceneContext 传入，沿世界 +Y 带随机倾斜）。</param>
        /// <param name="height">竹子总高（世界单位）。</param>
        /// <param name="radius">竹竿半径（世界单位）。</param>
        /// <param name="fxPrefab">粒子 prefab，可为 null。</param>
        /// <param name="leafMaterial">竹叶材质，供运行时构造粒子复用，可为 null。</param>
        public void Configure(
            Transform trunk,
            Vector3 depthAxis,
            float height,
            float radius,
            GameObject fxPrefab,
            Material leafMaterial)
        {
            _trunk = trunk;
            _depthAxis = depthAxis.sqrMagnitude > 1e-6f
                ? depthAxis.normalized
                : new Vector3(0.0f, 0.0f, -1.0f);
            _height = Mathf.Max(1.0f, height);
            _radius = Mathf.Max(0.01f, radius);
            _leafMaterial = leafMaterial;

            if (inkLeafPrefab == null)
            {
                inkLeafPrefab = fxPrefab;
            }

            // AddComponent 之后 Awake 已经跑过，可能已按「第一个子节点」抓过一次静止
            // 姿态。这里注入的才是权威竹竿，必须强制重抓，否则晃动会以错误姿态为基准。
            _restCaptured = false;
            CaptureRest();

            // 记录整根竹子的「家」变换，供重生时复位（含其下所有叶子子节点）。
            if (_trunk != null)
            {
                _trunkHomeParent = _trunk.parent;
                _trunkHomePos = _trunk.localPosition;
                _trunkHomeRot = _trunk.localRotation;
                _trunkHomeScale = _trunk.localScale;
                _homeCaptured = true;
            }
        }

        private void Awake()
        {
            if (_trunk == null)
            {
                // 允许手工挂载：没有 Configure 时退回「第一个子节点即竹竿」。
                _trunk = transform.childCount > 0 ? transform.GetChild(0) : transform;
            }
            CaptureRest();
        }

        private void CaptureRest()
        {
            if (_restCaptured || _trunk == null)
            {
                return;
            }
            _trunkRestRotation = _trunk.localRotation;
            _restCaptured = true;
        }

        // =====================================================================
        // 命中入口
        // =====================================================================

        /// <summary>
        /// 命中入口。由 <c>BambooSceneContext.DetectHarvest</c> 在扇形命中本竹时调用。
        ///
        /// 【伤害语义】damage 来自 <c>AttackController.AttackRaw(12) + CombatBridge.PlayerAtkBonus</c>，
        /// 与 AttackController.ResolveHits 的 effectiveRaw 同公式 —— 因此竹子断裂节奏
        /// 随玩家等级成长。但它**只用于本类的表现计时**，绝不回灌战斗内核、
        /// 不调 DamageResolver、不产生任何 Combatant 事件。
        /// </summary>
        /// <param name="damage">本次有效伤害（只读用途）。</param>
        /// <param name="hitPoint">命中点（玩法平面 XY 坐标）。</param>
        /// <param name="hitDir">命中方向（玩家 → 竹子，XY 单位向量）。</param>
        public void OnHit(float damage, Vector2 hitPoint, Vector2 hitDir)
        {
            if (_broken)
            {
                return;
            }

            _hits++;
            _accumDmg += Mathf.Max(0.0f, damage);

            // 晃动轴 = 与命中方向垂直（在玩法平面内），使竹子「被推着倒向刀锋方向」。
            Vector2 dir = hitDir.sqrMagnitude > 1e-6f ? hitDir.normalized : Vector2.right;
            _shakeAxis = new Vector3(-dir.y, dir.x, 0.0f);
            if (_shakeAxis.sqrMagnitude < 1e-6f)
            {
                _shakeAxis = Vector3.right;
            }
            _shakeAxis = _shakeAxis.normalized;

            _shakeRemain = shakeDuration;

            SpawnFx(hitPoint, dir);
            PlaySfx(WoodSfx.Tick, 0.5f);

            if (_accumDmg >= breakThreshold || _hits >= breakHitCount)
            {
                Break(dir);
            }
        }

        // =====================================================================
        // 每帧推进（唯一时钟 = FeedbackClock.Delta）
        // =====================================================================

        private void Update()
        {
            // 【红线】只读 FeedbackClock.Delta。顿帧期间它恒为 0 →
            // 晃动、倾倒、粒子寿命全部同步冻结，与全场景表现层一致。
            // 本类从不写 FeedbackClock.Frozen，也从不碰 Time.timeScale。
            float dt = FeedbackClock.Delta;
            bool frozen = dt <= 0.0f;

            // ParticleSystem 自己走 Time.deltaTime，不认 FeedbackClock。
            // 顿帧期间显式 Pause/Play，让粒子与晃动/倾倒真正同步冻结。
            SyncFxPause(frozen);

            if (frozen)
            {
                return;
            }

            TickShake(dt);
            TickFall(dt);
            TickFx(dt);

            // 砍竹内容闭环：倒下动画播完后开始重生倒计时；生长动画推进。
            // 全部走 FeedbackClock.Delta，顿帧期间 dt=0 → 与全场景同步冻结。
            if (_broken && _fallingPart == null && !_growing)
            {
                _regrowRemain -= dt;
                if (_regrowRemain <= 0.0f)
                {
                    Regrow();
                }
            }
            if (_growing)
            {
                TickGrow(dt);
            }
        }

        /// <summary>阻尼正弦晃动，绕 <see cref="_shakeAxis"/> 摆动竹竿。</summary>
        private void TickShake(float dt)
        {
            if (_shakeRemain <= 0.0f || _trunk == null || _broken)
            {
                return;
            }

            _shakeRemain -= dt;
            if (_shakeRemain <= 0.0f)
            {
                _shakeRemain = 0.0f;
                _trunk.localRotation = _trunkRestRotation;
                return;
            }

            float t = shakeDuration > 1e-4f ? _shakeRemain / shakeDuration : 0.0f;
            // t 从 1 衰减到 0，damp 让振幅逐渐收敛。
            float damp = t * t;
            float phase = (shakeDuration - _shakeRemain) * shakeFrequency * Mathf.PI * 2.0f;
            float angle = Mathf.Sin(phase) * shakeAmplitude * damp;

            _trunk.localRotation = Quaternion.AngleAxis(angle, _shakeAxis) * _trunkRestRotation;
        }

        /// <summary>断裂倾倒插值（EaseIn，像被砍断后加速倒下）。</summary>
        private void TickFall(float dt)
        {
            if (_fallingPart == null)
            {
                return;
            }

            _fallElapsed += dt;
            float t = fallDuration > 1e-4f ? Mathf.Clamp01(_fallElapsed / fallDuration) : 1.0f;
            // EaseIn：t^2，起手慢、落地快。
            float eased = t * t;
            _fallingPart.localRotation = Quaternion.Slerp(_fallFrom, _fallTo, eased);

            if (t >= 1.0f)
            {
                _fallingPart = null;
            }
        }

        /// <summary>粒子实例寿命递减（同样走 FeedbackClock.Delta）。</summary>
        private void TickFx(float dt)
        {
            for (int i = _liveFx.Count - 1; i >= 0; i--)
            {
                FxInstance fx = _liveFx[i];
                if (fx.Go == null)
                {
                    _liveFx.RemoveAt(i);
                    continue;
                }

                fx.Remain -= dt;
                if (fx.Remain <= 0.0f)
                {
                    Destroy(fx.Go);
                    _liveFx.RemoveAt(i);
                }
                else
                {
                    _liveFx[i] = fx;
                }
            }
        }

        /// <summary>
        /// 顿帧同步：ParticleSystem 内部按 Time.deltaTime 推进，不认 FeedbackClock，
        /// 因此这里显式 Pause/Play，让粒子与竹子动画在同一帧一起停、一起走。
        /// 只调 ParticleSystem 自己的 API，不碰 Time.timeScale、不写 FeedbackClock.Frozen。
        /// </summary>
        private void SyncFxPause(bool frozen)
        {
            if (frozen == _fxPaused)
            {
                return;
            }
            _fxPaused = frozen;

            for (int i = 0; i < _liveFx.Count; i++)
            {
                ParticleSystem[] systems = _liveFx[i].Systems;
                if (systems == null)
                {
                    continue;
                }

                for (int j = 0; j < systems.Length; j++)
                {
                    ParticleSystem ps = systems[j];
                    if (ps == null)
                    {
                        continue;
                    }

                    // systems 已经是 GetComponentsInChildren 的全量结果，
                    // 这里逐个处理即可，withChildren 传 false 避免对同一子节点重复递归。
                    if (frozen)
                    {
                        ps.Pause(false);
                    }
                    else
                    {
                        ps.Play(false);
                    }
                }
            }
        }

        // =====================================================================
        // 断裂
        // =====================================================================

        /// <summary>
        /// 断裂：竹竿绕断口倾倒，留一截残桩，并在断口再补一发粒子。
        /// 纯表现，不通知任何战斗系统。
        /// </summary>
        /// <param name="dir">命中方向（XY），决定倒向。</param>
        /// <summary>砍竹音效：程序化合成的竹裂/挥砍声（零外部资源）。</summary>
        private void PlaySfx(AudioClip clip, float volume)
        {
            if (clip == null)
            {
                return;
            }
            if (_sfx == null)
            {
                _sfx = GetComponent<AudioSource>();
            }
            if (_sfx == null)
            {
                _sfx = gameObject.AddComponent<AudioSource>();
            }
            _sfx.PlayOneShot(clip, volume);
        }

        private void Break(Vector2 dir)
        {
            if (_broken)
            {
                return;
            }
            _broken = true;
            PlaySfx(WoodSfx.Chop, 1.0f);
            _shakeRemain = 0.0f;
            _regrowRemain = regrowSeconds;

            // 通知场景层「这根被砍断了」——只触发一次（_broken 由 false→true 的瞬间）。
            // 场景层据此给玩家加竹材掉落。本类严格只管表现，掉落数据由场景层负责。
            if (OnBroken != null)
            {
                OnBroken(this);
            }

            if (_trunk != null)
            {
                _trunk.localRotation = _trunkRestRotation;

                // 倒向 = 命中方向；旋转轴与之垂直（玩法平面内）。
                Vector3 axis = new Vector3(-dir.y, dir.x, 0.0f);
                if (axis.sqrMagnitude < 1e-6f)
                {
                    axis = Vector3.right;
                }
                axis = axis.normalized;

                // 用一个「铰链」节点承载倾倒：把它放在断口高度，
                // 竹竿挂到它下面，旋转铰链即可实现绕断口倒下。
                GameObject hinge = new GameObject("BreakHinge");
                _hinge = hinge.transform;
                hinge.transform.SetParent(transform, false);
                hinge.transform.localPosition = _depthAxis * (_height * breakHeightRatio);
                hinge.transform.localRotation = Quaternion.identity;

                _trunk.SetParent(hinge.transform, true);

                _fallingPart = hinge.transform;
                _fallFrom = hinge.transform.localRotation;
                _fallTo = Quaternion.AngleAxis(fallAngle, axis) * _fallFrom;
                _fallElapsed = 0.0f;

                // 叶子跟着上半段一起倒：把断口以上的叶片也挂到铰链下。
                ReparentLeavesAbove(hinge.transform);
            }

            // 软碰撞体失效：断了的竹子不该再挡路，也不该再被检测到。
            Collider col = GetComponent<Collider>();
            if (col != null)
            {
                col.enabled = false;
            }

            Vector3 world = transform.position + _depthAxis * (_height * breakHeightRatio);
            SpawnFx(new Vector2(world.x, world.y), dir);
        }

        /// <summary>把断口以上的叶片改挂到铰链下，使其随上半段一起倾倒。</summary>
        private void ReparentLeavesAbove(Transform hinge)
        {
            float cutDepth = _height * breakHeightRatio;

            // 倒序遍历：SetParent 会即时改变 childCount。
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child == hinge || child == _trunk)
                {
                    continue;
                }

                // 沿深度轴的投影高度 > 断口高度 → 属于上半段。
                float depth = Vector3.Dot(child.localPosition, _depthAxis);
                if (depth > cutDepth)
                {
                    child.SetParent(hinge, true);
                }
            }
        }

        // =====================================================================
        // 粒子
        // =====================================================================

        /// <summary>
        /// 在命中点生成粒子。三级取用：Inspector 引用 → Resources 兜底 → 运行时构造。
        /// </summary>
        private void SpawnFx(Vector2 hitPoint, Vector2 dir)
        {
            // 命中点抬到竹子中段高度，粒子不至于贴在地面上。
            Vector3 pos = new Vector3(hitPoint.x, hitPoint.y, transform.position.z)
                + _depthAxis * (_height * 0.45f);
            Quaternion rot = Quaternion.LookRotation(
                new Vector3(dir.x, dir.y, 0.0f).sqrMagnitude > 1e-6f
                    ? new Vector3(dir.x, dir.y, 0.0f).normalized
                    : Vector3.right,
                _depthAxis);

            GameObject go = null;

            GameObject prefab = inkLeafPrefab;
            if (prefab == null)
            {
                prefab = Resources.Load<GameObject>(FxResourcePath);
                if (prefab != null)
                {
                    // 命中一次就缓存下来，后续不再走 Resources.Load。
                    inkLeafPrefab = prefab;
                }
            }

            if (prefab != null)
            {
                go = Instantiate(prefab, pos, rot);
            }
            else if (allowRuntimeFxFallback)
            {
                go = BuildRuntimeFx(pos, rot);
            }

            if (go == null)
            {
                return;
            }

            ParticleSystem[] systems = go.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                {
                    continue;
                }

                // 外部 prefab 不保证 playOnAwake=true（烘焙器会置 true，但用户可能自己拖了一个
                // 关掉 playOnAwake 的 prefab 进来）。这里补一刀 Play，保证 prefab 分支一定出粒子。
                if (!ps.isPlaying)
                {
                    ps.Play(false);
                }

                // 生成瞬间若正处于顿帧，新粒子也要立刻停住，避免「全场冻住只有它在动」。
                if (_fxPaused)
                {
                    ps.Pause(false);
                }
            }

            _liveFx.Add(new FxInstance
            {
                Go = go,
                Remain = Mathf.Max(0.1f, fxLifetime),
                Systems = systems
            });
        }

        /// <summary>
        /// 运行时构造的占位粒子：两路 ParticleSystem —— 墨点飞溅 + 竹叶飘落。
        ///
        /// 【这是 BambooHitFx.prefab 的等效代码实现】
        /// 正式 prefab 由 Editor 工具烘焙：菜单
        /// Shuimo/2.5D/烘焙 BambooHitFx.prefab
        /// （BambooHitFxPrefabBaker 内部调用 <see cref="CreateFxTemplate"/>，
        /// 由 Unity 自己完成 ParticleSystem 的序列化，避免手写 YAML 出错）。
        /// 烘焙并把资产拖回 inkLeafPrefab 后，本方法不会再被调用（prefab 分支优先）。
        /// </summary>
        private GameObject BuildRuntimeFx(Vector3 pos, Quaternion rot)
        {
            GameObject root = CreateFxTemplate(RuntimeFxName, _radius, _depthAxis, _leafMaterial);
            root.transform.SetPositionAndRotation(pos, rot);
            return root;
        }

        /// <summary>
        /// 构造一份「命中特效」层级并返回根节点（根节点上不挂任何脚本，纯视觉）。
        ///
        /// 运行时兜底与 Editor 烘焙 prefab 共用这一份实现 —— 保证「代码兜底的样子」
        /// 和「烘焙出来的 prefab 的样子」永远一致，不会出现两套参数各跑各的。
        /// </summary>
        /// <param name="name">根节点名字。</param>
        /// <param name="radius">竹干半径，用来把粒子尺寸/速度换算到世界单位（工程是 px 尺度）。</param>
        /// <param name="depthAxis">深度轴（指向相机的方向），竹叶会沿其反方向飘落。</param>
        /// <param name="leafMaterial">竹叶材质；为 null 时退回 Sprites/Default。</param>
        /// <returns>新建的特效根节点，调用方负责其生命周期。</returns>
        public static GameObject CreateFxTemplate(
            string name,
            float radius,
            Vector3 depthAxis,
            Material leafMaterial)
        {
            string rootName = string.IsNullOrEmpty(name) ? RuntimeFxName : name;
            float r = radius > 0.01f ? radius : 14.0f;
            Vector3 axis = depthAxis.sqrMagnitude > 1e-6f
                ? depthAxis.normalized
                : new Vector3(0.0f, 0.0f, -1.0f);

            GameObject root = new GameObject(rootName);

            BuildInkSplash(root.transform, r);
            BuildLeafFall(root.transform, r, axis, leafMaterial);

            return root;
        }

        /// <summary>墨点飞溅：小而快、沿刀锋方向锥形喷出。</summary>
        private static void BuildInkSplash(Transform parent, float radius)
        {
            GameObject go = new GameObject("InkSplash");
            go.transform.SetParent(parent, false);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            // 先停下来再配置，避免 Unity 用默认值抢跑一帧。
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(radius * 6.0f, radius * 16.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(radius * 0.18f, radius * 0.55f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.06f, 0.09f, 0.07f, 0.95f),
                new Color(0.16f, 0.22f, 0.15f, 0.85f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(0.0f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;
            main.playOnAwake = false;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(0.0f);
            emission.SetBursts(new[] { new ParticleSystem.Burst(0.0f, (short)18, (short)26) });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 32.0f;
            shape.radius = radius * 0.4f;

            ParticleSystem.SizeOverLifetimeModule sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1.0f, DecayCurve());

            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeOutGradient());

            ConfigureRenderer(go, ps, null);
            ps.Play(true);
        }

        /// <summary>竹叶飘落：大而慢、带重力和旋转。</summary>
        private static void BuildLeafFall(
            Transform parent,
            float radius,
            Vector3 depthAxis,
            Material leafMaterial)
        {
            GameObject go = new GameObject("LeafFall");
            go.transform.SetParent(parent, false);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(radius * 1.5f, radius * 5.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(radius * 0.8f, radius * 1.8f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.15f, 0.22f, 0.13f, 0.9f),
                new Color(0.28f, 0.34f, 0.20f, 0.75f));
            // 重力沿「深度轴的反方向」= 往地面落（深度轴指向相机 = 竹子的上）。
            main.gravityModifier = new ParticleSystem.MinMaxCurve(0.0f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 24;
            main.playOnAwake = false;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(0.0f);
            emission.SetBursts(new[] { new ParticleSystem.Burst(0.0f, (short)5, (short)9) });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = radius * 1.2f;

            // 自定义「下落」：深度轴指向相机，所以竹叶要沿 -depthAxis 落回地面。
            ParticleSystem.ForceOverLifetimeModule force = ps.forceOverLifetime;
            force.enabled = true;
            force.space = ParticleSystemSimulationSpace.World;
            Vector3 down = -depthAxis * (radius * 22.0f);
            force.x = new ParticleSystem.MinMaxCurve(down.x);
            force.y = new ParticleSystem.MinMaxCurve(down.y);
            force.z = new ParticleSystem.MinMaxCurve(down.z);

            ParticleSystem.RotationOverLifetimeModule rol = ps.rotationOverLifetime;
            rol.enabled = true;
            rol.z = new ParticleSystem.MinMaxCurve(-2.2f, 2.2f);

            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeOutGradient());

            ConfigureRenderer(go, ps, leafMaterial);
            ps.Play(true);
        }

        /// <summary>
        /// 配置粒子渲染器。优先复用传入的竹叶材质（水墨观感一致），
        /// 否则退到 Built-in 的 Sprites/Default（永远存在，不会粉红）。
        /// </summary>
        private static void ConfigureRenderer(GameObject go, ParticleSystem ps, Material leafMaterial)
        {
            ParticleSystemRenderer psr = go.GetComponent<ParticleSystemRenderer>();
            if (psr == null)
            {
                psr = go.AddComponent<ParticleSystemRenderer>();
            }

            psr.renderMode = ParticleSystemRenderMode.Billboard;
            psr.alignment = ParticleSystemRenderSpace.View;
            psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psr.receiveShadows = false;

            Material mat = leafMaterial;
            if (mat == null)
            {
                Shader s = Shader.Find("Sprites/Default");
                if (s == null)
                {
                    s = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                }
                if (s != null)
                {
                    mat = new Material(s);
                    mat.name = "MAT_BambooHitFx_Runtime";
                    mat.hideFlags = HideFlags.DontSave;
                }
            }

            if (mat != null)
            {
                psr.sharedMaterial = mat;
            }
        }

        /// <summary>1 → 0 的衰减曲线，供 sizeOverLifetime 用。</summary>
        private static AnimationCurve DecayCurve()
        {
            AnimationCurve c = new AnimationCurve();
            c.AddKey(0.0f, 1.0f);
            c.AddKey(0.6f, 0.55f);
            c.AddKey(1.0f, 0.0f);
            return c;
        }

        /// <summary>末段淡出的墨色渐变，供 colorOverLifetime 用。</summary>
        private static Gradient FadeOutGradient()
        {
            Gradient g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0.0f),
                    new GradientColorKey(Color.white, 1.0f)
                },
                new[]
                {
                    new GradientAlphaKey(1.0f, 0.0f),
                    new GradientAlphaKey(0.85f, 0.55f),
                    new GradientAlphaKey(0.0f, 1.0f)
                });
            return g;
        }

        // =====================================================================
        // 重生（砍竹内容闭环：断后定时长回）
        // =====================================================================

        /// <summary>
        /// 重生：把整根竹子（含叶，叶子是 trunk 的子节点）从断口铰链移回原位，
        /// 并以高度生长动画重新立起。纯表现，不通知战斗内核。
        /// 复用 Configure 时记录的「家」变换，因此无需外部重建对象。
        /// </summary>
        private void Regrow()
        {
            if (_trunk != null && _homeCaptured)
            {
                _trunk.SetParent(_trunkHomeParent, false);
                _trunk.localPosition = _trunkHomePos;
                _trunk.localRotation = _trunkHomeRot;
                // 从近 0 高度开始生长，避免重生瞬间的位置跳变。
                _trunk.localScale = new Vector3(
                    _trunkHomeScale.x,
                    Mathf.Max(0.01f, _trunkHomeScale.y * 0.02f),
                    _trunkHomeScale.z);
            }

            if (_hinge != null)
            {
                Destroy(_hinge.gameObject);
                _hinge = null;
            }

            _fallingPart = null;
            _broken = false;
            _growing = true;
            _growElapsed = 0.0f;
            _accumDmg = 0.0f;
            _hits = 0;

            // 断时禁用过的软碰撞体恢复生效，竹子重新可被命中与遮挡。
            Collider col = GetComponent<Collider>();
            if (col != null)
            {
                col.enabled = true;
            }
        }

        /// <summary>重生生长动画：竹竿高度从近 0 缓动回原高（EaseIn）。</summary>
        private void TickGrow(float dt)
        {
            if (_trunk == null || !_homeCaptured)
            {
                _growing = false;
                return;
            }

            _growElapsed += dt;
            float t = regrowGrowDuration > 1e-4f ? Mathf.Clamp01(_growElapsed / regrowGrowDuration) : 1.0f;
            float eased = t * t;
            float y = Mathf.Lerp(Mathf.Max(0.01f, _trunkHomeScale.y * 0.02f), _trunkHomeScale.y, eased);
            _trunk.localScale = new Vector3(_trunkHomeScale.x, y, _trunkHomeScale.z);

            if (t >= 1.0f)
            {
                _trunk.localScale = _trunkHomeScale;
                _growing = false;
            }
        }

        // =====================================================================
        // 清理
        // =====================================================================

        private void OnDestroy()
        {
            for (int i = 0; i < _liveFx.Count; i++)
            {
                if (_liveFx[i].Go != null)
                {
                    Destroy(_liveFx[i].Go);
                }
            }
            _liveFx.Clear();
        }
    }
}
