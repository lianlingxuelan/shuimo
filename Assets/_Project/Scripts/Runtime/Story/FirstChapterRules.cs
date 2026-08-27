// -----------------------------------------------------------------------------
// FirstChapterRules.cs —— 第一章“初入幽篁”的纯状态规则
//
// 不引用 Unity，不创建对象，不触碰战斗/移动/背包。Unity 表现层只把已发生的事实
// 传进来，再按这里给出的唯一合法状态推进，因此可在编辑器外单测。
// -----------------------------------------------------------------------------

namespace Xianxia.Unity.T2
{
    /// <summary>第一章唯一的剧情阶段。</summary>
    public enum FirstChapterStage
    {
        Opening,
        TravelToRoad,
        DefeatRoadEnemy,
        MeetGuide,
        VisitShop,
        Completed,
    }

    /// <summary>由场景检测、战斗事件或册页按钮报告的事实。</summary>
    public enum FirstChapterEvent
    {
        ChapterShown,
        RoadReached,
        RoadEnemyDefeated,
        ShopOpened,
        ShopVisited,
    }

    /// <summary>第一章的状态机表。非法或重复事件保持当前状态。</summary>
    public static class FirstChapterRules
    {
        public static FirstChapterStage Advance(FirstChapterStage current, FirstChapterEvent occurred)
        {
            if (current == FirstChapterStage.Opening && occurred == FirstChapterEvent.ChapterShown)
            {
                return FirstChapterStage.TravelToRoad;
            }
            if (current == FirstChapterStage.TravelToRoad && occurred == FirstChapterEvent.RoadReached)
            {
                return FirstChapterStage.DefeatRoadEnemy;
            }
            if (current == FirstChapterStage.DefeatRoadEnemy && occurred == FirstChapterEvent.RoadEnemyDefeated)
            {
                return FirstChapterStage.MeetGuide;
            }
            if (current == FirstChapterStage.MeetGuide && occurred == FirstChapterEvent.ShopOpened)
            {
                return FirstChapterStage.VisitShop;
            }
            if (current == FirstChapterStage.VisitShop && occurred == FirstChapterEvent.ShopVisited)
            {
                return FirstChapterStage.Completed;
            }
            return current;
        }

        public static bool CanOpenGuide(FirstChapterStage stage)
        {
            return stage == FirstChapterStage.MeetGuide || stage == FirstChapterStage.VisitShop;
        }

        public static bool CanCompleteFromShop(FirstChapterStage stage)
        {
            return stage == FirstChapterStage.VisitShop;
        }
    }
}
