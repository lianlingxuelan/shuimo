namespace Xianxia.Unity.T2
{
    /// <summary>Visual emphasis belongs to information, not empty space.</summary>
    public static class InkUiDensityRules
    {
        public const bool ShowOuterBookFrame = false;

        public static bool ShouldShowDecorativeSlotFrame(int itemCount)
        {
            return itemCount > 0;
        }
    }
}
