// -----------------------------------------------------------------------------
// AdventurePanelRules.cs —— 冒险界面入口的无 Unity 状态规则
// -----------------------------------------------------------------------------

namespace Xianxia.Unity.T2
{
    /// <summary>底部入口能打开的唯一一个面板。None 表示没有展开的面板。</summary>
    public enum AdventurePanelKind
    {
        None,
        Character,
        Skills,
        Inventory,
        Cultivation,
        Quests,
        DaoHeart,
    }

    /// <summary>
    /// 底部按钮的开关规则。UI 只调用这里，不自己判断，以保证“同键关闭、异键切换”
    /// 在鼠标点击和未来快捷键两条路径上一致。
    /// </summary>
    public static class AdventurePanelRules
    {
        public static AdventurePanelKind Toggle(AdventurePanelKind current, AdventurePanelKind requested)
        {
            if (requested == AdventurePanelKind.None || current == requested)
            {
                return AdventurePanelKind.None;
            }
            return requested;
        }
    }
}
