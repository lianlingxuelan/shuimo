using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>世界边界反馈的无状态判定。</summary>
    public static class BoundaryFeedbackRules
    {
        private const float ClampDifferenceSqrEpsilon = 0.0001f;

        /// <summary>尝试移动且该移动被世界边界钳制时返回 true。</summary>
        public static bool WasBlocked(Vector2 attemptedPosition, Vector2 clampedPosition, Vector2 inputDirection)
        {
            return inputDirection.sqrMagnitude > 0.0f
                && (attemptedPosition - clampedPosition).sqrMagnitude > ClampDifferenceSqrEpsilon;
        }

        /// <summary>冷却期结束时允许下一次边界反馈。</summary>
        public static bool CanNotify(float now, float lastNotification, float cooldown)
        {
            return now - lastNotification >= cooldown;
        }
    }
}
