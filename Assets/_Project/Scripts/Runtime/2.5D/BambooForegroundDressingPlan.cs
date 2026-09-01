using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>One painted bamboo-clump placement in the movable foreground dressing layer.</summary>
    public readonly struct BambooForegroundDressingPlacement
    {
        public Vector2 Position { get; }
        public float Scale { get; }
        public int SortingOrder { get; }

        public BambooForegroundDressingPlacement(Vector2 position, float scale, int sortingOrder)
        {
            Position = position;
            Scale = scale;
            SortingOrder = sortingOrder;
        }
    }

    /// <summary>
    /// Deterministic hand-composed dressing for the starting road. It creates the feeling of a denser
    /// bamboo grove without placing opaque clumps over the central player, encounter, and dialogue lane.
    /// </summary>
    public static class BambooForegroundDressingPlan
    {
        public static BambooForegroundDressingPlacement[] Create(float halfExtent)
        {
            float extent = Mathf.Max(360f, halfExtent);
            return new[]
            {
                new BambooForegroundDressingPlacement(new Vector2(-extent * 0.47f, extent * 0.08f), 15.0f, -49),
                new BambooForegroundDressingPlacement(new Vector2(-extent * 0.58f, extent * 0.39f), 11.4f, -50),
                new BambooForegroundDressingPlacement(new Vector2(-extent * 0.53f, -extent * 0.34f), 10.8f, -48),
                new BambooForegroundDressingPlacement(new Vector2(extent * 0.50f, extent * 0.31f), 10.2f, -50),
                new BambooForegroundDressingPlacement(new Vector2(extent * 0.60f, -extent * 0.24f), 9.6f, -47),
                new BambooForegroundDressingPlacement(new Vector2(extent * 0.48f, extent * 0.61f), 8.8f, -46),
            };
        }
    }
}
