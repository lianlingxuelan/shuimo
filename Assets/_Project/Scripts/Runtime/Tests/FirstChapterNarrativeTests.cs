using NUnit.Framework;

namespace Xianxia.Unity.T2.Tests
{
    public sealed class FirstChapterNarrativeTests
    {
        [Test]
        public void For_OpeningExplainsWhyThePlayerLeavesTheMountain()
        {
            DialogueLine line = FirstChapterNarrative.For(FirstChapterNarrativeBeat.Opening);

            Assert.AreEqual("墨尘子", line.Speaker);
            StringAssert.Contains("下山", line.Body);
        }

        [Test]
        public void For_GuideOffersTwoMeaningfulResponses()
        {
            DialogueLine line = FirstChapterNarrative.For(FirstChapterNarrativeBeat.Guide);

            Assert.AreEqual("竹市引路人", line.Speaker);
            Assert.AreEqual(2, line.Choices.Length);
            Assert.IsFalse(string.IsNullOrEmpty(line.Choices[0]));
            Assert.IsFalse(string.IsNullOrEmpty(line.Choices[1]));
        }

        [Test]
        public void For_ChapterCompletePointsThePlayerTowardTheVillageIllness()
        {
            DialogueLine line = FirstChapterNarrative.For(FirstChapterNarrativeBeat.ChapterComplete);

            StringAssert.Contains("青竹村", line.Body);
            StringAssert.Contains("病", line.Body);
        }

        [TestCase(FirstChapterEvent.ChapterShown, FirstChapterNarrativeBeat.Opening)]
        [TestCase(FirstChapterEvent.RoadReached, FirstChapterNarrativeBeat.RoadEncounter)]
        [TestCase(FirstChapterEvent.RoadEnemyDefeated, FirstChapterNarrativeBeat.RoadAftermath)]
        [TestCase(FirstChapterEvent.ShopOpened, FirstChapterNarrativeBeat.Guide)]
        [TestCase(FirstChapterEvent.ShopVisited, FirstChapterNarrativeBeat.ChapterComplete)]
        public void Schedule_MapsEveryPlayableChapterEventToItsNarrativeBeat(
            FirstChapterEvent occurred,
            FirstChapterNarrativeBeat expected)
        {
            FirstChapterNarrativeBeat actual;

            Assert.IsTrue(FirstChapterNarrativeSchedule.TryGet(occurred, out actual));
            Assert.AreEqual(expected, actual);
        }
    }
}
