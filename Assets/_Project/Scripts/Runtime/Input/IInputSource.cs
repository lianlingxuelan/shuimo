// -----------------------------------------------------------------------------
// IInputSource.cs —— 「一个输入设备」需要满足的契约（接口）
//
// 键盘鼠标、手柄各实现一份这个接口；将来要加触屏、录像回放、AI 托管，
// 也只需再写一个实现类，上层 InputBinder 一行都不用改。
//
// 【接口是什么，为什么这里要用它】
// 接口 = 一份"能力清单"，只规定"必须能做到什么"，不规定"怎么做到"。
// KeyboardMouseInputSource 用 Input.GetKey 实现 IsHeld，
// GamepadInputSource 用手柄轴向实现 IsHeld，两者做法完全不同，
// 但因为都满足同一份清单，InputBinder 可以把它们塞进同一个 List<IInputSource> 里一视同仁地遍历。
// 这就是"多态"：同一句调用，运行时按对象实际类型分派到不同实现。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>一个输入设备（键鼠 / 手柄 / 未来的触屏等）必须提供的能力。</summary>
    public interface IInputSource
    {
        /// <summary>方案名，用于 HUD 显示当前在用哪套操作（"KeyboardMouse" / "Gamepad"）。</summary>
        string SchemeName { get; }

        /// <summary>设备当前是否接入。手柄拔掉后为 false，键盘恒为 true。</summary>
        bool IsPresent { get; }

        /// <summary>这一帧该设备上是否有任何输入活动。用于自动切换当前操作方案。</summary>
        bool HasActivity { get; }

        /// <summary>最近一次 Poll 采到的输入快照。</summary>
        InputSnapshot Snapshot { get; }

        /// <summary>
        /// 采样一次输入，把结果写进 Snapshot。
        ///
        /// 【为什么要把"采样"单独做成一个方法，而不是每次读取时现查】
        /// 一帧之内，AttackController、DodgeController、SkillController 都会来问"攻击键按了吗"。
        /// 如果每次询问都现场去查 Unity 的输入 API，一是重复开销，二是更危险：
        /// "刚按下"这种边沿状态一旦被读取多次，就可能出现有的控制器读到 true、
        /// 有的读到 false 的撕裂现象。先统一采样成快照、当帧所有人都读同一份快照，
        /// 这叫"每帧一次采样"，能从根上杜绝这类时序 bug。
        /// </summary>
        /// <param name="profile">按键绑定表，决定物理按键如何翻译成 GameAction。</param>
        void Poll(InputBindingProfile profile);

        /// <summary>读取移动向量。</summary>
        Vector2 ReadMove();

        /// <summary>该动作是否正被按住。</summary>
        bool IsHeld(GameAction action);

        /// <summary>该动作是否本帧刚按下。</summary>
        bool WasPressed(GameAction action);

        /// <summary>
        /// 清空内部状态。
        ///
        /// 【什么时候需要它】
        /// 组件被禁用、场景切换、手柄被拔出时。如果不清空，会残留"某键正被按住"的旧状态，
        /// 等组件重新启用时角色会莫名其妙地自己往一个方向跑 —— 俗称"输入粘键"。
        /// </summary>
        void ResetState();
    }
}
