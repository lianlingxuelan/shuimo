using System;
using System.Reflection;
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

        [Test]
        public void InputMovement_ClampedAtWorldEdge_ReportsBlockedDirection()
        {
            GameObject host = new GameObject("BoundaryInputTest");
            try
            {
                ConfigureWorldBounds();
                PlayerController player = host.AddComponent<PlayerController>();
                player.transform.position = new Vector3(WorldBuilder.WorldWidth - WorldBuilder.TileUnit * 0.5f, 160f, 0f);
                typeof(PlayerController).GetProperty("MoveDir").SetValue(player, Vector2.right, null);

                InvokePrivate(player, "Move", 1f);

                Assert.IsTrue(player.BoundaryBlockedThisFrame);
                Assert.AreEqual(Vector2.right, player.BoundaryDirection);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                ClearWorldBounds();
            }
        }

        [Test]
        public void MoveExternal_ClampedAtWorldEdge_DoesNotReportBlocked()
        {
            GameObject host = new GameObject("BoundaryExternalTest");
            try
            {
                ConfigureWorldBounds();
                PlayerController player = host.AddComponent<PlayerController>();
                player.transform.position = new Vector3(WorldBuilder.WorldWidth - WorldBuilder.TileUnit * 0.5f, 160f, 0f);

                player.MoveExternal(new Vector2(PlayerController.MoveSpeed, 0f), 1f);

                Assert.IsFalse(player.BoundaryBlockedThisFrame);
                Assert.AreEqual(Vector2.zero, player.BoundaryDirection);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                ClearWorldBounds();
            }
        }

        [Test]
        public void MoveExternal_AfterInputWasBlocked_PreservesInputBoundaryState()
        {
            GameObject host = new GameObject("BoundaryInputThenExternalTest");
            try
            {
                ConfigureWorldBounds();
                PlayerController player = host.AddComponent<PlayerController>();
                player.transform.position = new Vector3(WorldBuilder.WorldWidth - WorldBuilder.TileUnit * 0.5f, 160f, 0f);
                typeof(PlayerController).GetProperty("MoveDir").SetValue(player, Vector2.right, null);
                InvokePrivate(player, "Move", 1f);

                player.MoveExternal(new Vector2(PlayerController.MoveSpeed, 0f), 1f);

                Assert.IsTrue(player.BoundaryBlockedThisFrame);
                Assert.AreEqual(Vector2.right, player.BoundaryDirection);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                ClearWorldBounds();
            }
        }

        [Test]
        public void WorldBoundaryFeedback_ThrottlesTriggeredAndKeepsLastDirection()
        {
            GameObject playerHost = new GameObject("BoundaryFeedbackPlayerTest");
            GameObject feedbackHost = new GameObject("BoundaryFeedbackTest");
            try
            {
                ConfigureWorldBounds();
                PlayerController player = playerHost.AddComponent<PlayerController>();
                player.transform.position = new Vector3(WorldBuilder.WorldWidth - WorldBuilder.TileUnit * 0.5f, 160f, 0f);
                typeof(PlayerController).GetProperty("MoveDir").SetValue(player, Vector2.right, null);
                InvokePrivate(player, "Move", 1f);

                WorldBoundaryFeedback feedback = feedbackHost.AddComponent<WorldBoundaryFeedback>();
                int triggerCount = 0;
                Vector2 triggeredDirection = Vector2.zero;
                feedback.Triggered += direction =>
                {
                    triggerCount++;
                    triggeredDirection = direction;
                };
                feedback.Bind(player);

                InvokePrivate(feedback, "Update");
                InvokePrivate(feedback, "Update");

                Assert.AreEqual(1, triggerCount);
                Assert.AreEqual(Vector2.right, triggeredDirection);
                Assert.AreEqual(Vector2.right, feedback.LastDirection);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(playerHost);
                UnityEngine.Object.DestroyImmediate(feedbackHost);
                ClearWorldBounds();
            }
        }

        private static void ConfigureWorldBounds()
        {
            Type worldBuilder = typeof(WorldBuilder);
            PropertyInfo grid = worldBuilder.GetProperty("Grid");
            Array generatedGrid = Array.CreateInstance(grid.PropertyType.GetElementType(), 1);
            grid.SetValue(null, generatedGrid, null);
            worldBuilder.GetProperty("Width").SetValue(null, 10, null);
            worldBuilder.GetProperty("Height").SetValue(null, 10, null);
        }

        private static void ClearWorldBounds()
        {
            Type worldBuilder = typeof(WorldBuilder);
            worldBuilder.GetProperty("Grid").SetValue(null, null, null);
            worldBuilder.GetProperty("Width").SetValue(null, 0, null);
            worldBuilder.GetProperty("Height").SetValue(null, 0, null);
        }

        private static void InvokePrivate(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(target, arguments);
        }
    }
}
