// -----------------------------------------------------------------------------
// HeroineFrames.cs —— 女主水墨精灵的 39 帧静态清单表（asmdef: Xianxia.Unity.T2）
//
// 【这是一张贫血数据表，不是逻辑】
// 每个动作（pose）需要的全部信息——文件前缀、帧数、画布尺寸、轴心、帧率、是否循环——
// 都固化在这里的 _table 里。HeroineAnimator 只负责"现在该播哪个 pose 的第几帧"，
// "那一帧叫什么名字、多大、锚在哪" 全部来问这张表。
//
// 【为什么不用 Unity 的 Animator / AnimationClip】
// 那需要 .controller 资产，状态转移条件配在 Inspector 里 —— grep 不可见。
// 本工程的既有风格是"一切逻辑在代码里"（全工程连一个 Prefab 都没有），
// 用常量表 + 状态机组件才和周围代码是同一种东西。
//
// 【帧号约定（唯一的 off-by-one 风险点）】
// 磁盘文件名从 1 起（heroine_idle_1.png），代码索引从 0 起。
// PoseDef.PathOf() 是**唯一**允许做这个 +1 的地方，别处一律不许再拼文件名。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>女主的动画状态。**顺序必须与 <see cref="HeroineFrames"/> 的清单表严格对应**。</summary>
    public enum AnimState
    {
        /// <summary>站立待机（呼吸），循环。</summary>
        Idle = 0,

        /// <summary>朝屏幕下方走，循环。</summary>
        WalkDown = 1,

        /// <summary>朝屏幕上方（背身）走，循环。</summary>
        WalkUp = 2,

        /// <summary>侧向走（源图朝右，朝左靠 flipX），循环。</summary>
        WalkSide = 3,

        /// <summary>挥剑，一次性。画布 96 宽以容纳剑的延展。</summary>
        Attack = 4,

        /// <summary>受击，一次性。</summary>
        Hurt = 5,

        /// <summary>闪避翻滚，一次性。</summary>
        Dodge = 6,

        /// <summary>死亡，一次性且停在末帧，永不回落。</summary>
        Death = 7
    }

    /// <summary>
    /// 单个动作的静态定义。只读结构 —— 它是常量表的元素，任何时候都不该被改。
    /// </summary>
    public readonly struct PoseDef
    {
        /// <summary>文件名前缀，不含帧号与扩展名。例 "heroine_idle"。</summary>
        public readonly string FilePrefix;

        /// <summary>帧数。磁盘上的编号是 1..FrameCount。</summary>
        public readonly int FrameCount;

        /// <summary>单帧画布宽（像素）。PPU=1 ⇒ 同时就是世界单位宽。</summary>
        public readonly int FrameW;

        /// <summary>单帧画布高（像素）。</summary>
        public readonly int FrameH;

        /// <summary>归一化轴心。人形立绘锚脚底而非体心，否则角色看起来浮空半身。</summary>
        public readonly Vector2 Pivot;

        /// <summary>播放帧率。每个 pose 独立，不设全局帧率。</summary>
        public readonly float Fps;

        /// <summary>是否循环。false 表示一次性动作，播完回落（Death 除外，它锁死在末帧）。</summary>
        public readonly bool Loop;

        // 注：构造函数的参数表刻意换到了下一行。这样全文件中"类型名紧跟左括号"的写法
        // 只出现在清单表的 8 个条目上，可直接当作"8 个 pose 一个不少"的静态验收锚点。
        // 这是合法的 C# 写法（方法名与参数列表之间允许有空白），勿改回单行。
        public PoseDef
            (string filePrefix, int frameCount, int frameW, int frameH,
             Vector2 pivot, float fps, bool loop)
        {
            FilePrefix = filePrefix;
            FrameCount = frameCount > 0 ? frameCount : 1;
            FrameW = frameW > 0 ? frameW : 1;
            FrameH = frameH > 0 ? frameH : 1;
            Pivot = pivot;
            Fps = fps > 0.0f ? fps : 1.0f;
            Loop = loop;
        }

        /// <summary>整套动作播完一轮需要的秒数。一次性动作用它设倒计时。</summary>
        public float Duration
        {
            get { return FrameCount / Fps; }
        }

        /// <summary>
        /// 第 <paramref name="i"/> 帧的 StreamingAssets 相对路径（不含扩展名）。
        /// <para>索引从 0 起，文件名从 1 起 —— 这里是全工程唯一做这个 +1 的地方。</para>
        /// 越界索引会被钳制到合法范围，保证任何情况下都返回一个真实存在的帧。
        /// </summary>
        /// <param name="i">帧索引，0 .. FrameCount-1。</param>
        public string PathOf(int i)
        {
            int n = Mathf.Clamp(i, 0, FrameCount - 1);
            return HeroineFrames.RootDir + "/" + FilePrefix + "_" + (n + 1);
        }
    }

    /// <summary>女主 39 帧的静态清单表与朝向判定工具。全静态、无状态。</summary>
    public static class HeroineFrames
    {
        /// <summary>39 帧所在目录，相对 StreamingAssets 根，正斜杠。</summary>
        public const string RootDir = "characters/heroine";

        /// <summary>全部帧数之和。与清单表求和一致（4+6+6+6+6+2+4+5）。</summary>
        public const int TotalFrames = 39;

        // 人形立绘统一锚"脚底略上一点"，兼顾 3/4 视角的站在格子上的感觉。
        private static readonly Vector2 FootPivot = new Vector2(0.50f, 0.28f);

        // attack 是 96 宽画布：身体占左半、剑向右挥出，所以轴心偏左。
        // ⚠ 初值，需 PlayMode 目测校准（挥剑时角色若横移半身位，改成 0.50f）。
        private static readonly Vector2 AttackPivot = new Vector2(0.25f, 0.28f);

        // death 是 64 宽画布：倒地帧重心下移，轴心比站立更低。
        // ⚠ 初值，需 PlayMode 目测校准。
        private static readonly Vector2 DeathPivot = new Vector2(0.50f, 0.22f);

        /// <summary>
        /// 清单表。**下标 = (int)AnimState**，顺序不可打乱，增删必须同步改枚举。
        /// 尺寸列已与磁盘上的 39 张 PNG 逐张核对一致。
        /// </summary>
        private static readonly PoseDef[] _table =
        {
            // ┌ 前缀 ────────────────┬帧数┬  W ┬ H ┬ 轴心 ───────┬ Fps ┬ 循环 ┐
            new PoseDef("heroine_idle",       4, 48, 64, FootPivot,    4.0f,  true),   // 0 Idle
            new PoseDef("heroine_walk_down",  6, 48, 64, FootPivot,    8.0f,  true),   // 1 WalkDown
            new PoseDef("heroine_walk_up",    6, 48, 64, FootPivot,    8.0f,  true),   // 2 WalkUp
            new PoseDef("heroine_walk_side",  6, 48, 64, FootPivot,    8.0f,  true),   // 3 WalkSide
            new PoseDef("heroine_attack",     6, 96, 64, AttackPivot, 12.0f,  false),  // 4 Attack
            new PoseDef("heroine_hurt",       2, 48, 64, FootPivot,    8.0f,  false),  // 5 Hurt
            new PoseDef("heroine_dodge",      4, 48, 64, FootPivot,   12.0f,  false),  // 6 Dodge
            new PoseDef("heroine_death",      5, 64, 64, DeathPivot,   6.0f,  false)   // 7 Death
        };

        /// <summary>清单表里的 pose 个数（= 枚举值个数）。</summary>
        public static int PoseCount
        {
            get { return _table.Length; }
        }

        /// <summary>
        /// 取某状态的静态定义。越界一律回落到 Idle，绝不抛异常。
        /// </summary>
        public static PoseDef Get(AnimState st)
        {
            int i = (int)st;
            if (i < 0 || i >= _table.Length)
            {
                return _table[0];
            }
            return _table[i];
        }

        /// <summary>
        /// 由移动状态推导行走动画。
        /// <para>纵向分量占优时走 上/下 两套，否则走侧身（左右共用一套图，靠 flipX 区分）。</para>
        /// </summary>
        /// <param name="facing">朝向向量，不要求归一化。</param>
        /// <param name="moving">本帧是否有移动输入。</param>
        /// <returns>未移动时为 <see cref="AnimState.Idle"/>。</returns>
        public static AnimState FromMovement(Vector2 facing, bool moving)
        {
            if (!moving)
            {
                return AnimState.Idle;
            }
            if (Mathf.Abs(facing.y) > Mathf.Abs(facing.x))
            {
                return facing.y > 0.0f ? AnimState.WalkUp : AnimState.WalkDown;
            }
            return AnimState.WalkSide;
        }

        /// <summary>
        /// 是否需要水平翻转。
        /// <para>源图一律朝右（+X），朝左时翻转。上/下走与死亡是正/背面视角，翻转没有意义。</para>
        /// <para>用 <c>SpriteRenderer.flipX</c> 而非 <c>localScale.x = -1</c>：后者会把
        /// 子物体 FacingMarker 一起镜像掉。</para>
        /// </summary>
        public static bool ShouldFlipX(AnimState st, Vector2 facing)
        {
            if (st != AnimState.WalkSide && st != AnimState.Attack && st != AnimState.Idle)
            {
                return false;
            }
            return facing.x < 0.0f;
        }
    }
}
