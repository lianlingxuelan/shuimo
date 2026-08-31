using NUnit.Framework;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class InkQuestBookTests
    {
        [Test]
        public void Summary_UsesChapterTitleAndCurrentObjective()
        {
            string summary = InkQuestBook.BuildSummary(FirstChapterStage.TravelToRoad, 2, 1);

            StringAssert.Contains("竹海初试", summary);
            StringAssert.Contains("前路", summary);
        }
    }
}
