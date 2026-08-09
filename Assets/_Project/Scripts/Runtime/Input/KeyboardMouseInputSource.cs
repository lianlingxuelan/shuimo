// -----------------------------------------------------------------------------
// KeyboardMouseInputSource.cs —— 键盘鼠标输入源（IInputSource 的键鼠实现）
//
// 职责：把物理按键（KeyCode.J、KeyCode.W……）翻译成抽象动作（GameAction.Attack……）。
// 翻译规则来自 InputBindingProfile，所以支持玩家自定义键位。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>键鼠输入源。键盘永远视为"已接入"，所以它同时充当所有场景的保底输入方式。</summary>
    public sealed class KeyboardMouseInputSource : IInputSource
    {
        /// <summary>Unity 传统输入管理器里的水平轴名称。</summary>
        public const string AxisHorizontal = "Horizontal";

        /// <summary>Unity 传统输入管理器里的垂直轴名称。</summary>
        public const string AxisVertical = "Vertical";

        private InputSnapshot _snapshot;

        // 上一帧的"按住"掩码。存它是为了算出"刚按下"的边沿：
        // 本帧按住 && 上帧没按住 == 刚按下。见 Poll 里的 edge 计算。
        private int _prevHeldMask;

        // Input Manager 里是否存在 Horizontal/Vertical 轴配置。见 ReadAxes 的降级逻辑。
        private bool _axisAvailable = true;

        public string SchemeName
        {
            get { return "KeyboardMouse"; }
        }

        public bool IsPresent
        {
            get { return true; }
        }

        public bool HasActivity
        {
            get { return _snapshot.HeldMask != 0 || _snapshot.MoveKeysHeld; }
        }

        public InputSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        public bool AxisAvailable
        {
            get { return _axisAvailable; }
        }

        /// <summary>
        /// 采样一帧键盘状态。
        ///
        /// 【顺序很关键：必须先把当前掩码存进 _prevHeldMask，再 Clear】
        /// 边沿检测靠的就是"这一帧"和"上一帧"的对比。如果顺序写反（先 Clear 再存），
        /// _prevHeldMask 永远是 0，于是每一帧都会被判定为"刚按下"，
        /// 玩家按住攻击键就会每帧触发一次攻击。这两行的先后不能调换。
        /// </summary>
        public void Poll(InputBindingProfile profile)
        {
            _prevHeldMask = _snapshot.HeldMask;
            _snapshot.Clear();

            if (profile == null)
            {
                // 没有绑定表就什么都读不了。顺手把 _prevHeldMask 也清零，
                // 否则等 profile 恢复后，上一帧的陈旧掩码会让边沿判断错位一帧。
                _prevHeldMask = 0;
                return;
            }

            bool up = AnyKey(profile.MoveUpKeys);
            bool down = AnyKey(profile.MoveDownKeys);
            bool left = AnyKey(profile.MoveLeftKeys);
            bool right = AnyKey(profile.MoveRightKeys);
            _snapshot.MoveKeysHeld = up || down || left || right;
            _snapshot.Move = ReadAxes(up, down, left, right);

            // 遍历所有抽象动作，逐个查询它绑定的物理键。
            // 这里把枚举值当下标用（i → (GameAction)i），正是 GameAction 必须连续编号的原因。
            for (int i = 0; i < InputSnapshot.ActionCount; i++)
            {
                GameAction action = (GameAction)i;
                bool held = AnyKey(profile.KeysFor(action));
                // 边沿 = 现在按着 且 上一帧没按着。这就是"刚按下"的完整定义。
                bool edge = held && (_prevHeldMask & InputSnapshot.Bit(action)) == 0;
                _snapshot.SetHeld(action, held, edge);
            }
        }

        public Vector2 ReadMove()
        {
            return _snapshot.Move;
        }

        public bool IsHeld(GameAction action)
        {
            return _snapshot.IsHeld(action);
        }

        public bool WasPressed(GameAction action)
        {
            return _snapshot.WasPressed(action);
        }

        public void ResetState()
        {
            _snapshot.Clear();
            _prevHeldMask = 0;
        }

        /// <summary>
        /// 读取移动向量，优先用 Unity 输入管理器的轴，失败则退回手工合成。
        ///
        /// 【为什么要 try/catch，以及为什么用 _axisAvailable 记住失败结果】
        /// GetAxisRaw 在项目的 Input Manager 里没有配置对应轴名时会抛 ArgumentException。
        /// 本工程的场景可以由代码动态搭建（见 WorldBuilder），不保证一定带着标准输入配置，
        /// 所以这里不假定轴一定存在，抓住异常后改用四个方向键手工合成向量 —— 玩家照样能动。
        ///
        /// 关键是 _axisAvailable 这个"记住失败"的标志位：异常在 .NET 里开销很大
        /// （要抓调用栈），若每帧都抛一次，帧率会肉眼可见地掉。用一个 bool 记下
        /// "这个项目就是没配轴"，之后所有帧直接走手工分支，异常总共只抛一次。
        /// 这个模式可以叫"一次性探测 + 永久降级"，处理"环境能力未知"时很好用。
        /// </summary>
        private Vector2 ReadAxes(bool up, bool down, bool left, bool right)
        {
            if (_axisAvailable)
            {
                try
                {
                    // GetAxisRaw 而不是 GetAxis：Raw 版不做平滑，按下立刻是 ±1。
                    // 动作游戏要的就是这种"跟手"，GetAxis 的渐变会让操作发黏。
                    return new Vector2(
                        UnityEngine.Input.GetAxisRaw(AxisHorizontal),
                        UnityEngine.Input.GetAxisRaw(AxisVertical));
                }
                catch (System.ArgumentException)
                {
                    _axisAvailable = false;     // 记住失败，后续帧不再重试
                }
            }

            // 降级路径：用"右减左、上减下"手工合成。
            // 同时按左右会得到 1-1=0（原地不动），这正是我们想要的行为。
            float x = (right ? 1.0f : 0.0f) - (left ? 1.0f : 0.0f);
            float y = (up ? 1.0f : 0.0f) - (down ? 1.0f : 0.0f);
            return new Vector2(x, y);
        }

        /// <summary>
        /// 一组键里只要有任意一个被按住就返回 true。
        /// 这就是"一个动作可以绑多个键"（比如 W 和 ↑ 都能向上走）的实现。
        /// </summary>
        private static bool AnyKey(KeyCode[] keys)
        {
            if (keys == null)
            {
                return false;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                // 跳过 KeyCode.None：绑定表里的空槽位用 None 占位，
                // 直接拿去查询虽然不会崩，但语义上是"没绑键"，应当视为未按下。
                if (keys[i] != KeyCode.None && UnityEngine.Input.GetKey(keys[i]))
                {
                    return true;   // 提前返回，不必检查剩下的键
                }
            }
            return false;
        }
    }
}
