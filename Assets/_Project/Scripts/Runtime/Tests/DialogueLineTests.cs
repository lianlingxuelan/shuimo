using NUnit.Framework;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class DialogueLineTests
    {
        [Test]
        public void GuideIntro_HasSpeakerBodyAndTwoChoices()
        {
            DialogueLine line = DialogueLine.CreateGuideIntro();

            Assert.That(line.Speaker, Is.Not.Empty);
            Assert.That(line.Body, Is.Not.Empty);
            Assert.That(line.Choices, Has.Length.EqualTo(2));
        }
    }
}
