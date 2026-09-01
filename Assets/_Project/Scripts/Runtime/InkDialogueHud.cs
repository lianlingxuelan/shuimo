using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Xianxia.Unity.T2
{
    /// <summary>Reusable bottom dialogue scroll. It keeps a single static canvas for the entire scene lifetime.</summary>
    [DisallowMultipleComponent]
    public sealed class InkDialogueHud : MonoBehaviour
    {
        private GameObject _canvasGo;
        private GameObject _frame;
        private Text _speaker;
        private Text _role;
        private Text _body;
        private readonly Button[] _choices = new Button[3];
        private bool _built;

        public bool IsVisible
        {
            get { return _frame != null && _frame.activeSelf; }
        }

        private void Awake()
        {
            Build();
        }

        private void Update()
        {
            if (IsVisible && (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return)))
            {
                Hide();
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

        public void Show(DialogueLine line)
        {
            Build();
            if (line == null)
            {
                Hide();
                return;
            }

            _speaker.text = line.Speaker;
            _role.text = line.Role;
            _body.text = line.Body;
            for (int i = 0; i < _choices.Length; i++)
            {
                Button button = _choices[i];
                bool available = i < line.Choices.Length && !string.IsNullOrEmpty(line.Choices[i]);
                button.gameObject.SetActive(available);
                if (available)
                {
                    Text label = button.GetComponentInChildren<Text>();
                    if (label != null)
                    {
                        label.text = (i + 1).ToString() + "  " + line.Choices[i];
                    }
                }
            }
            _frame.SetActive(true);
        }

        public void Hide()
        {
            if (_frame != null)
            {
                _frame.SetActive(false);
            }
        }

        private void Build()
        {
            if (_built)
            {
                return;
            }

            EnsureEventSystem();
            _canvasGo = new GameObject("InkDialogueCanvas");
            _canvasGo.transform.SetParent(transform, false);
            Canvas canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 170;
            CanvasScaler scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Hud.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            Image shadow = InkUiFactory.CreatePanel("InkDialogueShadow", _canvasGo.transform, new Vector2(1090.0f, 300.0f));
            shadow.color = InkUiTheme.PaperShadow;
            Hud.Anchor(shadow.rectTransform, new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f));
            shadow.rectTransform.anchoredPosition = new Vector2(10.0f, 58.0f);

            Image panel = InkUiFactory.CreatePanel("InkDialogueFrame", shadow.transform, new Vector2(1080.0f, 290.0f));
            Hud.Anchor(panel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _frame = shadow.gameObject;

            Image avatar = InkUiFactory.CreateLine("InkDialogueAvatar", panel.transform, new Color(0.23f, 0.33f, 0.28f, 0.88f));
            Hud.Anchor(avatar.rectTransform, new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f), new Vector2(0.0f, 0.5f));
            avatar.rectTransform.anchoredPosition = new Vector2(52.0f, 0.0f);
            avatar.rectTransform.sizeDelta = new Vector2(158.0f, 204.0f);

            Text silhouette = InkUiFactory.CreateText("InkDialogueSilhouette", avatar.transform, 52, TextAnchor.MiddleCenter, new Color(0.88f, 0.84f, 0.72f, 0.86f));
            silhouette.text = "人\n影";
            Hud.Stretch(silhouette.rectTransform, 0.0f);

            _speaker = InkUiFactory.CreateText("InkDialogueSpeaker", panel.transform, 28, TextAnchor.MiddleLeft, InkUiTheme.Ink);
            Hud.Anchor(_speaker.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _speaker.rectTransform.anchoredPosition = new Vector2(244.0f, -38.0f);
            _speaker.rectTransform.sizeDelta = new Vector2(380.0f, 36.0f);

            _role = InkUiFactory.CreateText("InkDialogueRole", panel.transform, 15, TextAnchor.MiddleLeft, InkUiTheme.Bamboo);
            Hud.Anchor(_role.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, 1.0f));
            _role.rectTransform.anchoredPosition = new Vector2(246.0f, -75.0f);
            _role.rectTransform.sizeDelta = new Vector2(300.0f, 22.0f);

            _body = InkUiFactory.CreateText("InkDialogueBody", panel.transform, 22, TextAnchor.UpperLeft, InkUiTheme.Ink);
            Hud.Anchor(_body.rectTransform, new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(0.5f, 1.0f));
            _body.rectTransform.anchoredPosition = new Vector2(244.0f, -106.0f);
            _body.rectTransform.sizeDelta = new Vector2(-292.0f, 74.0f);
            _body.horizontalOverflow = HorizontalWrapMode.Wrap;
            _body.verticalOverflow = VerticalWrapMode.Overflow;

            for (int i = 0; i < _choices.Length; i++)
            {
                Button choice = InkUiFactory.CreateSealButton("InkDialogueChoice" + i, panel.transform, string.Empty);
                Hud.Anchor(choice.GetComponent<RectTransform>(), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f));
                RectTransform choiceRect = choice.GetComponent<RectTransform>();
                choiceRect.anchoredPosition = new Vector2(244.0f + i * 248.0f, 30.0f);
                choiceRect.sizeDelta = new Vector2(224.0f, 42.0f);
                choice.onClick.AddListener(Hide);
                _choices[i] = choice;
            }

            Button close = InkUiFactory.CreateSealButton("InkDialogueClose", panel.transform, "收");
            Hud.Anchor(close.GetComponent<RectTransform>(), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 1.0f));
            close.GetComponent<RectTransform>().anchoredPosition = new Vector2(-38.0f, -34.0f);
            close.GetComponent<RectTransform>().sizeDelta = new Vector2(58.0f, 42.0f);
            close.onClick.AddListener(Hide);

            _built = true;
            Hide();
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || Object.FindObjectOfType<EventSystem>() != null)
            {
                return;
            }
            GameObject go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
