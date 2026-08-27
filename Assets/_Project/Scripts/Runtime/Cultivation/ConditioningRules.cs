// -----------------------------------------------------------------------------
// ConditioningRules.cs —— 调理成长第一版的纯规则层
//
// 只描述“选哪种调理、需要哪些材料、它给什么倾向”，不认识 Unity、背包或战斗桥。
// 这样配方和数值可以脱离场景单测，Unity 侧只负责读取和展示。
// -----------------------------------------------------------------------------

namespace Xianxia.Unity.T2
{
    /// <summary>同一时间只能启用一种的调理倾向。</summary>
    public enum ConditioningKind
    {
        None,
        QingQi,
        StrongSinew,
        NourishOrigin,
    }

    /// <summary>一份药散的材料成本和玩家可读名称。</summary>
    public readonly struct ConditioningRecipe
    {
        public readonly ConditioningKind Kind;
        public readonly int BambooWoodCost;
        public readonly int BambooShootCost;
        public readonly string DisplayName;

        public ConditioningRecipe(ConditioningKind kind, int bambooWoodCost, int bambooShootCost, string displayName)
        {
            Kind = kind;
            BambooWoodCost = bambooWoodCost;
            BambooShootCost = bambooShootCost;
            DisplayName = displayName;
        }
    }

    /// <summary>调理配方和战斗倾向的唯一数值入口。</summary>
    public static class ConditioningRules
    {
        private const float QingQiCooldownMultiplier = 0.85f;
        private const float StrongSinewDodgeDamageMultiplier = 1.25f;
        private const float NourishOriginRecoveryMultiplier = 1.5f;

        public static ConditioningRecipe GetRecipe(ConditioningKind kind)
        {
            switch (kind)
            {
                case ConditioningKind.QingQi:
                    return new ConditioningRecipe(kind, 2, 1, "清气散");
                case ConditioningKind.StrongSinew:
                    return new ConditioningRecipe(kind, 3, 1, "强筋散");
                case ConditioningKind.NourishOrigin:
                    return new ConditioningRecipe(kind, 2, 2, "养元散");
                case ConditioningKind.None:
                default:
                    return new ConditioningRecipe(ConditioningKind.None, 0, 0, "未调理");
            }
        }

        public static bool CanCraft(int bambooWood, int bambooShoot, ConditioningRecipe recipe)
        {
            return bambooWood >= recipe.BambooWoodCost && bambooShoot >= recipe.BambooShootCost;
        }

        /// <summary>
        /// 按配方一次性扣除材料。材料不够时两个计数都保持原样，避免“竹材扣了、竹笋没扣到”
        /// 这种半成品状态；Unity 背包控制器可复用这条预检语义。
        /// </summary>
        public static bool TrySpend(ref int bambooWood, ref int bambooShoot, ConditioningRecipe recipe)
        {
            if (!CanCraft(bambooWood, bambooShoot, recipe))
            {
                return false;
            }

            bambooWood -= recipe.BambooWoodCost;
            bambooShoot -= recipe.BambooShootCost;
            return true;
        }

        public static float SkillCooldownMultiplier(ConditioningKind kind)
        {
            return kind == ConditioningKind.QingQi ? QingQiCooldownMultiplier : 1.0f;
        }

        /// <summary>
        /// 把调理倾向换算成可写入技能运行时的整数冷却帧。
        /// 只在清气散时缩短；其他调理不碰技能 CD，避免“强筋散也意外加速施法”。
        /// </summary>
        public static int AdjustCooldownFrames(int baseFrames, ConditioningKind kind)
        {
            if (baseFrames <= 0)
            {
                return 0;
            }
            return kind == ConditioningKind.QingQi
                ? System.Math.Max(0, (int)System.Math.Round(baseFrames * QingQiCooldownMultiplier))
                : baseFrames;
        }

        public static float DodgeDamageMultiplier(ConditioningKind kind)
        {
            return kind == ConditioningKind.StrongSinew ? StrongSinewDodgeDamageMultiplier : 1.0f;
        }

        public static float OutOfCombatRecoveryMultiplier(ConditioningKind kind)
        {
            return kind == ConditioningKind.NourishOrigin ? NourishOriginRecoveryMultiplier : 1.0f;
        }
    }
}
