// -----------------------------------------------------------------------------
// Bootstrap.cs —— T2 世界生成器的注册入口 + 切片参数唯一真源（T01）
//
// 【它只做三件事】
//   1. 持有切片的三个入口参数（zone / visits / safe）—— 这是**唯一真源**，
//      WorldBuilder、DeterminismDump、Hud 全部从这里读，不各存一份。
//   2. 把 WorldBuilder.BuildScene 挂到 ShuimoSceneBuilder.BuildScene 静态委托上，
//      让编辑器菜单「Shuimo/Scene/Clean And Rebuild」点下去有反应（PRD §3.1）。
//   3. 运行时兜底：进入 Play 后若场景里没有「活的」已生成世界，就自动生成一次（Q9）。
//
// 【为什么注册必须走 InitializeOnLoadMethod】
// ShuimoSceneBuilder.BuildScene 是个静态委托，没有任何东西会主动去碰 WorldBuilder。
// 如果只在某个 MonoBehaviour 的 Awake 里注册，编辑器不进 Play 就永远注册不上——
// 菜单点下去静默无事发生，是最容易浪费半小时的一类「没坏但不动」。
// [InitializeOnLoadMethod] 在每次域重载（改代码后）都会跑，注册永远是最新的。
//
// 【为什么运行时还要再兜一次底】
// 编辑期生成的世界，其 Sprite / Tile / Texture2D 全是**运行时 new 出来的对象**，
// 不是磁盘资产，无法随 .unity 场景序列化。域重载或重开工程后，场景里那批
// GameObject 还在，但 SpriteRenderer.sprite 已经是 null（表现为「一片空白但层级很满」）。
// 因此判定条件不能只看「有没有根节点」，还要看 WorldBuilder 的静态状态是否仍然活着；
// 两者有一个不成立就重建，保证 Play 下去一定是一个完整世界。
//
// 【★ 本文件的两个 static bool 必须有复位钩子 —— 这是本工程第三次栽在同一处】
// _registered / _firstSceneLoaded 都是 static。Unity 的
// 「Enter Play Mode Options → Reload Domain 关闭」是加速迭代的常用设置，
// 开着它时 static 字段**跨 PlayMode 存活**，上一局的残值会直接决定下一局的行为。
// 本工程的同构事故已经有两起，教训一模一样：
//   1. MainMenuHud.SkipOnNextLoad 残留 → P0_5 的 MENU09 假红（跨夹具污染）；
//   2. FeedbackClock.Frozen / HitFeedbackConfig.FeedbackIntensity 残留
//      → 开局即全局冻结 / 反馈全没了，且一条报错都没有，于是补了 ResetStatics。
// 这是**第三次**：_firstSceneLoaded 残留为 true 会让 EnsureWorldAtRuntime 直接
// 早退，而首个场景的 sceneLoaded 事件发生在订阅之前，于是世界永远不建 —— 黑屏。
// 结论写死在这里：本文件（以及将来任何新增的可写 static）一旦多一个 static 字段，
// 就必须同步在 ResetStatics() 里加一行。没有例外。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.SceneManagement;
using Shuimo;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// T2 切片的引导器。纯静态，无实例、无场景对象。
    /// </summary>
    public static class Bootstrap
    {
        /// <summary>切片区域 id。P0 固定为幽篁竹海（PRD Q10）。</summary>
        public const string DefaultZoneId = "zone_youhuang";

        /// <summary>切片默认访问次数。参与 <c>ZoneSeed.Derive</c>，改它就换一张图。</summary>
        public const int DefaultZoneVisits = 1;

        private static string _zoneId = DefaultZoneId;
        private static int _zoneVisits = DefaultZoneVisits;
        private static bool _isSafeZone;

        /// <summary>
        /// 当前切片区域 id。设为空串会被忽略——空 zoneId 会让 ZoneSeed 派出一个
        /// 与任何区域都对不上的种子，排查时极难发现。
        /// </summary>
        public static string ZoneId
        {
            get { return _zoneId; }
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _zoneId = value;
                }
            }
        }

        /// <summary>
        /// 当前访问次数（种子的第三个输入）。负数会被夹到 0：
        /// ZoneSeed 对负 visits 没有定义，让它静默产生一个"合法但无意义"的种子
        /// 比直接夹住更糟。
        /// </summary>
        public static int ZoneVisits
        {
            get { return _zoneVisits; }
            set { _zoneVisits = value < 0 ? 0 : value; }
        }

        /// <summary>当前区域是否安全区。zone_youhuang 为 false。</summary>
        public static bool IsSafeZone
        {
            get { return _isSafeZone; }
            set { _isSafeZone = value; }
        }

        /// <summary>是否已完成委托注册。重复注册是幂等的，这个标志只用于日志降噪。</summary>
        private static bool _registered;

        /// <summary>
        /// 标记第一个场景是否已经走完 EnsureWorldAtRuntime 的兜底重建。
        /// 用于区分「第一次场景加载」和「后续 SceneManager.LoadScene 重开」，
        /// 避免 AfterSceneLoad 与 sceneLoaded 事件在同一帧里重复 Build。
        /// </summary>
        private static bool _firstSceneLoaded;

        /// <summary>
        /// <see cref="_firstSceneLoaded"/> 的**只读**观察窗口。
        ///
        /// 【为什么只给 get，不给 set】
        /// 这个标志的唯一合法写入者是 <see cref="EnsureWorldAtRuntime"/>（置位）
        /// 与 <see cref="ResetStatics"/>（复位）。开放 set 等于允许任何人跳过
        /// 兜底重建，那正是本次黑屏 bug 的成因。开 get 是为了让「复位契约」
        /// 能被测试从外部证伪，而不必把字段本身改成 public。
        /// </summary>
        public static bool FirstSceneBootstrapped
        {
            get { return _firstSceneLoaded; }
        }

        /// <summary>
        /// <see cref="_registered"/> 的**只读**观察窗口。理由同
        /// <see cref="FirstSceneBootstrapped"/>：只暴露"读"，不暴露"写"。
        /// </summary>
        public static bool DelegateRegistered
        {
            get { return _registered; }
        }

        /// <summary>
        /// 把本类的可写静态状态复位到出厂值。由 Unity 在每次进入运行时自动调用，
        /// 也可以被测试显式调用来隔离用例之间的污染。
        ///
        /// 【为什么必须有它 —— 不加就是必然黑屏，不是"可能"】
        /// 关闭 Domain Reload 后 static 跨 PlayMode 存活。第 2 次进 PlayMode 时：
        ///   ① <see cref="_firstSceneLoaded"/> 仍是上一局遗留的 true；
        ///   ② <see cref="EnsureWorldAtRuntime"/>（AfterSceneLoad）走到早退分支，
        ///      **跳过** BuildWorldIfNeeded()；
        ///   ③ 而首个场景的 sceneLoaded 事件早在 AfterSceneLoad 之前就发过了，
        ///      <see cref="OnSceneLoaded"/> 不会为首场景补触发；
        ///   ④ 于是世界永远不建 —— 地图和人物都没有，只剩黑背景。
        /// 这与玩家报的"复活 / 按 R 重开后黑屏"是同一个症状的两条成因之一。
        ///
        /// 【为什么是 SubsystemRegistration 而不是 BeforeSceneLoad / AfterSceneLoad】
        /// 与 <c>FeedbackClock.ResetStatics</c>、<c>HitFeedbackConfig.ResetStatics</c>
        /// 取同一档，理由也同一条：SubsystemRegistration 是 RuntimeInitializeLoadType
        /// 里**最早**的一档，早于任何 Awake，也早于 BeforeSceneLoad / AfterSceneLoad。
        /// 放晚了就会出现"已经有人读到脏值"的窗口 —— 而本类的
        /// <see cref="RegisterBeforeSceneLoad"/>(BeforeSceneLoad) 与
        /// <see cref="EnsureWorldAtRuntime"/>(AfterSceneLoad) 恰恰都在那个窗口里，
        /// 复位挂晚一档就等于没挂。
        ///
        /// 【复位 _registered 的副作用是"多打一行日志"，可接受】
        /// _registered 只用于日志降噪（见其注释），复位后每次进 PlayMode 会多打一行
        /// 注册日志。之所以敢复位，是因为 <see cref="Register"/> 本身**真幂等**：
        /// 它对 ShuimoSceneBuilder.BuildScene 用的是赋值（=）而不是订阅（+=），
        /// 无论调多少次，委托调用链长度恒为 1，不会堆叠、不会重复建世界。
        /// 反过来，不复位 _registered 才是错的 —— 那样日志会谎称"已注册过"，
        /// 而实际上 BuildScene 委托在关闭 Domain Reload 时是否仍指向有效目标
        /// 并无保证，排查时会被这行"降噪"直接带偏。
        ///
        /// 【为什么这里不顺手 SceneManager.sceneLoaded -= OnSceneLoaded】
        /// 因为订阅的去重责任已经**完整地**收在 <see cref="EnsureWorldAtRuntime"/>
        /// 的"先减后加"里，那里是全仓唯一的订阅点。在这里再减一次，就让
        /// "谁负责保证只有一个订阅"出现两个答案 —— 与本文件 @「为什么不在这里
        /// 先删旧根节点」是同一条纪律：清理策略只许有一个归属方。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetStatics()
        {
            _firstSceneLoaded = false;
            _registered = false;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器域重载后立即注册。改完代码不用进 Play，菜单就能用。
        /// </summary>
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterInEditor()
        {
            Register();
        }
#endif

        /// <summary>播放前注册。打包后的运行时没有 InitializeOnLoadMethod，靠这条。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterBeforeSceneLoad()
        {
            Register();
        }

        /// <summary>
        /// 把世界生成逻辑挂进 Shuimo 的统一构建入口。幂等，可重复调用。
        ///
        /// 【★ 这里的 "=" 是承重的，绝不能手滑写成 "+="】
        /// ShuimoSceneBuilder.BuildScene 是 Action 委托。用赋值（=）时，
        /// 无论 Register() 被调多少次（编辑器域重载 1 次 + BeforeSceneLoad 1 次
        /// + AfterSceneLoad 1 次 + 测试若干次），调用链长度恒为 1。
        /// 一旦改成 +=，每调一次就多挂一份，之后任意一次 BuildAll() 都会把整个
        /// 世界重建 N 遍 —— 与 sceneLoaded 重复订阅是同一类事故的两个入口。
        /// <see cref="ResetStatics"/> 敢于复位 <see cref="_registered"/>，
        /// 前提正是这条不变量成立。
        /// </summary>
        public static void Register()
        {
            ShuimoSceneBuilder.BuildScene = WorldBuilder.BuildScene;
            if (!_registered)
            {
                _registered = true;
                Debug.Log("[T2] Bootstrap 已注册 WorldBuilder.BuildScene → ShuimoSceneBuilder。");
            }
        }

        /// <summary>
        /// 世界当前是否「活着」：既有生成的根节点，又有仍然有效的静态生成结果。
        ///
        /// 两个条件缺一不可 —— 只看根节点会把域重载后的空壳当成完好世界，
        /// 只看静态状态会在「代码热重载但场景已被手动清空」时重复生成。
        /// </summary>
        public static bool IsWorldLive()
        {
            return WorldBuilder.HasGeneratedWorld() && WorldBuilder.Grid != null;
        }

        /// <summary>
        /// 运行时兜底（Q9）。场景加载完立刻检查世界是否可用，不可用就重建一次。
        ///
        /// 只在 Play 模式触发（RuntimeInitializeOnLoadMethod 本身就只在 Play 跑），
        /// 因此不会与编辑期的 Clean And Rebuild 打架。
        ///
        /// 【为什么还要订阅 sceneLoaded 事件】
        /// AfterSceneLoad 这个回调只在「第一次」场景加载后触发一次；后续按 R 重开
        /// 走的是 SceneManager.LoadScene，不会再触发它。于是新场景里往往只剩下磁盘上
        /// 编辑期残留的旧世界（其运行时 Sprite/Texture 已丢失，所以 Player 看不见）。
        /// 订阅 SceneManager.sceneLoaded 后，每次场景加载后都会再检查一次世界是否活着，
        /// 必要时重建——包括重开。
        ///
        /// 【为什么不在这里先删旧根节点】
        /// WorldBuilder.BuildScene 自己的第一步就是 DestroyGeneratedRoots()。
        /// 在这里再删一遍不但重复，还会让「谁负责清理」这件事出现两个答案——
        /// 将来改清理策略时必然漏掉一处。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureWorldAtRuntime()
        {
            Register();

            // 【★ 幂等订阅：先减后加，一个字都不能删】
            // SceneManager.sceneLoaded 是引擎侧的 static 事件，它的委托调用链
            // **不随场景卸载清空**，关闭 Domain Reload 时更是跨 PlayMode 存活。
            // 只写 += 的话，第 N 次进 PlayMode 就累积 N 个 OnSceneLoaded 订阅，
            // 此后每按一次 R 重开，BuildAll() 就被连续调用 N 次 —— 整个世界被
            // 反复完整重建 N 遍（每遍都 DestroyGeneratedRoots + 重新烘 Sprite），
            // 表现为"重开一次卡好几秒、越调越卡"，且中途状态可能错乱。
            //
            // "-= 之后再 +=" 是 .NET 事件去重的标准手法：Delegate.Remove 对
            // 未订阅的委托是安全的 no-op（不抛异常、不报警），所以首次进入时
            // 这一行等价于什么都没做；而在残留订阅存在时它恰好清掉那一份。
            // 两行合起来的不变量是：调用链里 OnSceneLoaded **恒为且仅为 1 份**。
            //
            // 注意这里必须用同一个静态方法组作为目标才能配对成功 ——
            // 不要图省事改写成 lambda 或本地函数，那样每次生成的都是新委托实例，
            // -= 永远匹配不上，去重会静默失效（这类退化极难在 review 中看出）。
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            // 第一次场景加载：sceneLoaded 事件可能稍后触发，也可能不触发，
            // 所以这里直接兜底重建。后续重开统一走 OnSceneLoaded。
            if (_firstSceneLoaded)
            {
                return;
            }
            _firstSceneLoaded = true;
            BuildWorldIfNeeded();
        }

        /// <summary>
        /// 场景加载后兜底：世界若不可用就重建一次。被 EnsureWorldAtRuntime（首次）
        /// 与 OnSceneLoaded（后续每次重开）共用，确保逻辑只有一份。
        /// </summary>
        private static void BuildWorldIfNeeded()
        {
            if (IsWorldLive())
            {
                return;
            }

            if (WorldBuilder.HasGeneratedWorld())
            {
                // 有壳没芯：编辑期生成的世界在域重载后丢了运行时资源（Sprite/Tile/Texture2D
                // 都不是磁盘资产，无法随场景序列化），BuildScene 会先清掉它们再重建。
                Debug.Log("[T2] 场景加载后兜底：检测到失效的世界根节点（运行时生成的 Sprite/Tile 无法随场景序列化），即将清理并重建。");
            }
            else
            {
                Debug.Log("[T2] 场景加载后兜底：场景中没有已生成的世界，自动生成一次。");
            }

            ShuimoSceneBuilder.BuildAll();
        }

        /// <summary>
        /// 每次场景加载完成后回调。覆盖「按 R 重开」等后续场景重载——
        /// 这些情况下 AfterSceneLoad 不会再触发，必须由本事件兜底。
        ///
        /// 第一次场景加载已由 EnsureWorldAtRuntime 处理，这里用 _firstSceneLoaded
        /// 跳过，避免同一帧里重复 Build。
        /// </summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 第一次场景加载已由 EnsureWorldAtRuntime 处理，避免重复 Build。
            if (!_firstSceneLoaded)
            {
                return;
            }

            // 【按 R 重开 / 暂停菜单"重新开始"修复】
            // 场景已整体重载，旧世界根节点必然失效（LoadScene 会销毁所有场景对象），
            // 必须**无条件完整重建**，不能再走 BuildWorldIfNeeded() 的 IsWorldLive() 判定。
            //
            // 旧逻辑依赖 IsWorldLive() = HasGeneratedWorld() && Grid != null。
            // 在「旧世界根 GameObject 壳还在、但其运行时 Sprite/Tile/Texture 已随域重载丢失」
            // 的边界下，HasGeneratedWorld() 仍可能返回 true 且 Grid 也非 null，从而误判为
            // "世界还活着"并跳过 BuildAll() —— 这就是复活/重开后黑屏（地图与人物不重建、
            // 只剩旧敌人 + 黑背景）的根因。BuildScene 自身第一步就是 DestroyGeneratedRoots()，
            // 幂等自清理，因此无条件重建绝不会堆叠或退化。
            Debug.Log("[T2] 场景重载完成，无条件强制重建世界（修复重开黑屏）。");
            ShuimoSceneBuilder.BuildAll();
        }
    }
}
