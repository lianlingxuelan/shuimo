namespace Xianxia.Equipment
{
    /// <summary>
    /// 装备部位（8 个）。顺序即索引，供 EquipLoadout 数组与配置表对齐。
    /// 名字为结构占位，用户后续可在 UI / 文案层改名（如 武器→本命剑），不影响逻辑。
    /// </summary>
    public enum EquipSlot
    {
        Weapon,     // 0 武器
        Head,       // 1 头冠/束发
        Body,       // 2 衣/甲
        Feet,       // 3 鞋/履
        Hands,      // 4 护腕/手套
        Accessory,  // 5 佩饰
        Ring,       // 6 戒指
        Talisman    // 7 符箓/玉佩
    }

    /// <summary>
    /// 品质 tier（白→…→神）。索引即倍率数组下标。
    /// 名字与层级数为占位，用户后续可改为 凡/灵/宝/法/地/天/仙/神 等。
    /// </summary>
    public enum EquipQuality
    {
        Common,     // 0
        Uncommon,   // 1
        Rare,       // 2
        Epic,       // 3
        Legendary,  // 4
        Mythic,     // 5
        Divine,     // 6
        Supreme     // 7
    }

    /// <summary>
    /// 装备聚合后的属性贡献。纯数据，支持相加。
    /// 字段集合为占位，后续要加「五行灵根 / 真气 / 体力 / 暴伤」等由用户拍板后扩字段。
    /// </summary>
    public struct EquipStats
    {
        public float Atk;        // 攻击
        public float Def;        // 防御
        public float Hp;         // 生命
        public float Spd;        // 身法/移速
        public float CritRate;   // 暴击率 0..1
        public float CritDmg;    // 暴击伤害加成（乘区附加值）
        public float Dodge;      // 闪避 0..1
        public float Pierce;     // 穿透（无视防御比例）0..1
        public float Toughness;  // 韧性（减伤比例）0..1

        public static readonly EquipStats Zero = new EquipStats();

        public static EquipStats Add(EquipStats a, EquipStats b)
        {
            return new EquipStats
            {
                Atk = a.Atk + b.Atk,
                Def = a.Def + b.Def,
                Hp = a.Hp + b.Hp,
                Spd = a.Spd + b.Spd,
                CritRate = a.CritRate + b.CritRate,
                CritDmg = a.CritDmg + b.CritDmg,
                Dodge = a.Dodge + b.Dodge,
                Pierce = a.Pierce + b.Pierce,
                Toughness = a.Toughness + b.Toughness
            };
        }

        public static EquipStats operator +(EquipStats a, EquipStats b) => Add(a, b);
    }

    /// <summary>
    /// 单件装备。数值全部占位，用户后续在 Unity 桥接层（ScriptableObject）填真实名字/属性/词条。
    /// 注：词条 / 套装 / 洗练 / 镶嵌 / 强化 等属内容系统，留后续周次；此处仅承载「基础属性 + 品质 + 部位」结构。
    /// </summary>
    public class EquipPiece
    {
        public string Id;
        public string DisplayName;    // 占位，用户填
        public EquipSlot Slot;
        public EquipQuality Quality;
        public int Level;             // 强化等级占位（数值/上限后续定）
        public EquipStats BaseStats;  // 单件基础属性（占位数值，用户填）
    }
}
