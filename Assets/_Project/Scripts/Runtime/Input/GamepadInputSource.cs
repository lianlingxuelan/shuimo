// -----------------------------------------------------------------------------
// GamepadInputSource.cs —— 手柄输入源（IInputSource 的手柄实现）
//
// 与键鼠源的最大区别：手柄是可以随时插拔的，所以本类要额外处理"设备在不在"的问题。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>手柄输入源。支持热插拔：运行中插上手柄，最多 1 秒后自动被识别。</summary>
    public sealed class GamepadInputSource : IInputSource
    {
        /// <summary>
        /// 手柄探测间隔（秒）。
        ///
        /// 【为什么是"每秒探一次"而不是每帧探】
        /// GetJoystickNames() 每次调用都会向操作系统查询设备列表并分配一个新的字符串数组，
        /// 是个不便宜的操作。每帧调用 = 每秒 60 次系统查询 + 60 次数组分配喂给 GC。
        /// 而玩家插拔手柄这件事，晚 1 秒被发现完全无感。
        /// 用"降低采样频率"换性能，是处理这类低频事件的标准做法。
        /// </summary>
        public const float ProbeIntervalSeconds = 1.0f;

        // Unity 的 KeyCode 里手柄按钮只到 JoystickButton19，越界会取到别的枚举值。
        private const int MaxJoystickButton = 19;

        private InputSnapshot _snapshot;
        private int _prevHeldMask;
        private bool _present;

        // 下次允许探测的时间点。初值为负无穷 ⇒ 第一次 Poll 必定立刻探一次，不用等 1 秒。
        private float _nextProbeTime = float.NegativeInfinity;

        private string _deviceName = string.Empty;

        public string SchemeName
        {
            get { return "Gamepad"; }
        }

        public bool IsPresent
        {
            get { return _present; }
        }

        public bool HasActivity
        {
            get { return _present && (_snapshot.HeldMask != 0 || _snapshot.Move.sqrMagnitude > 0.0f); }
        }

        public InputSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        public string DeviceName
        {
            get { return _deviceName; }
        }

        public void Poll(InputBindingProfile profile)
        {
            Probe();

            _prevHeldMask = _snapshot.HeldMask;
            _snapshot.Clear();

            if (!_present || profile == null)
            {
                _prevHeldMask = 0;
                return;
            }

            for (int i = 0; i < InputSnapshot.ActionCount; i++)
            {
                GameAction action = (GameAction)i;
                bool held = AnyButton(profile.PadButtonsFor(action));
                bool edge = held && (_prevHeldMask & InputSnapshot.Bit(action)) == 0;
                _snapshot.SetHeld(action, held, edge);
            }

            _snapshot.Move = ReadStick(profile.StickDeadzone);
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
            _nextProbeTime = float.NegativeInfinity;
        }

        public void ForceProbe()
        {
            _nextProbeTime = float.NegativeInfinity;
            Probe();
        }

        /// <summary>
        /// 探测手柄是否接入，按 ProbeIntervalSeconds 限频。
        ///
        /// 【为什么用 Time.unscaledTime 而不是 Time.time】
        /// Time.time 会被 Time.timeScale 缩放。游戏暂停时通常把 timeScale 设成 0，
        /// 此时 Time.time 完全停止增长 —— 于是 now &lt; _nextProbeTime 永远成立，
        /// 探测被冻结，玩家在暂停菜单里插上手柄将永远不被识别。
        /// unscaledTime 是不受缩放影响的真实时间，凡是"UI 层面、与游戏速度无关"的计时
        /// （输入探测、界面动画、暂停菜单）都应该用它。
        /// </summary>
        private void Probe()
        {
            float now = Time.unscaledTime;
            if (now < _nextProbeTime)
            {
                return;
            }
            _nextProbeTime = now + ProbeIntervalSeconds;

            string[] names = UnityEngine.Input.GetJoystickNames();
            // 先悲观地置为"没有手柄"，找到了再置回 true。
            // 这样写的好处：手柄被拔掉后，这两个字段会自动回到正确状态，不需要额外的清理分支。
            _present = false;
            _deviceName = string.Empty;
            if (names == null)
            {
                return;
            }
            for (int i = 0; i < names.Length; i++)
            {
                // 关键：必须跳过空字符串。Unity 返回的数组长度是固定的（通常 16 个槽位），
                // 没插设备的槽位是 ""。只看 names.Length > 0 就判定"有手柄"是个经典错误 ——
                // 那样在完全没接手柄的机器上也会返回 true。
                if (!string.IsNullOrEmpty(names[i]))
                {
                    _present = true;
                    _deviceName = names[i];
                    return;     // 只认第一个有效设备，本工程是单人游戏，不做多手柄
                }
            }
        }

        private static Vector2 ReadStick(float deadzone)
        {
            float x = 0.0f;
            float y = 0.0f;
            try
            {
                x = UnityEngine.Input.GetAxisRaw(KeyboardMouseInputSource.AxisHorizontal);
                y = UnityEngine.Input.GetAxisRaw(KeyboardMouseInputSource.AxisVertical);
            }
            catch (System.ArgumentException)
            {
                return Vector2.zero;
            }

            Vector2 raw = new Vector2(x, y);
            float mag = raw.magnitude;

            // 【死区（deadzone）是什么，为什么必不可少】
            // 摇杆是模拟量。用久了的手柄即使松手回正，也常常输出 (0.03, -0.05) 这类微小残值。
            // 不做处理的话，角色会自己缓慢地往一个方向漂移 —— 玩家会以为游戏出了 bug。
            // 死区的做法：向量长度小于阈值时直接当作零。
            //
            // 注意这里用 magnitude（真实长度）而不是上面见过的 sqrMagnitude：
            // 因为要和 deadzone 这个"以长度为单位"的配置值比较，必须开方换算到同一量纲。
            // 之前用 sqrMagnitude 的地方都只是和 0 比较，才可以省掉开方。
            //
            // 另外要按"向量长度"判定，而不是分别判断 x 和 y。若逐轴判断，
            // 死区区域会是一个正方形，斜向推杆时的手感会和正向不一致；按长度判定得到的是圆形死区。
            if (mag <= deadzone)
            {
                return Vector2.zero;
            }
            return raw;
        }

        /// <summary>
        /// 一组手柄按钮里有任意一个被按住即返回 true。
        /// </summary>
        private static bool AnyButton(int[] buttons)
        {
            if (buttons == null)
            {
                return false;
            }
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = buttons[i];
                // 越界保护：配置表可能被手改坏。KeyCode.JoystickButton0 + 越界值
                // 会滑到枚举里别的按键上（比如键盘键），造成"按了键盘却触发手柄动作"的诡异现象。
                // 与其相信配置，不如在这里挡住。
                if (index < 0 || index > MaxJoystickButton)
                {
                    continue;
                }
                // KeyCode 里 JoystickButton0..19 是连续排列的，所以可以直接做加法定位到第 index 个按钮。
                if (UnityEngine.Input.GetKey(KeyCode.JoystickButton0 + index))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
