// -----------------------------------------------------------------------------
// SkillCodexPanel.cs —— 技能图鉴面板
//
// 把战斗内核里已有的技能表（SkillConfig.BuildDefaultTable）以纯文本列表展示出来，
// 相当于一本「技能书」。
//
// 【不重复造技能系统】技能的定义、数值、装配全在 Xianxia.Combat（纯逻辑）里，
// 这里只读、不写。你看到的每一行都来自 SkillDef 的真实字段，
// 改 SkillConfig.cs 里的常量，图鉴会自动跟着变。
// -----------------------------------------------------------------------------

using System.Text;
using Xianxia.Combat;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2
{
    /// <summary>技能图鉴面板（读技能表，出文本列表）。</summary>
    public sealed class SkillCodexPanel
    {
        /// <summary>构建图鉴面板（固定在屏幕左下，背包网格右侧）。</summary>
        public SkillCodexPanel(Transform canvas, SkillTable table)
        {
            Transform root = Hud.NewRect("SkillCodex", canvas);
            Hud.Anchor(root, new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f));
            // 放在背包网格右侧：背包在 x=24 起、约 4 列×72=288 宽，这里 x=340 避开。
            root.anchoredPosition = new Vector2(340.0f, 24.0f);
            root.sizeDelta = new Vector2(360.0f, 300.0f);

            Text title = Hud.NewText("CodexTitle", root, 22, TextAnchor.UpperLeft,
                                     new Color(0.90f, 0.94f, 0.86f, 0.96f));
            Hud.Anchor(title.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            title.rectTransform.anchoredPosition = Vector2.zero;
            title.rectTransform.sizeDelta = new Vector2(360.0f, 30.0f);
            title.text = "技能图鉴";

            Text body = Hud.NewText("CodexBody", root, 17, TextAnchor.UpperLeft,
                                    new Color(0.84f, 0.90f, 0.84f, 0.94f));
            Hud.Anchor(body.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            body.rectTransform.anchoredPosition = new Vector2(0.0f, -34.0f);
            body.rectTransform.sizeDelta = new Vector2(360.0f, 260.0f);
            body.text = BuildText(table);
        }

        private static string BuildText(SkillTable table)
        {
            StringBuilder sb = new StringBuilder(256);
            for (int i = 0; i < SkillTable.SlotCount; i++)
            {
                SkillDef def = table.GetSlot(i);
                if (def == null)
                {
                    continue;
                }
                sb.Append("[").Append(def.DisplayName).Append("] ")
                  .Append(def.Shape == SkillShape.Circle ? "圆形" : "扇形")
                  .Append(" r").Append(def.Range.ToString("F0"));
                if (def.DealsDamage)
                {
                    sb.Append(" 伤").Append(def.Raw.ToString("F0"));
                }
                else
                {
                    sb.Append(" 无伤");
                }
                if (def.QiCost > 0.0f)
                {
                    sb.Append(" 蓝").Append(def.QiCost.ToString("F0"));
                }
                if (def.StaminaCost > 0.0f)
                {
                    sb.Append(" 体").Append(def.StaminaCost.ToString("F0"));
                }
                sb.Append(" CD").Append(def.CooldownSeconds.ToString("F1")).Append("s\n");
            }
            return sb.ToString();
        }
    }
}
