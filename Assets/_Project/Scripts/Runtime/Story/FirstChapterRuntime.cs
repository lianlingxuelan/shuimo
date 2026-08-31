using System.Collections.Generic;
using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// Unity adapter for the pure first-chapter state rules. It owns only authored locations, navigation
    /// markers and story enemy placement; combat, movement and damage remain with the existing systems.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FirstChapterRuntime : MonoBehaviour
    {
        public const float RoadTriggerRadius = 120.0f;
        public const float GuideTriggerRadius = 110.0f;
        public const float ShopTriggerRadius = 135.0f;
        public const float ShopSafeRadius = 220.0f;
        public const float MinimumPatrolSeparation = 180.0f;

        public const string GuideMarkerId = "chapter_guide";
        public const string ShopMarkerId = "chapter_shop";
        public const string BuildingMarkerId = "chapter_inn";
        public const string QuestMarkerId = "chapter_road_goal";

        private readonly List<Transform> _patrolEnemies = new List<Transform>();

        [SerializeField] private Transform player;
        [SerializeField] private EnemyNpcSpawner enemySpawner;
        [SerializeField] private InkDialogueHud dialogueHud;

        private bool _built;
        private Transform _guideNpc;
        private Transform _shop;
        private Transform _building;
        private Transform _questTarget;

        public FirstChapterLayout Layout { get; private set; }

        public FirstChapterStage Stage { get; private set; } = FirstChapterStage.Opening;

        public Transform TeachingEnemy { get; private set; }

        public IReadOnlyList<Transform> PatrolEnemies
        {
            get { return _patrolEnemies; }
        }

        public Transform GuideNpc
        {
            get { return _guideNpc; }
        }

        /// <summary>Explicit assembly binding avoids runtime scene searches in the normal generated-world path.</summary>
        public void Bind(Transform playerTransform, EnemyNpcSpawner spawner)
        {
            player = playerTransform;
            enemySpawner = spawner;
        }

        /// <summary>Visual dialogue is optional so the story loop remains usable in headless tests.</summary>
        public void BindDialogue(InkDialogueHud hud)
        {
            dialogueHud = hud;
        }

        /// <summary>Builds authored chapter entities once from the same authoritative layout as navigation dressing.</summary>
        public void Build()
        {
            if (_built || player == null || enemySpawner == null)
            {
                return;
            }

            Layout = FirstChapterLayout.Build(player.position);
            TeachingEnemy = enemySpawner.SpawnStoryEnemy(
                Layout.RoadEncounter,
                true,
                Layout.RoadEncounter,
                75.0f,
                0xC101u);

            _patrolEnemies.Add(enemySpawner.SpawnStoryEnemy(
                Layout.RoadEncounter + new Vector2(0.0f, 280.0f),
                false,
                Layout.RoadEncounter + new Vector2(0.0f, 280.0f),
                90.0f,
                0xC102u));
            _patrolEnemies.Add(enemySpawner.SpawnStoryEnemy(
                Layout.RoadEncounter + new Vector2(85.0f, 540.0f),
                false,
                Layout.RoadEncounter + new Vector2(85.0f, 540.0f),
                95.0f,
                0xC103u));

            _questTarget = CreateLocation("FirstChapterQuestTarget", Layout.RoadEncounter);
            ConfigureMarker(_questTarget, MinimapMarkerKind.QuestTarget, QuestMarkerId, "前路异动");

            _guideNpc = CreateLocation("FirstChapterGuide", Layout.GuideNpc);
            ConfigureMarker(_guideNpc, MinimapMarkerKind.Npc, GuideMarkerId, "竹市引路人");
            _guideNpc.gameObject.SetActive(false);

            _shop = CreateLocation("FirstChapterShop", Layout.Shop);
            ConfigureMarker(_shop, MinimapMarkerKind.Shop, ShopMarkerId, "竹市小铺");
            _shop.gameObject.SetActive(false);

            _building = CreateLocation("FirstChapterInn", Layout.InnPlaceholder);
            ConfigureMarker(_building, MinimapMarkerKind.Building, BuildingMarkerId, "竹海客栈");
            _building.gameObject.SetActive(false);

            _built = true;
            Advance(FirstChapterEvent.ChapterShown);
        }

        private void Update()
        {
            if (!_built || player == null)
            {
                return;
            }

            Vector2 playerPosition = player.position;
            if (Stage == FirstChapterStage.TravelToRoad && IsNear(playerPosition, Layout.RoadEncounter, RoadTriggerRadius))
            {
                Advance(FirstChapterEvent.RoadReached);
            }
            if (Stage == FirstChapterStage.DefeatRoadEnemy && TeachingEnemy == null)
            {
                Advance(FirstChapterEvent.RoadEnemyDefeated);
                RevealGuide();
            }
            if (Stage == FirstChapterStage.MeetGuide && IsNear(playerPosition, Layout.GuideNpc, GuideTriggerRadius))
            {
                Advance(FirstChapterEvent.ShopOpened);
                RevealShopAndBuilding();
            }
            if (Stage == FirstChapterStage.VisitShop && IsNear(playerPosition, Layout.Shop, ShopTriggerRadius))
            {
                Advance(FirstChapterEvent.ShopVisited);
            }
        }

        private void Advance(FirstChapterEvent occurred)
        {
            FirstChapterStage before = Stage;
            Stage = FirstChapterRules.Advance(Stage, occurred);
            if (Stage != before)
            {
                ShowNarrativeFor(occurred);
            }
        }

        private void RevealGuide()
        {
            if (_guideNpc != null)
            {
                _guideNpc.gameObject.SetActive(true);
            }
        }

        private void RevealShopAndBuilding()
        {
            if (_shop != null)
            {
                _shop.gameObject.SetActive(true);
            }
            if (_building != null)
            {
                _building.gameObject.SetActive(true);
            }
        }

        private void ShowNarrativeFor(FirstChapterEvent occurred)
        {
            if (dialogueHud == null)
            {
                return;
            }

            FirstChapterNarrativeBeat beat;
            if (FirstChapterNarrativeSchedule.TryGet(occurred, out beat))
            {
                dialogueHud.Show(FirstChapterNarrative.For(beat));
            }
        }

        private Transform CreateLocation(string name, Vector2 position)
        {
            GameObject location = new GameObject(name);
            location.transform.SetParent(transform, false);
            location.transform.localPosition = new Vector3(position.x, position.y, 0.0f);
            return location.transform;
        }

        private static void ConfigureMarker(Transform location, MinimapMarkerKind kind, string stableId, string displayName)
        {
            MinimapMarker marker = location.GetComponent<MinimapMarker>();
            if (marker == null)
            {
                marker = location.gameObject.AddComponent<MinimapMarker>();
            }
            marker.Configure(kind, stableId, displayName, true);
        }

        private static bool IsNear(Vector2 a, Vector2 b, float radius)
        {
            return (a - b).sqrMagnitude <= radius * radius;
        }
    }
}
