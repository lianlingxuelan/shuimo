using NUnit.Framework;
using Xianxia.Equipment;

[TestFixture]
public class EquipTests
{
    private static EquipConfig MakeCfg()
    {
        return new EquipConfig
        {
            // Common=1, Uncommon=1.5, Rare=2, Epic=3, Legendary=4, Mythic=6, Divine=8, Supreme=10
            QualityMul = new float[] { 1f, 1.5f, 2f, 3f, 4f, 6f, 8f, 10f }
        };
    }

    private static EquipPiece Piece(EquipSlot slot, EquipQuality q, EquipStats baseStats)
    {
        return new EquipPiece
        {
            Id = slot + "_" + q,
            DisplayName = slot.ToString(),
            Slot = slot,
            Quality = q,
            Level = 0,
            BaseStats = baseStats
        };
    }

    [Test]
    public void EmptyLoadout_AggregateIsZero()
    {
        var cfg = MakeCfg();
        var loadout = new EquipLoadout();
        EquipStats total = EquipResolver.ComputeAggregate(loadout, cfg);
        Assert.AreEqual(0f, total.Atk, 0.0001f);
        Assert.AreEqual(0f, total.Hp, 0.0001f);
        Assert.IsTrue(loadout.IsEmpty);
    }

    [Test]
    public void EquipOnePiece_ScalesByQuality()
    {
        var cfg = MakeCfg();
        var loadout = new EquipLoadout();
        // Rare = index 2 => 2x
        loadout.Equip(Piece(EquipSlot.Weapon, EquipQuality.Rare, new EquipStats { Atk = 10f, Hp = 5f }));
        EquipStats total = EquipResolver.ComputeAggregate(loadout, cfg);
        Assert.AreEqual(20f, total.Atk, 0.0001f); // 10 * 2
        Assert.AreEqual(10f, total.Hp, 0.0001f);  // 5 * 2
        Assert.IsFalse(loadout.IsEmpty);
    }

    [Test]
    public void SameBase_HigherQuality_GreaterStats()
    {
        var cfg = MakeCfg();
        var common = new EquipLoadout();
        common.Equip(Piece(EquipSlot.Weapon, EquipQuality.Common, new EquipStats { Atk = 10f }));
        var rare = new EquipLoadout();
        rare.Equip(Piece(EquipSlot.Weapon, EquipQuality.Rare, new EquipStats { Atk = 10f }));

        Assert.AreEqual(10f, EquipResolver.ComputeAggregate(common, cfg).Atk, 0.0001f);
        Assert.AreEqual(20f, EquipResolver.ComputeAggregate(rare, cfg).Atk, 0.0001f);
    }

    [Test]
    public void Unequip_RemovesContribution()
    {
        var cfg = MakeCfg();
        var loadout = new EquipLoadout();
        loadout.Equip(Piece(EquipSlot.Weapon, EquipQuality.Rare, new EquipStats { Atk = 10f }));
        Assert.AreEqual(20f, EquipResolver.ComputeAggregate(loadout, cfg).Atk, 0.0001f);

        EquipPiece removed = loadout.Unequip(EquipSlot.Weapon);
        Assert.IsNotNull(removed);
        Assert.AreEqual(0f, EquipResolver.ComputeAggregate(loadout, cfg).Atk, 0.0001f);
        Assert.IsTrue(loadout.IsEmpty);
    }

    [Test]
    public void FullEightSlots_SumCorrectly()
    {
        var cfg = MakeCfg();
        var loadout = new EquipLoadout();
        foreach (EquipSlot slot in System.Enum.GetValues(typeof(EquipSlot)))
        {
            // Common(1x), base Atk 1 each
            loadout.Equip(Piece(slot, EquipQuality.Common, new EquipStats { Atk = 1f }));
        }
        Assert.AreEqual(8f, EquipResolver.ComputeAggregate(loadout, cfg).Atk, 0.0001f);
    }

    [Test]
    public void EquipNull_Rejected()
    {
        var loadout = new EquipLoadout();
        Assert.IsFalse(loadout.Equip(null));
        Assert.IsTrue(loadout.IsEmpty);
    }

    [Test]
    public void ComputeAggregate_NullGuards_ReturnZero()
    {
        var cfg = MakeCfg();
        var loadout = new EquipLoadout();
        loadout.Equip(Piece(EquipSlot.Weapon, EquipQuality.Rare, new EquipStats { Atk = 10f }));

        // null loadout
        Assert.AreEqual(0f, EquipResolver.ComputeAggregate(null, cfg).Atk, 0.0001f);
        // null config
        Assert.AreEqual(0f, EquipResolver.ComputeAggregate(loadout, null).Atk, 0.0001f);
    }

    [Test]
    public void QualityMul_OutOfRange_ReturnsSafeDefault()
    {
        var cfg = new EquipConfig { QualityMul = new float[] { 1f } }; // 只给 1 个，其余越界
        var loadout = new EquipLoadout();
        loadout.Equip(Piece(EquipSlot.Weapon, EquipQuality.Supreme, new EquipStats { Atk = 10f }));
        // 越界品质 -> 安全默认 1 倍
        Assert.AreEqual(10f, EquipResolver.ComputeAggregate(loadout, cfg).Atk, 0.0001f);
    }
}
