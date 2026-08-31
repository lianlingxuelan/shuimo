using UnityEngine;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>Small, allocation-light construction helpers shared by every static ink UI window.</summary>
    public static class InkUiFactory
    {
        public static Image CreatePanel(string name, Transform parent, Vector2 size)
        {
            RectTransform rect = Hud.NewRect(name, parent);
            rect.sizeDelta = size;
            Image image = rect.gameObject.AddComponent<Image>();
            ApplyPanelStyle(image, InkUiTheme.LoadOptionalSprite("ink-paper-panel-v1"), InkUiTheme.Paper);
            return image;
        }

        public static Image CreateLine(string name, Transform parent, Color color)
        {
            RectTransform rect = Hud.NewRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = InkUiTheme.CreateFallbackSprite();
            image.color = color;
            return image;
        }

        public static Button CreateSealButton(string name, Transform parent, string label)
        {
            RectTransform rect = Hud.NewRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            ApplyPanelStyle(image, InkUiTheme.LoadOptionalSprite("ink-seal-v1"), InkUiTheme.Vermilion);
            image.raycastTarget = true;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.0f, 0.91f, 0.80f, 1.0f);
            colors.pressedColor = new Color(0.70f, 0.70f, 0.70f, 1.0f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1.0f, 1.0f, 1.0f, 0.40f);
            colors.colorMultiplier = 1.0f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Text text = CreateText(name + "Label", rect, 17, TextAnchor.MiddleCenter, new Color(1.0f, 0.94f, 0.82f, 1.0f));
            Hud.Stretch(text.rectTransform, 0.0f);
            text.text = label;
            return button;
        }

        public static Text CreateText(string name, Transform parent, int fontSize, TextAnchor alignment, Color color)
        {
            return Hud.NewText(name, parent, fontSize, alignment, color);
        }

        public static void ApplyPanelStyle(Image image, Sprite sprite, Color color)
        {
            image.sprite = sprite != null ? sprite : InkUiTheme.CreateFallbackSprite();
            image.type = InkUiTheme.HasSliceBorder(image.sprite) ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
        }
    }
}
