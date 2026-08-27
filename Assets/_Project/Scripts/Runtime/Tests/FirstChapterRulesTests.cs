using NUnit.Framework;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class FirstChapterRulesTests
    {
        [Test]
        public void Advance_WhenChapterIsShown_StartsTravel()
        {
            Assert.AreEqual(FirstChapterStage.TravelToRoad,
                FirstChapterRules.Advance(FirstChapterStage.Opening, FirstChapterEvent.ChapterShown));
        }

        [Test]
        public void Advance_WhenPlayerReachesRoad_EntersRoadEnemyStage()
        {
            Assert.AreEqual(FirstChapterStage.DefeatRoadEnemy,
                FirstChapterRules.Advance(FirstChapterStage.TravelToRoad, FirstChapterEvent.RoadReached));
        }

        [Test]
        public void Advance_WhenEnemyDeathOccursBeforeRoadReached_DoesNotSkipTravel()
        {
            Assert.AreEqual(FirstChapterStage.TravelToRoad,
                FirstChapterRules.Advance(FirstChapterStage.TravelToRoad, FirstChapterEvent.RoadEnemyDefeated));
        }

        [Test]
        public void Advance_WhenShopIsOnlyOpened_MovesToVisitShopBeforeCompletion()
        {
            Assert.AreEqual(FirstChapterStage.VisitShop,
                FirstChapterRules.Advance(FirstChapterStage.MeetGuide, FirstChapterEvent.ShopOpened));
            Assert.AreEqual(FirstChapterStage.Completed,
                FirstChapterRules.Advance(FirstChapterStage.VisitShop, FirstChapterEvent.ShopVisited));
        }

        [Test]
        public void GuideAndCompletionGates_OnlyOpenAtTheirExpectedStages()
        {
            Assert.IsFalse(FirstChapterRules.CanOpenGuide(FirstChapterStage.DefeatRoadEnemy));
            Assert.IsTrue(FirstChapterRules.CanOpenGuide(FirstChapterStage.MeetGuide));
            Assert.IsFalse(FirstChapterRules.CanCompleteFromShop(FirstChapterStage.MeetGuide));
            Assert.IsTrue(FirstChapterRules.CanCompleteFromShop(FirstChapterStage.VisitShop));
        }
    }
}
