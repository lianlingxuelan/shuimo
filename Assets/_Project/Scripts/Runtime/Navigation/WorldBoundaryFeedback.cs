using System;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>把玩家的边界阻挡状态转成可供 HUD/VFX 消费的节流事件。</summary>
    [DisallowMultipleComponent]
    public sealed class WorldBoundaryFeedback : MonoBehaviour
    {
        public const float Cooldown = 1.5f;

        [SerializeField] private PlayerController player;

        private float _lastNotificationTime = float.NegativeInfinity;

        /// <summary>最近一次实际触发的边界方向。</summary>
        public Vector2 LastDirection { get; private set; }

        /// <summary>边界阻挡通过冷却后触发，参数为阻挡方向。</summary>
        public event Action<Vector2> Triggered;

        /// <summary>绑定唯一的玩家控制器，供场景装配代码显式注入。</summary>
        public void Bind(PlayerController controller)
        {
            player = controller;
        }

        private void Update()
        {
            if (player == null || !player.BoundaryBlockedThisFrame)
            {
                return;
            }

            Vector2 direction = player.BoundaryDirection;
            if (direction.sqrMagnitude <= 0.0f
                || !BoundaryFeedbackRules.CanNotify(Time.unscaledTime, _lastNotificationTime, Cooldown))
            {
                return;
            }

            _lastNotificationTime = Time.unscaledTime;
            LastDirection = direction;
            Action<Vector2> handler = Triggered;
            if (handler != null)
            {
                handler(direction);
            }
        }
    }
}
