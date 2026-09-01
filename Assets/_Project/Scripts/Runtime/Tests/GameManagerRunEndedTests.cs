using NUnit.Framework;
using UnityEngine;
using Xianxia.Unity.T2.Core;

namespace Xianxia.Unity.T2.Tests
{
    [TestFixture]
    public sealed class GameManagerRunEndedTests
    {
        [TearDown]
        public void TearDown()
        {
            EventManager.Clear();
            if (GameManager.Instance != null)
            {
                Object.DestroyImmediate(GameManager.Instance.gameObject);
            }
        }

        [Test]
        public void NotifyRunEnded_PublishesOutcomeExactlyOnce()
        {
            int count = 0;
            bool won = true;
            EventManager.Subscribe<RunEndedEvent>(evt =>
            {
                count++;
                won = evt.Won;
            });
            GameManager manager = GameManager.Ensure();
            manager.NotifyRunStarted();

            manager.NotifyRunEnded(false);
            manager.NotifyRunEnded(false);

            Assert.AreEqual(GamePhase.GameOver, manager.Phase);
            Assert.AreEqual(1, count, "重复终局通知不能让死亡或胜利表现重播。");
            Assert.IsFalse(won, "RunEndedEvent 必须保留内核给出的失败结果。");
        }
    }
}
