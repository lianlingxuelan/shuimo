using UnityEngine;

namespace Xianxia.Unity.T2
{
    public static class MinimapProjection
    {
        public static Vector2 Project(Vector2 worldPosition, Vector2 worldSize, float iconPadding01)
        {
            if (worldSize.x <= 0f || worldSize.y <= 0f ||
                float.IsNaN(worldSize.x) || float.IsNaN(worldSize.y) ||
                float.IsInfinity(worldSize.x) || float.IsInfinity(worldSize.y))
            {
                return new Vector2(0.5f, 0.5f);
            }

            float pad = Mathf.Clamp(iconPadding01, 0f, 0.49f);
            float x = Mathf.Lerp(pad, 1f - pad, Mathf.Clamp01(worldPosition.x / worldSize.x));
            float y = Mathf.Lerp(pad, 1f - pad, Mathf.Clamp01(worldPosition.y / worldSize.y));
            return new Vector2(x, y);
        }
    }
}
