using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>水墨地表的确定性布局数据，不依赖渲染对象，便于单元测试。</summary>
    public static class InkGroundLayout
    {
        /// <summary>生成一条由画面下方通往上方的轻微曲折道路锚点。</summary>
        public static Vector2[] CreateRoadAnchors(float halfExtent)
        {
            float extent = Mathf.Max(1.0f, halfExtent);
            return new[]
            {
                new Vector2(-extent * 0.14f, -extent * 0.92f),
                new Vector2(extent * 0.20f, -extent * 0.38f),
                new Vector2(-extent * 0.16f, extent * 0.20f),
                new Vector2(extent * 0.10f, extent * 0.84f),
            };
        }

        /// <summary>主景太湖石放在道路右侧，不压住人物留白。</summary>
        public static Vector2 CreateHeroRockPosition(float halfExtent)
        {
            float extent = Mathf.Max(1.0f, halfExtent);
            return new Vector2(extent * 0.34f, -extent * 0.12f);
        }
    }
}
