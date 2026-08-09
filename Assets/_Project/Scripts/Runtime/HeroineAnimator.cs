// -----------------------------------------------------------------------------
// HeroineAnimator.cs —— 女主水墨精灵的帧动画驱动（asmdef: Xianxia.Unity.T2）
//
// 【核心设计：纯轮询，零侵入】
// 本组件**只读**四个战斗层组件的 public 属性，不订阅事件、不注册回调、
// 不往它们身上写任何东西。因此 PlayerController / AttackController /
// DodgeController / CombatBridge **一行都不用改**。
//
//   Death → CombatBridge.PlayerHp <= 0（锁死，永不回落）
//   Dodge → DodgeController.IsDodging（连续态）
//   Hurt  → CombatBridge.PlayerHp 的下降沿
//   Attack→ AttackController.SwingCount 的增长沿
//   Walk* → PlayerController.IsMoving / LastFacing
//   Idle  → 兜底
//
// 代价是至多 1 帧延迟（60fps 下 16ms，而动画最快才 12fps，根本感知不到）；
// 收益是内核红线与战斗层零改动风险，且全部约束都能靠 grep 静态自查。
//
// 【暂停】只读 CombatBridge.IsGameplayBlocked / IsRunOver，二者都是既有 public 属性。
//   注意闸门语义与 PlayerController **刻意不同**：PlayerController 终局要停止操控，
//   本组件终局却必须继续推进 —— 否则死亡动画一帧都播不出来。故只在
//   「菜单暂停」(IsGameplayBlocked && !IsRunOver) 时 return。
// ★红线（本文件受 grep 静态巡检，以下标识符一个都不许出现）：
//   · 不得触碰全局时间缩放，也不得改用「不受缩放影响的帧间隔」那个 API
//     —— 暂停靠上面那道闸门 return，不靠改时间。
//   · 不得引用战斗内核的调度器 / 回合阶段类型，更不许写它们的暂停位
//     （CombatBridge 里那一处是全工程唯一合法写点）。
//   表现层只观察，不参与内核推进。
//
// 【失败时】Awake 预热不到 idle 首帧 ⇒ IsReady = false，组件自我 disable，
//         由 WorldBuilder 销毁本组件并保留蓝方块占位，游戏照常跑。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 按战斗状态驱动女主 39 帧精灵的动画机。执行序 -50：
    /// 晚于 PlayerController(-100) 与 DodgeController(-90)（保证读到的是本帧最新状态），
    /// 早于默认组件(0)。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class HeroineAnimator : MonoBehaviour
    {
        /// <summary>血量下降沿的判定阈值。浮点血量直接比大小会被舍入噪声误触发。</summary>
        private const float HpEpsilon = 0.01f;

        /// <summary>单帧最大推进时间。防止暂停/断点后的一个超大 deltaTime 把动画快进一大段。</summary>
        private const float MaxStepSeconds = 0.1f;

        /// <summary>CombatBridge 未就绪时的重查间隔（秒）。FindFirstObjectByType 很贵，不能每帧调。</summary>
        private const float BridgeRetryInterval = 0.5f;

        // ── 组件引用（Awake 缓存；对它们只读属性，绝不赋值）────────────────────
        private SpriteRenderer _sr;
        private PlayerController _pc;
        private AttackController _atk;
        private DodgeController _dodge;
        private CombatBridge _bridge;

        // ── 播放状态 ──────────────────────────────────────────────────────────
        private AnimState _state;
        private int _frame;
        private float _clock;
        private float _oneShotRemain;

        // ── 边沿检测 ──────────────────────────────────────────────────────────
        private int _lastSwingCount;
        private float _lastHp;
        private bool _deathLatched;

        /// <summary>
        /// 是否已经见过一次正血量。
        /// <para>CombatBridge.PlayerHp 在内核尚未初始化时返回 0.0f —— 若不设这道闸，
        /// 角色会在出生的第一帧就被判定为死亡并锁死在 death 末帧。</para>
        /// </summary>
        private bool _hpArmed;

        private bool _forceRestart;
        private float _bridgeRetry;

        /// <summary>39 帧是否可用。false 表示资源缺失，调用方应销毁本组件并保留程序化占位。</summary>
        public bool IsReady { get; private set; }

        /// <summary>当前动画状态。</summary>
        public AnimState State
        {
            get { return _state; }
        }

        /// <summary>当前帧索引（0 起）。</summary>
        public int Frame
        {
            get { return _frame; }
        }

        private void Awake()
        {
            _sr = GetComponent<SpriteRenderer>();
            _pc = GetComponent<PlayerController>();
            _atk = GetComponent<AttackController>();
            _dodge = GetComponent<DodgeController>();
            ResolveBridge(0.0f);

            _state = AnimState.Idle;
            _frame = 0;
            _clock = 0.0f;
            _oneShotRemain = 0.0f;
            _deathLatched = false;
            _forceRestart = false;

            // 边沿基线：从当前值起算，避免把"出生时已经存在的值"误当成一次新事件。
            _lastSwingCount = _atk != null ? _atk.SwingCount : 0;
            _lastHp = 0.0f;
            _hpArmed = false;

            IsReady = Warmup();
            if (!IsReady)
            {
                // 资源不齐就别每帧空转。WorldBuilder 随后会把本组件整个销毁。
                enabled = false;
                return;
            }

            Apply();   // 立刻出图，避免第一帧还是蓝方块造成闪烁。
        }

        private void Update()
        {
            if (!IsReady)
            {
                return;
            }

            // 吃表现层时钟：hitstop 期间主角的动画帧必须停住。
            // 角色是顿帧的主角——它要是继续挥手，其他东西冻得再齐也没用。
            //
            // MaxStepSeconds 的钳制保留不动：它防的是「Editor 断点 / 场景加载后
            // 第一帧 dt 巨大导致动画一次跳好几帧」，与顿帧是两码事。
            // 顿帧时 Delta 为 0，钳制自然不触发，两者不冲突。
            float dt = FeedbackClock.Delta;
            if (dt > MaxStepSeconds)
            {
                dt = MaxStepSeconds;
            }

            // ★暂停闸门：只冻结「菜单暂停」，放行「终局」。
            //
            // 【为什么这里传 Time.deltaTime 而不是上面钳过的 dt】
            // ResolveBridge 的 dt 只用来给 FindObjectByType 的重试做节流，属于
            // **记账**，不属于表现。顿帧期间 dt 恒为 0，节流计时就永远走不完，
            // 万一 hitstop 卡住（哪怕只是调试时手动置了 Frozen），惰性解析会一起
            // 停摆，暂停闸门就再也接不上了。记账类计时一律吃真实时间。
            CombatBridge b = ResolveBridge(Time.deltaTime);
            if (b != null)
            {
                // IsGameplayBlocked = IsRunOver || _menuPaused。
                // 菜单暂停 → 真冻结；终局（死亡/通关）→ 必须放行，
                // 否则 Evaluate() 永不执行，死亡动画一帧都播不出来。
                // 死亡后 _deathLatched 会把状态锁在 Death，Advance 把帧钳在末帧，
                // 自然静止，不需额外冻结。
                bool menuPaused = b.IsGameplayBlocked && !b.IsRunOver;
                if (menuPaused)
                {
                    return;
                }
            }

            if (_oneShotRemain > 0.0f)
            {
                _oneShotRemain -= dt;
            }

            AnimState next = Evaluate();
            SetState(next, _forceRestart);
            _forceRestart = false;

            Advance(dt);
            Apply();
        }

        /// <summary>
        /// 强制切换状态。**调试 / 单元测试用，生产路径不调用**（生产走 Evaluate 轮询）。
        /// </summary>
        /// <param name="st">目标状态。</param>
        /// <param name="force">为 true 时即使状态相同也从第 0 帧重播（连续两次挥剑需要它）。</param>
        public void SetState(AnimState st, bool force = false)
        {
            if (!force && st == _state)
            {
                return;
            }

            _state = st;
            _frame = 0;
            _clock = 0.0f;

            PoseDef pose = HeroineFrames.Get(st);
            _oneShotRemain = pose.Loop ? 0.0f : pose.Duration;

            if (st == AnimState.Death)
            {
                _deathLatched = true;
            }
        }

        /// <summary>当前帧对应的 Sprite（命中缓存，O(1)）。资源缺失时为 null。</summary>
        public Sprite CurrentSprite()
        {
            PoseDef pose = HeroineFrames.Get(_state);
            return SpriteFactory.TryLoadPng(pose.PathOf(_frame), pose.FrameW, pose.FrameH,
                                            pose.Pivot, Color.white);
        }

        // ---------------------------------------------------------------------
        // 内部
        // ---------------------------------------------------------------------

        /// <summary>
        /// 预热全部 39 帧到 SpriteFactory 缓存。
        /// <para>预热而非懒加载：否则玩家第一次挥剑 / 死亡时才去读盘，会卡在最不该卡的瞬间。</para>
        /// </summary>
        /// <returns>idle 首帧可用则 true。</returns>
        private bool Warmup()
        {
            int loaded = 0;
            for (int i = 0; i < HeroineFrames.PoseCount; i++)
            {
                PoseDef pose = HeroineFrames.Get((AnimState)i);
                loaded += SpriteFactory.PreloadPngSequence(
                    HeroineFrames.RootDir, pose.FilePrefix, pose.FrameCount,
                    pose.FrameW, pose.FrameH, pose.Pivot, Color.white);
            }

            PoseDef idle = HeroineFrames.Get(AnimState.Idle);
            bool ready = SpriteFactory.TryLoadPng(idle.PathOf(0), idle.FrameW, idle.FrameH,
                                                  idle.Pivot, Color.white) != null;

            if (!ready)
            {
                Debug.LogWarning("[HeroineAnimator] 女主精灵帧不可用（"
                                 + HeroineFrames.RootDir + " 下读不到 idle 首帧），"
                                 + "已回退到程序化蓝方块占位。若非预期，请确认 39 张 PNG 位于 "
                                 + "Assets/StreamingAssets/" + HeroineFrames.RootDir + "/。");
            }
            else if (loaded != HeroineFrames.TotalFrames)
            {
                Debug.LogWarning("[HeroineAnimator] 只加载到 " + loaded + " / "
                                 + HeroineFrames.TotalFrames + " 帧，部分动作会停在可用的最后一帧。");
            }

            return ready;
        }

        /// <summary>
        /// 按优先级裁决本帧应处的状态：Death &gt; Dodge &gt; Hurt &gt; Attack &gt; Walk* &gt; Idle。
        /// </summary>
        private AnimState Evaluate()
        {
            if (_deathLatched)
            {
                return AnimState.Death;   // 终态，短路，不再采样任何输入。
            }

            // 【先采样全部边沿，再裁决优先级】
            // 顺序很重要：若把计数器同步写在优先级分支内部，被高优先级状态"跳过"的
            // 那次挥剑会残留在计数器里，等状态回落后凭空补放一次攻击动画。
            bool hurtEdge = false;
            bool dead = false;
            if (_bridge != null)
            {
                float hp = _bridge.PlayerHp;
                if (!_hpArmed)
                {
                    // 见到第一次正血量才开始做边沿检测（见 _hpArmed 注释）。
                    if (hp > 0.0f)
                    {
                        _hpArmed = true;
                        _lastHp = hp;
                    }
                }
                else if (hp <= 0.0f)
                {
                    dead = true;
                    _lastHp = hp;
                }
                else
                {
                    hurtEdge = hp < _lastHp - HpEpsilon;
                    _lastHp = hp;
                }
            }

            bool attackEdge = false;
            if (_atk != null)
            {
                int swings = _atk.SwingCount;
                attackEdge = swings > _lastSwingCount;
                _lastSwingCount = swings;
            }

            bool dodging = _dodge != null && _dodge.IsDodging;

            // 裁决
            if (dead)
            {
                _deathLatched = true;
                _forceRestart = true;
                return AnimState.Death;
            }
            if (dodging)
            {
                return AnimState.Dodge;
            }
            if (hurtEdge)
            {
                _forceRestart = true;   // 连续挨打要能重播，而不是卡在上一次的末帧
                return AnimState.Hurt;
            }
            if (attackEdge)
            {
                _forceRestart = true;   // 连续两刀必须从第 0 帧重播
                return AnimState.Attack;
            }
            if (_oneShotRemain > 0.0f)
            {
                return _state;          // 一次性动作还没播完，保持
            }

            bool moving = _pc != null && _pc.IsMoving;
            Vector2 facing = _pc != null ? _pc.LastFacing : Vector2.right;
            return HeroineFrames.FromMovement(facing, moving);
        }

        /// <summary>按当前 pose 的独立帧率推进帧号。循环取模，一次性钳到末帧。</summary>
        private void Advance(float dt)
        {
            PoseDef pose = HeroineFrames.Get(_state);
            _clock += dt;

            float step = 1.0f / pose.Fps;
            while (_clock >= step)
            {
                _clock -= step;
                _frame++;
            }

            if (pose.Loop)
            {
                _frame %= pose.FrameCount;
            }
            else if (_frame >= pose.FrameCount)
            {
                _frame = pose.FrameCount - 1;   // Death 就是靠这句停在末帧
            }
        }

        /// <summary>把当前帧写进 SpriteRenderer。</summary>
        private void Apply()
        {
            if (_sr == null)
            {
                return;
            }

            PoseDef pose = HeroineFrames.Get(_state);
            Sprite s = SpriteFactory.TryLoadPng(pose.PathOf(_frame), pose.FrameW, pose.FrameH,
                                                pose.Pivot, Color.white);
            if (s != null)
            {
                _sr.sprite = s;
            }
            // s == null（个别帧缺失）时刻意**保留上一帧**，
            // 让角色定格总好过闪成空白 —— SpriteFactory 已经告过警了。

            Vector2 facing = _pc != null ? _pc.LastFacing : Vector2.right;
            _sr.flipX = HeroineFrames.ShouldFlipX(_state, facing);
        }

        /// <summary>
        /// 惰性解析 CombatBridge。玩家对象可能先于 CombatBridge 建好，
        /// 所以 Awake 拿不到时要能在后续帧补上；但 FindFirstObjectByType 会遍历整个场景，
        /// 绝不能每帧调，故加 <see cref="BridgeRetryInterval"/> 节流。
        /// </summary>
        private CombatBridge ResolveBridge(float dt)
        {
            if (_bridge != null)
            {
                return _bridge;
            }

            _bridgeRetry -= dt;
            if (_bridgeRetry > 0.0f)
            {
                return null;
            }
            _bridgeRetry = BridgeRetryInterval;

#if UNITY_2023_1_OR_NEWER
            _bridge = Object.FindFirstObjectByType<CombatBridge>();
#else
            _bridge = Object.FindObjectOfType<CombatBridge>();
#endif
            return _bridge;
        }
    }
}
