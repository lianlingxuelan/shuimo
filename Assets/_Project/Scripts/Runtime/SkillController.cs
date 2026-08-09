// -----------------------------------------------------------------------------
// SkillController.cs —— 技能输入控制器：把"玩家按了技能键"翻译成一条战斗意图
//
// 【这个文件是理解本工程架构的最佳入口，请读完下面这段】
// 注意 Update 里做的事有多"少"：检测按键 → 调 b.RequestCast(...) → 结束。
// 它没有扣灵力、没有算伤害、没有播特效、更没有推进任何一帧战斗逻辑。
//
// 这正是本工程的核心分层原则："T3 控制器只写意图，不推进帧"。
// 完整链路是这样的：
//     SkillController.Update           ← 你在这里，只负责"写下想做什么"
//       → CombatBridge.RequestCast
//         → CombatController.RequestPlayerCast
//           → Encounter.Intent.Request(...)   ← 意图被存进缓冲区，仅此而已
//     ...（本帧结束，什么都还没真正发生）...
//     CombatController.Update → Scheduler.Tick → Encounter.StepFixed
//                                                 ← 直到这里，内核才真正消费意图、
//                                                   判定灵力够不够、进入前摇、结算伤害
//
// 【为什么要费这么大劲绕一圈，不能在这里直接放技能？】
// 因为"何时结算"必须由内核独占。如果控制器自己结算：
//   1. 结算会发生在渲染帧上（帧率不固定），高帧率机器每秒结算 144 次、低帧 30 次，
//      同一套操作在不同电脑上打出不同伤害 —— 战斗手感与平衡性全废；
//   2. 录像回放、自动化测试都失去意义，因为结果不可复现；
//   3. 技能、闪避、攻击三个控制器各自结算，谁先谁后取决于组件执行顺序这种偶然因素，
//      "闪避无敌帧到底挡没挡住这一刀"会变成薛定谔状态。
// 把"意图产生"（可以随渲染帧任意频率发生）和"意图消费"（严格固定步长 1/60）分开，
// 是所有需要确定性的动作游戏都会采用的结构。详见 CombatScheduler 的注释。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// "谁能告诉我角色当前朝向"的抽象接口。
    ///
    /// 【为什么不直接依赖 PlayerController，非要抽个接口】
    /// 技能需要知道朝哪个方向放。朝向通常来自 PlayerController，但也可能来自
    /// 锁定目标系统、载具、或测试用的假数据。抽成接口后，SkillController 只依赖
    /// "能提供朝向"这个能力，而不绑死在某个具体类上，替换实现时无需改动本文件。
    /// 这叫"依赖倒置"：依赖抽象而非具体。
    /// </summary>
    public interface IFacingProvider
    {
        /// <summary>角色当前朝向（单位向量）。</summary>
        Vector2 CurrentFacing { get; }
    }

    /// <summary>
    /// 监听技能键，向战斗内核投递施法意图。只写意图，不做任何结算（原因见文件头）。
    ///
    /// 【[DefaultExecutionOrder(-50)] 的含义】
    /// -50 比 InputBinder 的 -300 大、比默认的 0 小，所以执行顺序是：
    ///     InputBinder(-300) 先采样输入 → 本组件(-50) 读输入并写意图 → 其它默认组件(0)
    /// 三者的先后由这个数字精确控制，不依赖 Unity 的组件挂载顺序（那是不可靠的）。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class SkillController : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private CombatBridge bridge;
        [SerializeField] private PlayerController player;

        [Header("基线回归")]
        [Tooltip("开启后本组件不投递任何技能意图（P0-09 基线配置）。")]
        [SerializeField] private bool disableForBaseline;

        private IFacingProvider _facing;

        // 下面三个计数/记录属性是给自动化测试和调试面板看的"观测点"。
        // 注意 { get; private set; } 这个写法：外部只能读、只有本类能写。
        // 测试可以断言"按了 3 次 Skill1，计数就该是 3"，而外部代码无法篡改它。
        /// <summary>技能 1 被请求的累计次数（供测试与调试观测）。</summary>
        public int Skill1RequestCount { get; private set; }

        /// <summary>技能 2 被请求的累计次数（供测试与调试观测）。</summary>
        public int Skill2RequestCount { get; private set; }

        /// <summary>最近一次请求的槽位。</summary>
        public IntentSlot LastRequestedSlot { get; private set; } = IntentSlot.Basic;

        public bool DisableForBaseline
        {
            get { return disableForBaseline; }
            set { disableForBaseline = value; }
        }

        public IFacingProvider Facing
        {
            get { return _facing; }
            set { _facing = value; }
        }

        private void Awake()
        {
            if (player == null)
            {
                player = GetComponent<PlayerController>();
            }
            if (_facing == null && player != null)
            {
                _facing = player;
            }
        }

        private void Update()
        {
            // 基线回归开关：把本组件从链路上摘掉，用于"退化成纯 T2"的对照实验。
            // 详见 CombatBridge.baselineMode 的说明（五个面同时断开，这是其中的"输入面"）。
            if (disableForBaseline)
            {
                return;
            }

            CombatBridge b = ResolveBridge();
            // 四道闸门，任意一道不通就什么都不做（而不是报错）：
            //   b == null      —— 场景里压根没有战斗桥（比如主菜单场景），静默跳过；
            //   !b.IsReady     —— 战斗还没装配完成，此时投意图会写进一个空的 Encounter；
            //   b.BaselineMode —— 基线模式，整条 T3 链路关闭；
            //   !b.T3Enabled   —— T3 功能未启用。
            // 这里再次体现"空组件 = 原路径"：缺少任何依赖都只是安静地不工作，绝不抛异常。
            // 对于每帧执行的 Update 来说这一点尤其重要 —— 一旦抛异常，
            // Unity 会每帧都打一条错误日志，几秒钟就能把控制台刷爆、把帧率拖垮。
            //   b.IsGameplayBlocked —— 终局或菜单打开，玩法已冻结（P0-5 统一闸门）。
            if (b == null || !b.IsReady || b.BaselineMode || !b.T3Enabled || b.IsGameplayBlocked)
            {
                return;
            }

            // 用 Pressed（边沿）而不是 Held：技能是一次性触发，按住不放不应连发。
            if (InputBinder.Pressed(GameAction.Skill1))
            {
                Skill1RequestCount++;
                LastRequestedSlot = IntentSlot.Skill1;
                b.RequestCast(IntentSlot.Skill1, ResolveFacing());
            }

            if (InputBinder.Pressed(GameAction.Skill2))
            {
                Skill2RequestCount++;
                LastRequestedSlot = IntentSlot.Skill2;
                b.RequestCast(IntentSlot.Skill2, ResolveFacing());
            }
        }

        /// <summary>
        /// 求出本次施法的朝向，按"接口 → 玩家组件 → 默认向右"三级回落。
        ///
        /// 【为什么零向量要兜底成 Vector2.right，而不是就让它是零】
        /// 角色静止不动时朝向可能是 (0,0)。把零向量当施法方向传下去，
        /// 内核里做归一化会得到 NaN（0 除以 0），而 NaN 一旦产生就会像病毒一样
        /// 污染后续所有计算：位置变 NaN → 物体从屏幕上消失 → 且不会报任何错，极难排查。
        /// 所以在"数值可能退化"的边界上强制给一个合法默认值。
        /// 选 right 只是个约定（角色默认朝右），重点是它必须是个有效的单位向量。
        /// </summary>
        public Vector2 ResolveFacing()
        {
            Vector2 f = Vector2.zero;
            if (_facing != null)
            {
                f = _facing.CurrentFacing;
            }
            else if (player != null)
            {
                f = player.LastFacing;
            }
            if (f.sqrMagnitude <= 0.0f)
            {
                return Vector2.right;
            }
            return f;
        }

        /// <summary>
        /// 取得战斗桥引用：优先用面板上拖好的，没有就自己去场景里找一次。
        ///
        /// 【注意这里的"找到后写回字段"是关键优化】
        /// FindFirstObjectByType 会遍历整个场景，是相当昂贵的操作，绝不能每帧调用。
        /// 这里找到之后立刻赋值给 bridge 字段，下次进来第一个 if 就直接返回了 ——
        /// 于是全场景搜索一辈子只发生一次。这个模式叫"惰性初始化 + 缓存"。
        /// （小提醒：若始终找不到，则每帧都会搜一次。本工程可接受，因为没有桥就等于没战斗，
        ///  这种场景本来就不该挂本组件。）
        ///
        /// 【#if UNITY_2023_1_OR_NEWER 是什么】
        /// 条件编译：编译期按 Unity 版本二选一，只有一个分支会被编进最终程序。
        /// FindObjectOfType 在 2023.1 起被标记为过时（新 API 更快），
        /// 但直接换掉会导致老版本 Unity 编译失败。用条件编译可以同时兼容新旧版本，
        /// 既不吃过时警告，也不放弃老版本支持。
        /// </summary>
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
