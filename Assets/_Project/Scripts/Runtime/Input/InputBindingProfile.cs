// -----------------------------------------------------------------------------
// InputBindingProfile.cs —— 按键绑定表：抽象动作 ←→ 物理按键 的对照表
//
// 这是输入系统里唯一存放"具体键位"的地方。想改键位、想支持不同品牌手柄，
// 都只动这个文件的数据，不用碰任何逻辑代码 —— 这就是"数据驱动"的价值。
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 手柄按键布局。
    ///
    /// 【为什么需要区分布局】
    /// 手柄按钮在系统层只是编号 0/1/2/3，不同厂商把这些编号印在不同的物理键上：
    /// Xbox 手柄按钮 0 是 A（下方），PlayStation 手柄按钮 0 却是 ×（也在下方但布局序不同）。
    /// 直接写死编号，会导致 PS 手柄玩家的"攻击"跑到了错误的键上。
    /// 所以把编号按布局分组，见 ApplyLayout。
    /// </summary>
    public enum GamepadLayout
    {
        XInput = 0,
        DualShock = 1,
        Generic = 2
    }

    /// <summary>
    /// 一套完整的输入绑定配置。可在 Unity 检视面板里编辑，也可代码生成默认值。
    ///
    /// 【[Serializable] 是干嘛的】
    /// 告诉 Unity："这个普通 C# 类可以被序列化"。加了它，本类作为字段出现在
    /// MonoBehaviour 里时（见 InputBinder.profile）才能显示在检视面板中、
    /// 才能随场景一起保存。不加的话面板上根本看不到这些键位配置。
    /// </summary>
    [Serializable]
    public sealed class InputBindingProfile
    {
        /// <summary>动作总数，直接复用 InputSnapshot 的定义以保证两处永远一致。</summary>
        public const int ActionCount = InputSnapshot.ActionCount;

        // 【为什么要预备两个静态空数组】
        // 下面大量 getter 在字段为 null 时返回它们，而不是返回 null。
        // 好处是调用方永远能安全地写 foreach / .Length，不必到处判空 —— 这叫"空对象模式"。
        // 做成 static readonly 是为了全局只分配这一份：如果每次都 new KeyCode[0]，
        // 每帧每动作都会产生一个短命垃圾对象，白白增加 GC 压力。
        private static readonly KeyCode[] NoKeys = new KeyCode[0];
        private static readonly int[] NoButtons = new int[0];

        [Header("键鼠")]
        [SerializeField] private KeyCode[] attackKeys = { KeyCode.Mouse0, KeyCode.J };
        [SerializeField] private KeyCode[] skill1Keys = { KeyCode.K, KeyCode.Mouse1 };
        [SerializeField] private KeyCode[] skill2Keys = { KeyCode.L };
        [SerializeField] private KeyCode[] dodgeKeys = { KeyCode.LeftShift, KeyCode.RightShift, KeyCode.Space };
        [SerializeField] private KeyCode[] lockOnKeys = { KeyCode.Tab };
        [SerializeField] private KeyCode[] toggleDebugKeys = { KeyCode.F1 };

        [Header("移动键（仅用于输入方案判定与轴不可用时的降级）")]
        [SerializeField] private KeyCode[] moveUpKeys = { KeyCode.W, KeyCode.UpArrow };
        [SerializeField] private KeyCode[] moveDownKeys = { KeyCode.S, KeyCode.DownArrow };
        [SerializeField] private KeyCode[] moveLeftKeys = { KeyCode.A, KeyCode.LeftArrow };
        [SerializeField] private KeyCode[] moveRightKeys = { KeyCode.D, KeyCode.RightArrow };

        [Header("手柄")]
        [SerializeField] private GamepadLayout layout = GamepadLayout.XInput;
        [SerializeField] private int[] attackPad = { 0 };
        [SerializeField] private int[] skill1Pad = { 2 };
        [SerializeField] private int[] skill2Pad = { 3 };
        [SerializeField] private int[] dodgePad = { 1 };
        [SerializeField] private int[] lockOnPad = { 9, 4 };
        [SerializeField] private int[] toggleDebugPad = { 6 };
        [SerializeField] private float stickDeadzone = 0.25f;

        /// <summary>当前手柄布局。</summary>
        public GamepadLayout Layout
        {
            get { return layout; }
        }

        /// <summary>
        /// 摇杆死区。
        /// 注意 getter 里带了兜底：配置为 0 或负数时强制回到 0.25 —— 死区为 0 等于没有死区，
        /// 老手柄会漂移；负数更是没有意义。在读取处兜底比信任配置更稳妥。
        /// </summary>
        public float StickDeadzone
        {
            get { return stickDeadzone > 0.0f ? stickDeadzone : 0.25f; }
        }

        public KeyCode[] MoveUpKeys
        {
            get { return moveUpKeys != null ? moveUpKeys : NoKeys; }
        }

        public KeyCode[] MoveDownKeys
        {
            get { return moveDownKeys != null ? moveDownKeys : NoKeys; }
        }

        public KeyCode[] MoveLeftKeys
        {
            get { return moveLeftKeys != null ? moveLeftKeys : NoKeys; }
        }

        public KeyCode[] MoveRightKeys
        {
            get { return moveRightKeys != null ? moveRightKeys : NoKeys; }
        }

        /// <summary>
        /// 查某个抽象动作绑定了哪些键盘/鼠标键。
        ///
        /// 【为什么是一堆独立字段 + switch，而不是一个 KeyCode[][] 数组】
        /// 数组写法代码短得多，但 Unity 的序列化系统不支持"数组的数组"（锯齿数组），
        /// 那样写的话检视面板里什么都编辑不了，策划就没法调键位了。
        /// 这是被引擎能力约束的一处妥协：牺牲代码简洁度，换取可视化编辑能力。
        /// 遇到"这代码怎么这么啰嗦"的地方，先想想是不是有这类外部约束。
        /// </summary>
        public KeyCode[] KeysFor(GameAction action)
        {
            switch (action)
            {
                case GameAction.Attack: return attackKeys != null ? attackKeys : NoKeys;
                case GameAction.Skill1: return skill1Keys != null ? skill1Keys : NoKeys;
                case GameAction.Skill2: return skill2Keys != null ? skill2Keys : NoKeys;
                case GameAction.Dodge: return dodgeKeys != null ? dodgeKeys : NoKeys;
                case GameAction.LockOn: return lockOnKeys != null ? lockOnKeys : NoKeys;
                case GameAction.ToggleDebug: return toggleDebugKeys != null ? toggleDebugKeys : NoKeys;
                default: return NoKeys;
            }
        }

        /// <summary>查某个抽象动作绑定了哪些手柄按钮编号。</summary>
        public int[] PadButtonsFor(GameAction action)
        {
            switch (action)
            {
                case GameAction.Attack: return attackPad != null ? attackPad : NoButtons;
                case GameAction.Skill1: return skill1Pad != null ? skill1Pad : NoButtons;
                case GameAction.Skill2: return skill2Pad != null ? skill2Pad : NoButtons;
                case GameAction.Dodge: return dodgePad != null ? dodgePad : NoButtons;
                case GameAction.LockOn: return lockOnPad != null ? lockOnPad : NoButtons;
                case GameAction.ToggleDebug: return toggleDebugPad != null ? toggleDebugPad : NoButtons;
                default: return NoButtons;
            }
        }

        /// <summary>
        /// 取该动作在 HUD 上显示的按键提示文字。
        /// 一个动作可能绑了多个键，这里只取第一个作为"主键"显示 ——
        /// HUD 空间有限，显示 "LMB / J" 会挤爆技能图标。约定第一个即主键。
        /// </summary>
        public string HintFor(GameAction action)
        {
            KeyCode[] keys = KeysFor(action);
            if (keys.Length == 0)
            {
                return string.Empty;
            }
            return ShortName(keys[0]);
        }

        /// <summary>
        /// 按目标布局重置全部手柄按钮映射。
        ///
        /// 【注意 switch 里 Generic 和 XInput 共用一个分支】
        /// 这不是漏写 break。C# 允许多个 case 标签落到同一段代码上（只要中间没有语句）。
        /// 含义是"未知品牌的通用手柄按 XInput 处理" —— 因为 XInput 是 PC 上事实标准，
        /// 绝大多数第三方手柄出厂就模拟 XInput。选它做默认值命中率最高。
        /// </summary>
        public void ApplyLayout(GamepadLayout target)
        {
            layout = target;
            switch (target)
            {
                case GamepadLayout.DualShock:
                    attackPad = new[] { 1 };
                    skill1Pad = new[] { 0 };
                    skill2Pad = new[] { 3 };
                    dodgePad = new[] { 2 };
                    lockOnPad = new[] { 11, 4 };
                    toggleDebugPad = new[] { 8 };
                    break;
                case GamepadLayout.Generic:
                case GamepadLayout.XInput:
                default:
                    attackPad = new[] { 0 };
                    skill1Pad = new[] { 2 };
                    skill2Pad = new[] { 3 };
                    dodgePad = new[] { 1 };
                    lockOnPad = new[] { 9, 4 };
                    toggleDebugPad = new[] { 6 };
                    break;
            }
        }

        /// <summary>
        /// 补齐所有空缺的绑定，保证配置一定可用。
        ///
        /// 【为什么明明字段声明处已经写了初始值，还要再来一遍】
        /// 因为字段初始值只在"C# 代码 new 出对象"时生效。当这个对象是从场景文件
        /// 反序列化出来的（Unity 的常规路径），字段会被存档里的值覆盖 ——
        /// 而老场景文件里可能根本没有某些字段（是后来才加的），于是它们会是 null 或空数组。
        /// 这个方法就是"数据迁移"的兜底：无论配置从哪来、有多旧，跑一遍之后一定完整。
        /// 判断条件用 `== null || Length == 0` 而不只是判 null，是因为 Unity 序列化
        /// 常把缺失的数组还原成长度 0 的空数组而非 null，只判 null 会漏掉这种情况。
        /// </summary>
        public void EnsureDefaults()
        {
            if (attackKeys == null || attackKeys.Length == 0)
            {
                attackKeys = new[] { KeyCode.Mouse0, KeyCode.J };
            }
            if (skill1Keys == null || skill1Keys.Length == 0)
            {
                skill1Keys = new[] { KeyCode.K, KeyCode.Mouse1 };
            }
            if (skill2Keys == null || skill2Keys.Length == 0)
            {
                skill2Keys = new[] { KeyCode.L };
            }
            if (dodgeKeys == null || dodgeKeys.Length == 0)
            {
                dodgeKeys = new[] { KeyCode.LeftShift, KeyCode.RightShift, KeyCode.Space };
            }
            if (lockOnKeys == null || lockOnKeys.Length == 0)
            {
                lockOnKeys = new[] { KeyCode.Tab };
            }
            if (toggleDebugKeys == null || toggleDebugKeys.Length == 0)
            {
                toggleDebugKeys = new[] { KeyCode.F1 };
            }
            if (moveUpKeys == null || moveUpKeys.Length == 0)
            {
                moveUpKeys = new[] { KeyCode.W, KeyCode.UpArrow };
            }
            if (moveDownKeys == null || moveDownKeys.Length == 0)
            {
                moveDownKeys = new[] { KeyCode.S, KeyCode.DownArrow };
            }
            if (moveLeftKeys == null || moveLeftKeys.Length == 0)
            {
                moveLeftKeys = new[] { KeyCode.A, KeyCode.LeftArrow };
            }
            if (moveRightKeys == null || moveRightKeys.Length == 0)
            {
                moveRightKeys = new[] { KeyCode.D, KeyCode.RightArrow };
            }
            // 手柄部分不逐个补，而是"只要有任何一项缺失就整套重置"。
            // 因为手柄按钮编号是成套的（见 ApplyLayout）：单独补一个 attackPad 默认值，
            // 可能和已有的 DualShock 布局混搭出一套四不像的键位。整套重置更安全。
            if (attackPad == null || attackPad.Length == 0
                || skill1Pad == null || skill1Pad.Length == 0
                || skill2Pad == null || skill2Pad.Length == 0
                || dodgePad == null || dodgePad.Length == 0
                || lockOnPad == null || lockOnPad.Length == 0
                || toggleDebugPad == null || toggleDebugPad.Length == 0)
            {
                ApplyLayout(layout);
            }
            if (stickDeadzone <= 0.0f)
            {
                stickDeadzone = 0.25f;
            }
        }

        /// <summary>造一份全默认的绑定表。给"场景里没有 InputBinder"的退路场景用。</summary>
        public static InputBindingProfile CreateDefault()
        {
            InputBindingProfile p = new InputBindingProfile();
            p.EnsureDefaults();
            return p;
        }

        /// <summary>
        /// 把 KeyCode 转成 HUD 上显示的短名称。
        /// 只特判了名字过长或不直观的几个（Mouse0 显示成 "LMB" 更符合玩家习惯），
        /// 其余直接用枚举名 —— KeyCode.J 的 ToString() 就是 "J"，已经够好了。
        /// 不必为所有按键都写一遍映射，那是维护负担而非收益。
        /// </summary>
        private static string ShortName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Mouse0: return "LMB";
                case KeyCode.Mouse1: return "RMB";
                case KeyCode.Mouse2: return "MMB";
                case KeyCode.LeftShift: return "Shift";
                case KeyCode.RightShift: return "Shift";
                case KeyCode.Space: return "Space";
                case KeyCode.Tab: return "Tab";
                default: return key.ToString();
            }
        }
    }
}
