// -----------------------------------------------------------------------------
// CameraShake.cs —— 叠加在 CameraFollow 之上的屏震（asmdef: Xianxia.Unity.T2）
//
// ⚠️【本文件未在当前环境验证】
// 交付环境没有 dotnet / Unity Editor，本文件**未经编译**。引用 UnityEngine，
// 不参与 Python 对拍。请在本地 Unity 工程打开后确认编译通过。
//
// 【明确不要什么：在 CameraFollow.LateUpdate 里直接加一个 offset】
// 这是最容易想到、也是三头堵死的写法：
//   (a) 抖动会混进下一帧 SmoothDamp 的起点，污染 _velocity —— 跟随本身开始抖，
//       屏震停下后镜头还会自己荡几下，留下累计漂移（R-02 零分项）；
//   (b) 偏移加在 ClampToBounds **之前**，会被随后的钳制吃掉，等于没抖；
//       加在**之后**，会把画面推出世界边界露出黑边（A-6 一票否决）；
//   (c) 跟随和屏震两个关注点糊进同一个 40 行方法，将来加方向性屏震（R-11）
//       无处下手。
//
// 【明确不要什么：Random.insideUnitCircle 逐帧随机】
// 白噪声会出现连续两帧同向的情况，看起来不像"抖"，像镜头在**漂**。而且它没有
// 任何机制保证结束时回到原点，只能靠额外写一句"归零"去补，一旦哪条分支漏了
// 就是永久偏移。
//
// 【明确不要什么：抖 orthographicSize】
// 那是缩放不是位移，观感是"呼吸 / 心跳"，不是"被砸了一下"；而且它会连带改变
// ClampToBounds 的合法范围，边界逻辑立刻失稳。
//
// 【要什么：两路异相正弦 × 线性衰减，数学上保证回零】
// offset = (sin(w)·A·k, sin(w·1.37 + 1.7)·A·0.72·k)，k = 1 - age/duration。
// age ≥ duration ⇒ k == 0 ⇒ offset 精确等于零向量。回零不靠任何清理代码，
// 它是公式的直接结论 —— 这正是"连续挨打 20 次后位置无漂移"能被验收的原因。
// 两路频率取 1.37 倍这种非整数比，是为了让它们永不同周期回合；同周期会退化成
// 一条固定斜率的直线往复，看起来像画面在滑轨上推拉。
//
// 【执行顺序 150 是硬要求】
// 必须排在 CameraFollow(100) 之后：小于 100 的话，CameraFollow 会在本组件之后
// 无条件覆盖 transform.position，屏震完全不可见。
// 也必须排在 Hud(200) / HudSkillBar(210) / DamagePopupLayer(215) 之前：
// 它们要读**最终**相机位置做世界→屏幕投影。
// -----------------------------------------------------------------------------

using UnityEngine;
using Xianxia.Combat.UnityBridge;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 相机抖动。与 <see cref="CameraFollow"/> 挂在同一个 Main Camera 上，
    /// 由 <see cref="WorldBuilder"/> 装配、由 <c>HitFeedbackDirector</c> 驱动。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(150)]
    public sealed class CameraShake : MonoBehaviour
    {
        /// <summary>跟随组件。抖动的基准点与钳制都由它提供。</summary>
        private CameraFollow _follow;

        /// <summary>本次抖动的峰值幅度（世界单位）。0 表示没有正在进行的抖动。</summary>
        private float _amp;

        /// <summary>本次抖动的总时长（秒）。</summary>
        private float _duration;

        /// <summary>本次抖动已经播了多久（秒）。吃 <see cref="FeedbackClock.Delta"/>。</summary>
        private float _age;

        /// <summary>是否有正在进行的抖动。用于在完全静默时跳过对 transform 的写入。</summary>
        public bool IsShaking
        {
            get { return _amp > 0.0f && _duration > 0.0f && _age < _duration; }
        }

        private void Awake()
        {
            // 同物体上找。WorldBuilder 会显式 Configure，这里只是兜底，
            // 让"手动把两个组件拖到相机上"这种调试用法也能直接工作。
            if (_follow == null)
            {
                _follow = GetComponent<CameraFollow>();
            }
            ResetState();
        }

        private void OnDisable()
        {
            // 组件被关掉时抖动不会再有人推进，必须立刻回正，
            // 否则相机会永久停在最后那一帧的偏移位置上。
            StopAndRecenter();
        }

        /// <summary>
        /// 注入跟随组件。由 <see cref="WorldBuilder"/> 在装配相机时调用。
        /// </summary>
        /// <param name="follow">同一台相机上的跟随组件。</param>
        public void Configure(CameraFollow follow)
        {
            if (follow != null)
            {
                _follow = follow;
            }
            ResetState();
        }

        /// <summary>
        /// 起一次抖动。
        ///
        /// 【为什么是"取最强"而不是叠加或排队】
        /// 叠加会让范围技能一帧内清场时幅度累到十几倍，画面直接失控；排队则会让
        /// 屏震滞后于战斗，玩家看到的是三秒前那一刀的震动。R-01/R-02 都明文规定
        /// 并发时"只取最强的一次"。
        ///
        /// 【"更强"怎么比】
        /// 只比幅度，不比时长。幅度才是玩家感知到的强度；一次弱而长的抖动不应该
        /// 把一次强而短的重击盖过去。判定为更强时时长一并接管并重新计时。
        /// </summary>
        /// <param name="amplitude">峰值幅度（世界单位）。调用方已乘好强度系数。</param>
        /// <param name="duration">时长（秒）。</param>
        public void Kick(float amplitude, float duration)
        {
            if (amplitude <= 0.0f || duration <= 0.0f)
            {
                return;
            }

            // 正在抖且新的一下更弱 ⇒ 整个丢弃，不打断当前这一次。
            if (IsShaking && amplitude <= _amp)
            {
                return;
            }

            _amp = amplitude;
            _duration = duration;
            _age = 0.0f;
        }

        /// <summary>
        /// 立刻结束抖动并把相机放回无偏移位置。
        ///
        /// 【谁会调它】
        /// 菜单暂停与终局收敛（A-7 / A-8）：暂停面板和结算界面上镜头必须是静止且
        /// 回正的，"暂停时画面还在抖"会让人以为游戏没停住。
        ///
        /// 【为什么要显式写一次 transform.position 而不是等下一帧 CameraFollow 覆盖】
        /// 暂停期间 CameraFollow 的 SmoothDamp 吃 FeedbackClock.Delta，而 hitstop
        /// 一被清掉 Delta 就恢复正常，看似会自愈；但 target 为 null（还没 Configure）
        /// 时 CameraFollow.LateUpdate 会直接 return，相机就会永久停在偏移位上。
        /// 自己造的偏移自己收干净，不要指望别人。
        /// </summary>
        public void StopAndRecenter()
        {
            ResetState();

            if (_follow != null)
            {
                transform.position = _follow.ClampPoint(_follow.BaseCenter);
            }
        }

        private void LateUpdate()
        {
            if (_follow == null)
            {
                return;
            }

            if (!IsShaking)
            {
                // 抖动刚好在上一帧走完 ⇒ 把状态清干净，并确保相机落在无偏移位置。
                // 不清的话 _amp 会一直留着，下一次 Kick 的"取最强"比较就会拿一个
                // 早已过期的旧幅度当基准，弱的一下会被误判为"更弱"而被丢弃。
                if (_amp > 0.0f)
                {
                    ResetState();
                    transform.position = _follow.ClampPoint(_follow.BaseCenter);
                }
                return;
            }

            // 吃表现层时钟：hitstop 期间抖动定格，与角色、特效同步冻住。
            _age += FeedbackClock.Delta;

            Vector3 basePos = _follow.BaseCenter;
            Vector3 offset = OffsetAt(_age);

            // ★ 钳制必须在偏移之后（A-6）。地图边缘时 basePos 已经贴边，
            // basePos + offset 会被按住，表现为"贴边时屏震幅度自动衰减到 0"。
            // 这不是 bug，这正是要的行为：宁可少抖，绝不露黑边。
            Vector3 shaken = _follow.ClampPoint(basePos + offset);
            shaken.z = basePos.z;   // 抖动只在 XY 平面，Z 是取景距离，动了会改变裁剪
            transform.position = shaken;
        }

        /// <summary>
        /// 按年龄算出本帧偏移量。纯函数，同样的输入必然得到同样的输出（可复现）。
        /// </summary>
        /// <param name="age">已播时长（秒）。</param>
        private Vector3 OffsetAt(float age)
        {
            // 线性衰减到 0。用线性而不是指数：指数衰减尾巴长，末段是一段肉眼几乎
            // 看不见却又确实在动的微抖，会让画面显得"不干净"；线性衰减到点即停。
            float k = 1.0f - Mathf.Clamp01(_duration > 0.0f ? age / _duration : 1.0f);
            if (k <= 0.0f)
            {
                return Vector3.zero;
            }

            float w = HitFeedbackConfig.ShakeFrequencyHz * Mathf.PI * 2.0f * age;

            float x = Mathf.Sin(w) * _amp * k;
            float y = Mathf.Sin(w * HitFeedbackConfig.ShakeYFreqMul + HitFeedbackConfig.ShakeYPhase)
                      * _amp * HitFeedbackConfig.ShakeYRatio * k;

            return new Vector3(x, y, 0.0f);
        }

        /// <summary>把抖动状态清成"没有任何抖动在进行"。</summary>
        private void ResetState()
        {
            _amp = 0.0f;
            _duration = 0.0f;
            _age = 0.0f;
        }
    }
}
