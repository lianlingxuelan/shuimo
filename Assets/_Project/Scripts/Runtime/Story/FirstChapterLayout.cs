using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// First chapter story-node placement expressed relative to the generated world's player spawn.
    /// Keeping these positions together prevents scenery density from hiding the encounter or dialogue beats.
    /// </summary>
    public sealed class FirstChapterLayout
    {
        public const float RoadHalfWidth = 140f;
        public const float StoryNodeReserveRadius = 145f;

        public Vector2 RoadEncounter { get; }
        public Vector2 GuideNpc { get; }
        public Vector2 Shop { get; }
        public Vector2 InnPlaceholder { get; }
        public Vector2 HerbPlaceholder { get; }
        public Vector2 GatePlaceholder { get; }
        public float RoadCenterX => roadCenterX;

        private readonly float roadCenterX;

        private FirstChapterLayout(
            float roadCenterX,
            Vector2 roadEncounter,
            Vector2 guideNpc,
            Vector2 shop,
            Vector2 innPlaceholder,
            Vector2 herbPlaceholder,
            Vector2 gatePlaceholder)
        {
            this.roadCenterX = roadCenterX;
            RoadEncounter = roadEncounter;
            GuideNpc = guideNpc;
            Shop = shop;
            InnPlaceholder = innPlaceholder;
            HerbPlaceholder = herbPlaceholder;
            GatePlaceholder = gatePlaceholder;
        }

        public static FirstChapterLayout Build(Vector2 playerSpawn)
        {
            return new FirstChapterLayout(
                playerSpawn.x,
                playerSpawn + new Vector2(0f, 420f),
                playerSpawn + new Vector2(-120f, 570f),
                playerSpawn + new Vector2(285f, 640f),
                playerSpawn + new Vector2(-420f, 860f),
                playerSpawn + new Vector2(420f, 850f),
                playerSpawn + new Vector2(0f, 1180f));
        }

        public bool IsInRoadLane(Vector2 position)
        {
            return Mathf.Abs(position.x - roadCenterX) <= RoadHalfWidth;
        }

        public bool IsNearReservedStoryNode(Vector2 position)
        {
            float reserveSqr = StoryNodeReserveRadius * StoryNodeReserveRadius;
            return (position - RoadEncounter).sqrMagnitude <= reserveSqr
                || (position - GuideNpc).sqrMagnitude <= reserveSqr;
        }

        /// <summary>
        /// Returns whether scenery would crowd any authored first-chapter anchor. This broader check is
        /// for static navigation dressing; the narrower reserved-node rule remains the combat/dialogue seam.
        /// </summary>
        public bool IsNearAnyChapterNode(Vector2 position)
        {
            float reserveSqr = StoryNodeReserveRadius * StoryNodeReserveRadius;
            return (position - RoadEncounter).sqrMagnitude <= reserveSqr
                || (position - GuideNpc).sqrMagnitude <= reserveSqr
                || (position - Shop).sqrMagnitude <= reserveSqr
                || (position - InnPlaceholder).sqrMagnitude <= reserveSqr
                || (position - HerbPlaceholder).sqrMagnitude <= reserveSqr
                || (position - GatePlaceholder).sqrMagnitude <= reserveSqr;
        }
    }
}
