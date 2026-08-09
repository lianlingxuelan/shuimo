// -----------------------------------------------------------------------------
// DodgeController.cs —— 闪避控制器：投递闪避意图 + 把内核算出的闪避位移搬回 Unity
//
// 【本文件同时承担两个方向的数据流，这是它比 SkillController 复杂的原因】
//   ↓ 去程（写意图）：玩家按闪避键 → RequestDodge → 写进 Encounter.Intent，就结束了。
//                     和 SkillController 完全一样，只写不算。
//   ↑ 回程（读状态）：DriveFromKernel 每帧读取内核里的闪避进度（第几帧、方向、
//                     是否处于无敌帧），据此算出这一帧该位移多少，再交给 PlayerController。
//
// 【为什么闪避位移要由内核算，而不是这里直接 transform.Translate】
// 因为"闪避的速度曲线"是玩法数据，必须和无敌帧窗口严格对齐：
// 比如设计上"第 4~10 帧无敌、且这段位移最快"。若表现层自己算位移、内核自己算无敌帧，
// 两边各按各的时钟走，很快就会错位 —— 玩家会遇到"看着已经翻滚出去了却还是被打中"。
// 让内核成为唯一的真相来源（single source of truth），表现层只做"读数 → 搬运"，
// 两者就永远不可能不同步。
//
// 【为什么"体力"耗在闪避、而"灵力"耗在技能（双资源池）】
// 见 ResourcePool.cs。简单说：单一资源会让"放技能"和"保命"抢同一个池子，
// 玩家的最优解退化成"永远留着资源闪避"，技能形同虚设。
// 拆成两个池后，两种行为各花各的钱，互不挤占，玩家才会既想放技能又想闪避。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 闪避控制器。执行序 -90：晚于输入采样(-300)，早于默认组件(0)。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-90)]
    public sealed class DodgeController : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private CombatBridge bridge;
        [SerializeField] private PlayerController player;

        [Header("基线回归")]
        [Tooltip("开启后本组件不投递任何闪避意图，也不接管移动（P0-09 基线配置）。")]
        [SerializeField] private bool disableForBaseline;

        // 本组件当前是否正在"接管"玩家移动。接管期间 PlayerController 的常规移动被覆盖。
        // 需要这个标志是为了知道"何时该把控制权还回去"，见 ReleaseDrive。
        private bool _driving;

        // 上一帧内核是否处于闪避中。用来检测"闪避刚开始"这个瞬间（边沿检测，
        // 和输入层的 WasPressed 是同一个套路：本帧是 && 上帧否 = 刚开始）。
        private bool _wasDodging;

        /// <summary>玩家按下闪避键的次数（不代表真的闪成功了，可能体力不足被内核拒绝）。</summary>
        public int DodgeRequestCount { get; private set; }

        /// <summary>
        /// 内核真正开始执行闪避的次数。
        /// 【为什么要和 DodgeRequestCount 分开统计】
        /// 两者的差值就是"被拒绝的请求数"（体力不够、正在硬直中等）。
        /// 调试时对比这两个数，能立刻分辨出"是输入没读到"还是"内核拒绝了"，
        /// 这在排查"我明明按了闪避却没反应"这类反馈时极其有用。
        /// </summary>
        public int DodgeStartCount { get; private set; }

        /// <summary>当前是否在闪避中（镜像自内核状态）。</summary>
        public bool IsDodging { get; private set; }

        /// <summary>当前是否处于无敌帧（镜像自内核状态，HUD 据此闪白提示）。</summary>
        public bool IframeActive { get; private set; }

        /// <summary>闪避动作进行到第几帧（镜像自内核）。</summary>
        public int DodgeCursor { get; private set; }

        /// <summary>最近一次闪避方向。初值给 right 而非零向量，理由同 SkillController.ResolveFacing。</summary>
        public Vector2 LastDodgeDir { get; private set; } = Vector2.right;

        public bool DisableForBaseline
        {
            get { return disableForBaseline; }
            set { disableForBaseline = value; }
        }

        public bool IsDrivingMovement
        {
            get { return _driving; }
        }

        private void Awake()
        {
            if (player == null)
            {
                player = GetComponent<PlayerController>();
            }
        }

        /// <summary>
        /// 组件被禁用时必须归还移动控制权。
        /// 【为什么这一步不能省】
        /// 假如玩家正翻滚到一半时本组件被禁用（切场景、角色死亡、开菜单），
        /// PlayerController 身上还挂着我们设的"外部速度覆盖"，且再也没人来清除它 ——
        /// 角色会以闪避速度朝一个方向永远飞出去。凡是"接管了别人状态"的组件，
        /// 都必须在 OnDisable/OnDestroy 里成对地还回去，这是资源管理的基本纪律。
        /// </summary>
        private void OnDisable()
        {
            ReleaseDrive();
        }

        private void Update()
        {
            if (disableForBaseline)
            {
                ReleaseDrive();
                return;
            }

            // 追加 IsGameplayBlocked：菜单打开 / 终局时不接受闪避输入。
            // 走这个分支会顺带 ReleaseDrive()，正好满足上面 OnDisable 注释里那条纪律 ——
            // 冻结瞬间若玩家正翻滚到一半，移动控制权必须还给 PlayerController。
            CombatBridge b = ResolveBridge();
            if (b == null || !b.IsReady || b.BaselineMode || !b.T3Enabled || b.IsGameplayBlocked)
            {
                ReleaseDrive();
                return;
            }

            if (InputBinder.Pressed(GameAction.Dodge))
            {
                Vector2 dir = ResolveDodgeDir();
                DodgeRequestCount++;
                LastDodgeDir = dir;
                b.RequestDodge(dir);
            }

            DriveFromKernel(b);
        }

        /// <summary>
        /// 回程数据流：读取内核的闪避状态，换算成这一帧的位移交给 PlayerController。
        ///
        /// 【注意本方法一个字都没有"决定"闪避该怎么走 —— 它只是搬运工】
        /// 速度曲线由 DodgeAction.VelocityAtFrame 给出（纯逻辑层），
        /// 无敌帧由 action.IsIframeActive 给出（纯逻辑层），
        /// 这里只负责把纯逻辑层的 Vec2 翻译成 Unity 的 Vector2 并应用上去。
        /// 保持这种"只搬不算"的克制，是表现层与逻辑层能长期不打架的关键。
        /// </summary>
        private void DriveFromKernel(CombatBridge b)
        {
            Combatant p = b.Player;
            // ?: 三元判空，层层取值。内核可能还没装配好，任何一环为 null 都要能安全走到下面。
            ActionState action = p != null ? p.Action : null;

            // 四个条件同时成立才算"正在闪避"：
            //   有动作状态 && 动作类型是闪避 && 动作阶段不是"无" && 已经推进过至少一帧。
            // Cursor > 0 这条容易被忽略但很重要：意图刚写入、内核还没消费时 Cursor 是 0，
            // 此时若当成已在闪避去驱动位移，会比内核提前一帧动，造成表现与判定错开一帧。
            bool dodging = action != null
                           && action.Kind == ActionKind.Dodge
                           && action.Phase != ActionPhase.None
                           && action.Cursor > 0;

            if (!dodging)
            {
                _wasDodging = false;
                ReleaseDrive();
                return;
            }

            // 边沿检测：只在闪避的第一帧计数一次，而不是闪避期间每帧都加。
            if (!_wasDodging)
            {
                _wasDodging = true;
                DodgeStartCount++;
            }

            // 用内核锁定的方向，而不是玩家此刻的输入方向。
            // 【为什么闪避方向要在起手时"锁死"】
            // 如果每帧都跟随当前输入，玩家就能在翻滚过程中转向甚至掉头，
            // 闪避从"有风险的位移技"退化成"无敌状态下的自由飞行"，所有攻击都能无脑躲开。
            // 起手定方向、中途不可改，是动作游戏闪避设计的通用做法。
            Vec2 dirKernel = action.LockedFacing;
            Vec2 v = DodgeAction.VelocityAtFrame(action.Cursor, action.Frames, dirKernel);

            IsDodging = true;
            IframeActive = action.IsIframeActive;
            DodgeCursor = action.Cursor;
            if (!dirKernel.IsZero())
            {
                LastDodgeDir = new Vector2(dirKernel.X, dirKernel.Y);
            }

            Vector2 velocity = new Vector2(v.X, v.Y);
            if (player == null)
            {
                return;
            }

            // 三步接管移动：告知速度（供其它系统查询）、锁定朝向（翻滚中不许转身）、实际位移。
            // 位移用 Time.deltaTime 而不是固定 1/60：这一步属于"表现"，
            // 应该跟随渲染帧平滑地走，高刷屏才不会看到卡顿。
            // 而"总共走多远"由内核的帧数决定，所以平滑与否不影响最终落点 —— 表现可以自由，判定必须严格。
            player.SetExternalVelocity(velocity);
            player.SetFacingOverride(LastDodgeDir);
            player.MoveExternal(velocity, Time.deltaTime);
            _driving = true;
        }

        /// <summary>
        /// 归还移动控制权，清空对外镜像状态。可以安全地重复调用。
        /// </summary>
        private void ReleaseDrive()
        {
            IsDodging = false;
            IframeActive = false;
            DodgeCursor = 0;

            // 这个提前返回是"幂等"的关键：没在接管就什么都不用还。
            // 本方法每帧都会被非闪避分支调用，若不判断就每帧都去调 SetExternalVelocity(null)，
            // 会持续覆盖掉别的系统（比如击退、传送）设置的速度 —— 变成一个隐形的干扰源。
            // 只清理自己造成的影响，不越界，这是组件之间和平共处的前提。
            if (!_driving)
            {
                return;
            }
            _driving = false;
            if (player != null)
            {
                player.SetExternalVelocity(null);   // null = 取消覆盖，回到常规移动
            }
        }

        /// <summary>
        /// 决定闪避方向：优先当前移动方向，其次角色朝向，最后兜底向右。
        ///
        /// 【为什么"移动方向"优先于"朝向"】
        /// 玩家一边向左走一边按闪避，期望的显然是"往左翻"。
        /// 若用朝向优先，而角色因为锁定敌人正面朝右，就会翻向危险的方向 —— 手感灾难。
        /// 只有在完全静止（没有移动输入）时，才退而使用朝向。
        /// </summary>
        public Vector2 ResolveDodgeDir()
        {
            if (player != null)
            {
                Vector2 move = player.MoveDir;
                if (move.sqrMagnitude > 0.0f)
                {
                    return move.normalized;
                }
                Vector2 facing = player.LastFacing;
                if (facing.sqrMagnitude > 0.0f)
                {
                    return facing.normalized;
                }
            }
            return Vector2.right;
        }

        private CombatBridge ResolveBridge()
        {
            if (bridge != null)
            {
                return bridge;
            }
#if UNITY_2023_1_OR_NEWER
            bridge = Object.FindFirstObjectByType<CombatBridge>();
#else
            bridge = Object.FindObjectOfType<CombatBridge>();
#endif
            return bridge;
        }
    }
}
