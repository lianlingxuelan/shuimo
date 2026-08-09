// -----------------------------------------------------------------------------
// DodgeAction.cs —— 闪避的位移曲线与无敌帧窗口（引擎无关 · 静态纯函数）
//
// 【为什么位移只给"意图"而不直接改 Position】
// 玩家位置的权威在 Unity：CombatController.SyncPlayerIntoKernel() 每帧把
// transform.position 写进内核，内核里改 Player.Position 下一帧就会被冲掉。
// 所以这里的设计与 EnemyAI.DesiredVelocity 同构 —— 内核只给出"本帧应该以多快的速度
// 朝哪个方向走"，由 Unity 侧 DodgeController 接管 PlayerController 去执行。
// 好处：曲线是纯函数，可 headless 单测；位置权威不变，不引入第二个真源。
//
// 【为什么是 ease-out 而不是匀速】
// 翻滚的手感来自"起步猛、收尾缓"。匀速位移看起来像滑冰。
// 权重 w(k) = 1 − (k−1)/N 是最简单的线性衰减 ease-out，且它的和有闭式解
//     Σ w(k) = N − (N−1)/2
// 于是可以精确反解出"总位移恰好等于 130px"的速度系数，不需要数值积分或魔数标定。
//
// 【i-frame 不在这里生效】
// 本类只回答"第几帧算无敌帧"，真正写 WCoreState.Iframe 的只有 Encounter 的 ①-A 阶段
// （§7.5 约定：其它任何地方，尤其是 Unity 侧，都不许写 Iframe）。
//
// 【禁止事项】不得引用 UnityEngine。
//
// =============================================================================
// 【新手向导】
// =============================================================================
// 一句话：这个文件回答两个问题 ——「翻滚时每一帧该走多快」和「第几帧算无敌」。
//
// · 翻滚的完整设定（PRD 给的）：总共 0.25 秒、向前窜 130 像素、
//   其中有 0.20 秒是无敌的（从第 3 帧开始，持续 12 帧）。
//
// · 为什么速度不是恒定的？
//   如果整个翻滚都用同一个速度，看起来像在冰上滑行，很假。
//   真实的翻滚是"一蹬地窜出去，然后慢慢减速停住"。
//   所以我们给每一帧配一个逐渐变小的权重（第 1 帧最快、最后一帧最慢），
//   这种"先快后慢"的曲线在游戏行业叫 ease-out（缓出）。
//
// · 一个很容易踩的坑：怎么保证总距离刚好是 130 像素？
//   笨办法是随便设个速度然后反复试、手动调参，改一次帧数就得重调。
//   这里用的是数学办法：权重之和有一个现成的公式（WeightSum），
//   用总距离 ÷ 权重之和 就能反推出精确的速度系数。
//   于是"总位移 = 130"变成了一个恒等式，改帧数也不会跑偏。
//
// · 为什么这里只算速度，不直接把人挪走？
//   因为在 Unity 项目里，"玩家现在站在哪"这件事的最终话语权在 Unity 的
//   Transform（也就是场景里那个物体的坐标）。战斗内核每帧都会被 Unity 覆盖一次坐标。
//   所以内核在这里只提"建议速度"，实际挪动交给 Unity 侧的 DodgeController 执行。
//   好处：这段曲线是纯数学函数，可以脱离 Unity 单独跑测试。
//
// · 无敌帧的开关不在这里！
//   本文件只回答"第几帧算无敌"，真正让角色免伤的那一行代码在 Encounter.cs 里。
//   这是团队约定：全项目只有一个地方能写无敌开关，避免到处都能免伤、查 bug 查到崩溃。
// =============================================================================
// -----------------------------------------------------------------------------

namespace Xianxia.Combat
{
    /// <summary>
    /// 闪避动作的帧数据与位移曲线。全部为静态纯函数，无状态。
    ///
    /// 【新手解释】"static（静态）"表示这个类不需要 new 出实例，直接
    /// <c>DodgeAction.VelocityAtFrame(...)</c> 这样调用就行；
    /// "纯函数"表示同样的输入永远得到同样的输出，不记录任何东西，
    /// 所以它绝不可能成为 bug 的藏身处。
    /// </summary>
    public static class DodgeAction
    {
        /// <summary>
        /// 闪避的帧数据：前摇 2 / 判定 12 / 后摇 1 = 共 15 帧 = 0.25s（PRD 建议 0.24s）。
        /// i-frame 自第 3 帧起持续 12 帧 = <b>0.20s</b>，精确命中 PRD。
        /// 取消窗 12：翻滚尾段可被下一个动作接上，不产生"滚完僵直半秒"的粘手感。
        /// </summary>
        public static FrameData Frames
        {
            get
            {
                return new FrameData(
                    SkillConfig.DODGE_STARTUP_FRAMES,
                    SkillConfig.DODGE_ACTIVE_FRAMES,
                    SkillConfig.DODGE_RECOVERY_FRAMES,
                    SkillConfig.DODGE_CANCEL_FROM,
                    SkillConfig.DODGE_IFRAME_START,
                    SkillConfig.DODGE_IFRAME_LEN);
            }
        }

        /// <summary>
        /// 指定帧是否落在 i-frame 窗口内。等价于
        /// <c>ActionState.IsIframeActive</c>，独立出来是为了能脱离 ActionState 单测。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"翻滚演到第 cursor 帧时，这一帧挨打会不会掉血？"
        /// 按默认配置：第 3 帧到第 14 帧（共 12 帧 = 0.2 秒）是无敌的。
        /// 注意开头 2 帧和结尾 1 帧**不无敌** —— 这是故意的，
        /// 逼玩家提前预判、而不是被打中的瞬间才按翻滚。
        /// </remarks>
        /// <param name="cursor">帧游标（1 基，即第几帧，从 1 开始数）。</param>
        /// <param name="frames">帧数据。</param>
        /// <returns>true = 该帧无敌。</returns>
        public static bool IsIframeFrame(int cursor, FrameData frames)
        {
            if (frames.IframeLen <= 0)
            {
                return false;
            }
            return cursor >= frames.IframeStart && cursor < frames.IframeStart + frames.IframeLen;
        }

        /// <summary>
        /// 位移权重（ease-out 线性衰减）。第 1 帧最大，最后一帧最小。
        /// </summary>
        /// <param name="cursor">帧游标（1 基）。</param>
        /// <param name="total">动作总帧数。</param>
        /// <returns>无量纲权重，&gt; 0。</returns>
        public static float Weight(int cursor, int total)
        {
            if (total <= 0 || cursor < 1 || cursor > total)
            {
                return 0.0f;
            }
            return 1.0f - (float)(cursor - 1) / total;
        }

        /// <summary>
        /// 权重之和的闭式解：<c>Σ(k=1..N) [1 − (k−1)/N] = N − (N−1)/2</c>。
        /// 用闭式解而不是循环累加，是为了让"总位移 == 130px"成为代数恒等式而非近似。
        /// </summary>
        /// <param name="total">动作总帧数。</param>
        /// <returns>权重和。</returns>
        public static float WeightSum(int total)
        {
            if (total <= 0)
            {
                return 0.0f;
            }
            return total - (total - 1) * 0.5f;
        }

        /// <summary>
        /// 本帧的位移速度意图（像素/秒）。逐帧积分的总位移恰为
        /// <see cref="SkillConfig.DODGE_DISTANCE"/>。
        /// </summary>
        /// <remarks>
        /// 【新手解释】"翻滚的第 cursor 帧，人应该以多快的速度朝哪儿窜？"
        /// 什么时候被调用：Unity 侧的 DodgeController 每一帧都会问一次，
        /// 拿到速度后接管玩家移动（翻滚期间你按方向键是没用的，这也是故意的）。
        /// "归一化"是指把方向向量的长度统一变成 1，这样朝斜方向翻滚
        /// 才不会比朝正方向翻得更远（这是新手最常见的 8 方向移动 bug）。
        /// </remarks>
        /// <param name="cursor">帧游标（1 基）。超出动作范围时返回零向量。</param>
        /// <param name="frames">帧数据。</param>
        /// <param name="dir">闪避方向（内部归一化；零向量返回零速度）。</param>
        /// <returns>速度向量（像素/秒）。</returns>
        public static Vec2 VelocityAtFrame(int cursor, FrameData frames, Vec2 dir)
        {
            return VelocityAtFrame(cursor, frames, dir, SkillConfig.DODGE_DISTANCE);
        }

        /// <summary>
        /// 本帧的位移速度意图（像素/秒），可指定总位移。
        /// </summary>
        /// <param name="cursor">帧游标（1 基）。</param>
        /// <param name="frames">帧数据。</param>
        /// <param name="dir">闪避方向（内部归一化）。</param>
        /// <param name="distance">整段动作的总位移（像素）。</param>
        /// <returns>速度向量（像素/秒）。</returns>
        public static Vec2 VelocityAtFrame(int cursor, FrameData frames, Vec2 dir, float distance)
        {
            if (dir.IsZero() || distance <= 0.0f)
            {
                return Vec2.Zero;
            }

            int total = frames.Total;
            float w = Weight(cursor, total);
            if (w <= 0.0f)
            {
                return Vec2.Zero;
            }

            float sum = WeightSum(total);
            if (sum <= 0.0f)
            {
                return Vec2.Zero;
            }

            // v(k) = distance * w(k) / (Σw * FixedStep)
            // → Σ v(k) * FixedStep = distance（精确，不依赖标定）
            float speed = distance * w / (sum * CombatScheduler.FixedStep);
            return dir.Normalized() * speed;
        }

        /// <summary>
        /// 整段动作的位移总和（自校验用）。理论上恒等于
        /// <paramref name="distance"/>，单测用它断言曲线实现没写错。
        /// </summary>
        /// <param name="frames">帧数据。</param>
        /// <param name="distance">期望总位移。</param>
        /// <returns>逐帧积分得到的实际总位移。</returns>
        public static float IntegrateDistance(FrameData frames, float distance)
        {
            float acc = 0.0f;
            int total = frames.Total;
            for (int k = 1; k <= total; k++)
            {
                Vec2 v = VelocityAtFrame(k, frames, Vec2.Right, distance);
                acc += v.Length() * CombatScheduler.FixedStep;
            }
            return acc;
        }
    }
}
