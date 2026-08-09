// -----------------------------------------------------------------------------
// SkillResolver.cs —— 技能形状命中判定 + 软索敌（引擎无关 · 静态纯函数）
//
// 【为什么扇形判定必须逐字复刻 AttackController.ResolveHits】
// 水剑斩在 T3 里从「Unity 侧自己算」改为「走通用技能路径」。这是一次纯重构，
// PRD 明确要求 T2 手感一字不改。只要几何判定有任何一处不同（比如把
// "距离 ≈ 0 时判命中"的兜底漏掉、或把 <= 写成 <），玩家就会在贴脸时砍空，
// 而这种 bug 在自动测试里看不见、只有手测才会撞上。所以这里的三条判定
//     d2 <= r2  /  d2 > 1e-6 才检查角度  /  角度 <= halfArc
// 与 AttackController.cs:174-184 完全一致，注释里标明出处以便日后对账。
//
// 【为什么软索敌只改朝向、不改位置、不追踪】
// P0-07 的验收是「±15° 内吸附、超出不吸附、可整体关闭」。它是一个**瞄准辅助**，
// 不是制导。一旦允许它改位置或在前摇中持续追踪，"我朝哪打"就不再由玩家决定，
// 手感会从"我打中了"变成"游戏帮我打中了"——这是同类作品最常见的差评来源。
// 因此它只在起手那一帧算一次，把结果写进 ActionState.LockedFacing 就结束了。
//
// 【为什么吸附取"角度最小"而不是"距离最近"】
// 最近的敌人可能在身后。玩家的输入方向表达的是意图，吸附应当在**尊重意图**的
// 前提下修正误差 —— 所以先用 maxDeg 把候选裁到"玩家显然是想打它"的锥形里，
// 再在锥内取角度最小者。距离只用于平手时的次级排序（同角度取更近的）。
//
// 【禁止事项】不得引用 UnityEngine。
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Xianxia.Combat
{
    /// <summary>技能命中判定与软索敌的纯几何计算。全部为静态函数，无状态。</summary>
    public static class SkillResolver
    {
        /// <summary>
        /// 方向退化阈值：距离平方小于它时认为"贴脸"，方向无意义。
        /// 与 <c>AttackController.ResolveHits</c> 的 1e-6f 保持一致。
        /// </summary>
        public const float DegenerateDistanceSq = 1e-6f;

        /// <summary>
        /// 点是否落在扇形内。
        /// </summary>
        /// <param name="origin">扇形顶点（施法者位置）。</param>
        /// <param name="facing">朝向（无需预先归一化；零向量时只判距离）。</param>
        /// <param name="range">半径。</param>
        /// <param name="halfArcDeg">半张角（度）。&gt;= 180 时等价于圆形。</param>
        /// <param name="target">被判定点。</param>
        /// <returns>true = 命中。</returns>
        public static bool InSector(Vec2 origin, Vec2 facing, float range, float halfArcDeg, Vec2 target)
        {
            if (range <= 0.0f)
            {
                return false;
            }

            Vec2 to = target - origin;
            float d2 = to.LengthSquared();
            if (d2 > range * range)
            {
                return false;
            }

            // 贴脸兜底：距离≈0 时方向无意义，判定为命中（人贴在脸上不该砍空）。
            if (d2 <= DegenerateDistanceSq)
            {
                return true;
            }
            if (halfArcDeg >= 180.0f)
            {
                return true;
            }
            if (facing.IsZero())
            {
                return true;
            }

            return facing.AngleDegBetween(to) <= halfArcDeg;
        }

        /// <summary>
        /// 点是否落在圆内。
        /// </summary>
        /// <param name="center">圆心。</param>
        /// <param name="range">半径。</param>
        /// <param name="target">被判定点。</param>
        /// <returns>true = 命中。</returns>
        public static bool InCircle(Vec2 center, float range, Vec2 target)
        {
            if (range <= 0.0f)
            {
                return false;
            }
            return center.DistanceSquaredTo(target) <= range * range;
        }

        /// <summary>
        /// 计算某个技能的判定原点。扇形以施法者为顶点；圆形沿朝向前推
        /// <see cref="SkillDef.ForwardOffset"/>（法阵冲击的"往前砸一个法阵"）。
        /// </summary>
        /// <param name="def">技能定义。</param>
        /// <param name="casterPos">施法者位置。</param>
        /// <param name="facing">锁定朝向（内部归一化）。</param>
        /// <returns>判定原点。</returns>
        public static Vec2 ResolveOrigin(SkillDef def, Vec2 casterPos, Vec2 facing)
        {
            if (def == null || def.ForwardOffset == 0.0f || facing.IsZero())
            {
                return casterPos;
            }
            return casterPos + facing.Normalized() * def.ForwardOffset;
        }

        /// <summary>
        /// 单目标命中判定。
        /// </summary>
        /// <param name="def">技能定义。</param>
        /// <param name="casterPos">施法者位置。</param>
        /// <param name="facing">锁定朝向。</param>
        /// <param name="targetPos">目标位置。</param>
        /// <returns>true = 命中。</returns>
        public static bool HitsPoint(SkillDef def, Vec2 casterPos, Vec2 facing, Vec2 targetPos)
        {
            if (def == null)
            {
                return false;
            }
            if (def.Shape == SkillShape.Circle)
            {
                return InCircle(ResolveOrigin(def, casterPos, facing), def.Range, targetPos);
            }
            return InSector(casterPos, facing, def.Range, def.HalfArcDeg, targetPos);
        }

        /// <summary>
        /// 收集一次技能命中的全部目标。
        ///
        /// **遍历顺序即结算顺序**（§7.3-5）：本方法严格按 <paramref name="candidates"/>
        /// 的下标顺序追加到 <paramref name="outHits"/>，不做任何排序 ——
        /// 调用方传进来的是 <c>Encounter.AliveEnemies()</c> 的快照，其顺序由生成顺序决定，
        /// 同种子下完全可复现。
        /// </summary>
        /// <param name="def">技能定义。null 时返回 0。</param>
        /// <param name="casterPos">施法者位置。</param>
        /// <param name="facing">锁定朝向。</param>
        /// <param name="candidates">候选目标（通常是存活敌人快照）。</param>
        /// <param name="outHits">命中输出列表。**由调用方负责 Clear**（复用缓冲避免 GC）。</param>
        /// <returns>命中数量。</returns>
        public static int QueryHits(
            SkillDef def,
            Vec2 casterPos,
            Vec2 facing,
            List<Combatant> candidates,
            List<Combatant> outHits)
        {
            if (def == null || candidates == null || outHits == null)
            {
                return 0;
            }

            bool circle = def.Shape == SkillShape.Circle;
            Vec2 origin = circle ? ResolveOrigin(def, casterPos, facing) : casterPos;
            float r2 = def.Range * def.Range;
            float halfArc = def.HalfArcDeg;
            int hits = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                Combatant c = candidates[i];
                if (c == null || !c.IsAlive)
                {
                    continue;
                }

                Vec2 to = c.Position - origin;
                float d2 = to.LengthSquared();
                if (d2 > r2)
                {
                    continue;
                }

                if (!circle && d2 > DegenerateDistanceSq && halfArc < 180.0f && !facing.IsZero())
                {
                    if (facing.AngleDegBetween(to) > halfArc)
                    {
                        continue;
                    }
                }

                outHits.Add(c);
                hits++;
            }

            return hits;
        }

        /// <summary>
        /// 软索敌（P0-07）：把原始朝向吸附到锥内角度最小的存活敌人身上。
        ///
        /// 【为什么把 enabled 做成参数而不是读全局开关】
        /// 验收③要求"可整体关闭"。若在这里读某个静态开关，单测就得改全局状态、
        /// 测试之间会互相污染。开关的真源放在调用方（<c>Encounter.SoftAimEnabled</c>），
        /// 本函数保持纯粹。
        /// </summary>
        /// <param name="casterPos">施法者位置。</param>
        /// <param name="rawFacing">玩家输入的原始朝向（内部归一化）。</param>
        /// <param name="candidates">候选目标（通常是存活敌人快照）。</param>
        /// <param name="maxDeg">最大吸附角（度）。&lt;= 0 时不吸附。</param>
        /// <param name="maxRange">最大吸附距离。&lt;= 0 表示不限距离。</param>
        /// <param name="enabled">总开关。false 时原样返回。</param>
        /// <param name="snapped">输出：被吸附到的敌人；未吸附时为 null。</param>
        /// <returns>吸附后的朝向（已归一化）；未吸附时返回归一化的原始朝向。</returns>
        public static Vec2 SoftAim(
            Vec2 casterPos,
            Vec2 rawFacing,
            List<Combatant> candidates,
            float maxDeg,
            float maxRange,
            bool enabled,
            out Combatant snapped)
        {
            snapped = null;

            Vec2 baseFacing = rawFacing.IsZero() ? Vec2.Right : rawFacing.Normalized();
            if (!enabled || maxDeg <= 0.0f || candidates == null || candidates.Count == 0)
            {
                return baseFacing;
            }

            float bestDeg = maxDeg;
            float bestDistSq = float.MaxValue;
            Combatant best = null;
            float maxRangeSq = maxRange > 0.0f ? maxRange * maxRange : float.MaxValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                Combatant c = candidates[i];
                if (c == null || !c.IsAlive)
                {
                    continue;
                }

                Vec2 to = c.Position - casterPos;
                float d2 = to.LengthSquared();
                if (d2 <= DegenerateDistanceSq || d2 > maxRangeSq)
                {
                    continue;
                }

                float deg = baseFacing.AngleDegBetween(to);
                if (deg > maxDeg)
                {
                    continue;
                }

                // 主序：角度最小（最贴近玩家意图）。次序：同角度取更近的。
                // 用 > 而非 >= 保证平手时保留先出现者，遍历顺序即决定顺序。
                if (deg < bestDeg || (deg == bestDeg && d2 < bestDistSq))
                {
                    bestDeg = deg;
                    bestDistSq = d2;
                    best = c;
                }
            }

            if (best == null)
            {
                return baseFacing;
            }

            snapped = best;
            Vec2 aimed = best.Position - casterPos;
            return aimed.IsZero() ? baseFacing : aimed.Normalized();
        }

        /// <summary>
        /// 软索敌的简化重载：不限吸附距离，使用
        /// <see cref="SkillConfig.SOFT_AIM_MAX_DEG"/> 作为最大吸附角。
        /// </summary>
        /// <param name="casterPos">施法者位置。</param>
        /// <param name="rawFacing">原始朝向。</param>
        /// <param name="candidates">候选目标。</param>
        /// <param name="enabled">总开关。</param>
        /// <param name="snapped">输出：被吸附到的敌人。</param>
        /// <returns>吸附后的朝向。</returns>
        public static Vec2 SoftAim(
            Vec2 casterPos,
            Vec2 rawFacing,
            List<Combatant> candidates,
            bool enabled,
            out Combatant snapped)
        {
            return SoftAim(casterPos, rawFacing, candidates, SkillConfig.SOFT_AIM_MAX_DEG, 0.0f, enabled, out snapped);
        }
    }
}
