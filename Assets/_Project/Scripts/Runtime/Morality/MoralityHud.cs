// -----------------------------------------------------------------------------
// MoralityHud.cs —— 正魔墨条 + 性格面板（asmdef: Xianxia.Unity.T2.Morality）
//
// 【它是什么】
// 极简、零美术的数据呈现层：两条墨色渐变条（正道清 / 魔道浓）+ 路线标签，
// 外加五性格条。全部用 Hud 的 public 静态工厂（NewRect/NewImageRect/NewText）
// 在代码里现搭，不依赖任何美术 prefab——符合 P0-11「零美术资源」铁律。
//
// 【为什么和 MoralityBridge 拆开】
// 表现与状态机分离：桥接层只管"算 + 推事件"，本类只管"画"。两者通过
// Bridge 调 Build/Refresh 串起来，任一方坏了都不连累另一方。
//
// 【墨色语义】正道=清（青绿）→ 魔道=浓（墨黑）。蓝图 1.4「墨色渐变条，正道清/魔道浓」。
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using Xianxia.Morality;
using Xianxia.Unity.T2;

namespace Xianxia.Unity.T2.Morality
{
    /// <summary>正魔墨条 + 性格面板。由 MoralityBridge 创建并驱动。</summary>
    [DisallowMultipleComponent]
    public sealed class MoralityHud : MonoBehaviour
    {
        private Image _zhengFill;
        private Image _moFill;
        private Text _routeText;
        private readonly Image[] _traitFills = new Image[5];
        private readonly string[] _traitNames = { "悟性", "冲动", "隐忍", "冷静", "贪婪" };

        /// <summary>在给定 Canvas 根下搭建控件。</summary>
        public void Build(Transform canvasRoot)
        {
            BuildMeter(canvasRoot);
            BuildPersonality(canvasRoot);
        }

        private void BuildMeter(Transform canvasRoot)
        {
            RectTransform panel = Hud.NewRect("MoralityPanel", canvasRoot);
            Hud.Anchor(panel, new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f));
            panel.anchoredPosition = new Vector2(28.0f, 150.0f);
            panel.sizeDelta = new Vector2(420.0f, 120.0f);

            _routeText = Hud.NewText("MoralityRoute", panel, 22, TextAnchor.UpperLeft,
                                     new Color(0.92f, 0.92f, 0.86f, 0.96f));
            Hud.Anchor(_routeText.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _routeText.rectTransform.anchoredPosition = Vector2.zero;
            _routeText.rectTransform.sizeDelta = new Vector2(420.0f, 26.0f);
            _routeText.text = "路线  正道    正 50 / 魔 0";

            // 正道条（清·青绿）
            RectTransform zBg = Hud.NewImageRect("ZhengBg", panel, new Color(0.06f, 0.07f, 0.06f, 0.80f));
            Hud.Anchor(zBg, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            zBg.anchoredPosition = new Vector2(0.0f, -34.0f);
            zBg.sizeDelta = new Vector2(420.0f, 22.0f);
            RectTransform zFill = Hud.NewImageRect("ZhengFill", zBg, new Color(0.45f, 0.78f, 0.55f, 0.95f));
            Hud.Stretch(zFill, 2.0f);
            _zhengFill = zFill.GetComponent<Image>();
            _zhengFill.type = Image.Type.Filled;
            _zhengFill.fillMethod = Image.FillMethod.Horizontal;
            _zhengFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _zhengFill.fillAmount = 0.5f;

            // 魔道条（浓·墨黑）
            RectTransform mBg = Hud.NewImageRect("MoBg", panel, new Color(0.06f, 0.07f, 0.06f, 0.80f));
            Hud.Anchor(mBg, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            mBg.anchoredPosition = new Vector2(0.0f, -62.0f);
            mBg.sizeDelta = new Vector2(420.0f, 22.0f);
            RectTransform mFill = Hud.NewImageRect("MoFill", mBg, new Color(0.16f, 0.14f, 0.22f, 0.98f));
            Hud.Stretch(mFill, 2.0f);
            _moFill = mFill.GetComponent<Image>();
            _moFill.type = Image.Type.Filled;
            _moFill.fillMethod = Image.FillMethod.Horizontal;
            _moFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _moFill.fillAmount = 0.0f;
        }

        private void BuildPersonality(Transform canvasRoot)
        {
            RectTransform panel = Hud.NewRect("PersonalityPanel", canvasRoot);
            Hud.Anchor(panel, new Vector2(1.0f, 0.0f), new Vector2(1.0f, 0.0f), new Vector2(1.0f, 0.0f));
            panel.anchoredPosition = new Vector2(-28.0f, 150.0f);
            panel.sizeDelta = new Vector2(300.0f, 170.0f);

            Text title = Hud.NewText("PersonalityTitle", panel, 22, TextAnchor.UpperLeft,
                                      new Color(0.92f, 0.92f, 0.86f, 0.96f));
            Hud.Anchor(title.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            title.rectTransform.anchoredPosition = Vector2.zero;
            title.rectTransform.sizeDelta = new Vector2(300.0f, 26.0f);
            title.text = "性格（玩出来）";

            for (int i = 0; i < 5; i++)
            {
                RectTransform row = Hud.NewRect("Trait" + i, panel);
                Hud.Anchor(row, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
                row.anchoredPosition = new Vector2(0.0f, -34.0f - i * 26.0f);
                row.sizeDelta = new Vector2(300.0f, 24.0f);

                Text name = Hud.NewText("TraitName" + i, row, 16, TextAnchor.MiddleLeft,
                                        new Color(0.90f, 0.90f, 0.86f, 0.95f));
                Hud.Anchor(name.rectTransform, new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f));
                name.rectTransform.anchoredPosition = new Vector2(4.0f, 0.0f);
                name.rectTransform.sizeDelta = new Vector2(70.0f, 22.0f);
                name.text = _traitNames[i];

                RectTransform bBg = Hud.NewImageRect("TraitBg" + i, row, new Color(0.06f, 0.07f, 0.06f, 0.75f));
                Hud.Anchor(bBg, new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f));
                bBg.anchoredPosition = new Vector2(180.0f, 0.0f);
                bBg.sizeDelta = new Vector2(200.0f, 14.0f);

                RectTransform bFill = Hud.NewImageRect("TraitFill" + i, bBg, new Color(0.72f, 0.62f, 0.42f, 0.95f));
                Hud.Stretch(bFill, 1.0f);
                Image fi = bFill.GetComponent<Image>();
                fi.type = Image.Type.Filled;
                fi.fillMethod = Image.FillMethod.Horizontal;
                fi.fillOrigin = (int)Image.OriginHorizontal.Left;
                fi.fillAmount = 0.0f;
                _traitFills[i] = fi;
            }
        }

        /// <summary>刷新墨条与性格条。</summary>
        public void Refresh(MoralityRoute route, float zheng, float mo, float[] traits)
        {
            if (_zhengFill != null)
            {
                _zhengFill.fillAmount = Mathf.Clamp01(zheng / 100.0f);
            }
            if (_moFill != null)
            {
                _moFill.fillAmount = Mathf.Clamp01(mo / 100.0f);
            }
            if (_routeText != null)
            {
                _routeText.text = string.Format("路线  {0}    正 {1:F0} / 魔 {2:F0}",
                                                 RouteName(route), zheng, mo);
            }
            if (traits != null)
            {
                for (int i = 0; i < _traitFills.Length && i < traits.Length; i++)
                {
                    if (_traitFills[i] != null)
                    {
                        _traitFills[i].fillAmount = Mathf.Clamp01(traits[i] / 100.0f);
                    }
                }
            }
        }

        private static string RouteName(MoralityRoute r)
        {
            switch (r)
            {
                case MoralityRoute.Zheng: return "正道";
                case MoralityRoute.Xia: return "侠道";
                case MoralityRoute.Mo: return "魔道";
                case MoralityRoute.Enchanted: return "入魔";
                default: return "未知";
            }
        }
    }
}
