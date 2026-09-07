using DigBlocks.Client.UI.Views;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.UI.Placeholders
{
    //minimal code-built uGUI used until a menu is authored as a prefab; also exercises the code-menu factory seam
    internal static class PlaceholderMenuBuilder
    {
        private static readonly Vector2 ButtonSize = new Vector2(360f, 64f);
        private static readonly Color PanelColor = new Color(0.05f, 0.06f, 0.08f, 0.92f);
        private static readonly Color LabelColor = new Color(0.92f, 0.93f, 0.95f, 1f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.45f, 0.4f, 1f);

        public static IMenuView BuildTitle(Transform parent)
        {
            GameObject root = CreatePanel(parent, "Title Menu (Placeholder)");
            CreateLabel(root.transform, "Heading", "DIGBLOCKS", 64, new Vector2(0f, 180f), new Vector2(900f, 100f));
            CreateLabel(
                root.transform,
                "Subheading",
                "placeholder title screen",
                24,
                new Vector2(0f, 120f),
                new Vector2(900f, 40f));

            Button play = CreateButton(root.transform, "Play Button", "Start Game", new Vector2(0f, 0f));
            Button quit = CreateButton(root.transform, "Quit Button", "Quit", new Vector2(0f, -80f));

            var view = root.AddComponent<TitleMenuView>();
            view.SetControls(play, quit);
            view.SetInitialSelection(play);
            return view;
        }

        public static IMenuView BuildLoading(Transform parent)
        {
            GameObject root = CreatePanel(parent, "Loading Screen (Placeholder)");
            Text status = CreateLabel(
                root.transform,
                "Status Label",
                "Loading...",
                36,
                new Vector2(0f, 20f),
                new Vector2(1200f, 80f));
            Text error = CreateLabel(
                root.transform,
                "Error Label",
                string.Empty,
                26,
                new Vector2(0f, -70f),
                new Vector2(1200f, 120f));
            error.color = ErrorColor;
            error.gameObject.SetActive(false);

            var view = root.AddComponent<LoadingView>();
            view.SetLabels(status, error);
            return view;
        }

        public static IMenuView BuildPause(Transform parent)
        {
            GameObject root = CreatePanel(parent, "Pause Menu (Placeholder)");
            CreateLabel(root.transform, "Heading", "PAUSED", 52, new Vector2(0f, 160f), new Vector2(900f, 90f));

            Button resume = CreateButton(root.transform, "Resume Button", "Resume", new Vector2(0f, 20f));
            Button returnToTitle = CreateButton(
                root.transform,
                "Return To Title Button",
                "Return to Title",
                new Vector2(0f, -60f));

            var view = root.AddComponent<PauseMenuView>();
            view.SetControls(resume, returnToTitle);
            view.SetInitialSelection(resume);
            return view;
        }

        private static GameObject CreatePanel(Transform parent, string name)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));

            //built inactive so view components wire their controls before Awake runs
            panel.SetActive(false);

            var rectTransform = (RectTransform)panel.transform;
            rectTransform.SetParent(parent, false);
            Stretch(rectTransform);

            var image = panel.GetComponent<Image>();
            image.color = PanelColor;

            return panel;
        }

        private static Text CreateLabel(
            Transform parent,
            string name,
            string content,
            int fontSize,
            Vector2 anchoredPosition,
            Vector2 size)
        {
            var labelObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rectTransform = (RectTransform)labelObject.transform;
            rectTransform.SetParent(parent, false);
            Center(rectTransform, anchoredPosition, size);

            var text = labelObject.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = LabelColor;
            text.text = content;

            //decorative text should never be a raycast target
            text.raycastTarget = false;

            return text;
        }

        private static Button CreateButton(Transform parent, string name, string label, Vector2 anchoredPosition)
        {
            var buttonObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            var rectTransform = (RectTransform)buttonObject.transform;
            rectTransform.SetParent(parent, false);
            Center(rectTransform, anchoredPosition, ButtonSize);

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.18f, 0.21f, 0.26f, 1f);

            var button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;

            Text text = CreateLabel(buttonObject.transform, "Label", label, 28, Vector2.zero, ButtonSize);
            Stretch((RectTransform)text.transform);

            return button;
        }

        private static Font BuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                Debug.LogWarning("The builtin legacy font is unavailable; placeholder menu text will not render.");
            }

            return font;
        }

        private static void Center(RectTransform rectTransform, Vector2 anchoredPosition, Vector2 size)
        {
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.sizeDelta = size;
            rectTransform.anchoredPosition = anchoredPosition;
        }

        private static void Stretch(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
            rectTransform.localScale = Vector3.one;
        }
    }
}
