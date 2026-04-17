using UnityEngine;
using UnityEngine.UI;

namespace JumJump.Game
{
    public class GameBootstrapView : MonoBehaviour
    {
        [Header("Hud")]
        [SerializeField] private RectTransform hudRoot;

        private Text statusText;
        private Text roomText;
        private Text selfText;
        private Text turnText;
        private Text selectedText;
        private Text plannedPathText;

        [Header("Buttons")]
        private Button submitJumpButton;
        private Button undoJumpButton;
        private Button clearSelectionButton;
        private Button startMatchButton;
        private Button cancelMatchButton;
        private Button coordToggleButton;
        private Text coordToggleLabel;

        [Header("Board")]
        [SerializeField] private RectTransform boardGridRoot;
        [SerializeField] private RectTransform boardCellRoot;

        public Text StatusText => statusText;
        public Text RoomText => roomText;
        public Text SelfText => selfText;
        public Text TurnText => turnText;
        public Text SelectedText => selectedText;
        public Text PlannedPathText => plannedPathText;

        public Button SubmitJumpButton => submitJumpButton;
        public Button UndoJumpButton => undoJumpButton;
        public Button ClearSelectionButton => clearSelectionButton;
        public Button StartMatchButton => startMatchButton;
        public Button CancelMatchButton => cancelMatchButton;
        public Button CoordToggleButton => coordToggleButton;
        public Text CoordToggleLabel => coordToggleLabel;

        public RectTransform BoardGridRoot => boardGridRoot;
        public RectTransform BoardCellRoot => boardCellRoot;

        public bool IsValid =>
            hudRoot != null &&
            statusText != null &&
            roomText != null &&
            selfText != null &&
            turnText != null &&
            selectedText != null &&
            plannedPathText != null &&
            submitJumpButton != null &&
            undoJumpButton != null &&
            clearSelectionButton != null &&
            startMatchButton != null &&
            cancelMatchButton != null &&
            coordToggleButton != null &&
            coordToggleLabel != null &&
            boardGridRoot != null &&
            boardCellRoot != null;

        private void Awake()
        {
            EnsureBuilt();
        }

        private void EnsureBuilt()
        {
            if (IsValid)
            {
                return;
            }

            if (hudRoot == null)
            {
                throw new MissingReferenceException("GameBootstrapView.hudRoot is missing.");
            }

            statusText = CreateInfoText("StatusText", new Vector2(12f, -20f), new Vector2(296f, 42f), 18);
            roomText = CreateInfoText("RoomText", new Vector2(12f, -62f), new Vector2(296f, 24f), 18);
            selfText = CreateInfoText("SelfText", new Vector2(12f, -90f), new Vector2(296f, 24f), 18);
            turnText = CreateInfoText("TurnText", new Vector2(12f, -118f), new Vector2(296f, 24f), 18);
            selectedText = CreateInfoText("SelectedText", new Vector2(12f, -146f), new Vector2(296f, 24f), 18);
            plannedPathText = CreateInfoText("PlannedPathText", new Vector2(12f, -174f), new Vector2(296f, 48f), 18);

            submitJumpButton = CreateButton("SubmitJumpButton", "Submit Jump Path", new Vector2(12f, -238f), new Vector2(150f, 38f), out _);
            undoJumpButton = CreateButton("UndoJumpButton", "Undo Last Jump", new Vector2(170f, -238f), new Vector2(138f, 38f), out _);
            clearSelectionButton = CreateButton("ClearSelectionButton", "Clear Selection", new Vector2(12f, -284f), new Vector2(150f, 38f), out _);
            startMatchButton = CreateButton("StartMatchButton", "Start Match", new Vector2(12f, -330f), new Vector2(120f, 38f), out _);
            cancelMatchButton = CreateButton("CancelMatchButton", "Cancel Match", new Vector2(140f, -330f), new Vector2(120f, 38f), out _);
            coordToggleButton = CreateButton("CoordToggleButton", "Coords: OFF", new Vector2(12f, -376f), new Vector2(160f, 38f), out coordToggleLabel);
        }

        private Text CreateInfoText(string name, Vector2 anchoredPosition, Vector2 size, int fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(hudRoot, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.black;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private Button CreateButton(string name, string label, Vector2 anchoredPosition, Vector2 size, out Text labelText)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(hudRoot, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var image = go.GetComponent<Image>();
            image.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            image.type = Image.Type.Sliced;
            image.color = new Color(0.82f, 0.86f, 0.92f, 1f);

            var button = go.GetComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.highlightedColor = new Color(0.90f, 0.93f, 0.98f, 1f);
            colors.pressedColor = new Color(0.70f, 0.75f, 0.84f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;

            labelText = CreateButtonLabel(go.transform, label);
            return button;
        }

        private static Text CreateButtonLabel(Transform parent, string label)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(8f, 4f);
            rect.offsetMax = new Vector2(-8f, -4f);

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.black;
            text.raycastTarget = false;
            text.text = label;
            return text;
        }
    }
}
