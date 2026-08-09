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
            BuildWorldIfNeeded();
        }
    }
}
