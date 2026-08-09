// -----------------------------------------------------------------------------
// HitEffects.cs —— 命中粒子 / 光效分发通道（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor / 美术资源，本文件**未经编译、未经视觉验证**。
// 引用 UnityEngine，不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// =============================================================================
// 【它是什么：第五路派发，不是第五个订阅者】
//
// 受击反馈原有四路（顿帧 / 屏震 / 闪白 / 飘字），本文件补上第五路：**粒子光效**。
//
// 【★ 明确不要什么：让本类自己订阅 CombatEventsUnity.HitFeedback】
// 这条路看起来最省事（新文件零侵入、Director 一行不改），但它是错的，
// 而且错得很隐蔽 —— 自己订阅意味着本类要重新获得下面这四样东西：
//   ① 轻/重分档（要抄一遍 HeavyRatio 的两套阈值）；
//   ② 同目标 0.02s 去重（不抄的话范围技能一帧内多段结算会炸出 6 份粒子）；
//   ③ 暂停 / 终局闸门（不抄的话结算面板背后会继续喷粒子，违反 A-8 零残留）；
//   ④ 组件禁用守卫。
// 抄第五遍的下场，就是 HitFeedbackDirector 文件头写死的那句话：
// "玩家会看到飘字说是重击、屏震却是轻的"。
//
// 所以本类**不订阅任何事件**，只暴露 PlayHit / PlayKill 这些哑接口，
// 由 Director 在**同一次 Grade() 之后**顺手调一次。副作用最小：
//   · 事件订阅数不变（仍然只有 Director 一个），不可能双重触发；
//   · 去重 / 暂停 / 禁用三道闸自动继承，一行都不用抄；
//   · 轻重档、濒死、连续强度系数全部现成，粒子可以直接按档位放大。
//
// 【★ 明确不要什么：在本文件里生成粒子贴图 / 法阵 sprite / shader】
// 本工程是"零资产、全程序化"，但那条纪律的适用范围是**几何形状**
// （SpriteFactory 能画 Circle / Crescent）。粒子系统的火花、灵气、拖尾
// 不是几何形状，硬用代码拼出来只会得到一堆抖动的圆点 —— 那不是"美术效果好"，
// 那是"看起来像调试可视化"（VfxSlash 文件头已经论证过同一件事）。
//
// 因此本文件交付的是**通道**而不是**美术**：全部 prefab 走 [SerializeField]
// 占位，null 时静默跳过。没挂资源 → 行为与本文件不存在时逐帧等价；
// 挂了资源 → 立刻有粒子。这是在"无美术资源、无法验证视觉"的约束下
// 唯一诚实的交付形态。
//
// 【★ 为什么除了 [SerializeField] 还要一条 Resources 兜底路径】
// 本工程**不落场景文件**：CombatBridge 由 WorldBuilder.cs:733 在运行时
// AddComponent 出来（已 grep 实证）。也就是说，Bridge 自动补挂的这一份
// HitEffects 是运行时创建的，Inspector 里根本没有它，拖不进任何 prefab。
// 于是给本地用户留两条都能走通的路（详见 ClearAll 下方的"接资源指南"）：
//   路线 A（推荐）：场景里手工建一个空物体挂本组件 → Inspector 里拖 prefab。
//                   Director / Bridge 的 FindFirstObjectByType 会优先认它。
//   路线 B（零场景编辑）：把 prefab 丢进 Assets/Resources/Vfx/ 下的约定路径，
//                   本类 Awake 时自动 Resources.Load 补齐。
// Resources.Load 找不到时返回 null 且**不报错**，与 null 守卫天然合流。
//
// 【生命周期：每击 Instantiate + 定长看板 + 到期 Destroy】
// 沿用 VfxSlash 已经论证过的量级判断：命中约 2.5 次/秒，每次一次分配是
// 可接受的；真正会泄漏的是"prefab 自己不销毁、又没人管它"。所以本类维护
// 一个定长看板（默认 24 格）：到期销毁、看板满则先销毁最老的那个。
// 这既是回收，也是同屏粒子数的硬上限 —— 清场时不至于一屏几百个粒子把帧率打死。
//
// 【为什么不做"熄灭复用"式对象池】
// 池化 ParticleSystem 必须 Stop(WithChildren, Clear) → SetActive(false) →
// 复用时 Play()，这套流程对**用户自带的任意 prefab** 不成立：它可能是
// Animator 驱动的、可能是 TrailRenderer、可能挂着自定义脚本。
// 对不认识的东西做状态复用，等于把"用户 prefab 长什么样"变成本类的隐式契约。
// 每击新建则对任何 prefab 都成立。等到有明确的、可验证的性能压力时再池化。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 命中粒子 / 光效的分发通道。**哑渲染器**：不认识阵营、不算档位、不订阅事件。
    ///
    /// <para>驱动方：<see cref="HitFeedbackDirector"/>。它在 <c>Grade()</c> 之后
    /// 把已经裁定好的档位（轻/重、是否玩家挨打、连续强度系数）传进来，
    /// 本类只负责"在这个位置、按这个倍率、实例化这个 prefab"。</para>
    ///
    /// <para>全部 prefab 字段均可为 null —— 那是**合法状态**，此时本类静默跳过，
    /// 不报错、不刷警告、不抛 MissingReferenceException。</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(125)]
    public sealed class HitEffects : MonoBehaviour
    {
        // =====================================================================
        // Resources 兜底路径的出厂默认值
        //
        // 【为什么是 public const 而不是写死在字段初始化里】
        // 本地用户需要知道"prefab 该放在哪个文件夹"，而这个答案必须只有一份。
        // 做成 const 之后，接资源指南、Inspector 默认值、单元测试三处引用同一个
        // 字面量；散成三份的话，将来改目录会静默漏掉其中之一。
        //
        // 【路径不带 Resources/ 前缀、不带扩展名】
        // 这是 Resources.Load 的口径：Assets/Resources/Vfx/HitLight.prefab
        // 对应的 key 就是 "Vfx/HitLight"。写错前缀不会报错，只会静默返回 null，
        // 所以这里把正确写法钉成常量，不让调用方现编。
        // =====================================================================

        /// <summary>轻击粒子的 Resources 默认路径（Assets/Resources/Vfx/HitLight.prefab）。</summary>
        public const string DefaultHitResPath = "Vfx/HitLight";

        /// <summary>重击粒子的 Resources 默认路径（Assets/Resources/Vfx/HitHeavy.prefab）。</summary>
        public const string DefaultHeavyResPath = "Vfx/HitHeavy";

        /// <summary>玩家受击粒子的 Resources 默认路径（Assets/Resources/Vfx/HitPlayer.prefab）。</summary>
        public const string DefaultPlayerResPath = "Vfx/HitPlayer";

        /// <summary>击杀粒子的 Resources 默认路径（Assets/Resources/Vfx/HitKill.prefab）。</summary>
        public const string DefaultKillResPath = "Vfx/HitKill";

        /// <summary>拖尾 prefab 的 Resources 默认路径（预留，待美术资源接入）。</summary>
        public const string DefaultTrailResPath = "Vfx/Trail";

        /// <summary>增益光环 prefab 的 Resources 默认路径（预留，待美术资源接入）。</summary>
        public const string DefaultAuraResPath = "Vfx/BuffAura";

        // =====================================================================
        // Inspector 配置：命中通道（本期主干）
        // =====================================================================

        [Header("命中粒子（挂上即生效；留空 = 静默跳过，不报错）")]

        [Tooltip("轻击命中粒子。所有命中的兜底 prefab —— 重击/玩家档留空时会回落到它。")]
        [SerializeField] private GameObject _hitParticlePrefab;

        [Tooltip("重击命中粒子。留空则重击也用轻击 prefab（仅靠尺寸倍率区分）。")]
        [SerializeField] private GameObject _heavyParticlePrefab;

        [Tooltip("玩家挨打时的粒子（建议朱砂调，与 PlayerHitFlash 同一套视觉语言）。留空则回落到轻/重档。")]
        [SerializeField] private GameObject _playerHitParticlePrefab;

        [Tooltip("击杀爆发粒子。留空则击杀不额外出粒子（顿帧+屏震仍在）。")]
        [SerializeField] private GameObject _killParticlePrefab;

        // =====================================================================
        // Inspector 配置：预留通道
        //
        // 【为什么现在就把字段摆出来，而不是等有资源再加】
        // 加字段是零风险的（null 守卫已经全覆盖），而"等有资源再改代码"意味着
        // 本地用户拿到美术资源的那一刻还得先来改一遍 C#。摆出来之后，
        // 拖进去就生效，代码侧零改动 —— 这正是配置驱动的意义。
        //
        // 【为什么本期不深做】
        // 拖尾要接 AttackController 的挥砍时序、光环要接 StatusTable 的增益进出场，
        // 两者都是**需要看着画面调**的东西。在没有 Unity、没有资源、无法验证视觉的
        // 环境里把它们写完，交付的只会是"看起来很完整但没人验证过"的代码，
        // 那比留一个诚实的骨架更糟。
        // =====================================================================

        [Header("预留通道（待美术资源接入；本期只提供骨架，未接时序）")]

        [Tooltip("拖尾 prefab。预留：接上后可由挥砍/冲刺时序调 PlayTrail。")]
        [SerializeField] private GameObject _trailPrefab;

        [Tooltip("增益光环 prefab。预留：接上后可由状态进出场调 PlayAura / StopAura。")]
        [SerializeField] private GameObject _buffAuraPrefab;

        // =====================================================================
        // Inspector 配置：Resources 兜底路径
        // =====================================================================

        [Header("Resources 兜底路径（对应字段为空时才尝试；清空 = 不尝试）")]

        [Tooltip("轻击粒子的 Resources 路径。不含 Resources/ 前缀与扩展名。")]
        [SerializeField] private string _hitResPath = DefaultHitResPath;

        [Tooltip("重击粒子的 Resources 路径。")]
        [SerializeField] private string _heavyResPath = DefaultHeavyResPath;

        [Tooltip("玩家受击粒子的 Resources 路径。")]
        [SerializeField] private string _playerResPath = DefaultPlayerResPath;

        [Tooltip("击杀粒子的 Resources 路径。")]
        [SerializeField] private string _killResPath = DefaultKillResPath;

        [Tooltip("拖尾 prefab 的 Resources 路径（预留）。")]
        [SerializeField] private string _trailResPath = DefaultTrailResPath;

        [Tooltip("增益光环 prefab 的 Resources 路径（预留）。")]
        [SerializeField] private string _auraResPath = DefaultAuraResPath;

        // =====================================================================
        // Inspector 配置：行为
        // =====================================================================

        [Header("行为")]

        [Tooltip("单个粒子实例的强制存活时长（秒）。必须 >= prefab 自身的播放时长，否则会被提前切掉。")]
        [SerializeField] private float _lifetime = HitFeedbackConfig.HitFxLifetime;

        [Tooltip("击杀粒子的强制存活时长（秒）。通常比命中粒子长一点。")]
        [SerializeField] private float _killLifetime = HitFeedbackConfig.KillFxLifetime;

        [Tooltip("同屏粒子实例上限。超出时先销毁最老的一个，保证新命中一定有回应。")]
        [SerializeField] private int _capacity = HitFeedbackConfig.HitFxCapacity;

        [Tooltip("是否按伤害档位缩放粒子尺寸。关掉后所有命中的粒子一样大。")]
        [SerializeField] private bool _scaleByGrade = true;

        [Tooltip("调试日志。默认关闭 —— 命中每秒好几次，开着会刷屏。")]
        [SerializeField] private bool _verboseLog;

        // =====================================================================
        // 运行时状态
        // =====================================================================

        /// <summary>
        /// 一个在播的粒子实例。
        ///
        /// 【为什么是 struct】
        /// 看板长度固定（默认 24），数组在 Awake 里一次性建好，此后永不再分配。
        /// 三个字段里只有一个是引用，做成 class 就是每击一次堆分配喂 GC ——
        /// 与 <c>HitGrade</c> 选 struct 的理由完全相同。
        /// </summary>
        private struct LiveFx
        {
            /// <summary>实例根节点。可能已被 prefab 自带脚本销毁，读之前必须判空。</summary>
            public GameObject Go;

            /// <summary>已存活时长（秒）。吃 <see cref="FeedbackClock.Delta"/>。</summary>
            public float Age;

            /// <summary>本实例的强制存活上限（秒）。</summary>
            public float Life;
        }

        /// <summary>定长看板。前 <see cref="_liveCount"/> 格有效，其余为空。</summary>
        private LiveFx[] _live;

        /// <summary>看板里的有效格数。</summary>
        private int _liveCount;

        /// <summary>
        /// prefab 是否已经解析过（含 Resources 兜底）。
        ///
        /// 【为什么只解析一次】
        /// Resources.Load 在资源不存在时每次都会走一遍完整的路径查找。
        /// 命中每秒好几次，不缓存"我已经找过了、确实没有"这个结论的话，
        /// 就是每秒好几次无谓的资源系统查询 —— 而它永远不会成功。
        /// </summary>
        private bool _prefabsResolved;

        /// <summary>
        /// 是否被冻结。冻结期间存活计时停摆，但一个都不销毁。
        /// 语义与 <c>DamagePopupLayer._frozen</c> 逐字对齐（菜单暂停 = 冻结，终局 = 清空）。
        /// </summary>
        private bool _frozen;

        /// <summary>
        /// 当前是否至少有一个可用的命中 prefab。供调试与本地接资源时自查。
        /// 生产路径不依赖它 —— 生产路径靠的是每个 Spawn 前的 null 守卫。
        /// </summary>
        public bool HasHitPrefab
        {
            get
            {
                return _hitParticlePrefab != null
                    || _heavyParticlePrefab != null
                    || _playerHitParticlePrefab != null;
            }
        }

        /// <summary>当前在播的粒子实例数。供测试与调试读取。</summary>
        public int LiveCount
        {
            get { return _liveCount; }
        }

        // =====================================================================
        // 生命周期
        // =====================================================================

        private void Awake()
        {
            EnsureBoard();
            EnsurePrefabs();
        }

        private void OnDisable()
        {
            // 组件被关掉之后 LateUpdate 不再执行，存活计时永远走不完，
            // 那些粒子实例就会**永久留在场景里**。自己造的东西自己收干净 ——
            // 与 DamagePopupLayer.OnDisable 同一条纪律。
            ClearAll();
        }

        private void OnDestroy()
        {
            // 销毁路径（换场景 / Destroy(gameObject) / 退出 PlayMode）不保证一定
            // 先走 OnDisable，两处都写才叫兜底。沿用 PlayerHitFlash 的既有写法。
            ClearAll();
        }

        /// <summary>
        /// 推进全部在播实例的存活计时，到期销毁。
        ///
        /// 【为什么吃 FeedbackClock.Delta 而不是 Time.deltaTime】
        /// 顿帧期间画面"看起来停住了"，粒子的**存活计时**也必须跟着停，
        /// 否则 0.12s 的玩家顿帧会白白吃掉粒子寿命的一大截，解冻瞬间
        /// 玩家看到的是一个已经快消失的特效 —— 节奏对不上。
        /// 这与 VfxSlash.Update / PlayerHitFlash.LateUpdate 的选择一致。
        ///
        /// 【注意：这只冻住"我什么时候销毁它"，冻不住 prefab 内部的播放】
        /// ParticleSystem 自己走 Time.deltaTime（或 unscaled），本类无权干涉，
        /// 也不该干涉 —— 去反射用户 prefab 的内部组件把它按住，是把
        /// "prefab 长什么样"变成本类的隐式契约（见文件头）。
        /// 顿帧只有 ≤0.14s，这点偏差肉眼够不着。真要精确同步，
        /// 正确做法是让美术把粒子的 Simulation Space 设成 Custom 并接时钟，
        /// 那属于资源侧的事，不在代码侧解决。
        ///
        /// 【为什么 dt == 0 时也要扫一遍】
        /// prefab 可能自带销毁脚本（Stop Action = Destroy），它一销毁，
        /// 看板里就留下一格 Go == null 的僵尸。不扫的话这一格会一直占着名额，
        /// 满 24 格之后新粒子会开始顶掉活着的实例 —— 表现为"打久了特效变少"。
        /// </summary>
        private void LateUpdate()
        {
            if (_live == null || _liveCount <= 0)
            {
                return;
            }

            float dt = _frozen ? 0.0f : FeedbackClock.Delta;

            // 倒序遍历 + 尾部交换删除：被换到 i 位的那个元素来自 i 之后，
            // 本帧已经处理过，跳过它是正确的（下一帧再轮到它）。
            for (int i = _liveCount - 1; i >= 0; i--)
            {
                _live[i].Age += dt;

                if (_live[i].Go == null || _live[i].Age >= _live[i].Life)
                {
                    RemoveAt(i);
                }
            }
        }

        // =====================================================================
        // 对外入口 —— 命中通道（本期主干）
        // =====================================================================

        /// <summary>
        /// 在命中点播一次粒子。由 <see cref="HitFeedbackDirector"/> 在四路派发之后调用。
        ///
        /// <para>【prefab 选择顺序】玩家挨打 → <c>_playerHitParticlePrefab</c>；
        /// 重击 → <c>_heavyParticlePrefab</c>；两者缺位时一律回落到
        /// <c>_hitParticlePrefab</c>。全部为 null 则**静默返回**。</para>
        ///
        /// <para>【为什么档位由调用方传进来，而不是本类自己判】
        /// 与 <c>DamagePopupLayer.Push</c> 同理：分档只在
        /// <c>HitFeedbackDirector.Grade()</c> 发生一次。本类再判一次
        /// <c>defender.Faction</c>，就是同一条规则的第二份实现，
        /// 迟早出现"飘字判成重击、粒子却出了轻击那一套"。</para>
        /// </summary>
        /// <param name="world">受击方世界坐标（本类会自行加上 <c>HitFxSpawnOffsetY</c> 抬到体心）。</param>
        /// <param name="heavy">是否重击档。</param>
        /// <param name="isPlayerVictim">受击方是否玩家。</param>
        /// <param name="gradeScale">R-06 连续强度系数（已含 FeedbackIntensity）。</param>
        /// <returns>实例化出来的对象；prefab 缺位或本组件未启用时为 null。</returns>
        public GameObject PlayHit(Vector3 world, bool heavy, bool isPlayerVictim, float gradeScale)
        {
            if (!isActiveAndEnabled)
            {
                return null;
            }

            EnsurePrefabs();

            GameObject prefab = PickHitPrefab(heavy, isPlayerVictim);
            if (prefab == null)
            {
                // ★ 没挂资源是**合法状态**，不是错误。静默跳过，
                //   行为与本组件不存在时逐帧等价。
                return null;
            }

            Vector3 at = new Vector3(
                world.x,
                world.y + HitFeedbackConfig.HitFxSpawnOffsetY,
                world.z);

            return Spawn(prefab, at, Quaternion.identity,
                         ResolveScale(heavy, gradeScale), _lifetime, null);
        }

        /// <summary>
        /// 播一次击杀爆发粒子。由 <see cref="HitFeedbackDirector.OnEnemyDied"/> 路径调用。
        ///
        /// <para>【为什么击杀单独一个 prefab 而不是复用重击】
        /// 与 R-07 击杀强调同因：致命一击的伤害往往极小，按占比分档会判成最轻的一档。
        /// 击杀是**事件**不是数值，视觉上也该有自己的一档（通常是更大的一次爆散）。
        /// 留空时回落到重击 → 轻击，都没有就静默跳过。</para>
        /// </summary>
        /// <param name="world">死亡实体的世界坐标。</param>
        /// <returns>实例化出来的对象；prefab 缺位时为 null。</returns>
        public GameObject PlayKill(Vector3 world)
        {
            if (!isActiveAndEnabled)
            {
                return null;
            }

            EnsurePrefabs();

            GameObject prefab = _killParticlePrefab;
            if (prefab == null)
            {
                prefab = _heavyParticlePrefab;
            }
            if (prefab == null)
            {
                prefab = _hitParticlePrefab;
            }
            if (prefab == null)
            {
                return null;
            }

            Vector3 at = new Vector3(
                world.x,
                world.y + HitFeedbackConfig.HitFxSpawnOffsetY,
                world.z);

            return Spawn(prefab, at, Quaternion.identity,
                         HitFeedbackConfig.HitFxScaleKill, _killLifetime, null);
        }

        // =====================================================================
        // 对外入口 —— 预留通道（骨架，待美术资源接入）
        // =====================================================================

        /// <summary>
        /// 【预留 · 待美术资源接入】在指定位置按朝向播一段拖尾。
        ///
        /// <para>本期**没有任何调用方**：拖尾要贴着挥砍 / 冲刺的时序走，
        /// 而那是必须看着画面调的东西，在无 Unity、无资源、无法验证视觉的环境里
        /// 硬接时序，交付的只会是"看起来完整但没人验证过"的代码。</para>
        ///
        /// <para>【本地接入方式】把 prefab 拖进 <c>_trailPrefab</c>，
        /// 然后在 <c>AttackController</c> 挥砍起手处 / <c>DodgeController</c>
        /// 冲刺起步处调本方法即可 —— 代码侧无需再改本文件。</para>
        /// </summary>
        /// <param name="world">拖尾起点世界坐标。</param>
        /// <param name="facing">朝向（可不归一化；零向量时不旋转）。</param>
        /// <param name="seconds">存活时长（秒）。&lt;= 0 时取 <see cref="_lifetime"/>。</param>
        /// <returns>实例化出来的对象；prefab 缺位时为 null。</returns>
        public GameObject PlayTrail(Vector3 world, Vector2 facing, float seconds)
        {
            if (!isActiveAndEnabled)
            {
                return null;
            }

            EnsurePrefabs();

            if (_trailPrefab == null)
            {
                return null;
            }

            return Spawn(_trailPrefab, world, RotationOf(facing), 1.0f,
                         seconds > 0.0f ? seconds : _lifetime, null);
        }

        /// <summary>
        /// 【预留 · 待美术资源接入】给某个宿主挂一圈增益光环。
        ///
        /// <para>本期**没有任何调用方**：光环的进出场要跟着
        /// <c>StatusTable</c> 的增益增删走，而增益的视觉表达（几种增益共用一个环，
        /// 还是各有各的颜色）是一个需要美术拍板的问题，代码侧现在猜不出来。</para>
        ///
        /// <para>【★ 这是全类唯一一处 parent 非 null 的实例化】
        /// 光环必须**跟着宿主移动**，所以它得是宿主的子物体。其余所有特效
        /// 一律挂在场景根下（理由见 <see cref="Spawn"/> 的注释）。</para>
        ///
        /// <para>【本地接入方式】拖 prefab 进 <c>_buffAuraPrefab</c>，
        /// 在增益获得时调本方法、失去时对返回值调 <see cref="StopFx"/>。</para>
        /// </summary>
        /// <param name="host">宿主 Transform。为 null 时静默返回。</param>
        /// <param name="seconds">存活时长（秒）。&lt;= 0 表示"由调用方用 StopFx 手动结束"，
        /// 此时仍受看板上限保护，不会真的永生。</param>
        /// <returns>实例化出来的对象；prefab 缺位或宿主为 null 时为 null。</returns>
        public GameObject PlayAura(Transform host, float seconds)
        {
            if (!isActiveAndEnabled || host == null)
            {
                return null;
            }

            EnsurePrefabs();

            if (_buffAuraPrefab == null)
            {
                return null;
            }

            // seconds <= 0 时给一个很长但有限的上限，而不是真的不销毁：
            // "永生特效"只要有一次忘了调 StopFx 就是一个永久泄漏，
            // 而看板满了之后它还会开始顶掉正常的命中粒子 —— 故障会跑到
            // 一个完全无关的地方去显现，那是最难排查的一类 bug。
            float life = seconds > 0.0f ? seconds : HitFeedbackConfig.AuraFxMaxSeconds;

            return Spawn(_buffAuraPrefab, host.position, host.rotation, 1.0f, life, host);
        }

        /// <summary>
        /// 【预留 · 待美术资源接入】提前结束一个由本类创建的特效实例。
        ///
        /// <para>只销毁**登记在看板上的**实例 —— 传进来一个不是本类造的对象时
        /// 什么都不做，绝不去 Destroy 别人家的东西。</para>
        /// </summary>
        /// <param name="fx">此前由 PlayAura / PlayTrail 返回的对象。可为 null。</param>
        /// <returns>true = 找到并销毁了；false = 不在看板上（或已经没了）。</returns>
        public bool StopFx(GameObject fx)
        {
            if (fx == null || _live == null)
            {
                return false;
            }

            for (int i = 0; i < _liveCount; i++)
            {
                if (_live[i].Go == fx)
                {
                    RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        // =====================================================================
        // 对外入口 —— 收敛
        // =====================================================================

        /// <summary>
        /// 冻结 / 解冻整层。冻结期间存活计时停摆，但一个都不销毁。
        ///
        /// <para>【谁会调它】<see cref="HitFeedbackDirector"/> 的暂停三态收敛：
        /// 菜单暂停 <c>FreezeAll(true)</c>，恢复后 <c>FreezeAll(false)</c>。
        /// 本类**不读任何暂停标志** —— 判定权在 CombatBridge、转达权在 Director、
        /// 执行权才在本类。三权分立与 DamagePopupLayer 逐字一致。</para>
        /// </summary>
        /// <param name="frozen">true = 冻结。</param>
        public void FreezeAll(bool frozen)
        {
            _frozen = frozen;
        }

        /// <summary>
        /// 立刻销毁全部在播粒子。终局（A-8：结算面板上零残留）、拆线、
        /// 以及本类的 OnDisable / OnDestroy 调用。
        ///
        /// <para>【为什么顺带解冻】清空之后还留着 <c>_frozen == true</c> 的话，
        /// 下一局第一个粒子会诞生即定格、并且永远不到期。这类"上一局的脏值"
        /// 在本工程已经出过事（MainMenuHud.SkipOnNextLoad 导致 MENU09 假红）。
        /// 清就清干净 —— 与 DamagePopupLayer.ClearAll 同一条纪律。</para>
        /// </summary>
        public void ClearAll()
        {
            _frozen = false;

            if (_live == null)
            {
                return;
            }

            for (int i = _liveCount - 1; i >= 0; i--)
            {
                RemoveAt(i);
            }
            _liveCount = 0;
        }

        // =====================================================================
        // 内部 —— prefab 解析
        // =====================================================================

        /// <summary>
        /// 补齐所有为 null 的 prefab 字段：Inspector 里没拖的，尝试走 Resources。
        /// 只执行一次（见 <see cref="_prefabsResolved"/>）。
        /// </summary>
        private void EnsurePrefabs()
        {
            if (_prefabsResolved)
            {
                return;
            }
            _prefabsResolved = true;

            _hitParticlePrefab = ResolveOne(_hitParticlePrefab, _hitResPath);
            _heavyParticlePrefab = ResolveOne(_heavyParticlePrefab, _heavyResPath);
            _playerHitParticlePrefab = ResolveOne(_playerHitParticlePrefab, _playerResPath);
            _killParticlePrefab = ResolveOne(_killParticlePrefab, _killResPath);
            _trailPrefab = ResolveOne(_trailPrefab, _trailResPath);
            _buffAuraPrefab = ResolveOne(_buffAuraPrefab, _auraResPath);

            if (_verboseLog)
            {
                Debug.LogFormat(
                    "[T2] HitEffects prefab 解析完成: hit={0} heavy={1} player={2} kill={3} trail={4} aura={5}",
                    _hitParticlePrefab != null, _heavyParticlePrefab != null,
                    _playerHitParticlePrefab != null, _killParticlePrefab != null,
                    _trailPrefab != null, _buffAuraPrefab != null);
            }
        }

        /// <summary>
        /// 单个字段的解析：Inspector 已赋值就用它，否则试一次 Resources。
        ///
        /// <para>【为什么 Resources.Load 失败不报错】
        /// "本地还没做这个特效"是本期的**默认状态**，不是异常。
        /// 在默认状态下刷一条警告，等于开局就有六条红字，真正的问题会被淹掉。
        /// 想知道解析结果的人打开 <c>_verboseLog</c> 即可（见 EnsurePrefabs）。</para>
        /// </summary>
        /// <param name="assigned">Inspector 里拖进来的引用，可为 null。</param>
        /// <param name="resPath">Resources 路径，可为空串。</param>
        /// <returns>可用的 prefab；两条路都没有则 null。</returns>
        private static GameObject ResolveOne(GameObject assigned, string resPath)
        {
            if (assigned != null)
            {
                return assigned;
            }
            if (string.IsNullOrEmpty(resPath))
            {
                return null;
            }
            return Resources.Load<GameObject>(resPath);
        }

        /// <summary>
        /// 按档位挑一个命中 prefab。全部缺位时返回 null（调用方负责静默跳过）。
        ///
        /// <para>【为什么用逐级 if 而不是 ?? / 三元链】
        /// Unity 重载了 <c>UnityEngine.Object == null</c> 来表达"已销毁"，
        /// 而 C# 的 <c>??</c> 走的是**真正的**引用判空，两者对"已销毁但引用还在"
        /// 的对象给出相反的答案。在 Unity 里对 Object 用 <c>??</c> 是一个
        /// 编译得过、跑起来才炸的经典坑。全类一律显式 <c>!= null</c>。</para>
        /// </summary>
        private GameObject PickHitPrefab(bool heavy, bool isPlayerVictim)
        {
            if (isPlayerVictim && _playerHitParticlePrefab != null)
            {
                return _playerHitParticlePrefab;
            }
            if (heavy && _heavyParticlePrefab != null)
            {
                return _heavyParticlePrefab;
            }
            if (_hitParticlePrefab != null)
            {
                return _hitParticlePrefab;
            }

            // 轻击 prefab 也没挂时，退到重击那一个 —— 只挂了一个 prefab 的
            // 本地用户应该看到特效，而不是"只有重击才有、轻击什么都没有"。
            if (_heavyParticlePrefab != null)
            {
                return _heavyParticlePrefab;
            }
            return _playerHitParticlePrefab;
        }

        // =====================================================================
        // 内部 —— 实例化与看板
        // =====================================================================

        /// <summary>
        /// 实例化一个特效并登记到看板。
        ///
        /// <para>【★ 为什么默认 parent 传 null（挂在场景根下）而不是挂在本组件下】
        /// 本组件很可能被 <c>CombatBridge.SetupHitFeedback()</c> 补挂在
        /// Combat 那个 GameObject 上，而"那个对象将来会不会移动 / 缩放"
        /// 不是本类能保证的。一旦它动了，所有已经播出去的粒子会**跟着一起漂**——
        /// 这是那种"看起来像物理 bug、实际是父子关系写错"的问题，极难联想到根因。
        /// 挂在场景根下则位置永远是命中点本身。清理不依赖父子关系，
        /// 由看板显式 Destroy 负责（<see cref="RemoveAt"/>）。
        /// 唯一的例外是 <see cref="PlayAura"/>：光环本来就必须跟着宿主走。</para>
        /// </summary>
        /// <param name="prefab">已确认非 null 的 prefab。</param>
        /// <param name="at">世界坐标。</param>
        /// <param name="rot">世界旋转。</param>
        /// <param name="scale">在 prefab 自身缩放基础上再乘的倍率。</param>
        /// <param name="life">强制存活时长（秒）。</param>
        /// <param name="parent">父节点，通常为 null。</param>
        private GameObject Spawn(GameObject prefab, Vector3 at, Quaternion rot,
                                 float scale, float life, Transform parent)
        {
            // NaN 会一路写进 transform.position，而 Transform 一旦被写进 NaN
            // 就再也回不来了（后续任何算术仍是 NaN），表现为"这个特效永久消失"
            // 或者更糟：连带把父级层级的包围盒算坏。宁可丢一次特效。
            // 这条守卫与 DamagePopupLayer.Push 的 NaN 判断同因同治。
            if (float.IsNaN(at.x) || float.IsNaN(at.y) || float.IsNaN(at.z)
                || float.IsInfinity(at.x) || float.IsInfinity(at.y) || float.IsInfinity(at.z))
            {
                return null;
            }

            GameObject go = Instantiate(prefab, at, rot, parent);
            if (go == null)
            {
                return null;
            }

            if (scale > 0.0f && !Mathf.Approximately(scale, 1.0f))
            {
                // 乘而不是赋值：prefab 自己的缩放是美术定好的"这个特效多大"，
                // 直接赋值会把它冲掉，表现为"所有特效都变成 1 单位大"。
                go.transform.localScale = go.transform.localScale * scale;
            }

            Track(go, life);
            return go;
        }

        /// <summary>
        /// 把一个实例登记到看板。看板已满时先销毁最老的那一个。
        ///
        /// <para>【★ 池耗尽策略：覆盖最老，不丢弃最新 —— 与 DamagePopupLayer 同款】
        /// 特效是**瞬时反馈**，价值随年龄单调衰减：最老的那个已经播到尾巴了，
        /// 牺牲它的代价接近零；而它换来的是"玩家刚打出的这一下**一定**有特效"。
        /// 反过来"丢弃最新"会让战斗最激烈的那一秒反而最哑，把反馈曲线做反。</para>
        /// </summary>
        private void Track(GameObject go, float life)
        {
            if (go == null)
            {
                return;
            }

            EnsureBoard();

            if (_liveCount >= _live.Length)
            {
                EvictOldest();
            }

            // EvictOldest 之后仍然满，只可能是看板长度为 0 —— 那已经被
            // EnsureBoard 的下限钳制排除了。这一条是纯防御，走到这里说明
            // 有人改坏了 EnsureBoard，此时直接销毁比留一个不受管理的实例好。
            if (_liveCount >= _live.Length)
            {
                Destroy(go);
                return;
            }

            _live[_liveCount].Go = go;
            _live[_liveCount].Age = 0.0f;
            _live[_liveCount].Life = life > 0.0f ? life : HitFeedbackConfig.HitFxLifetime;
            _liveCount++;
        }

        /// <summary>销毁看板上 Age 最大（最老）的那一个，腾出一格。</summary>
        private void EvictOldest()
        {
            if (_liveCount <= 0)
            {
                return;
            }

            int oldest = 0;
            float maxAge = -1.0f;
            for (int i = 0; i < _liveCount; i++)
            {
                // 已经被 prefab 自带脚本销毁的僵尸格优先清掉：它不占任何画面，
                // 却占着一个名额。找到就立刻用它，不用再比 Age。
                if (_live[i].Go == null)
                {
                    oldest = i;
                    break;
                }
                if (_live[i].Age > maxAge)
                {
                    maxAge = _live[i].Age;
                    oldest = i;
                }
            }

            RemoveAt(oldest);
        }

        /// <summary>
        /// 从看板移除一格并销毁其实例。尾部交换删除，O(1)。
        /// </summary>
        /// <param name="index">有效下标，调用方保证在 [0, _liveCount) 内。</param>
        private void RemoveAt(int index)
        {
            if (_live == null || index < 0 || index >= _liveCount)
            {
                return;
            }

            GameObject go = _live[index].Go;
            if (go != null)
            {
                // 用 Destroy 而不是 DestroyImmediate：后者在运行时会打断
                // Unity 自己的销毁调度，且在编辑器里会真的删磁盘资产 ——
                // 万一有人把 prefab 本体（而不是实例）传进来，那就是删源文件。
                Destroy(go);
            }

            int last = _liveCount - 1;
            _live[index] = _live[last];
            _live[last] = default(LiveFx);
            _liveCount = last;
        }

        /// <summary>
        /// 确保看板数组已建好。容量取 Inspector 值，钳到合法区间。
        ///
        /// <para>【为什么要钳】<c>_capacity</c> 是 Inspector 可写字段，
        /// 谁都能把它填成 0 或 -1。填 0 的后果不是"没有特效"，
        /// 而是 <see cref="Track"/> 里那条防御分支每击 Instantiate 又立刻 Destroy ——
        /// 一秒钟好几次无谓的分配，而且屏幕上什么都看不见，
        /// 排查时根本想不到是容量填了 0。</para>
        /// </summary>
        private void EnsureBoard()
        {
            if (_live != null)
            {
                return;
            }

            int cap = Mathf.Clamp(_capacity,
                                  HitFeedbackConfig.HitFxCapacityMin,
                                  HitFeedbackConfig.HitFxCapacityMax);
            _live = new LiveFx[cap];
            _liveCount = 0;
        }

        // =====================================================================
        // 内部 —— 工具
        // =====================================================================

        /// <summary>
        /// 按档位算粒子尺寸倍率。
        ///
        /// <para>【为什么要钳到 [Min, Max]】<c>gradeScale</c> 里含
        /// <c>HitFeedbackConfig.FeedbackIntensity</c>，那是全仓唯一一个可写
        /// static，运行时可能被调成 0（无障碍全关）。不钳的话 0 会让
        /// <c>localScale</c> 变成零向量 —— 一个存在但看不见的特效，
        /// 白白占着看板名额。钳到下限至少还是"小了一点"这个可理解的表现。</para>
        /// </summary>
        private float ResolveScale(bool heavy, float gradeScale)
        {
            float baseK = heavy
                ? HitFeedbackConfig.HitFxScaleHeavy
                : HitFeedbackConfig.HitFxScaleLight;

            if (!_scaleByGrade)
            {
                return baseK;
            }

            float g = gradeScale;
            if (float.IsNaN(g) || float.IsInfinity(g) || g <= 0.0f)
            {
                g = 1.0f;
            }

            return Mathf.Clamp(baseK * g,
                               HitFeedbackConfig.HitFxScaleMin,
                               HitFeedbackConfig.HitFxScaleMax);
        }

        /// <summary>
        /// 把一个二维朝向变成绕 Z 轴的旋转。零向量（含极短向量）时返回单位旋转 ——
        /// <c>Atan2(0, 0)</c> 虽然有定义（返回 0），但"没有朝向"和"朝向正右"
        /// 是两件事，让它们撞在一起会在静止起手时出现一次朝向跳变。
        /// </summary>
        private static Quaternion RotationOf(Vector2 facing)
        {
            if (facing.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }

            float deg = Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg;
            return Quaternion.Euler(0.0f, 0.0f, deg);
        }
    }
}
