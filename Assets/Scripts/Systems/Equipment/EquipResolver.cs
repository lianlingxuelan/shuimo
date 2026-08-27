using System;

namespace Xianxia.Equipment
{
    /// <summary>
    /// 当前装备负载：每个部位最多一件。线程不安全（单玩家单线程游戏循环，足够）。
    /// 仅持有 EquipPiece 引用，不引用任何 Unity / 内核类型（编译期红线：纯逻辑自洽）。
    /// </summary>
    public class EquipLoadout
    {
        private readonly EquipPiece[] _slots = new EquipPiece[Enum.GetValues(typeof(EquipSlot)).Length];

        /// <summary>穿戴到对应部位（同部位会覆盖）。null 被拒绝。</summary>
        public bool Equip(EquipPiece piece)
        {
            if (piece == null) return false;
            _slots[(int)piece.Slot] = piece;
            return true;
        }

        /// <summary>卸下某部位，返回被卸下的装备（无则 null）。</summary>
        public EquipPiece Unequip(EquipSlot slot)
        {
            EquipPiece p = _slots[(int)slot];
            _slots[(int)slot] = null;
            return p;
        }

        public EquipPiece Get(EquipSlot slot) => _slots[(int)slot];

        public bool IsEmpty
        {
            get
            {
                foreach (EquipPiece p in _slots) if (p != null) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// 装备属性聚合 resolver（纯静态函数，便于 headless NUnit 自测）。
    /// 规则：每件装备贡献 = BaseStats × QualityMul[quality] + SlotBase[slot]，全部相加。
    /// 不修改任何 Combatant / 内核字段（数值红线，同 Morality/Cultivation）。
    /// </summary>
    public static class EquipResolver
    {
        public static EquipStats ComputeAggregate(EquipLoadout loadout, EquipConfig cfg)
        {
            EquipStats total = EquipStats.Zero;
            if (loadout == null || cfg == null) return total;

            foreach (EquipSlot slot in Enum.GetValues(typeof(EquipSlot)))
            {
                EquipPiece p = loadout.Get(slot);
                if (p == null) continue;

                float qm = cfg.QualityMultiplier(p.Quality);
                EquipStats scaled = Scale(p.BaseStats, qm);
                EquipStats slotBonus = cfg.SlotBonus(slot);
                total = total + scaled + slotBonus;
            }
            return total;
        }

        private static EquipStats Scale(EquipStats s, float m)
        {
            return new EquipStats
            {
                Atk = s.Atk * m,
                Def = s.Def * m,
                Hp = s.Hp * m,
                Spd = s.Spd * m,
                CritRate = s.CritRate * m,
                CritDmg = s.CritDmg * m,
                Dodge = s.Dodge * m,
                Pierce = s.Pierce * m,
                Toughness = s.Toughness * m
            };
        }
    }
}
