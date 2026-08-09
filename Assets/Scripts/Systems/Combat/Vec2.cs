// -----------------------------------------------------------------------------
// Vec2.cs —— 纯几何二维向量（引擎无关，替代 UnityEngine.Vector2）
//
// 【为什么不直接用 UnityEngine.Vector2】
// 战斗内核的红线是「零 UnityEngine 依赖」：内核必须能被 dotnet test 独立编译、
// 被 Python 对拍逐位复算。一旦位置类型来自引擎，整条链路（AI 决策 / 命中判定 /
// 击退积分）就被钉死在 Unity 里，既跑不了 headless 回归，也无法与 Godot 原型
// 对拍。Vec2 只有 8 字节两个 float，语义与 Godot Vector2 一一对应。
//
// 【与 Godot 的对齐点】
//   Godot a.distance_to(b)      ⇔ a.DistanceTo(b)
//   Godot a.dot(b)              ⇔ a.Dot(b)
//   Godot a.direction_to(b)     ⇔ a.DirectionTo(b)
//   Godot v.rotated(rad)        ⇔ v.Rotated(rad)
//   Godot v.normalized()        ⇔ v.Normalized()   （零向量返回零向量，同 Godot）
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using System;

namespace Xianxia.Combat
{
    /// <summary>
    /// 不可变风格的二维向量（struct，零 GC）。所有几何判定的唯一坐标类型。
    /// </summary>
    public struct Vec2 : IEquatable<Vec2>
    {
        /// <summary>零向量退化判定阈值。与 Godot enemy.gd 的 0.001 对齐。</summary>
        public const float Epsilon = 0.001f;

        /// <summary>X 分量。</summary>
        public float X;

        /// <summary>Y 分量。</summary>
        public float Y;

        /// <summary>按分量构造。</summary>
        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>零向量 (0, 0)。</summary>
        public static Vec2 Zero
        {
            get { return new Vec2(0.0f, 0.0f); }
        }

        /// <summary>单位向量 (1, 0)。方向退化时的兜底方向（对齐 Godot Vector2.RIGHT）。</summary>
        public static Vec2 Right
        {
            get { return new Vec2(1.0f, 0.0f); }
        }

        /// <summary>单位向量 (0, -1)。屏幕坐标系向上（对齐 Godot Vector2.UP）。</summary>
        public static Vec2 Up
        {
            get { return new Vec2(0.0f, -1.0f); }
        }

        // ---------------------------------------------------------------------
        // 长度与方向
        // ---------------------------------------------------------------------

        /// <summary>模长。</summary>
        public float Length()
        {
            return (float)Math.Sqrt(X * X + Y * Y);
        }

        /// <summary>
        /// 模长平方。比较距离时优先用它——开方是这条链路上最贵的一次运算，
        /// 而「谁更近」的判定根本不需要开方。
        /// </summary>
        public float LengthSquared()
        {
            return X * X + Y * Y;
        }

        /// <summary>
        /// 单位化。零向量返回零向量（与 Godot 一致，不抛异常也不返回 NaN）。
        /// </summary>
        public Vec2 Normalized()
        {
            float len = Length();
            if (len <= Epsilon)
            {
                return Zero;
            }
            return new Vec2(X / len, Y / len);
        }

        /// <summary>
        /// 缩放到指定模长。len &lt;= 0 或自身为零向量时返回零向量。
        /// 击退脉冲、突进步长都走它：一个「方向 + 期望速度」的直觉接口。
        /// </summary>
        public Vec2 WithLength(float len)
        {
            if (len <= 0.0f)
            {
                return Zero;
            }
            Vec2 n = Normalized();
            return new Vec2(n.X * len, n.Y * len);
        }

        /// <summary>到目标点的距离。等价于 Godot <c>distance_to</c>。</summary>
        public float DistanceTo(Vec2 o)
        {
            float dx = o.X - X;
            float dy = o.Y - Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>到目标点的距离平方（范围判定优先用它）。</summary>
        public float DistanceSquaredTo(Vec2 o)
        {
            float dx = o.X - X;
            float dy = o.Y - Y;
            return dx * dx + dy * dy;
        }

        /// <summary>指向目标点的单位向量。等价于 Godot <c>direction_to</c>。</summary>
        public Vec2 DirectionTo(Vec2 o)
        {
            return new Vec2(o.X - X, o.Y - Y).Normalized();
        }

        /// <summary>点积。等价于 Godot <c>Vector2.dot</c>。</summary>
        public float Dot(Vec2 o)
        {
            return X * o.X + Y * o.Y;
        }

        /// <summary>二维叉积（标量）。判定目标在左侧还是右侧时用。</summary>
        public float Cross(Vec2 o)
        {
            return X * o.Y - Y * o.X;
        }

        /// <summary>
        /// 与另一向量的夹角（角度制，0~180）。任一为零向量时返回 0。
        ///
        /// 【为什么要 clamp】
        /// dot / (l1*l2) 在两向量几乎共线时可能因浮点误差落到 1.0000001，
        /// Acos 立刻返回 NaN —— 一个只在「怪正对玩家」这种最常见情形下才复现的崩溃。
        /// </summary>
        public float AngleDegBetween(Vec2 o)
        {
            float l1 = Length();
            float l2 = o.Length();
            if (l1 <= Epsilon || l2 <= Epsilon)
            {
                return 0.0f;
            }
            float c = Dot(o) / (l1 * l2);
            if (c > 1.0f)
            {
                c = 1.0f;
            }
            else if (c < -1.0f)
            {
                c = -1.0f;
            }
            return (float)(Math.Acos(c) * 180.0 / Math.PI);
        }

        /// <summary>自身与 X 轴正方向的夹角（弧度，-π~π）。等价于 Godot <c>angle()</c>。</summary>
        public float Angle()
        {
            return (float)Math.Atan2(Y, X);
        }

        /// <summary>绕原点旋转（弧度）。等价于 Godot <c>rotated()</c>。</summary>
        public Vec2 Rotated(float radians)
        {
            float cs = (float)Math.Cos(radians);
            float sn = (float)Math.Sin(radians);
            return new Vec2(X * cs - Y * sn, X * sn + Y * cs);
        }

        /// <summary>绕原点旋转（角度制）。CHASE 侧向偏移用（±35°）。</summary>
        public Vec2 RotatedDeg(float degrees)
        {
            return Rotated((float)(degrees * Math.PI / 180.0));
        }

        /// <summary>线性插值。t 会被夹到 [0,1]。</summary>
        public Vec2 Lerp(Vec2 o, float t)
        {
            float k = t < 0.0f ? 0.0f : (t > 1.0f ? 1.0f : t);
            return new Vec2(X + (o.X - X) * k, Y + (o.Y - Y) * k);
        }

        /// <summary>是否近似零向量。</summary>
        public bool IsZero()
        {
            return Length() <= Epsilon;
        }

        // ---------------------------------------------------------------------
        // 运算符
        // ---------------------------------------------------------------------

        /// <summary>逐分量相加。</summary>
        public static Vec2 operator +(Vec2 a, Vec2 b)
        {
            return new Vec2(a.X + b.X, a.Y + b.Y);
        }

        /// <summary>逐分量相减。</summary>
        public static Vec2 operator -(Vec2 a, Vec2 b)
        {
            return new Vec2(a.X - b.X, a.Y - b.Y);
        }

        /// <summary>取反。</summary>
        public static Vec2 operator -(Vec2 a)
        {
            return new Vec2(-a.X, -a.Y);
        }

        /// <summary>数乘。</summary>
        public static Vec2 operator *(Vec2 a, float k)
        {
            return new Vec2(a.X * k, a.Y * k);
        }

        /// <summary>数乘（左乘形式）。</summary>
        public static Vec2 operator *(float k, Vec2 a)
        {
            return new Vec2(a.X * k, a.Y * k);
        }

        /// <summary>数除。k 为 0 时返回零向量而不是 Infinity。</summary>
        public static Vec2 operator /(Vec2 a, float k)
        {
            if (k == 0.0f)
            {
                return Zero;
            }
            return new Vec2(a.X / k, a.Y / k);
        }

        /// <summary>逐分量精确相等。</summary>
        public static bool operator ==(Vec2 a, Vec2 b)
        {
            return a.X == b.X && a.Y == b.Y;
        }

        /// <summary>逐分量不等。</summary>
        public static bool operator !=(Vec2 a, Vec2 b)
        {
            return a.X != b.X || a.Y != b.Y;
        }

        /// <summary>逐分量精确相等。</summary>
        public bool Equals(Vec2 other)
        {
            return X == other.X && Y == other.Y;
        }

        /// <summary>逐分量精确相等。</summary>
        public override bool Equals(object obj)
        {
            return obj is Vec2 && Equals((Vec2)obj);
        }

        /// <summary>哈希。</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }

        /// <summary>调试输出。</summary>
        public override string ToString()
        {
            return string.Format("({0:F3}, {1:F3})", X, Y);
        }

        // ---------------------------------------------------------------------
        // 静态便捷方法（与实例方法等价，便于测试代码书写）
        // ---------------------------------------------------------------------

        /// <summary>两点距离。</summary>
        public static float Distance(Vec2 a, Vec2 b)
        {
            return a.DistanceTo(b);
        }

        /// <summary>点积。</summary>
        public static float Dot(Vec2 a, Vec2 b)
        {
            return a.Dot(b);
        }

        /// <summary>由角度（弧度）与模长构造向量。</summary>
        public static Vec2 FromAngle(float radians, float length)
        {
            return new Vec2((float)Math.Cos(radians) * length, (float)Math.Sin(radians) * length);
        }
    }
}
