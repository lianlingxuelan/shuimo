// -----------------------------------------------------------------------------
// RunPhase.cs —— 胜负判定 / 对局阶段状态机（引擎无关）
//
// 【它解决什么问题】（PM 缺口清单 P0-3）
// 内核一直都知道「谁死了」——玩家和敌人都有 Combatant.IsAlive（血量 > 0）。
// 但在此之前，**没有任何一处代码把这些零散的 IsAlive 汇总成一个结论**：
// 「这一局到底是打赢了、打输了，还是还在打」。
// 于是玩家血条空掉之后，游戏依然一帧不落地在跑：怪继续追、伤害继续结算，
// 界面上却什么都不发生 —— 只能 Alt+F4。本文件补的就是这个"结论层"。
//
// 【为什么单独开一个文件 / 一个类，而不是往 Encounter 里塞几个 bool】
//   1. Encounter 已经 1000+ 行，是全项目最不该继续膨胀的文件；
//   2. 胜负判定是**纯查询**，它的输入只有三个数（有没有玩家 / 玩家死没死 /
//      还剩几只活敌人），完全可以脱离 Encounter 单独单测 —— 见 Evaluate 的
//      三参数重载。测试不需要搭一整场战斗，就能覆盖全部分支；
//   3. 将来 P1 要加"限时挑战超时判负""护送目标阵亡判负"这类新的结束条件时，
//      改动范围被锁死在这一个文件里。
//
// 【★ 最重要的设计决定：只观察，不干预】
// 本状态机**绝不会**去暂停战斗、清空敌人、冻结玩家、或者修改任何一个数值。
// 它每步只做一件事：读几个 bool，写一个枚举字段，必要时喊一嗓子（抛事件）。
//
// 理由有两条，都是硬理由：
//   · 数值红线。U1 平衡口径（HP 260 / d_eff 4.0 / raw 12 / CD 0.4s / 围攻 4.300 次/s）
//     被 64 条自动断言锁死。只要内核在"玩家死后"少跑一步或多跑一步，
//     那些长时间模拟的对拍就会整体错位。观察者不写状态 = 逐指令等价，零风险。
//   · 职责边界。「死了之后要不要暂停、要不要弹结算界面、要不要放慢镜头」
//     是**表现层**的决定，不同平台、不同模式答案都不一样。内核只负责给出
//     「你输了」这个事实，怎么呈现由 Unity 层自己定。
//
// 【纯逻辑产生状态、Unity 层消费状态 —— 这个解耦是怎么落地的】
// 内核（本文件）：算出 Playing / Won / Lost，通过 PhaseChanged 事件广播。
// Unity 层（以后 P0-2 死亡结束、P0-5 菜单要接的）：
//     encounter.RunState.PhaseChanged += OnRunPhaseChanged;
//     void OnRunPhaseChanged(RunPhase p) { if (p == RunPhase.Lost) 弹死亡界面; }
// 内核不认识 Unity，Unity 也不需要知道胜负是怎么算出来的。
// 于是同一套判定逻辑可以在没有 Unity 的环境里被 NUnit / Python 对拍直接验证。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这是**裁判**。它不下场踢球，只在旁边看着，然后吹哨说"结束了，红方赢"。
//
// · 什么是"状态机"？
//   就是"这个东西同一时刻只能处于几种确定状态之一，并且状态之间的转换有规矩"。
//   这里只有三种状态：进行中(Playing) / 胜利(Won) / 失败(Lost)。
//   规矩是：只能从 Playing 走向 Won 或 Lost，**永远不许走回来**。
//
// · 为什么"永远不许走回来"这么重要？（这就是注释里说的"幂等"）
//   假设玩家血空了 → 判负 → 弹出死亡界面。如果下一帧有个回血光环把血加回来了，
//   状态又变回 Playing，死亡界面就会自己消失 —— 玩家会觉得游戏坏了。
//   更糟的是"打赢了" → 弹出胜利结算 → BOSS 又召唤出一只小怪 → 状态退回 Playing
//   → 结算界面消失 → 小怪被清掉 → 结算界面又弹出来一次。奖励可能被领两遍。
//   所以：一旦分出胜负，这个裁判就永久闭麦。想重来？上层显式调 Reset()。
//
// · 为什么要有 "_armed"（布防）这个开关？
//   胜利条件是"敌人全死光了"。但战斗刚创建出来的那一瞬间，敌人还没被加进场，
//   "活敌人数量"天然就是 0 —— 如果不管三七二十一就判定，游戏一开始就直接通关了。
//   所以要先看到"场上确实出现过至少一只活敌人"，裁判才把胜利这条规则打开。
//   这类"必须先满足前置条件才启用某规则"的写法在游戏逻辑里非常常见。
//
// · 同一帧里我和最后一只怪同归于尽，算赢还是算输？
//   算**输**。这是本文件里刻意写死的优先级（见 Evaluate）。
//   理由很朴素：玩家一定看得见自己血条空掉，如果这时候弹的是"胜利"，
//   他会觉得被糊弄了。而且死亡是不可逆的强反馈，宁可判严不可判松。
// =============================================================================
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Combat
{
    /// <summary>
    /// 一局战斗的宏观阶段。
    ///
    /// 【新手解释】就三种情况：还在打、打赢了、打输了。
    /// 显式写出数值（0/1/2）是为了让它序列化进存档 / 网络包时有稳定含义，
    /// 将来在中间插入新枚举项也不会把旧存档的语义改掉。
    /// </summary>
    public enum RunPhase
    {
        /// <summary>进行中。默认状态。</summary>
        Playing = 0,

        /// <summary>胜利：玩家存活，且场上再无存活敌人（需先"布防"，见 RunPhaseTracker）。</summary>
        Won = 1,

        /// <summary>失败：玩家血量归零。优先级高于 <see cref="Won"/>。</summary>
        Lost = 2
    }

    /// <summary>
    /// 胜负判定器。每个固定步的末尾被 <see cref="Encounter.StepFixed"/> 调用一次，
    /// 把「玩家死没死 / 还剩几只活敌人」折叠成一个 <see cref="RunPhase"/>。
    ///
    /// 【线程模型】与整个内核一致：单线程，只在固定步推进里被调用。
    /// <see cref="PhaseChanged"/> 因此也在逻辑线程上同步抛出，
    /// Unity 侧订阅方若要动 UI，请确保自己也在主线程（现状下必然是）。
    /// </summary>
    public sealed class RunPhaseTracker
    {
        /// <summary>
        /// 阶段发生变化时抛出（参数为**新**阶段）。
        ///
        /// 【触发次数保证】由于幂等闸门的存在，从 <see cref="RunPhase.Playing"/>
        /// 出发到分出胜负，本事件在一局里**至多触发一次**。
        /// 订阅方因此可以放心地在回调里做"弹结算界面 / 发奖励"这类不可重入的事，
        /// 不需要自己再加一层"我是不是已经弹过了"的标志位。
        ///
        /// 【为什么用 event 而不是让上层每帧轮询 Phase】
        /// 轮询要求上层自己记住"上一帧是什么状态"来做边沿检测，
        /// 这份状态一旦有两个消费者（死亡界面 + 存档系统）就要各记一份，迟早不同步。
        /// 事件把"边沿检测"这件事收敛到内核一处完成。
        /// （当然 <see cref="Phase"/> 也照常公开，需要轮询的场合直接读即可。）
        /// </summary>
        public event Action<RunPhase> PhaseChanged;

        // 当前阶段。默认 Playing —— 新造出来的裁判总是认为"比赛正在进行"。
        private RunPhase _phase = RunPhase.Playing;

        // 胜利规则是否已"布防"。
        // 只有在某一步真正看到过「存活敌人数 > 0」之后才置 true。
        // 不加这道门，一个刚 new 出来、还没往里加怪的 Encounter 会在第一步直接判胜。
        private bool _armed;

        /// <summary>当前阶段。</summary>
        public RunPhase Phase
        {
            get { return _phase; }
        }

        /// <summary>是否已分出胜负（= 不再是 <see cref="RunPhase.Playing"/>）。</summary>
        public bool IsSettled
        {
            get { return _phase != RunPhase.Playing; }
        }

        /// <summary>
        /// 胜利规则是否已布防（场上是否出现过存活敌人）。
        /// 公开出来主要是为了让单测能直接断言这道门确实生效了，
        /// 而不是靠"结果碰巧对"来间接证明。
        /// </summary>
        public bool IsArmed
        {
            get { return _armed; }
        }

        /// <summary>
        /// 重置为 <see cref="RunPhase.Playing"/> 并撤销布防（重开一局 / 换区时调）。
        ///
        /// 【为什么不清空 <see cref="PhaseChanged"/> 的订阅者】
        /// 订阅者是上层（HUD、死亡界面）在装配阶段挂上的，生命周期跟随**场景**，
        /// 而 Reset 的生命周期跟随**一局**。在这里顺手清订阅，等于每次重开都要求
        /// 上层重新挂一遍回调，漏挂一次就是"第二局死了没反应"这种极难复现的 bug。
        /// 谁订阅谁负责退订，是本项目统一的约定。
        ///
        /// 【为什么不抛 PhaseChanged(Playing)】
        /// Reset 一定是上层主动调的 —— 它自己就是那个"要重开一局"的人，
        /// 不需要内核再通知它一遍。反过来，在清场/析构路径上抛事件，
        /// 很容易让还没来得及退订的 UI 在被销毁的过程中收到回调而崩溃。
        /// </summary>
        public void Reset()
        {
            _phase = RunPhase.Playing;
            _armed = false;
        }

        /// <summary>
        /// 判定一次（纯查询重载）。这是**唯一**的判定逻辑所在，
        /// <see cref="Evaluate(Encounter)"/> 只是帮你把三个参数从战场里取出来。
        ///
        /// 【判定顺序 —— 三条规则，顺序不可调换】
        /// <list type="number">
        /// <item>已分出胜负 → 直接返回，永不翻转（幂等）。</item>
        /// <item>没有玩家 → 不做任何判定。战场还没编好队（或已被 Clear），
        ///       此时既谈不上输也谈不上赢。</item>
        /// <item>玩家已死 → Lost。**排在 Won 前面**，于是同归于尽判负。</item>
        /// <item>已布防 且 活敌人为 0 → Won。</item>
        /// </list>
        /// </summary>
        /// <param name="playerPresent">战场上是否编入了玩家（<c>Encounter.Player != null</c>）。</param>
        /// <param name="playerAlive">玩家是否存活（<c>Player.IsAlive</c>，血量 &gt; 0）。</param>
        /// <param name="aliveEnemyCount">当前存活敌人数量。</param>
        /// <returns>判定后的阶段（也可随后从 <see cref="Phase"/> 读到）。</returns>
        public RunPhase Evaluate(bool playerPresent, bool playerAlive, int aliveEnemyCount)
        {
            // ① 幂等闸门。放在最前面，保证"已经吹过哨的裁判"连后面的判断都不做。
            if (_phase != RunPhase.Playing)
            {
                return _phase;
            }

            // ② 没有玩家 = 这局还没开始（或已被清场）。不判定、也不布防。
            //    不布防很关键：否则"先加怪、后设玩家"的装配顺序会在设玩家之前
            //    就把 _armed 点亮，万一那批怪在设玩家之前被清掉，一进场就判胜。
            if (!playerPresent)
            {
                return _phase;
            }

            // ③ 布防：只要见过一次活敌人，胜利规则就永久启用。
            //    用"曾经见过"而不是"当前有"，是为了兼容 BOSS 召唤：
            //    小怪全清但 BOSS 还活着时 aliveEnemyCount 仍 > 0，不会误判；
            //    而清完最后一只的那一步，_armed 早就是 true 了。
            if (aliveEnemyCount > 0)
            {
                _armed = true;
            }

            // ④ 失败优先于胜利（同一步同归于尽 → 判负，理由见文件头新手向导）。
            if (!playerAlive)
            {
                Settle(RunPhase.Lost);
                return _phase;
            }

            // ⑤ 胜利。必须已布防，否则"空场景"会被当成"已清场"。
            if (_armed && aliveEnemyCount <= 0)
            {
                Settle(RunPhase.Won);
                return _phase;
            }

            return _phase;
        }

        /// <summary>
        /// 判定一次（便捷重载）。从战场里取出三个输入后转发给纯查询重载。
        ///
        /// 【为什么要拆成两个重载】
        /// 纯查询重载不认识 <see cref="Encounter"/>，于是单测可以只写三个 bool/int
        /// 就覆盖全部分支，不用搭一整场战斗（造玩家、造怪、播种随机流……）。
        /// 而生产路径只想写一行 <c>RunState.Evaluate(this)</c>。两个诉求都满足。
        /// </summary>
        /// <param name="enc">战场。为 null 时不做任何判定。</param>
        /// <returns>判定后的阶段。</returns>
        public RunPhase Evaluate(Encounter enc)
        {
            if (enc == null)
            {
                return _phase;
            }

            Combatant p = enc.Player;
            bool present = p != null;
            bool alive = present && p.IsAlive;
            return Evaluate(present, alive, enc.AliveEnemyCount);
        }

        /// <summary>
        /// 落定到某个终局阶段并广播一次。
        /// 调用方已保证当前是 <see cref="RunPhase.Playing"/>，这里的相等判断只是双保险。
        /// </summary>
        /// <param name="next">新阶段（<see cref="RunPhase.Won"/> 或 <see cref="RunPhase.Lost"/>）。</param>
        private void Settle(RunPhase next)
        {
            if (_phase == next)
            {
                return;
            }

            _phase = next;

            // 先取到本地变量再判空调用：这是 C# 里抛事件的标准写法，
            // 避免"判空之后、调用之前"恰好被别处退订而导致空引用。
            Action<RunPhase> handler = PhaseChanged;
            if (handler != null)
            {
                handler(next);
            }
        }
    }
}
