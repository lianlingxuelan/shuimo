// -----------------------------------------------------------------------------
// WCore.cs —— W-CORE 承伤模型（引擎无关，1:1 移植自 godot/scripts/player.gd）
//
// 【这套模型解决什么问题】
// 旧模型命中后置一个全局无敌帧（iframe = 0.5s），后果是：被 1 只怪打和被 8 只怪
// 围攻，玩家掉血速度完全一样（恒 1.00x）。「围攻」于是彻底失去威胁，玩家学会了
// 无脑往怪堆里钻。W-CORE 拆成两层闸门：
//   · 每来源冷却 PER_SOURCE_HIT_CD = 0.6s —— 单只怪打不出连击，保证单挑节奏
//   · 全局最小间隔 GLOBAL_HIT_GAP = 0.2167s —— 承伤 DPS 的硬天花板，防止秒杀
// 两层叠加后，围攻/单挑承伤比 = 2.53x：怪多确实更痛，但痛得有上限。
//
// 【时间驱动，不是帧驱动】
// 闸门递减用真实秒数（Tick(dt)），因此 30/60/144/240 fps 下闸门的物理时长一致。
// 但「敌人发起攻击尝试」必须走 FixedStepAccumulator 的 1/60 固定步长——理由见
// 该类的注释。二者配合才能在任意帧率下复现 Godot 60Hz 的量化节奏。
//
// 【禁止事项】不得引用 UnityEngine。必须能被 dotnet test 独立编译。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Xianxia.Core
{
    /// <summary>
    /// W-CORE 承伤状态机。一个玩家一份，切区 / 重生时调 <see cref="Reset"/>。
    /// </summary>
    public sealed class WCoreState
    {
        // --- 常量：与 godot/scripts/game_config.gd:134-143 逐值对齐，改动即破坏手感 ---

        /// <summary>同一伤害来源的再次命中冷却（秒）。</summary>
        public const float PerSourceHitCd = 0.6f;

        /// <summary>任意来源之间的全局最小命中间隔（秒）。承伤 DPS 天花板。</summary>
        public const float GlobalHitGap = 0.2167f;

        /// <summary>来源冷却表的软上限条数，超出即裁剪。</summary>
        public const int HitCdMapSoftCap = 32;

        /// <summary>闪避成功后的短无敌时长（秒）。</summary>
        public const float DodgeIframe = 0.25f;

        /// <summary>
        /// 闸门归零判定的容差。
        ///
        /// 【为什么需要它】
        /// 0.6s ÷ (1/60) 恰好等于 36 步，浮点累减 36 次后结果可能落在 ±1e-7 附近。
        /// 若刚好停在 +1e-17，闸门就要多等一帧，单挑频率从 1.700 掉到 1.650 —— 这是
        /// 一个只在某些平台 / 某些编译器优化下复现的「薛定谔手感 bug」。
        /// 容差取 1e-6：远大于累积误差（~1e-7），又远小于最近的真实边界
        /// （0.2167 − 13/60 = 3.33e-5），不会把 14 帧量化误判成 13 帧。
        /// </summary>
        private const float GateEpsilon = 1e-6f;

        // --- 状态 ---

        /// <summary>
        /// 闪避残留的短无敌剩余秒数。
        /// 注意：普通命中**不写它**，只有闪避成功才写——这正是与旧模型的分水岭。
        /// </summary>
        public float Iframe;

        /// <summary>当前血量。</summary>
        public float Hp;

        /// <summary>血量上限。</summary>
        public float HpMax = 1.0f;

        /// <summary>
        /// 闪避开关。对拍 Godot 频率基线时必须为 false（默认关），
        /// 否则随机闪避会污染「纯承伤频率」的统计。
        /// </summary>
        public bool DodgeEnabled;

        /// <summary>
        /// 闪避骰。<see cref="DodgeEnabled"/> 为 true 且此委托非空时才生效。
        /// 抽成委托是为了让 Core 层不依赖 PlayerStats —— 数值公式归数值层。
        /// </summary>
        public Func<bool> DodgeRoll;

        /// <summary>
        /// 减伤过滤器（raw ⇒ 实扣）。null 表示原样扣血（对拍用）。
        /// 生产环境接 <see cref="Difficulty.DamageTaken"/>。
        /// </summary>
        public Func<float, float> DamageFilter;

        /// <summary>血量归零时触发。是否真的判死由上层（战斗区 / Hub / QA）决定。</summary>
        public event Action Died;

        /// <summary>instance_id ⇒ 剩余冷却秒数。归零即移除，见 <see cref="Tick"/>。</summary>
        private readonly Dictionary<int, float> _sourceCd = new Dictionary<int, float>(64);

        /// <summary>全局最小间隔剩余秒数。</summary>
        private float _globalGap;

        /// <summary>遍历时的 key 快照缓冲，复用以避免每帧分配。</summary>
        private readonly List<int> _keyBuf = new List<int>(64);

        /// <summary>裁剪时的排序缓冲，复用以避免 GC。</summary>
        private readonly List<KeyValuePair<int, float>> _trimBuf = new List<KeyValuePair<int, float>>(256);

        /// <summary>当前处于冷却中的来源数。C8 软上限压测断言用。</summary>
        public int SourceCdCount
        {
            get { return _sourceCd.Count; }
        }

        /// <summary>全局最小间隔剩余秒数（只读，调试 / 测试用）。</summary>
        public float GlobalGapRemaining
        {
            get { return _globalGap; }
        }

        /// <summary>查询某来源的剩余冷却，无记录返回 0。</summary>
        public float SourceCdRemaining(int sourceId)
        {
            float v;
            return _sourceCd.TryGetValue(sourceId, out v) ? v : 0.0f;
        }

        // ---------------------------------------------------------------------
        // 时间推进
        // ---------------------------------------------------------------------

        /// <summary>
        /// 推进两层闸门的倒计时。dt 单位为秒。
        ///
        /// 【为什么归零就移除而不是留着置 0】
        /// 留空条目会让冷却表随「本局见过的敌人总数」单调增长——刷 200 只怪就有
        /// 200 条常驻垃圾。归零即删之后，表的规模恒等于「最近 0.6s 内真正打到我
        /// 的来源数」，天然是个小常数。
        ///
        /// 【为什么先收集 key 再改】
        /// 迭代中修改 Dictionary 在部分运行时会抛异常（Unity 的 Mono 尤其）。
        /// 复用 _keyBuf 做快照，既安全又零分配。
        /// </summary>
        public void Tick(float dt)
        {
            if (dt <= 0.0f)
            {
                return;
            }

            if (Iframe > 0.0f)
            {
                Iframe = Iframe - dt;
                if (Iframe <= GateEpsilon)
                {
                    Iframe = 0.0f;
                }
            }

            if (_globalGap > 0.0f)
            {
                _globalGap = _globalGap - dt;
                if (_globalGap <= GateEpsilon)
                {
                    _globalGap = 0.0f;
                }
            }

            if (_sourceCd.Count == 0)
            {
                return;
            }

            _keyBuf.Clear();
            foreach (int sid in _sourceCd.Keys)
            {
                _keyBuf.Add(sid);
            }

            for (int i = 0; i < _keyBuf.Count; i++)
            {
                int sid = _keyBuf[i];
                float left = _sourceCd[sid] - dt;
                if (left <= GateEpsilon)
                {
                    _sourceCd.Remove(sid);
                }
                else
                {
                    _sourceCd[sid] = left;
                }
            }
        }

        // ---------------------------------------------------------------------
        // 承伤主入口
        // ---------------------------------------------------------------------

        /// <summary>
        /// 承伤结算。返回 true 表示本次**真正生效**（扣了血或触发闪避），
        /// false 表示被闸门拦下、状态完全没变——调用方可据此决定要不要放特效 / 音效。
        /// </summary>
        /// <param name="sourceId">
        /// 伤害来源实例 id。0 = 无源伤害（陷阱 / 脚本），走独立的 0 号槽位，
        /// 同样受两层闸门约束，不会绕过天花板。
        /// </param>
        /// <param name="dmg">原始伤害。减免交给 <see cref="DamageFilter"/>。</param>
        /// <remarks>
        /// 判定顺序：闪避无敌 → 全局最小间隔 → 该来源冷却 → 落闸 → 闪避骰 → 扣血。
        /// 闪避骰**放在落闸之后**：被闸门拦下的那一击根本不存在，不该消耗闪避的随机
        /// 序列，否则同样的 build 在不同怪量下闪避表现会漂移，回归测试无法复现。
        /// </remarks>
        public bool TakeDamageFrom(int sourceId, float dmg)
        {
            // ① 闪避残留的短无敌：全局生效，这是唯一还会读 Iframe 的承伤路径。
            if (Iframe > 0.0f)
            {
                return false;
            }

            // ② 全局最小间隔：承伤 DPS 的硬天花板。
            if (_globalGap > 0.0f)
            {
                return false;
            }

            // ③ 该来源自己的冷却。
            float cd;
            if (_sourceCd.TryGetValue(sourceId, out cd) && cd > 0.0f)
            {
                return false;
            }

            // ④ 落闸。闪避同样要占用两层闸门，否则「闪避成功」会变成一次免费的重置
            //    机会，让下一只怪立刻补刀，反而比不闪避更亏。
            ArmHitGates(sourceId);

            if (DodgeEnabled && DodgeRoll != null && DodgeRoll())
            {
                Iframe = DodgeIframe;
                return true;
            }

            float applied = DamageFilter != null ? DamageFilter(dmg) : dmg;
            Hp -= applied;
            if (Hp <= 0.0f)
            {
                Hp = 0.0f;
                Action handler = Died;
                if (handler != null)
                {
                    handler();
                }
            }
            return true;
        }

        /// <summary>无源伤害的薄壳（陷阱 / 脚本）。新代码一律用 <see cref="TakeDamageFrom"/>。</summary>
        public bool TakeDamage(float dmg)
        {
            return TakeDamageFrom(0, dmg);
        }

        // ---------------------------------------------------------------------
        // 闸门维护
        // ---------------------------------------------------------------------

        /// <summary>落闸：写入该来源的冷却与全局最小间隔。</summary>
        private void ArmHitGates(int sourceId)
        {
            _globalGap = GlobalHitGap;
            _sourceCd[sourceId] = PerSourceHitCd;

            // 软上限兜底。正常路径下表的规模不可能涨到这里（归零即删），
            // 真到了就说明有来源在异常高频写入——丢弃最旧的一批，保内存不保精确。
            if (_sourceCd.Count > HitCdMapSoftCap)
            {
                TrimHitCds();
            }
        }

        /// <summary>
        /// 裁剪：保留剩余冷却最长的 <see cref="HitCdMapSoftCap"/> 条，其余丢弃。
        /// 剩余时间越短的条目本来就快到期，丢掉它们造成的判定偏差最小。
        ///
        /// 【与 Godot 的一处刻意改进】
        /// GDScript 的 sort_custom 在「冷却值全部相等」（压测时 200 条都是 0.6）时
        /// 保留哪 32 条是未定义的。这里追加 sourceId 升序作为次级键，让裁剪结果
        /// 完全确定 —— 未定义行为在回归测试里就是定时炸弹。
        /// </summary>
        private void TrimHitCds()
        {
            _trimBuf.Clear();
            foreach (KeyValuePair<int, float> kv in _sourceCd)
            {
                _trimBuf.Add(kv);
            }

            _trimBuf.Sort(delegate (KeyValuePair<int, float> a, KeyValuePair<int, float> b)
            {
                int byCd = b.Value.CompareTo(a.Value);   // 剩余冷却降序
                return byCd != 0 ? byCd : a.Key.CompareTo(b.Key);
            });

            for (int i = HitCdMapSoftCap; i < _trimBuf.Count; i++)
            {
                _sourceCd.Remove(_trimBuf[i].Key);
            }
        }

        /// <summary>
        /// 清空所有承伤冷却（切区 / 重生 / 重置时调用）。
        ///
        /// 【为什么必须清】
        /// key 是实例 id，会被引擎回收复用。旧区敌人销毁后，新区生成的敌人完全可能
        /// 拿到同一个 id；若那条旧冷却还剩 0.4s 没走完，新怪的头一击就会被静默吞掉，
        /// 表现为「新区怪打不动我」这种极难复现的偶发 bug。
        /// </summary>
        public void Reset()
        {
            _sourceCd.Clear();
            _globalGap = 0.0f;
            Iframe = 0.0f;
        }

        /// <summary>满血复位 + 清闸门（Hub 回满用）。</summary>
        public void FullRestore()
        {
            Hp = HpMax;
            Reset();
        }

        /// <summary>
        /// 【仅供 QA / 回归测试】绕过两层闸门直接落闸。
        ///
        /// C8 软上限压测要求「强灌 200 个来源且中途不 tick」，而正常路径下全局闸门
        /// 会把第 2 次之后的写入全部拦掉，冷却表永远涨不到 32 条以上，裁剪分支就
        /// 测不到。这个后门只为触达 <see cref="TrimHitCds"/>，游戏逻辑里禁止调用。
        /// </summary>
        public void QaForceArmGates(int sourceId)
        {
            ArmHitGates(sourceId);
        }
    }

    /// <summary>
    /// 固定逻辑步长累加器：把任意帧率的 deltaTime 切成整数个 1/60 秒逻辑步。
    ///
    /// 【为什么攻击尝试必须走固定步长】
    /// GLOBAL_HIT_GAP = 0.2167s。在 60Hz 下它需要 ceil(0.2167 / (1/60)) = 14 步才
    /// 放行，实际间隔 14/60 = 0.2333s ⇒ 4.286 次/s（相对理想 4.615 有 7.1% 的
    /// 「帧量化损耗」）。Godot 原型的 2.53x 围攻倍率正是含这份损耗的实测值。
    /// 若在 144Hz 下让敌人每帧都尝试攻击，量化损耗会缩到 ~1%，围攻倍率飙到 2.7x
    /// 以上 —— 同一份数值在高刷屏上明显更难。把攻击尝试锁在 1/60 网格上，
    /// 手感就与帧率彻底解耦，且不必去动引擎的 Fixed Timestep（那会影响物理）。
    /// </summary>
    public sealed class FixedStepAccumulator
    {
        /// <summary>
        /// 固定逻辑步长 = 1/60 秒。
        ///
        /// 【绝对不要写成字面量 0.01667f】
        /// 13 × 0.01667 = 0.21671 &gt; 0.2167，全局闸门会在**第 13 步**就放行，
        /// 围攻频率从 4.300 次/s 跳到 4.650 次/s，2.53x 倍率直接变成 2.74x。
        /// 而 13 × (1/60) = 0.216667 &lt; 0.2167，必须等到第 14 步——这才是基线。
        /// 一个 3e-5 的常量写法差异，足以让整套手感验收失败。
        /// </summary>
        public const float FixedStep = 1.0f / 60.0f;

        /// <summary>
        /// 单次 Advance 允许追赶的最大步数。防止长卡顿后一口气补几百步，
        /// 那会让玩家在一瞬间被扣掉全部血量（俗称「死亡螺旋」）。
        /// </summary>
        public const int MaxStepsPerAdvance = 8;

        private float _acc;

        /// <summary>累加器余量（秒），用于插值渲染或调试。</summary>
        public float Remainder
        {
            get { return _acc; }
        }

        /// <summary>清空累加器（切场景 / 暂停恢复时调用，避免补步爆发）。</summary>
        public void Clear()
        {
            _acc = 0.0f;
        }

        /// <summary>
        /// 累积真实经过时间，并对每个满额的逻辑步调用 onStep(FixedStep)。
        /// 返回本次实际执行的步数。
        /// </summary>
        /// <remarks>
        /// 标准接法（推荐）：在回调里**先**推进 W-CORE 闸门、**再**让敌人尝试攻击，
        /// 且闸门也用 FixedStep 作为 dt。这样整条链路完全落在 1/60 网格上，
        /// 30 / 60 / 144 / 240 fps 的承伤序列逐次相同。
        /// <code>
        /// _acc.Advance(deltaTime, dt =&gt; {
        ///     _wcore.Tick(dt);
        ///     foreach (var e in enemies) if (e.InRange) _wcore.TakeDamageFrom(e.Id, e.Atk);
        /// });
        /// </code>
        /// </remarks>
        public int Advance(float deltaTime, Action<float> onStep)
        {
            if (deltaTime > 0.0f)
            {
                _acc += deltaTime;
            }

            int steps = 0;
            while (_acc >= FixedStep)
            {
                _acc -= FixedStep;
                steps++;
                if (onStep != null)
                {
                    onStep(FixedStep);
                }
                if (steps >= MaxStepsPerAdvance)
                {
                    // 追不上了，丢弃剩余余量而不是继续补——宁可慢放也不要瞬杀。
                    _acc = 0.0f;
                    break;
                }
            }
            return steps;
        }
    }
}
