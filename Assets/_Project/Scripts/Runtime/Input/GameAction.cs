// -----------------------------------------------------------------------------
// GameAction.cs —— 输入系统的「词汇表」：抽象动作枚举 + 一帧输入的快照结构
//
// 这个文件里没有任何"读键盘"的代码，它只定义两样东西：
//   1. GameAction    —— 游戏里到底有哪几种"动作"（攻击、闪避……），与设备无关。
//   2. InputSnapshot —— 把某一帧的输入状态打包成一个值类型，方便传递和合并。
//
// 【为什么要先有一层抽象动作，而不是各处直接判 KeyCode.J】
// 如果 AttackController 里直接写 Input.GetKeyDown(KeyCode.J)，那么：
//   · 想加手柄支持 → 要改每一个控制器；
//   · 想做按键重映射 → 无从下手，键位散落在十几个文件里；
//   · 想写自动化测试 → 没法伪造输入，只能真的去按键盘。
// 有了 GameAction 之后，键盘和手柄各自负责"把自己的按键翻译成 GameAction"，
// 上层控制器只问"这一帧 Attack 被按下了吗"，完全不关心玩家用的什么设备。
// 这就是所谓"面向接口编程"在输入层最直观的一个例子。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 游戏中所有可绑定的抽象动作。键盘/手柄各自把物理按键映射到这些值上。
    ///
    /// 【为什么要手写 = 0,1,2... 这些显式数值】
    /// 因为下面的 InputSnapshot 要把动作状态压进一个 int 的各个二进制位（见 Bit 方法），
    /// 枚举值直接当作"第几位"来用。显式写死数值是在提醒后来者：
    /// 这些数字是有语义的，中间不能插值、不能重排顺序，否则位掩码含义整体错位。
    /// 新动作一律往末尾追加，并同步把 InputSnapshot.ActionCount 加一。
    /// </summary>
    public enum GameAction
    {
        Attack = 0,
        Skill1 = 1,
        Skill2 = 2,
        Dodge = 3,
        LockOn = 4,
        ToggleDebug = 5
    }

    /// <summary>
    /// 一帧输入的完整快照：移动向量 + 各动作的"按住"和"刚按下"状态。
    ///
    /// 【为什么是 struct 而不是 class】
    /// 这东西每帧都要产生、传递、合并。用 class 的话每帧都在堆上 new 一个对象，
    /// 积少成多会喂饱 GC，导致游戏隔几秒卡一下（GC 尖峰）。struct 是值类型，
    /// 走栈或内联在宿主对象里，零堆分配。输入快照字段少、生命周期短，正是 struct 的典型场景。
    ///
    /// 【为什么用位掩码（int）存按键状态，而不是 bool[6]】
    /// 一个 int 就装下全部 6 个动作，好处有三：
    ///   · 零分配：数组是引用类型，会破坏上面 struct 的无 GC 特性；
    ///   · 合并极快：多个输入源（键盘+手柄同时接着）的合并就是一次按位或，见 MergeButtons；
    ///   · 判空极快：HeldMask != 0 一次比较就知道"有没有任何键被按着"。
    /// 代价是可读性稍差，所以才封装了 IsHeld / WasPressed 这些方法，
    /// 让调用方永远不必自己写位运算。
    /// </summary>
    public struct InputSnapshot
    {
        /// <summary>动作总数。新增 GameAction 时必须同步 +1，否则遍历所有动作的循环会漏掉新动作。</summary>
        public const int ActionCount = 6;

        /// <summary>移动方向，已归一化的二维向量（长度 0~1）。</summary>
        public Vector2 Move;

        /// <summary>移动键是否被按住。与 Move 分开存，是因为手柄摇杆轻推时 Move 很小但确实在动。</summary>
        public bool MoveKeysHeld;

        /// <summary>"正被按住"的动作位掩码。每一位对应一个 GameAction。</summary>
        public int HeldMask;

        /// <summary>"本帧刚按下"的动作位掩码（边沿）。与 HeldMask 的区别见 WasPressed 注释。</summary>
        public int PressedMask;

        /// <summary>把一个动作转成它在掩码里对应的那一位。1 &lt;&lt; 3 就是二进制的 1000，即第 3 位。</summary>
        public static int Bit(GameAction action)
        {
            return 1 << (int)action;
        }

        /// <summary>
        /// 该动作这一帧是否"正被按住"（持续为真）。
        /// 适合处理"按住蓄力""按住奔跑"这类需要持续状态的逻辑。
        /// </summary>
        public bool IsHeld(GameAction action)
        {
            // & 是按位与：只保留 action 对应的那一位，结果非 0 说明那位是 1。
            return (HeldMask & Bit(action)) != 0;
        }

        /// <summary>
        /// 该动作是否"本帧刚按下"（只在按下那一瞬间为真，即上升沿）。
        ///
        /// 【为什么必须区分"按住"和"刚按下"】
        /// 攻击、闪避这类动作只能在按下的瞬间触发一次。如果误用 IsHeld，
        /// 玩家按住不放会导致每帧都触发一次攻击，一秒钟打出 60 刀 —— 这是新手最常踩的坑。
        /// 反之"按住奔跑"若误用 WasPressed，就只会在按下那一帧跑一下然后立刻停。
        /// 记忆口诀：一次性动作用 WasPressed，持续性状态用 IsHeld。
        /// </summary>
        public bool WasPressed(GameAction action)
        {
            return (PressedMask & Bit(action)) != 0;
        }

        /// <summary>是否有任意动作键被按住。</summary>
        public bool HasAnyButton
        {
            get { return HeldMask != 0; }
        }

        /// <summary>
        /// 这一帧是否有任何输入活动（按键或移动）。
        /// 用途：多输入源自动切换 —— 玩家一碰手柄，HUD 就把按键提示切成手柄图标。
        /// </summary>
        public bool HasAnyActivity
        {
            // 用 sqrMagnitude（长度的平方）而不是 magnitude，是因为求长度要开平方根，
            // 而开方相对昂贵。这里只关心"是不是 0"，平方前后同样能判断，于是省掉开方。
            // 这是 Unity 里最常见的一条性能惯例，见到 sqrMagnitude 基本都是这个原因。
            get { return HeldMask != 0 || MoveKeysHeld || Move.sqrMagnitude > 0.0f; }
        }

        /// <summary>清空快照。每帧重新采样前调用，避免上一帧的状态残留。</summary>
        public void Clear()
        {
            Move = Vector2.zero;
            MoveKeysHeld = false;
            HeldMask = 0;
            PressedMask = 0;
        }

        /// <summary>
        /// 写入某个动作的状态。由具体输入源（键盘/手柄）在采样时调用。
        /// </summary>
        /// <param name="held">是否正被按住。</param>
        /// <param name="pressedEdge">是否是本帧刚按下的那一瞬间。</param>
        public void SetHeld(GameAction action, bool held, bool pressedEdge)
        {
            int bit = Bit(action);
            if (held)
            {
                HeldMask |= bit;    // |= 置位：把那一位设为 1，其余位不动
            }
            else
            {
                HeldMask &= ~bit;   // &= ~bit 清位：~bit 是"除了那位全是 1"，与一下就把那位抹成 0
            }
            if (pressedEdge)
            {
                PressedMask |= bit;
            }
            else
            {
                PressedMask &= ~bit;
            }
        }

        /// <summary>
        /// 把另一个输入源的按键状态并进来（按位或）。
        ///
        /// 【为什么要合并，以及为什么只合并按键、不合并 Move】
        /// 玩家可能键盘手柄同时接着，两个输入源各自产出一份快照。按键用"或"合并很自然：
        /// 任意一个设备按了攻击就算攻击。但移动向量不能这么合 ——
        /// 键盘给 (1,0)、摇杆给 (-1,0)，加起来是 (0,0)，玩家会发现"手放在摇杆上就走不动了"。
        /// 所以 Move 的取舍由上层 InputBinder 决定（谁有活动听谁的），这里只管按键。
        /// </summary>
        public void MergeButtons(InputSnapshot other)
        {
            HeldMask |= other.HeldMask;
            PressedMask |= other.PressedMask;
        }
    }
}
