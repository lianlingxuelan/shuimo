// -----------------------------------------------------------------------------
// RealmTests.cs —— 境界系统 EditMode 单测（headless，不进游戏）
//
// 和 PlayerProgression / Morality / Personality 的测试同思路：
// 纯逻辑系统「同输入必同输出」，把每条推进边和边界钉死，PlayMode 里肉眼难复现的
// 「卡在某一层 / 突破失败 / 越界」类 bug 在单测里一次性覆盖。
// -----------------------------------------------------------------------------

using NUnit.Framework;
using Xianxia.Cultivation;

[TestFixture]
public class RealmTests
{
    private static RealmConfig MakeCfg()
    {
        return new RealmConfig
        {
            Names = new[] { "炼气", "筑基", "金丹", "元婴", "化神" },
            Thresholds = new[] { 0f, 100f, 300f, 700f, 1500f },
            NeedsBreakthrough = new[] { false, false, true, false, true },
        };
    }

    [Test]
    public void 出生在第0层_名字取配置首项()
    {
        var sys = new RealmSystem(MakeCfg());
        Assert.AreEqual(0, sys.State.TierIndex);
        Assert.AreEqual("炼气", sys.CurrentName);
        Assert.IsFalse(sys.State.BreakthroughPending);
    }

    [Test]
    public void 跨过门槛自动进层_非gate层不停留()
    {
        var sys = new RealmSystem(MakeCfg());
        sys.AddCultivation(100f); // 进筑基(索引1)，非 gate
        Assert.AreEqual(1, sys.State.TierIndex);
        Assert.AreEqual("筑基", sys.CurrentName);
        Assert.IsFalse(sys.State.BreakthroughPending);
    }

    [Test]
    public void 进入gate层只置Pending_不自动穿过()
    {
        var sys = new RealmSystem(MakeCfg());
        sys.AddCultivation(300f); // 到金丹(索引2, NeedsBreakthrough=true)
        Assert.AreEqual(2, sys.State.TierIndex);
        Assert.IsTrue(sys.State.BreakthroughPending);
        // 即使点数远超下一层门槛，未突破也应停在本层、不自动跨过守卫
        sys.AddCultivation(500f);
        Assert.AreEqual(2, sys.State.TierIndex);
    }

    [Test]
    public void 突破成功清除Pending_非gate层TryBreakthrough返回false()
    {
        var sys = new RealmSystem(MakeCfg());
        sys.AddCultivation(100f); // 筑基，非 gate
        Assert.IsFalse(sys.TryBreakthrough()); // 没 Pending，直接 false

        sys.AddCultivation(200f); // 累计 300 -> 金丹(gate)
        Assert.IsTrue(sys.State.BreakthroughPending);
        Assert.IsTrue(sys.TryBreakthrough());   // 突破成功
        Assert.IsFalse(sys.State.BreakthroughPending);
        Assert.IsTrue(sys.State.BrokenThrough[2]);
    }

    [Test]
    public void 层内进度比例正确_顶层恒为1()
    {
        var sys = new RealmSystem(MakeCfg());
        sys.AddCultivation(50f); // 炼气内，0..100 -> 0.5
        Assert.AreEqual(0.5f, sys.ProgressInTier(), 1e-4f);

        sys.AddCultivation(1450f); // 累计 1500 -> 顶层化神，无下一层
        Assert.AreEqual(4, sys.State.TierIndex);
        Assert.IsFalse(sys.HasNextTier);
        Assert.AreEqual(1f, sys.ProgressInTier());
    }

    [Test]
    public void 海量点数不越界_停在最高层()
    {
        var sys = new RealmSystem(MakeCfg());
        sys.AddCultivation(999999f);
        Assert.AreEqual(4, sys.State.TierIndex); // 不超过化神(索引4)
        Assert.IsTrue(sys.State.BreakthroughPending); // 化神是 gate 层
    }
}
