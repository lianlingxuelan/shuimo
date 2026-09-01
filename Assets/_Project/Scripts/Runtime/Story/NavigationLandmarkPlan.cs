using UnityEngine;

namespace Xianxia.Unity.T2
{
    public enum NavigationLandmarkKind
    {
        BambooClump,
        ScholarRock,
        Sign,
        Lantern,
        Basket,
        BuildingSilhouette,
    }

    /// <summary>One deterministic, non-blocking piece of first-chapter road dressing.</summary>
    public readonly struct NavigationLandmarkPlacement
    {
        public NavigationLandmarkKind Kind { get; }
        public Vector2 Position { get; }
        public float Scale { get; }
        public int SortingOrder { get; }
        public bool BlocksRoadCenter { get; }

        public NavigationLandmarkPlacement(
            NavigationLandmarkKind kind,
            Vector2 position,
            float scale,
            int sortingOrder,
            bool blocksRoadCenter)
        {
            Kind = kind;
            Position = position;
            Scale = scale;
            SortingOrder = sortingOrder;
            BlocksRoadCenter = blocksRoadCenter;
        }
    }

    /// <summary>
    /// Hand-composed road landmarks with a tiny seeded variation pass. The anchors carry the composition;
    /// the supplied seed only softens repetition, so replaying a chapter always recreates the same road.
    /// </summary>
    public static class NavigationLandmarkPlan
    {
        private const float PositionJitterX = 14f;
        private const float PositionJitterY = 18f;
        private const float ScaleJitter = 0.06f;

        private readonly struct Anchor
        {
            public NavigationLandmarkKind Kind { get; }
            public Vector2 Offset { get; }
            public float Scale { get; }
            public int SortingOrder { get; }

            public Anchor(NavigationLandmarkKind kind, float x, float y, float scale, int sortingOrder)
            {
                Kind = kind;
                Offset = new Vector2(x, y);
                Scale = scale;
                SortingOrder = sortingOrder;
            }
        }

        private static readonly Anchor[] Anchors =
        {
            new Anchor(NavigationLandmarkKind.BambooClump,       -420f,  100f, 1.05f, -38),
            new Anchor(NavigationLandmarkKind.BambooClump,        390f,  160f, 0.96f, -39),
            new Anchor(NavigationLandmarkKind.ScholarRock,       -300f,  280f, 0.92f, -37),
            new Anchor(NavigationLandmarkKind.ScholarRock,        330f,  370f, 1.02f, -36),
            new Anchor(NavigationLandmarkKind.BuildingSilhouette,-560f,  420f, 0.94f, -35),
            new Anchor(NavigationLandmarkKind.BuildingSilhouette, 570f,  470f, 1.04f, -34),
            new Anchor(NavigationLandmarkKind.Sign,               -350f, 620f, 0.93f, -33),
            new Anchor(NavigationLandmarkKind.Sign,                520f, 650f, 1.00f, -32),
            new Anchor(NavigationLandmarkKind.Lantern,            -230f, 840f, 0.91f, -31),
            new Anchor(NavigationLandmarkKind.Lantern,             210f,1010f, 1.03f, -30),
            new Anchor(NavigationLandmarkKind.Basket,             -350f,1080f, 0.90f, -29),
            new Anchor(NavigationLandmarkKind.Basket,              360f,1120f, 1.01f, -28),
            new Anchor(NavigationLandmarkKind.BambooClump,        -460f,1320f, 1.08f, -27),
            new Anchor(NavigationLandmarkKind.ScholarRock,         440f,1380f, 0.95f, -26),
        };

        public static NavigationLandmarkPlacement[] Create(FirstChapterLayout layout, uint seed)
        {
            if (layout == null)
            {
                return new NavigationLandmarkPlacement[0];
            }

            uint state = seed != 0u ? seed : 0x6D2B79F5u;
            Vector2 origin = new Vector2(layout.RoadCenterX, layout.RoadEncounter.y - 420f);
            NavigationLandmarkPlacement[] result = new NavigationLandmarkPlacement[Anchors.Length];

            for (int i = 0; i < Anchors.Length; i++)
            {
                Anchor anchor = Anchors[i];
                Vector2 jitter = new Vector2(
                    NextSigned(ref state) * PositionJitterX,
                    NextSigned(ref state) * PositionJitterY);
                Vector2 position = origin + anchor.Offset + jitter;
                float scale = anchor.Scale + NextSigned(ref state) * ScaleJitter;

                result[i] = new NavigationLandmarkPlacement(
                    anchor.Kind,
                    position,
                    scale,
                    anchor.SortingOrder,
                    layout.IsInRoadLane(position));
            }

            return result;
        }

        private static float NextSigned(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0x00FFFFFFu) / 8388607.5f - 1f;
        }
    }
}
