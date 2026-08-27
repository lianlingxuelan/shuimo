using NUnit.Framework;
using UnityEngine;

namespace Xianxia.Unity.T2.Tests
{
    public sealed class BoundaryFeedbackRulesTests
    {
        [Test]
        public void WasBlocked_RequiresMovementInputAndAClampedAxis()
        {
            Assert.IsTrue(BoundaryFeedbackRules.WasBlocked(new Vector2(110f, 50f), new Vector2(100f, 50f), Vector2.right));
            Assert.IsFalse(BoundaryFeedbackRules.WasBlocked(new Vector2(100f, 50f), new Vector2(100f, 50f), Vector2.right));
            Assert.IsFalse(BoundaryFeedbackRules.WasBlocked(new Vector2(110f, 50f), new Vector2(100f, 50f), Vector2.zero));
        }

        [Test]
        public void CanNotify_ThrottlesContinuousBoundaryPressure()
        {
            Assert.IsTrue(BoundaryFeedbackRules.CanNotify(5f, 3f, 1.5f));
            Assert.IsFalse(BoundaryFeedbackRules.CanNotify(4f, 3f, 1.5f));
        }
    }
}
