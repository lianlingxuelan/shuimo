using System;

namespace Xianxia.Unity.T2
{
    /// <summary>Authored narrative beats for the first playable chapter. Runtime code decides when each beat appears.</summary>
    public enum FirstChapterNarrativeBeat
    {
        Opening,
        RoadEncounter,
        RoadAftermath,
        Guide,
        Shop,
        ChapterComplete,
    }

    /// <summary>Centralizes player-visible chapter prose so story revisions do not alter progression rules.</summary>
    public static class FirstChapterNarrative
    {
        public static DialogueLine For(FirstChapterNarrativeBeat beat)
        {
            switch (beat)
            {
                case FirstChapterNarrativeBeat.Opening:
                    return new DialogueLine(
                        "墨尘子",
                        "青墨门掌门",
                        "山上的药认得再多，也只是书上的东西。带上药囊，下山去青竹村看看。少管闲事，别露本事；遇到解决不了的，回来找我。");

                case FirstChapterNarrativeBeat.RoadEncounter:
                    return new DialogueLine(
                        "旁白",
                        "雨后山路",
                        "竹叶无风而动，泥水里留下一串不属于人的脚印。前方有东西被寒湿与妖气逼得发狂了。",
                        "上前查看。");

                case FirstChapterNarrativeBeat.RoadAftermath:
                    return new DialogueLine(
                        "旁白",
                        "灵觉微动",
                        "竹影兽倒下时，灰黑色的病气从它背上浮起，又像被雨水冲散。你眨了眨眼，仿佛什么也没有看见。",
                        "记下此事。");

                case FirstChapterNarrativeBeat.Guide:
                    return new DialogueLine(
                        "竹市引路人",
                        "山道行商",
                        "前路有妖气盘桓，莫只顾赶路。你若肯出手，竹市小铺的药与消息，都可与你分说。",
                        "我去看看。",
                        "先记下此事。");

                case FirstChapterNarrativeBeat.Shop:
                    return new DialogueLine(
                        "小草",
                        "竹市小铺学徒",
                        "你是从青墨山下来的？苏先生说，见着背青囊的人就留一盏热茶。村里这几日咳喘发热的多，你也当心些。",
                        "看看货架。",
                        "问问村里的病。"
                    );

                case FirstChapterNarrativeBeat.ChapterComplete:
                    return new DialogueLine(
                        "旁白",
                        "青竹村",
                        "雨落青竹，青竹村的病气未散。王婆婆的咳声从巷口传来——你第一次下山，便遇上了一桩不寻常的病。",
                        "前往墨壶堂。"
                    );

                default:
                    throw new ArgumentOutOfRangeException(nameof(beat), beat, "Unknown first chapter narrative beat.");
            }
        }
    }

    /// <summary>Keeps event-to-dialogue timing explicit while the gameplay state machine remains text-free.</summary>
    public static class FirstChapterNarrativeSchedule
    {
        public static bool TryGet(FirstChapterEvent occurred, out FirstChapterNarrativeBeat beat)
        {
            switch (occurred)
            {
                case FirstChapterEvent.ChapterShown:
                    beat = FirstChapterNarrativeBeat.Opening;
                    return true;

                case FirstChapterEvent.RoadReached:
                    beat = FirstChapterNarrativeBeat.RoadEncounter;
                    return true;

                case FirstChapterEvent.RoadEnemyDefeated:
                    beat = FirstChapterNarrativeBeat.RoadAftermath;
                    return true;

                case FirstChapterEvent.ShopOpened:
                    beat = FirstChapterNarrativeBeat.Guide;
                    return true;

                case FirstChapterEvent.ShopVisited:
                    beat = FirstChapterNarrativeBeat.ChapterComplete;
                    return true;

                default:
                    beat = FirstChapterNarrativeBeat.Opening;
                    return false;
            }
        }
    }
}
