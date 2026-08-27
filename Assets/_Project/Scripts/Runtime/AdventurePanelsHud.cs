// -----------------------------------------------------------------------------
// AdventurePanelsHud.cs —— 水墨风“行旅册”界面
//
// 这是一层只读为主的行旅界面：角色 / 技能 / 行囊 / 修行 / 游历 / 道心。
// 它读 CombatBridge、PlayerInventory、ConditioningController 的真实运行时数据；
// 道心尚未装配，所以诚实地显示“筹备中”，而不显示一套假数值。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Xianxia.Combat;

namespace Xianxia.Unity.T2
{
    /// <summary>
    /// 不暂停战斗的“行旅册”。左下六枚墨印负责导航，中央册页负责阅读与调理操作。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(220)]
    public sealed class AdventurePanelsHud : MonoBehaviour
    {
        private static readonly IntentSlot[] SkillSlots =
        {
            IntentSlot.Basic, IntentSlot.Skill1, IntentSlot.Skill2, IntentSlot.Dodge,
        };

        private static readonly string[] SkillKeys = { "鼠标左键", "K", "L", "Shift / Space" };
        private static readonly Color Ink = new Color(0.11f, 0.16f, 0.14f, 1.0f);
        private static readonly Color Paper = new Color(0.89f, 0.84f, 0.72f, 0.985f);
        private static readonly Color PaperText = new Color(0.16f, 0.21f, 0.17f, 1.0f);
        private static readonly Color MutedInk = new Color(0.28f, 0.34f, 0.29f, 1.0f);

        private GameObject _canvasGo;
        private GameObject _panel;
        private readonly Dictionary<AdventurePanelKind, GameObject> _contents = new Dictionary<AdventurePanelKind, GameObject>();
        private readonly Dictionary<AdventurePanelKind, Button> _navButtons = new Dictionary<AdventurePanelKind, Button>();
        private Text _title;
        private Text _subtitle;
        private Text _noticeText;
        private Text _characterText;
        private Text _skillsText;
        private Text _inventoryText;
        private Text _cultivationText;
        private Text _questsText;
        private Text _daoHeartText;
        private CombatBridge _bridge;
        private PlayerInventory _inventory;
        private ConditioningController _conditioning;
        private string _cultivationNotice;
        private bool _built;

        /// <summary>当前展开栏目；None 表示册页收起。</summary>
        public AdventurePanelKind ActivePanel { get; private set; } = AdventurePanelKind.None;

        private void Awake()
        {
            Build();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.C)) TogglePanel(AdventurePanelKind.Character);
            else if (Input.GetKeyDown(KeyCode.P)) TogglePanel(AdventurePanelKind.Skills);
            else if (Input.GetKeyDown(KeyCode.B)) TogglePanel(AdventurePanelKind.Inventory);
            else if (Input.GetKeyDown(KeyCode.R)) TogglePanel(AdventurePanelKind.Cultivation);
            else if (Input.GetKeyDown(KeyCode.J)) TogglePanel(AdventurePanelKind.Quests);
            else if (Input.GetKeyDown(KeyCode.M)) TogglePanel(AdventurePanelKind.DaoHeart);
            else if (Input.GetKeyDown(KeyCode.Escape) && ActivePanel != AdventurePanelKind.None) ClosePanel();

            if (ActivePanel != AdventurePanelKind.None)
            {
                RefreshActivePanel();
            }
        }

        private void OnDestroy()
        {
            if (_canvasGo != null)
            {
                Destroy(_canvasGo);
                _canvasGo = null;
            }
        }

        /// <summary>底部按钮和键盘快捷键共用的切换入口。</summary>
        public void TogglePanel(AdventurePanelKind requested)
        {
            SetActivePanel(AdventurePanelRules.Toggle(ActivePanel, requested));
        }

        /// <summary>仅关闭册页，不改角色的战斗、背包或调理状态。</summary>
        public void ClosePanel()
        {
            SetActivePanel(AdventurePanelKind.None);
        }

        private void Build()
        {
            if (_built) return;

            EnsureEventSystem();
            _canvasGo = new GameObject("AdventurePanelsCanvas");
            _canvasGo.transform.SetParent(transform, false);
            Canvas canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 160;

            CanvasScaler scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Hud.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            BuildBottomSealGrid(_canvasGo.transform);
            BuildBookPanel(_canvasGo.transform);
            _built = true;
            SetActivePanel(AdventurePanelKind.None);
        }

        private void BuildBottomSealGrid(Transform parent)
        {
            RectTransform shadow = Hud.NewImageRect("AdventureSealShadow", parent, new Color(0.01f, 0.02f, 0.015f, 0.82f));
            Hud.Anchor(shadow, Vector2.zero, Vector2.zero, Vector2.zero);
            shadow.anchoredPosition = new Vector2(25.0f, 25.0f);
            shadow.sizeDelta = new Vector2(472.0f, 136.0f);

            RectTransform root = Hud.NewImageRect("AdventureSealGrid", shadow, new Color(0.13f, 0.19f, 0.16f, 0.94f));
            Hud.Stretch(root, 4.0f);

            Text eyebrow = Hud.NewText("AdventureSealGridTitle", root, 15, TextAnchor.MiddleLeft, new Color(0.76f, 0.72f, 0.59f, 1.0f));
            eyebrow.text = "行 旅 · 书";
            Hud.Anchor(eyebrow.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            eyebrow.rectTransform.anchoredPosition = new Vector2(14.0f, -10.0f);
            eyebrow.rectTransform.sizeDelta = new Vector2(160.0f, 18.0f);

            BuildNavButton(root, AdventurePanelKind.Character, "角 色", "C", 10.0f, 30.0f, new Color(0.38f, 0.52f, 0.49f, 1.0f));
            BuildNavButton(root, AdventurePanelKind.Skills, "功 法", "P", 160.0f, 30.0f, new Color(0.35f, 0.50f, 0.67f, 1.0f));
            BuildNavButton(root, AdventurePanelKind.Inventory, "行 囊", "B", 310.0f, 30.0f, new Color(0.44f, 0.60f, 0.41f, 1.0f));
            BuildNavButton(root, AdventurePanelKind.Cultivation, "修 行", "R", 10.0f, 78.0f, new Color(0.58f, 0.49f, 0.34f, 1.0f));
            BuildNavButton(root, AdventurePanelKind.Quests, "游 历", "J", 160.0f, 78.0f, new Color(0.67f, 0.52f, 0.30f, 1.0f));
            BuildNavButton(root, AdventurePanelKind.DaoHeart, "道 心", "M", 310.0f, 78.0f, new Color(0.46f, 0.38f, 0.52f, 1.0f));
        }

        private void BuildNavButton(Transform parent, AdventurePanelKind kind, string label, string hotkey, float x, float y, Color tint)
        {
            Button button = NewInkButton("AdventureNav" + kind, parent, label, tint, 19);
            RectTransform rt = button.GetComponent<RectTransform>();
            Hud.Anchor(rt, Vector2.zero, Vector2.zero, Vector2.zero);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(142.0f, 40.0f);
            button.onClick.AddListener(() => TogglePanel(kind));
            _navButtons.Add(kind, button);

            Text key = Hud.NewText("AdventureNavKey" + kind, rt, 11, TextAnchor.MiddleRight, new Color(0.91f, 0.88f, 0.77f, 0.8f));
            key.text = hotkey;
            Hud.Anchor(key.rectTransform, new Vector2(1.0f, 0.5f), new Vector2(1.0f, 0.5f), new Vector2(1.0f, 0.5f));
            key.rectTransform.anchoredPosition = new Vector2(-8.0f, 0.0f);
            key.rectTransform.sizeDelta = new Vector2(20.0f, 20.0f);
        }

        private void BuildBookPanel(Transform parent)
        {
            RectTransform shadow = Hud.NewImageRect("AdventureBookShadow", parent, new Color(0.02f, 0.025f, 0.02f, 0.70f));
            Hud.Anchor(shadow, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            shadow.anchoredPosition = new Vector2(12.0f, 8.0f);
            shadow.sizeDelta = new Vector2(742.0f, 532.0f);

            RectTransform book = Hud.NewImageRect("AdventureBook", shadow, Paper);
            Hud.Stretch(book, 5.0f);
            _panel = shadow.gameObject;

            _title = Hud.NewText("AdventureBookTitle", book, 34, TextAnchor.UpperLeft, Ink);
            Hud.Anchor(_title.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _title.rectTransform.anchoredPosition = new Vector2(38.0f, -31.0f);
            _title.rectTransform.sizeDelta = new Vector2(520.0f, 42.0f);

            _subtitle = Hud.NewText("AdventureBookSubtitle", book, 16, TextAnchor.UpperLeft, MutedInk);
            Hud.Anchor(_subtitle.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _subtitle.rectTransform.anchoredPosition = new Vector2(40.0f, -76.0f);
            _subtitle.rectTransform.sizeDelta = new Vector2(570.0f, 23.0f);

            RectTransform divider = Hud.NewImageRect("AdventureBookDivider", book, new Color(0.30f, 0.38f, 0.29f, 0.50f));
            Hud.Anchor(divider, new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(0.5f, 1.0f));
            divider.anchoredPosition = new Vector2(0.0f, -108.0f);
            divider.sizeDelta = new Vector2(-64.0f, 2.0f);

            Button close = NewInkButton("AdventureBookClose", book, "收", new Color(0.50f, 0.34f, 0.30f, 1.0f), 17);
            RectTransform closeRt = close.GetComponent<RectTransform>();
            Hud.Anchor(closeRt, new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f));
            closeRt.anchoredPosition = new Vector2(-32.0f, -30.0f);
            closeRt.sizeDelta = new Vector2(54.0f, 38.0f);
            close.onClick.AddListener(ClosePanel);

            _noticeText = Hud.NewText("AdventureBookNotice", book, 17, TextAnchor.LowerLeft, new Color(0.40f, 0.30f, 0.17f, 1.0f));
            Hud.Anchor(_noticeText.rectTransform, Vector2.zero, new Vector2(1.0f, 0.0f), new Vector2(0.5f, 0.0f));
            _noticeText.rectTransform.anchoredPosition = new Vector2(0.0f, 30.0f);
            _noticeText.rectTransform.sizeDelta = new Vector2(-76.0f, 28.0f);

            _contents.Add(AdventurePanelKind.Character, BuildTextContent("CharacterContent", book, out _characterText));
            _contents.Add(AdventurePanelKind.Skills, BuildTextContent("SkillsContent", book, out _skillsText));
            _contents.Add(AdventurePanelKind.Inventory, BuildTextContent("InventoryContent", book, out _inventoryText));
            GameObject cultivation = BuildTextContent("CultivationContent", book, out _cultivationText);
            _contents.Add(AdventurePanelKind.Cultivation, cultivation);
            _contents.Add(AdventurePanelKind.Quests, BuildTextContent("QuestsContent", book, out _questsText));
            _contents.Add(AdventurePanelKind.DaoHeart, BuildTextContent("DaoHeartContent", book, out _daoHeartText));
            BuildConditioningButtons(cultivation.transform);
        }

        private static GameObject BuildTextContent(string name, Transform parent, out Text text)
        {
            RectTransform root = Hud.NewRect(name, parent);
            Hud.Stretch(root, 0.0f);
            text = Hud.NewText(name + "Text", root, 22, TextAnchor.UpperLeft, PaperText);
            Hud.Anchor(text.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            text.rectTransform.anchoredPosition = new Vector2(40.0f, -132.0f);
            text.rectTransform.sizeDelta = new Vector2(640.0f, 242.0f);
            return root.gameObject;
        }

        private void BuildConditioningButtons(Transform parent)
        {
            BuildConditioningButton("QingQiRecipe", parent, "清气散  ·  竹材 2  嫩笋 1", -282.0f, ConditioningKind.QingQi);
            BuildConditioningButton("StrongSinewRecipe", parent, "强筋散  ·  竹材 3  嫩笋 1", -334.0f, ConditioningKind.StrongSinew);
            BuildConditioningButton("NourishOriginRecipe", parent, "养元散  ·  竹材 2  嫩笋 2", -386.0f, ConditioningKind.NourishOrigin);
        }

        private void BuildConditioningButton(string name, Transform parent, string label, float y, ConditioningKind kind)
        {
            Button button = NewInkButton(name, parent, label, new Color(0.30f, 0.48f, 0.34f, 1.0f), 18);
            RectTransform rt = button.GetComponent<RectTransform>();
            Hud.Anchor(rt, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            rt.anchoredPosition = new Vector2(40.0f, y);
            rt.sizeDelta = new Vector2(400.0f, 40.0f);
            button.onClick.AddListener(() => TryActivateConditioning(kind));
        }

        private void SetActivePanel(AdventurePanelKind kind)
        {
            ActivePanel = kind;
            bool visible = kind != AdventurePanelKind.None;
            if (_panel != null) _panel.SetActive(visible);
            foreach (KeyValuePair<AdventurePanelKind, GameObject> entry in _contents)
            {
                entry.Value.SetActive(visible && entry.Key == kind);
            }
            RefreshNavState();
            if (visible) RefreshActivePanel();
        }

        private void RefreshNavState()
        {
            foreach (KeyValuePair<AdventurePanelKind, Button> entry in _navButtons)
            {
                Image image = entry.Value.targetGraphic as Image;
                if (image == null) continue;
                Color normal = entry.Value.colors.normalColor;
                image.color = entry.Key == ActivePanel ? Color.Lerp(normal, Color.white, 0.18f) : normal;
            }
        }

        private void RefreshActivePanel()
        {
            switch (ActivePanel)
            {
                case AdventurePanelKind.Character:
                    SetHeading("人物 · 行状", "修为、气血与本局基础战力");
                    _characterText.text = BuildCharacterText();
                    _noticeText.text = "人物数值直接读取当前战斗与成长状态。";
                    break;
                case AdventurePanelKind.Skills:
                    SetHeading("功法 · 招式", "已装配招式、资源消耗与实时冷却");
                    _skillsText.text = BuildSkillsText();
                    _noticeText.text = "技能栏仍可正常施放；这里用于查看真实数值与冷却。";
                    break;
                case AdventurePanelKind.Inventory:
                    SetHeading("行囊 · 采获", "本局采得的竹材与山中灵物");
                    _inventoryText.text = BuildInventoryText();
                    _noticeText.text = "材料来自实际砍竹与采集，并非演示数据。";
                    break;
                case AdventurePanelKind.Cultivation:
                    SetHeading("修行 · 调理", "以竹材与嫩笋配成一帖当局调理");
                    _cultivationText.text = BuildCultivationText();
                    _noticeText.text = string.IsNullOrEmpty(_cultivationNotice)
                        ? "选定药散会立即从当前行囊扣除材料。" : _cultivationNotice;
                    break;
                case AdventurePanelKind.Quests:
                    SetHeading("游历 · 竹海初试", "初入竹海的采集目标与下一步指引");
                    _questsText.text = BuildQuestsText();
                    _noticeText.text = "完成采集后可前往“修行”页试用调理。";
                    break;
                case AdventurePanelKind.DaoHeart:
                    SetHeading("道心 · 正邪", "正魔与性情会在后续战斗中留下痕迹");
                    _daoHeartText.text = "此页已预留在行旅册中。\n\n正邪、声望与性格的底层规则已在工程内，\n但尚未挂入当前这局游戏。\n\n状态：筹备中\n\n等第一只敌人与战斗掉落闭环稳定后，\n这里会显示你的道心倾向与对应影响。";
                    _noticeText.text = "当前不显示虚构数值；系统接入时此页会直接启用。";
                    break;
            }
        }

        private void SetHeading(string title, string subtitle)
        {
            _title.text = title;
            _subtitle.text = subtitle;
        }

        private string BuildCharacterText()
        {
            CombatBridge bridge = ResolveBridge();
            PlayerProgression progression = bridge != null ? bridge.Progression : null;
            int level = progression != null ? progression.Level : 1;
            int exp = progression != null ? progression.ExpInLevel : 0;
            int toNext = progression != null ? progression.ExpToNext : 0;
            float hp = bridge != null && bridge.Player != null ? bridge.Player.Hp : 0.0f;
            float hpMax = bridge != null ? bridge.PlayerHpMaxNow : CombatBridge.PlayerHpMax;
            float attackBonus = bridge != null ? bridge.PlayerAtkBonus : 0.0f;
            float qi = bridge != null ? bridge.QiCurrent : 0.0f;
            float qiMax = bridge != null ? bridge.QiMax : 0.0f;
            float stamina = bridge != null ? bridge.StaminaCurrent : 0.0f;
            float staminaMax = bridge != null ? bridge.StaminaMax : 0.0f;

            StringBuilder sb = new StringBuilder(280);
            sb.Append("境界    第 ").Append(level).Append(" 层\n");
            sb.Append("经验    ").Append(exp).Append(" / ").Append(toNext > 0 ? toNext : 0).Append("\n\n");
            sb.Append("气血    ").Append(hp.ToString("F0")).Append(" / ").Append(hpMax.ToString("F0")).Append("\n");
            sb.Append("灵力    ").Append(qi.ToString("F0")).Append(" / ").Append(qiMax.ToString("F0")).Append("\n");
            sb.Append("体力    ").Append(stamina.ToString("F0")).Append(" / ").Append(staminaMax.ToString("F0")).Append("\n\n");
            sb.Append("成长攻击加成    +").Append(attackBonus.ToString("F0"));
            return sb.ToString();
        }

        private string BuildSkillsText()
        {
            CombatBridge bridge = ResolveBridge();
            StringBuilder sb = new StringBuilder(360);
            sb.Append("当前已装配招式\n\n");
            for (int i = 0; i < SkillSlots.Length; i++)
            {
                SkillDef def = bridge != null ? bridge.SkillOf(SkillSlots[i]) : null;
                if (def == null)
                {
                    sb.Append(SkillKeys[i]).Append("  招式加载中\n");
                    continue;
                }
                sb.Append(SkillKeys[i]).Append("  ").Append(def.DisplayName)
                    .Append("  伤害 ").Append(def.Raw.ToString("F0"))
                    .Append("  冷却 ").Append(def.CooldownSeconds.ToString("F1")).Append(" 秒");
                if (def.QiCost > 0.0f) sb.Append("  灵力 ").Append(def.QiCost.ToString("F0"));
                if (def.StaminaCost > 0.0f) sb.Append("  体力 ").Append(def.StaminaCost.ToString("F0"));
                if (bridge != null && bridge.SkillCdSeconds(SkillSlots[i]) > 0.0f)
                {
                    sb.Append("  余 ").Append(bridge.SkillCdSeconds(SkillSlots[i]).ToString("F1")).Append(" 秒");
                }
                sb.Append("\n");
            }
            return sb.ToString();
        }

        private string BuildInventoryText()
        {
            PlayerInventory inventory = ResolveInventory();
            int wood = inventory != null ? inventory.Count(PlayerInventory.BambooWood) : 0;
            int shoot = inventory != null ? inventory.Count(PlayerInventory.BambooShoot) : 0;
            return string.Format("竹材    {0}\n嫩笋    {1}\n\n说明\n竹材来自砍伐后掉落，嫩笋会在竹林资源点附近生长。\n\n用途\n前往“修行”页，可消耗材料启用一帖当局调理。", wood, shoot);
        }

        private string BuildCultivationText()
        {
            ConditioningController conditioning = ResolveConditioning();
            ConditioningKind activeKind = conditioning != null ? conditioning.ActiveKind : ConditioningKind.None;
            string active = activeKind == ConditioningKind.None ? "尚未调理" : ConditioningRules.GetRecipe(activeKind).DisplayName;
            return "当前调理：" + active + "\n\n三帖药散各自改变本局的一项战斗倾向。\n材料足够时，点击下方配方即可启用：";
        }

        private string BuildQuestsText()
        {
            PlayerInventory inventory = ResolveInventory();
            int wood = inventory != null ? inventory.Count(PlayerInventory.BambooWood) : 0;
            int shoot = inventory != null ? inventory.Count(PlayerInventory.BambooShoot) : 0;
            bool complete = wood >= 6 && shoot >= 2;
            StringBuilder sb = new StringBuilder(260);
            sb.Append("主线 · 竹林初试\n\n")
                .Append("采集竹材    ").Append(Mathf.Min(wood, 6)).Append(" / 6\n")
                .Append("采集嫩笋    ").Append(Mathf.Min(shoot, 2)).Append(" / 2\n\n");
            if (complete)
            {
                sb.Append("状态：已完成\n下一步：在“修行”页选一帖调理，为战斗做准备。");
            }
            else
            {
                sb.Append("状态：进行中\n前往竹林砍竹；竹笋会在资源点附近刷新。");
            }
            return sb.ToString();
        }

        private void TryActivateConditioning(ConditioningKind kind)
        {
            ConditioningController conditioning = ResolveConditioning();
            if (conditioning == null)
            {
                _cultivationNotice = "调理系统加载中，请稍候。";
            }
            else if (conditioning.TryActivate(kind))
            {
                _cultivationNotice = "已启用「" + ConditioningRules.GetRecipe(kind).DisplayName + "」。";
            }
            else
            {
                ConditioningRecipe recipe = ConditioningRules.GetRecipe(kind);
                _cultivationNotice = string.Format("材料不足：需要竹材 {0}、嫩笋 {1}。", recipe.BambooWoodCost, recipe.BambooShootCost);
            }
            RefreshActivePanel();
        }

        private CombatBridge ResolveBridge()
        {
            if (_bridge == null) _bridge = FindObjectOfType<CombatBridge>();
            return _bridge;
        }

        private PlayerInventory ResolveInventory()
        {
            if (_inventory == null) _inventory = GetComponent<PlayerInventory>();
            return _inventory;
        }

        private ConditioningController ResolveConditioning()
        {
            if (_conditioning == null) _conditioning = GetComponent<ConditioningController>();
            return _conditioning;
        }

        private static Button NewInkButton(string name, Transform parent, string label, Color normalColor, int fontSize)
        {
            RectTransform rt = Hud.NewRect(name, parent);
            Image image = rt.gameObject.AddComponent<Image>();
            image.sprite = SpriteFactory.UiPixel();
            image.color = normalColor;
            image.raycastTarget = true;
            Button button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = normalColor;
            colors.highlightedColor = Color.Lerp(normalColor, Color.white, 0.22f);
            colors.pressedColor = Color.Lerp(normalColor, Color.black, 0.25f);
            colors.selectedColor = normalColor;
            colors.disabledColor = new Color(normalColor.r, normalColor.g, normalColor.b, 0.45f);
            colors.colorMultiplier = 1.0f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Text text = Hud.NewText(name + "Label", rt, fontSize, TextAnchor.MiddleCenter, new Color(0.96f, 0.94f, 0.84f, 1.0f));
            Hud.Stretch(text.rectTransform, 0.0f);
            text.text = label;
            return button;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindObjectOfType<EventSystem>() != null) return;
            GameObject go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
