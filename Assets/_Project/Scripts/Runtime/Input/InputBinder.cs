// -----------------------------------------------------------------------------
// InputBinder.cs —— 输入系统的总调度：管理所有输入源、合并快照、自动切换操作方案
//
// 它是整个输入层唯一对外的窗口。上层控制器（攻击/闪避/技能）都只跟它打交道，
// 通过静态方法 InputBinder.Pressed(GameAction.Attack) 这样的写法取输入。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 输入总线：每帧采样键盘和手柄，合并成一份快照供全场景使用。
    ///
    /// 【[DisallowMultipleComponent] 是干嘛的】
    /// 禁止在同一个 GameObject 上挂两份本组件。挂两份会导致同一帧被采样两次，
    /// "刚按下"的边沿状态被第一份吃掉，第二份读到的永远是 false —— 极难排查。
    /// 用这个特性让 Unity 编辑器在挂载时就直接拒绝，把运行期玄学 bug 提前到编辑期报错。
    ///
    /// 【[DefaultExecutionOrder(-300)] 是干嘛的】
    /// 指定本组件的 Update 早于其它组件执行（数字越小越早，默认是 0）。
    /// 必须早，因为所有控制器都要读它这一帧采好的输入；如果它比控制器晚跑，
    /// 控制器读到的就是上一帧的旧数据，操作会有整整一帧的延迟。
    /// 这是"数据生产者必须先于消费者执行"的典型安排。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-300)]
    public sealed class InputBinder : MonoBehaviour
    {
        /// <summary>键鼠方案名。</summary>
        public const string SchemeKeyboardMouse = "KeyboardMouse";

        /// <summary>手柄方案名。</summary>
        public const string SchemeGamepad = "Gamepad";

        // [SerializeField] 让私有字段也能显示在 Unity 检视面板里供策划调整，
        // 同时保持 private —— 既可配置，又不让别的脚本随便改，这比直接写 public 更严谨。
        [SerializeField] private InputBindingProfile profile = new InputBindingProfile();
        [SerializeField] private bool enableGamepad = true;

        private readonly List<IInputSource> _sources = new List<IInputSource>(2);
        private KeyboardMouseInputSource _keyboard;
        private GamepadInputSource _gamepad;

        // 本帧合并后的最终快照 —— 所有对外查询都从它读。
        private InputSnapshot _merged;
        private Vector2 _move;
        private string _activeScheme = SchemeKeyboardMouse;

        // 上次采样发生在第几帧。这是"每帧只采样一次"的守门员，见 Poll。
        private int _polledFrame = -1;

        // 场景里当前这一个实例（单例）。允许为 null —— 没有它也能工作，见文件下方"静态门面"。
        private static InputBinder _instance;

        // 没有 InputBinder 组件时使用的退路对象，惰性创建。
        private static KeyboardMouseInputSource _fallbackKeyboard;
        private static InputBindingProfile _fallbackProfile;
        private static int _fallbackFrame = -1;

        /// <summary>当前实例，可能为 null（场景没挂本组件时）。调用方必须判空。</summary>
        public static InputBinder I
        {
            get { return _instance; }
        }

        public InputBindingProfile Profile
        {
            get { return profile; }
        }

        public Vector2 Move
        {
            get { return _move; }
        }

        public string ActiveScheme
        {
            get { return _activeScheme; }
        }

        public bool GamepadPresent
        {
            get { return _gamepad != null && _gamepad.IsPresent; }
        }

        public string GamepadName
        {
            get { return _gamepad != null ? _gamepad.DeviceName : string.Empty; }
        }

        public IList<IInputSource> Sources
        {
            get { return _sources; }
        }

        private void Awake()
        {
            if (profile == null)
            {
                profile = new InputBindingProfile();
            }
            profile.EnsureDefaults();

            _keyboard = new KeyboardMouseInputSource();
            _gamepad = new GamepadInputSource();
            _sources.Clear();
            _sources.Add(_keyboard);
            _sources.Add(_gamepad);

            if (_instance == null)
            {
                _instance = this;
            }
        }

        private void OnEnable()
        {
            _polledFrame = -1;
            if (_keyboard != null)
            {
                _keyboard.ResetState();
            }
            if (_gamepad != null)
            {
                _gamepad.ResetState();
            }
            if (_instance == null)
            {
                _instance = this;
            }
        }

        private void OnDisable()
        {
            _merged.Clear();
            _move = Vector2.zero;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void Update()
        {
            Poll();
        }

        /// <summary>
        /// 采样并合并本帧输入。可以被安全地重复调用。
        ///
        /// 【为什么开头要用 _polledFrame 做"本帧已采过"的判断（幂等保护）】
        /// 本方法有两类调用者：一是自己的 Update，二是 IsHeld/WasPressed 这些查询方法
        /// （它们进门先 Poll 一次，保证即使执行顺序意外颠倒也能拿到当帧数据）。
        /// 于是一帧内 Poll 可能被调用五六次。若每次都真的重新采样，
        /// "刚按下"的边沿会被第一次采样消费掉，后面几次全读到 false ——
        /// 表现就是"攻击键时灵时不灵"。加上帧号判断后，一帧内无论调多少次都只真采一次，
        /// 后续调用直接返回，所有查询者共享同一份快照。
        /// 这种"重复调用与调用一次效果相同"的性质叫幂等，是消除时序 bug 的利器。
        /// </summary>
        public void Poll()
        {
            int frame = Time.frameCount;
            if (_polledFrame == frame)
            {
                return;     // 本帧已经采过了，直接复用现成的 _merged
            }
            _polledFrame = frame;

            if (profile == null)
            {
                profile = InputBindingProfile.CreateDefault();
            }
            if (_keyboard == null)
            {
                _keyboard = new KeyboardMouseInputSource();
            }
            if (_gamepad == null)
            {
                _gamepad = new GamepadInputSource();
            }

            _keyboard.Poll(profile);
            if (enableGamepad)
            {
                _gamepad.Poll(profile);
            }
            else
            {
                _gamepad.ResetState();
            }

            // 按键：直接按位或合并 —— 任意设备按下即视为按下，键鼠手柄可以混着用。
            _merged.Clear();
            _merged.MergeButtons(_keyboard.Snapshot);
            if (enableGamepad && _gamepad.IsPresent)
            {
                _merged.MergeButtons(_gamepad.Snapshot);
            }

            // 移动：不能像按键那样"或"起来，必须二选一（理由见 InputSnapshot.MergeButtons 注释：
            // 键盘给 (1,0)、摇杆给 (-1,0)，相加会互相抵消成原地不动）。
            // 这里采用"键盘优先"的仲裁策略：
            bool keyboardMove = _keyboard.Snapshot.MoveKeysHeld;
            _merged.MoveKeysHeld = keyboardMove;

            if (keyboardMove || !enableGamepad || !_gamepad.IsPresent)
            {
                // 只要玩家在按 WASD，就以键盘为准 —— 手明确放在键盘上时，
                // 一个没回正、有微小漂移的摇杆不该来抢方向（廉价手柄非常常见）。
                _move = _keyboard.Snapshot.Move;
            }
            else
            {
                // 键盘没在动，才轮到手柄。手柄也没推（摇杆回正）时回落到键盘值（此时其实是零向量），
                // 这样写比再嵌一层 if 更简洁，且结果一致。
                Vector2 pad = _gamepad.Snapshot.Move;
                _move = pad.sqrMagnitude > 0.0f ? pad : _keyboard.Snapshot.Move;
            }
            _merged.Move = _move;

            UpdateActiveScheme(keyboardMove);
        }

        public Vector2 ReadMove()
        {
            Poll();
            return _move;
        }

        public bool IsHeld(GameAction action)
        {
            Poll();
            return _merged.IsHeld(action);
        }

        public bool WasPressed(GameAction action)
        {
            Poll();
            return _merged.WasPressed(action);
        }

        public string HintFor(GameAction action)
        {
            return profile != null ? profile.HintFor(action) : string.Empty;
        }

        public void SetGamepadLayout(GamepadLayout layout)
        {
            if (profile == null)
            {
                profile = InputBindingProfile.CreateDefault();
            }
            profile.ApplyLayout(layout);
            if (_gamepad != null)
            {
                _gamepad.ResetState();
            }
        }

        /// <summary>
        /// 决定当前"活跃操作方案"，HUD 据此显示对应的按键提示图标。
        ///
        /// 【为什么只在检测到活动时才切换，而不是每帧重算】
        /// 注意本方法所有分支要么赋值要么直接 return，唯独"两个设备都没动"时
        /// 什么也不做 —— 保持上一次的方案。这是刻意的：玩家松开手的间隙里，
        /// 若把方案重置成默认值，HUD 提示会在"手柄图标"和"键盘图标"之间反复横跳。
        /// 记住最后一次有效输入，是所有支持多设备的游戏都在用的做法。
        /// </summary>
        private void UpdateActiveScheme(bool keyboardMove)
        {
            bool keyboardActive = _keyboard.Snapshot.HeldMask != 0 || keyboardMove;
            if (keyboardActive)
            {
                _activeScheme = SchemeKeyboardMouse;
                return;
            }

            if (!enableGamepad || !_gamepad.IsPresent)
            {
                _activeScheme = SchemeKeyboardMouse;
                return;
            }

            bool padActive = _gamepad.Snapshot.HeldMask != 0
                             || (!keyboardMove && _gamepad.Snapshot.Move.sqrMagnitude > 0.0f);
            if (padActive)
            {
                _activeScheme = SchemeGamepad;
            }
        }

        // ---------------------------------------------------------------------
        // 静态门面：场景里没有 InputBinder 组件时退回 T2 原路径（键鼠直读）
        //
        // 【这一整段体现了本工程的一条核心设计原则："空组件 = 原路径"】
        // 下面每个静态方法都是同一个套路：
        //     实例存在且启用  → 走完整的新路径（多设备合并、可重绑定）
        //     实例为 null     → 退回一个内置的键鼠退路，照样能玩
        // 为什么值得这么写？因为它让新系统的接入变成"可选"而非"必须"：
        //   · 老场景没挂 InputBinder，不会 NullReferenceException 崩溃，行为退化成原来的 T2 键鼠直读；
        //   · 想启用新输入系统，往场景里拖一个组件即可，一行调用代码都不用改；
        //   · 想临时排查"是不是新输入系统的锅"，把组件禁用掉就立刻回到旧行为，二分定位极快。
        // 这比"必须先初始化否则报错"的写法友好得多，代价只是每个入口多一个判空分支。
        // 同样的思路在本工程随处可见（CombatBridge 的各控制器为 null 时也走原逻辑）。
        // ---------------------------------------------------------------------

        /// <summary>读取移动轴。无实例时自动退回键鼠直读。</summary>
        public static Vector2 MoveAxis()
        {
            if (_instance != null && _instance.isActiveAndEnabled)
            {
                return _instance.ReadMove();
            }
            PollFallback();
            return _fallbackKeyboard.ReadMove();
        }

        public static bool Held(GameAction action)
        {
            if (_instance != null && _instance.isActiveAndEnabled)
            {
                return _instance.IsHeld(action);
            }
            PollFallback();
            return _fallbackKeyboard.IsHeld(action);
        }

        public static bool Pressed(GameAction action)
        {
            if (_instance != null && _instance.isActiveAndEnabled)
            {
                return _instance.WasPressed(action);
            }
            PollFallback();
            return _fallbackKeyboard.WasPressed(action);
        }

        public static string Scheme
        {
            get
            {
                return _instance != null && _instance.isActiveAndEnabled
                    ? _instance.ActiveScheme
                    : SchemeKeyboardMouse;
            }
        }

        public static string Hint(GameAction action)
        {
            if (_instance != null && _instance.Profile != null)
            {
                return _instance.Profile.HintFor(action);
            }
            EnsureFallbackProfile();
            return _fallbackProfile.HintFor(action);
        }

        private static void EnsureFallbackProfile()
        {
            if (_fallbackProfile == null)
            {
                _fallbackProfile = InputBindingProfile.CreateDefault();
            }
            if (_fallbackKeyboard == null)
            {
                _fallbackKeyboard = new KeyboardMouseInputSource();
            }
        }

        private static void PollFallback()
        {
            EnsureFallbackProfile();
            int frame = Time.frameCount;
            if (_fallbackFrame == frame)
            {
                return;
            }
            _fallbackFrame = frame;
            _fallbackKeyboard.Poll(_fallbackProfile);
        }
    }
}
