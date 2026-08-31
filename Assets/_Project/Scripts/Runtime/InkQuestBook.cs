using UnityEngine;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>Always-visible, small chapter bookmark. The full quest page remains owned by AdventurePanelsHud.</summary>
    [DisallowMultipleComponent]
    public sealed class InkQuestBook : MonoBehaviour
    {
        private GameObject _canvasGo;
        private Text _summary;
        private PlayerInventory _inventory;
        private FirstChapterRuntime _chapter;

        public static string BuildSummary(FirstChapterStage stage, int wood, int shoot)
        {
            string objective;
            switch (stage)
            {
                case FirstChapterStage.TravelToRoad:
                    objective = "循着前路异动前行";
                    break;
                case FirstChapterStage.DefeatRoadEnemy:
                    objective = "清除拦路妖影";
                    break;
                case FirstChapterStage.MeetGuide:
                    objective = "与竹市引路人交谈";
                    break;
                case FirstChapterStage.VisitShop:
                    objective = "前往竹市小铺";
                    break;
                case FirstChapterStage.Completed:
                    objective = "竹海初试已成";
                    break;
                default:
                    objective = "整装入山";
                    break;
            }
            return "第一章 · 竹海初试\n" + objective + "\n竹材 " + wood + "  嫩笋 " + shoot;
        }

        public void Bind(PlayerInventory inventory, FirstChapterRuntime chapter)
        {
            _inventory = inventory;
            _chapter = chapter;
        }

        private void Awake()
        {
            Build();
        }

        private void LateUpdate()
        {
            Refresh();
        }

        private void OnDestroy()
        {
            if (_canvasGo != null)
            {
                Destroy(_canvasGo);
                _canvasGo = null;
            }
        }

        private void Build()
        {
            if (_canvasGo != null) return;

            _canvasGo = new GameObject("InkQuestBookmarkCanvas");
            _canvasGo.transform.SetParent(transform, false);
            Canvas canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 145;
            CanvasScaler scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Hud.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            Image panel = InkUiFactory.CreatePanel("InkQuestBookmark", _canvasGo.transform, new Vector2(310.0f, 108.0f));
            Hud.Anchor(panel.rectTransform, new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f));
            panel.rectTransform.anchoredPosition = new Vector2(-42.0f, -238.0f);

            _summary = InkUiFactory.CreateText("InkQuestBookmarkText", panel.transform, 16, TextAnchor.UpperLeft, InkUiTheme.Ink);
            Hud.Stretch(_summary.rectTransform, 16.0f);
            _summary.horizontalOverflow = HorizontalWrapMode.Wrap;
            _summary.verticalOverflow = VerticalWrapMode.Overflow;
            Refresh();
        }

        private void Refresh()
        {
            if (_summary == null) return;
            int wood = _inventory != null ? _inventory.Count(PlayerInventory.BambooWood) : 0;
            int shoot = _inventory != null ? _inventory.Count(PlayerInventory.BambooShoot) : 0;
            FirstChapterStage stage = _chapter != null ? _chapter.Stage : FirstChapterStage.Opening;
            _summary.text = BuildSummary(stage, wood, shoot);
        }
    }
}
