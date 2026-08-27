namespace Xianxia.Equipment
{
    /// <summary>
    /// 装备系统的数据驱动配置。纯逻辑层只认这份配置，不写死任何数值/名字。
    /// 所有数组长度需与枚举一致（8）；越界访问按安全默认（1 倍 / 零属性）处理，不抛异常。
    /// 调数 / 命名决策权归用户 —— 本类只提供结构骨架。
    /// </summary>
    public class EquipConfig
    {
        private const int SlotCount = 8;       // = EquipSlot 枚举数
        private const int QualityCount = 8;   // = EquipQuality 枚举数

        /// <summary>各品质对基础属性的倍率（下标 = (int)EquipQuality）。占位数值，调数归用户。</summary>
        public float[] QualityMul = { 1f, 1.2f, 1.5f, 2f, 3f, 4f, 6f, 9f };

        /// <summary>各部位附加基础属性（下标 = (int)EquipSlot）。占位全 0，用户在 Unity 桥接层/编辑器填。</summary>
        public EquipStats[] SlotBase = new EquipStats[SlotCount];

        public float QualityMultiplier(EquipQuality q)
        {
            int i = (int)q;
            if (QualityMul == null || i < 0 || i >= QualityMul.Length) return 1f;
            return QualityMul[i];
        }

        public EquipStats SlotBonus(EquipSlot s)
        {
            int i = (int)s;
            if (SlotBase == null || i < 0 || i >= SlotBase.Length) return EquipStats.Zero;
            return SlotBase[i];
        }
    }
}
