using System;

namespace Xianxia.Unity.T2
{
    /// <summary>Small immutable dialogue payload. Story code owns when it appears; the HUD only renders it.</summary>
    public sealed class DialogueLine
    {
        public DialogueLine(string speaker, string role, string body, params string[] choices)
        {
            Speaker = speaker ?? string.Empty;
            Role = role ?? string.Empty;
            Body = body ?? string.Empty;
            Choices = choices ?? Array.Empty<string>();
        }

        public string Speaker { get; private set; }

        public string Role { get; private set; }

        public string Body { get; private set; }

        public string[] Choices { get; private set; }

        public static DialogueLine CreateGuideIntro()
        {
            return new DialogueLine(
                "竹市引路人",
                "山道行商",
                "前路有妖气盘桓，莫只顾赶路。你若肯出手，竹市小铺的药与消息，都可与你分说。",
                "我去看看。",
                "先记下此事。");
        }
    }
}
