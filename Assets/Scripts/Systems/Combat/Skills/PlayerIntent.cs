// -----------------------------------------------------------------------------
// PlayerIntent.cs —— 输入意图缓冲（引擎无关）
//
// 【为什么缓冲窗必须放在内核而不是 Unity 侧】
// Unity 的 Update 与内核的 1/60 逻辑步是非同步的：
//   · 144fps → 一个 Update 里可能推进 0 个逻辑步；
//   · 30fps  → 一个 Update 里可能推进 2~3 个逻辑步。
// 如果在 Unity 侧用 GetKeyDown 直接触发施法，144fps 时会出现「按了，但那一帧没有
// 逻辑步，意图凭空丢失」。把缓冲计数放进内核、按逻辑帧递减，帧率无关性就成了结构保证。
//
// 【写值不消费 —— 这一条决定了它是幂等的】
// Request 只做 _buffer[slot] = BufferFrames，不做任何消费。于是一个 Unity 帧内调 N 次
// 与调 1 次完全等价，"按住不放"（GetButton 语义，T2 普攻的手感来源）天然被支持：
// 节奏完全由 CD 与帧机决定，而不是由帧率决定。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是一个「按键留言板」。你按了键，先在这里留个条，战斗系统稍后来取。
//
// · 为什么按键不能直接触发技能？
//   游戏里其实有两套时钟在同时跑：
//     ① 画面时钟：显示器刷多少次就跑多少次，60Hz 显示器每秒 60 次，
//        144Hz 电竞屏每秒 144 次 —— 快慢因人而异。
//     ② 战斗时钟：不管你的电脑多快多慢，永远每秒精确跑 60 次。
//   你的键盘输入是被"画面时钟"读到的，而技能必须由"战斗时钟"来执行。
//   两套时钟对不齐，就会出问题：144Hz 下某一次画面刷新可能压根没轮到战斗时钟跑，
//   你那一下按键就凭空消失了 —— 玩家的感受是"我明明按了，人物没动"。
//
// · 留言板怎么解决？
//   按键时不直接放技能，只在留言板上写一句"我想放 1 号技能"，并给这张便签
//   贴上 6 帧（约 0.1 秒）的有效期。战斗时钟每跑一次就来看一眼留言板：
//   有便签就执行，没有就算了；便签每帧掉 1 点有效期，过期自动作废。
//   这样无论你的显示器是 30Hz 还是 144Hz，按键都不会丢。
//
// · 顺带解决了第二个问题：手感
//   6 帧的有效期意味着「你在收剑动作快结束时提前按下一刀，系统会帮你记着，
//   等收剑一结束立刻打出去」。这就是动作游戏里所谓的"输入缓冲"，
//   是"连招不卡手"的直接来源。
//
// · 为什么"写便签"不算消费？
//   Request 方法只负责把便签贴上去，不做任何执行。所以你在同一帧里按 100 次，
//   和按 1 次的效果完全一样。「按住不放持续攻击」也就自然支持了：
//   攻击节奏由技能冷却决定，而不是由你手速或显示器刷新率决定。
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// 意图槽位。与 <see cref="SkillTable"/> 的槽位一一对应，
    /// 定长 4 个（PRD Q4 拍板：首发 3 技能 + 1 闪避，槽位上限 4）。
    ///
    /// 【新手解释】就是"技能栏的第几格"。留言板上一共四格，每格独立留言，
    /// 互不干扰 —— 你可以同时留下"想普攻"和"想翻滚"两张便签。
    /// </summary>
    public enum IntentSlot
    {
        /// <summary>普攻（水剑斩）。左键 / J / 手柄 A。</summary>
        Basic = 0,

        /// <summary>技能 1（法阵冲击）。K / 右键 / 手柄 Y。</summary>
        Skill1 = 1,

        /// <summary>技能 2（血莲侵蚀）。L / 手柄 X。</summary>
        Skill2 = 2,

        /// <summary>闪避（踏雪）。Shift / 空格 / 手柄 B。</summary>
        Dodge = 3
    }

    /// <summary>
    /// 玩家意图缓冲区。挂在 <see cref="Encounter"/> 上（每场战斗一个），
    /// Unity 侧只写、内核侧只读消费。
    ///
    /// 【新手解释】就是上面说的那块「按键留言板」本体。
    /// 键盘那一侧（Unity 控制器）只负责往上贴便签；
    /// 战斗那一侧（Encounter）只负责撕便签执行。两边职责完全分开，
    /// 所以永远不会出现"一次按键被执行了两遍"这种 bug。
    /// </summary>
    public sealed class PlayerIntent
    {
        /// <summary>槽位数量。与 <see cref="IntentSlot"/> 枚举值数量一致。</summary>
        public const int SlotCount = 4;

        // 每槽剩余缓冲帧数。> 0 表示"有一次未被消费的按下"。
        private readonly int[] _buffer = new int[SlotCount];

        // 每槽按下时的朝向快照。施法瞬间用它，避免"按下时朝东、结算时朝西"。
        private readonly Vec2[] _facing = new Vec2[SlotCount];

        /// <summary>
        /// 缓冲窗长度（逻辑帧）。默认 <see cref="SkillConfig.INPUT_BUFFER_FRAMES"/> = 6 帧（100ms）：
        /// 后摇末尾提前按下会被接住，这是"连招不卡手"的来源。
        /// </summary>
        public int BufferFrames = SkillConfig.INPUT_BUFFER_FRAMES;

        /// <summary>构造。所有槽位初始为空，朝向初始为 <see cref="Vec2.Right"/>。</summary>
        public PlayerIntent()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                _buffer[i] = 0;
                _facing[i] = Vec2.Right;
            }
        }

        /// <summary>
        /// 投递一次意图。**幂等**：同一逻辑帧内调用多次等价于调用一次。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"贴便签"。什么时候被调用：Unity 那边检测到你按了键，
        /// 每一次画面刷新都会调一下（按住不放就是每帧都调）。
        /// "幂等"是程序员黑话，意思是"做一次和做一百次结果一样"，
        /// 因为它只是把有效期重置成 6，不会累加成 600。
        /// </remarks>
        /// <param name="slot">槽位（想放第几格的技能）。</param>
        /// <param name="facing">按下瞬间的朝向（零向量时保留上一次）。
        /// 记录朝向是为了避免"按下时朝东，0.1 秒后执行时人已经转向西"的错位。</param>
        public void Request(IntentSlot slot, Vec2 facing)
        {
            int i = (int)slot;
            if (i < 0 || i >= SlotCount)
            {
                return;
            }
            _buffer[i] = BufferFrames > 0 ? BufferFrames : 1;
            if (!facing.IsZero())
            {
                _facing[i] = facing;
            }
        }

        /// <summary>该槽是否有未消费的意图。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>true = 缓冲窗内仍有一次按下。</returns>
        public bool HasPending(IntentSlot slot)
        {
            int i = (int)slot;
            return i >= 0 && i < SlotCount && _buffer[i] > 0;
        }

        /// <summary>该槽剩余缓冲帧数（调试 / 单测用）。</summary>
        /// <param name="slot">槽位。</param>
        /// <returns>剩余帧数，无意图时为 0。</returns>
        public int PendingFrames(IntentSlot slot)
        {
            int i = (int)slot;
            return i >= 0 && i < SlotCount ? _buffer[i] : 0;
        }

        /// <summary>
        /// 只读预览该槽记录的朝向，<b>不消费</b>。
        ///
        /// 【为什么需要它 —— "起手失败不能吞掉输入"】
        /// <see cref="Encounter"/> 的起手流程是「软索敌 → 试着起手 → 成功才撕便签」。
        /// 软索敌需要按下瞬间的朝向，但那时还不知道能不能起手成功；
        /// 若直接用 <see cref="Consume"/> 取朝向，一旦起手失败（例如恰好还差一帧才出后摇窗），
        /// 这次按键就被白白吃掉了 —— 输入缓冲的意义当场归零。
        /// 于是拆成「预读 + 确认后消费」两步。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"隔着玻璃看一眼便签上写了什么，但不撕下来"。
        /// 什么时候被调用：战斗时钟准备起手一个动作、需要知道"你当时朝哪按的"时。
        /// </remarks>
        /// <param name="slot">槽位。</param>
        /// <returns>记录的朝向；槽位非法或从未记录时返回 <see cref="Vec2.Right"/>。</returns>
        public Vec2 PendingFacing(IntentSlot slot)
        {
            int i = (int)slot;
            return i >= 0 && i < SlotCount ? _facing[i] : Vec2.Right;
        }

        /// <summary>
        /// 消费一次意图并取出按下时的朝向。消费后缓冲立即清零，
        /// 保证「一次按下最多触发一次动作」。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"撕便签"。什么时候被调用：战斗时钟每帧检查留言板时。
        /// 撕下来之后便签就没了，所以"按一次 = 最多出一招"，
        /// 不会出现按一下连放三招的情况。
        /// </remarks>
        /// <param name="slot">槽位。</param>
        /// <param name="facing">输出参数：便签上记的朝向。
        /// （C# 里 out 的意思是"这个值由方法负责填给你"）</param>
        /// <returns>true = 确实消费到了一次意图。</returns>
        public bool Consume(IntentSlot slot, out Vec2 facing)
        {
            int i = (int)slot;
            if (i < 0 || i >= SlotCount || _buffer[i] <= 0)
            {
                facing = Vec2.Right;
                return false;
            }
            facing = _facing[i];
            _buffer[i] = 0;
            return true;
        }

        /// <summary>
        /// 每逻辑帧递减一次全部缓冲计数。由 <see cref="Encounter"/> 的 ①-A 阶段调用。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"所有便签的有效期各减 1"。每秒被调用 60 次。
        /// 减到 0 的便签就自动作废了 —— 这样"0.5 秒前按的那一下"不会
        /// 在你早已放弃之后突然诈尸放出来。
        /// </remarks>
        public void TickFrame()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_buffer[i] > 0)
                {
                    _buffer[i]--;
                }
            }
        }

        /// <summary>清空全部缓冲（切区 / 重生 / 进入基线模式时调用）。</summary>
        public void Clear()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                _buffer[i] = 0;
                _facing[i] = Vec2.Right;
            }
        }
    }
}
