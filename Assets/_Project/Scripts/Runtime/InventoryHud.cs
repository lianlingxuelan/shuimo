// -----------------------------------------------------------------------------
// InventoryHud.cs —— 竹材计数显示（砍竹内容闭环）
//
// 独立 Canvas + Legacy Text（与 Hud 同一套「零美术资源」策略，无 TMP 依赖），
// 每帧读取 PlayerInventory 显示竹材 / 嫩笋数量。挂在玩家身上，
// 玩家重建时随之一并销毁、重建（OnDestroy 清理自建 Canvas，避免叠加泄漏）。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>竹材背包的极简 HUD：右下角显示「竹材 xN　嫩笋 xM」。</summary>
    [DefaultExecutionOrder(210)]
    public sealed class InventoryHud : MonoBehaviour
    {
        private Text _text;
        private GameObject _canvasGo;

        private void Awake()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            _canvasGo = new GameObject("InventoryHudCanvas");
            Canvas canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20; // 高于 Hud 默认，确保叠在最上

            GameObject t = new GameObject("InventoryText");
            t.transform.SetParent(_canvasGo.transform, false);
            _text = t.AddComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.color = new Color(0.16f, 0.22f, 0.14f, 1.0f); // 墨绿，呼应水墨
            _text.fontSize = 30;
            _text.alignment = TextAnchor.MiddleRight;

            RectTransform rt = _text.rectTransform;
            rt.anchorMin = new Vector2(1.0f, 0.0f);
            rt.anchorMax = new Vector2(1.0f, 0.0f);
            rt.pivot = new Vector2(1.0f, 0.0f);
            rt.anchoredPosition = new Vector2(-24.0f, 24.0f);
            rt.sizeDelta = new Vector2(360.0f, 40.0f);
        }

        private void Update()
        {
            if (_text == null)
            {
                return;
            }
            int wood = PlayerInventory.Instance != null
                ? PlayerInventory.Instance.Count(PlayerInventory.BambooWood) : 0;
            int shoot = PlayerInventory.Instance != null
                ? PlayerInventory.Instance.Count(PlayerInventory.BambooShoot) : 0;
            _text.text = string.Format("竹材 {0}　嫩笋 {1}", wood, shoot);
        }

        private void OnDestroy()
        {
            if (_canvasGo != null)
            {
                Destroy(_canvasGo);
                _canvasGo = null;
            }
        }
    }
}
