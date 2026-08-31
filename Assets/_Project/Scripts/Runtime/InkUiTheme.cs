using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// One visual source of truth for the runtime ink UI. Art assets are optional:
    /// every caller can safely fall back to the opaque pixel sprite below.
    /// </summary>
    public static class InkUiTheme
    {
        public static readonly Color Paper = new Color(0.91f, 0.87f, 0.76f, 0.985f);
        public static readonly Color PaperShadow = new Color(0.11f, 0.13f, 0.10f, 0.62f);
        public static readonly Color Ink = new Color(0.10f, 0.15f, 0.13f, 1.0f);
        public static readonly Color MutedInk = new Color(0.27f, 0.32f, 0.27f, 1.0f);
        public static readonly Color Bamboo = new Color(0.26f, 0.42f, 0.31f, 1.0f);
        public static readonly Color Vermilion = new Color(0.56f, 0.18f, 0.13f, 1.0f);
        public static readonly Color Gold = new Color(0.67f, 0.52f, 0.30f, 1.0f);

        private static Sprite _fallbackSprite;

        public static Sprite CreateFallbackSprite()
        {
            if (_fallbackSprite != null)
            {
                return _fallbackSprite;
            }

            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.name = "InkUiFallbackPixel";
            texture.SetPixel(0, 0, Color.white);
            texture.Apply(false, true);
            _fallbackSprite = Sprite.Create(texture, new Rect(0.0f, 0.0f, 1.0f, 1.0f), new Vector2(0.5f, 0.5f));
            _fallbackSprite.name = "InkUiFallbackSprite";
            return _fallbackSprite;
        }

        public static Sprite LoadOptionalSprite(string resourceName)
        {
            Sprite sprite = Resources.Load<Sprite>("UI/Ink/" + resourceName);
            return sprite != null ? sprite : CreateFallbackSprite();
        }

        public static bool HasSliceBorder(Sprite sprite)
        {
            return sprite != null && (sprite.border.x > 0.0f || sprite.border.y > 0.0f
                || sprite.border.z > 0.0f || sprite.border.w > 0.0f);
        }
    }
}
