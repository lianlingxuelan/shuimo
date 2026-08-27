using NUnit.Framework;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class FallbackCutoutPoseTests
    {
        [Test]
        public void Attack_HasVisibleWindupAndFollowThrough()
        {
            CutoutPose windup = FallbackCutoutPose.Evaluate(CharacterAnimState.Attack, 0.12f);
            CutoutPose followThrough = FallbackCutoutPose.Evaluate(CharacterAnimState.Attack, 0.55f);

            Assert.Greater(windup.TiltDeg, 8.0f,
                "单张立绘的挥剑前摇必须后仰，玩家才能看出动作开始。");
            Assert.Less(followThrough.TiltDeg, -24.0f,
                "单张立绘的挥剑中段必须有明确前挥，不能只是微小缩放。");
        }

        [Test]
        public void Attack_ReturnsToNeutralAtTheEnd()
        {
            CutoutPose recovery = FallbackCutoutPose.Evaluate(CharacterAnimState.Attack, 1.0f);

            Assert.AreEqual(0.0f, recovery.TiltDeg, 1e-4f,
                "挥剑结束必须回到中性姿势，不能永久歪斜。");
        }
    }
}
