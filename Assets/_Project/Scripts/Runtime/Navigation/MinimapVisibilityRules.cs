using UnityEngine;

namespace Xianxia.Unity.T2
{
    public enum MinimapMarkerKind
    {
        Player,
        ChapterEnemy,
        QuestTarget,
        Enemy,
        Npc,
        Shop,
        Building,
    }

    public static class MinimapVisibilityRules
    {
        public static bool ShouldShow(MinimapMarkerKind kind, Vector2 player, Vector2 marker, float revealRadius)
        {
            if (kind == MinimapMarkerKind.Player ||
                kind == MinimapMarkerKind.ChapterEnemy ||
                kind == MinimapMarkerKind.QuestTarget ||
                kind == MinimapMarkerKind.Npc ||
                kind == MinimapMarkerKind.Shop ||
                kind == MinimapMarkerKind.Building)
            {
                return true;
            }

            float radius = Mathf.Max(0f, revealRadius);
            return (marker - player).sqrMagnitude <= radius * radius;
        }
    }
}
