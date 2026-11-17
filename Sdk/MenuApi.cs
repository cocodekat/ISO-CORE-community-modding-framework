using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ISOCORE.ModSdk
{
    public sealed class MenuApi
    {
        private static readonly Dictionary<int, Sprite> RoundedSprites =
            new Dictionary<int, Sprite>();
        private readonly ManualLogSource _log;
        private MenuTheme _theme;
        private RectTransform _controlRoot;
        private UiBindingHost _bindingHost;

        internal MenuApi(ManualLogSource log) => _log = log;
        internal MenuApi(MenuTheme theme) => _theme = theme;

        public MenuTheme Theme => _theme ?? (_theme = MenuTheme.Detect(_log));

        public UiState<T> State<T>(T initialValue = default(T))
        {
            return new UiState<T>(initialValue);
        }

        /// <summary>
        /// Creates a menu. Nothing exists in the scene until it opens: its native-canvas UI tree is
        /// built on open (the Build event fills ModMenu.Content) and destroyed on close.
        /// </summary>
        public ModMenu CreateMenu(string title, Vector2 size)
        {
            return new ModMenu(title, size, this);
        }

        /// <summary>Creates a menu whose content is composed through the simple container API.</summary>
        public ModMenu Menu(string title, Vector2 size, Action<ModContainer> build)
        {
            ModMenu menu = CreateMenu(title, size);
            if (build != null)
                menu.Build += content => build(new ModContainer(this, content));
            return menu;
        }

        /// <summary>Creates an overlay layer inside the native game Canvas.</summary>
        public ModOverlay CreateOverlay(string name, int sortingOrder = 32000)
        {
            return new ModOverlay(name, NativeRoot);
        }

        /// <summary>
        /// Creates a standalone button definition. The Unity objects are created lazily by Show.
        /// Named optional arguments keep simple controls short while Component, Label and Rect
        /// remain available for advanced uGUI customization.
        /// </summary>
        public ModButton Button(string text, TMP_FontAsset font = null, float fontSize = 16f,
            Vector2? size = null, Color? border = null, float roundedEdges = 3f,
            Sprite texture = null, Color? background = null, float borderThickness = 1f)
        {
            return new ModButton(this, text, font, fontSize, size ?? new Vector2(150f, 36f),
                border, roundedEdges, texture, background, borderThickness);
        }

        /// <summary>Creates standalone text that is placed lazily by Show.</summary>
        public ModText Text(string text, TMP_FontAsset font = null, float fontSize = 24f,
            Vector2? size = null, Color? color = null,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            return new ModText(this, text, font, fontSize, size ?? new Vector2(240f, 44f),
                color, alignment);
        }

        internal RectTransform ControlRoot
        {
            get
            {
                if (_controlRoot == null)
                {
                    _controlRoot = CreateRect("ICCMF Controls", NativeRoot);
                    Stretch(_controlRoot);
                    _controlRoot.SetAsLastSibling();
                }
                return _controlRoot;
            }
        }

        internal RectTransform NativeRoot
        {
            get
            {
                Canvas canvas = FindNativeCanvas();
                if (canvas == null)
                    throw new InvalidOperationException("The game's UI Canvas is not available yet.");
                return canvas.transform as RectTransform;
            }
        }

        internal UiBindingHost Bindings
        {
            get
            {
                if (_bindingHost == null)
                {
                    var host = new GameObject("ICCMF UI Bindings");
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    _bindingHost = host.AddComponent<UiBindingHost>();
                    _bindingHost.Log = _log;
                }
                return _bindingHost;
            }
        }

        private static Canvas FindNativeCanvas()
        {
            try
            {
                var singletonProperty = AccessTools.Property(typeof(UI_Controler), "singleton");
                var singletonField = AccessTools.Field(typeof(UI_Controler), "singleton");
                object controller = singletonProperty != null
                    ? singletonProperty.GetValue(null, null)
                    : singletonField?.GetValue(null);

                var canvasProperty = AccessTools.Property(typeof(UI_Controler), "MainCanvas");
                var canvasField = AccessTools.Field(typeof(UI_Controler), "MainCanvas");
                object value = canvasProperty != null
                    ? canvasProperty.GetValue(controller, null)
                    : canvasField?.GetValue(controller);
                GameObject gameObject = value as GameObject ?? (value as Component)?.gameObject;
                Canvas known = gameObject == null ? null : gameObject.GetComponent<Canvas>() ??
                    gameObject.GetComponentInParent<Canvas>();
                if (known != null) return known;
            }
            catch (Exception) { }

            return Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(canvas => canvas != null && canvas.gameObject.activeInHierarchy &&
                    canvas.GetComponent<GraphicRaycaster>() != null &&
                    !canvas.name.StartsWith("ICCMF", StringComparison.OrdinalIgnoreCase) &&
                    canvas.name.IndexOf("Mod UI", StringComparison.OrdinalIgnoreCase) < 0)
                .OrderByDescending(canvas =>
                {
                    RectTransform rect = canvas.transform as RectTransform;
                    return rect == null ? 0f : rect.rect.width * rect.rect.height;
                })
                .FirstOrDefault();
        }

        internal static void WhenDestroyed(GameObject owner, Action cleanup)
        {
            UiLifetime lifetime = owner.GetComponent<UiLifetime>() ?? owner.AddComponent<UiLifetime>();
            lifetime.Add(cleanup);
        }

        public RectTransform AddAnchored(Transform parent, string name, Vector2 anchor,
            Vector2 size, Vector2 offset)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;
            return rect;
        }

        public void Fill(RectTransform rect, float left = 0f, float right = 0f,
            float top = 0f, float bottom = 0f) => Stretch(rect, left, right, top, bottom);

        public RectTransform AddVertical(Transform parent, string name = "Column", float spacing = 12f,
            RectOffset padding = null)
        {
            RectTransform rect = CreateRect(name, parent);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding ?? new RectOffset();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rect;
        }

        public RectTransform AddHorizontal(Transform parent, string name = "Row", float spacing = 12f,
            RectOffset padding = null)
        {
            RectTransform rect = CreateRect(name, parent);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding ?? new RectOffset();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rect;
        }

        public TextMeshProUGUI AddLabel(Transform parent, string text, float size = 24f,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft, Color? color = null)
        {
            RectTransform rect = CreateRect("Label", parent);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            ApplyText(label, text, size, alignment, color ?? Theme.Text);
            label.raycastTarget = false;
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = size + 10f;
            layout.preferredHeight = size + 10f;
            return label;
        }

        public UnityEngine.UI.Button AddButton(Transform parent, string text, UnityAction clicked,
            ButtonTone tone = ButtonTone.Normal, float height = 36f,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center, float fontSize = 16f)
        {
            Color fill = tone == ButtonTone.Primary ? Theme.Accent :
                tone == ButtonTone.Danger ? Theme.Danger : Theme.Control;
            Color border = tone == ButtonTone.Primary ? Theme.AccentBright : Theme.Border;

            UnityEngine.UI.Image fillImage;
            RectTransform rect = CreateBordered("Button", parent, border, fill, 1f, out fillImage);
            fillImage.raycastTarget = false;

            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = fillImage;
            // Never take part in keyboard/gamepad navigation, so the game's own
            // Space/Enter/arrow handling is not hijacked by our buttons.
            button.navigation = new UnityEngine.UI.Navigation
            {
                mode = UnityEngine.UI.Navigation.Mode.None
            };
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(.72f, .72f, .72f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(.55f, .55f, .55f, .55f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = .06f;
            button.colors = colors;
            if (clicked != null) button.onClick.AddListener(clicked);

            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;

            RectTransform textRect = CreateRect("Text", fillImage.rectTransform);
            Stretch(textRect, 14f, 14f, 4f, 4f);
            var label = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            ApplyText(label, text, fontSize, alignment,
                tone == ButtonTone.Primary ? Theme.DarkText : Theme.Text);
            label.raycastTarget = false;
            return button;
        }

        public UnityEngine.UI.Button AddListItem(Transform parent, string title, string metadata,
            Sprite icon, bool selected, UnityAction clicked, float height = 48f)
        {
            Color border = selected ? Theme.AccentBright : Theme.Border;
            Color fill = selected ? (Color)new Color32(25, 82, 55, 245) :
                (Color)new Color32(31, 32, 29, 245);
            UnityEngine.UI.Image fillImage;
            RectTransform root = CreateBordered("List item", parent, border, fill,
                selected ? 2f : 1f, out fillImage);
            fillImage.raycastTarget = false;

            var button = root.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = fillImage;
            button.navigation = new UnityEngine.UI.Navigation
            {
                mode = UnityEngine.UI.Navigation.Mode.None
            };
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(.72f, .72f, .72f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = .06f;
            button.colors = colors;
            if (clicked != null) button.onClick.AddListener(clicked);

            var layout = root.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;

            float titleLeft = 14f;
            if (icon != null)
            {
                RectTransform iconRect = CreateRect("Icon", fillImage.rectTransform);
                iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, .5f);
                iconRect.pivot = new Vector2(0f, .5f);
                iconRect.anchoredPosition = new Vector2(10f, 0f);
                iconRect.sizeDelta = new Vector2(28f, 28f);
                var image = iconRect.gameObject.AddComponent<UnityEngine.UI.Image>();
                image.sprite = icon;
                image.preserveAspect = true;
                image.raycastTarget = false;
                titleLeft = 52f;
            }

            RectTransform metadataRect = CreateRect("Metadata", fillImage.rectTransform);
            metadataRect.anchorMin = new Vector2(1f, 0f);
            metadataRect.anchorMax = Vector2.one;
            metadataRect.pivot = new Vector2(1f, .5f);
            metadataRect.sizeDelta = new Vector2(118f, 0f);
            metadataRect.anchoredPosition = new Vector2(-12f, 0f);
            var metadataLabel = metadataRect.gameObject.AddComponent<TextMeshProUGUI>();
            ApplyText(metadataLabel, metadata, 13f, TextAlignmentOptions.MidlineRight, Theme.Muted);
            metadataLabel.raycastTarget = false;

            RectTransform titleRect = CreateRect("Title", fillImage.rectTransform);
            Stretch(titleRect, titleLeft, 138f, 4f, 4f);
            var titleLabel = titleRect.gameObject.AddComponent<TextMeshProUGUI>();
            ApplyText(titleLabel, title, 16f, TextAlignmentOptions.MidlineLeft, Theme.Text);
            titleLabel.raycastTarget = false;
            return button;
        }

        public TMP_InputField AddInput(Transform parent, string placeholder, float height = 48f)
        {
            UnityEngine.UI.Image fillImage;
            RectTransform root = CreateBordered("Input", parent, Theme.Border, Theme.Input, 1f,
                out fillImage);
            var layout = root.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;

            var input = root.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = fillImage;
            input.navigation = new UnityEngine.UI.Navigation
            {
                mode = UnityEngine.UI.Navigation.Mode.None
            };

            RectTransform viewport = CreateRect("Text Area", fillImage.rectTransform);
            Stretch(viewport, 12f, 12f, 6f, 6f);
            viewport.gameObject.AddComponent<RectMask2D>();

            RectTransform textRect = CreateRect("Text", viewport);
            Stretch(textRect);
            var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            ApplyText(text, "", 16f, TextAlignmentOptions.MidlineLeft, Theme.Text);
            text.raycastTarget = false;

            RectTransform placeholderRect = CreateRect("Placeholder", viewport);
            Stretch(placeholderRect);
            var hint = placeholderRect.gameObject.AddComponent<TextMeshProUGUI>();
            ApplyText(hint, placeholder, 15f, TextAlignmentOptions.MidlineLeft, Theme.Muted);
            hint.raycastTarget = false;

            input.textViewport = viewport;
            input.textComponent = text;
            input.placeholder = hint;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.richText = false;
            input.customCaretColor = true;
            input.caretColor = Theme.AccentBright;
            input.selectionColor = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, .55f);
            return input;
        }

        public ModScrollList AddScrollList(Transform parent, string name, float height = -1f)
        {
            UnityEngine.UI.Image fillImage;
            RectTransform root = CreateBordered(name, parent, Theme.Border, Theme.Inset, 1f,
                out fillImage);
            var layout = root.gameObject.AddComponent<LayoutElement>();
            layout.flexibleHeight = height < 0f ? 1f : 0f;
            if (height >= 0f) layout.preferredHeight = height;

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 8f;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            RectTransform viewport = CreateRect("Viewport", fillImage.rectTransform);
            Stretch(viewport, 2f, 9f, 2f, 2f);
            viewport.gameObject.AddComponent<RectMask2D>();

            RectTransform content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            var contentLayout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(6, 6, 6, 6);
            contentLayout.spacing = 6f;
            contentLayout.childAlignment = TextAnchor.UpperLeft;
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Visible scrollbar on the right edge.
            RectTransform track = CreateRect("Scrollbar", fillImage.rectTransform);
            track.anchorMin = new Vector2(1f, 0f);
            track.anchorMax = new Vector2(1f, 1f);
            track.offsetMin = new Vector2(-7f, 4f);
            track.offsetMax = new Vector2(-3f, -4f);
            var trackImage = track.gameObject.AddComponent<UnityEngine.UI.Image>();
            trackImage.color = Theme.Input;
            trackImage.sprite = RoundedSprite();
            trackImage.type = UnityEngine.UI.Image.Type.Sliced;
            RectTransform sliding = CreateRect("Sliding Area", track);
            Stretch(sliding);
            RectTransform handle = CreateRect("Handle", sliding);
            Stretch(handle);
            var handleImage = handle.gameObject.AddComponent<UnityEngine.UI.Image>();
            handleImage.color = Theme.Border;
            handleImage.sprite = RoundedSprite();
            handleImage.type = UnityEngine.UI.Image.Type.Sliced;
            var bar = track.gameObject.AddComponent<UnityEngine.UI.Scrollbar>();
            bar.handleRect = handle;
            bar.targetGraphic = handleImage;
            bar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            bar.navigation = new UnityEngine.UI.Navigation
            {
                mode = UnityEngine.UI.Navigation.Mode.None
            };

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            return new ModScrollList(this, content, scroll);
        }

        public LayoutElement SetLayout(Component component, float preferredWidth = -1f,
            float preferredHeight = -1f, float flexibleWidth = -1f, float flexibleHeight = -1f)
        {
            LayoutElement layout = component.GetComponent<LayoutElement>() ??
                component.gameObject.AddComponent<LayoutElement>();
            if (preferredWidth >= 0f) layout.preferredWidth = preferredWidth;
            if (preferredHeight >= 0f) layout.preferredHeight = preferredHeight;
            if (flexibleWidth >= 0f) layout.flexibleWidth = flexibleWidth;
            if (flexibleHeight >= 0f) layout.flexibleHeight = flexibleHeight;
            return layout;
        }

        internal static RectTransform CreateRect(string name, Transform parent)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.layer = 5;
            RectTransform rect = (RectTransform)gameObject.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>A real border: an outer image in the border colour with an inset fill image.</summary>
        internal static RectTransform CreateBordered(string name, Transform parent, Color border,
            Color fill, float thickness, out UnityEngine.UI.Image fillImage)
        {
            RectTransform outer = CreateRect(name, parent);
            var borderImage = outer.gameObject.AddComponent<UnityEngine.UI.Image>();
            borderImage.color = border;
            borderImage.sprite = RoundedSprite();
            borderImage.type = UnityEngine.UI.Image.Type.Sliced;
            RectTransform inner = CreateRect("Fill", outer);
            Stretch(inner, thickness, thickness, thickness, thickness);
            fillImage = inner.gameObject.AddComponent<UnityEngine.UI.Image>();
            fillImage.color = fill;
            fillImage.sprite = RoundedSprite();
            fillImage.type = UnityEngine.UI.Image.Type.Sliced;
            return outer;
        }

        internal static Sprite RoundedSprite(float requestedRadius = 7f)
        {
            const int size = 32;
            int radius = Mathf.Clamp(Mathf.RoundToInt(requestedRadius), 1, size / 2 - 1);
            Sprite cached;
            if (RoundedSprites.TryGetValue(radius, out cached)) return cached;
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.name = "ICCMF Rounded UI " + radius;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float centerX = Mathf.Clamp(x + .5f, radius, size - radius);
                    float centerY = Mathf.Clamp(y + .5f, radius, size - radius);
                    float distance = Vector2.Distance(new Vector2(x + .5f, y + .5f),
                        new Vector2(centerX, centerY));
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f,
                        Mathf.Clamp01(radius - distance + .5f)));
                }
            }
            texture.Apply();
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
            sprite.name = "ICCMF Rounded UI " + radius;
            RoundedSprites.Add(radius, sprite);
            return sprite;
        }

        internal static void Stretch(RectTransform rect, float left = 0f, float right = 0f,
            float top = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        internal void ApplyText(TMP_Text text, string value, float size,
            TextAlignmentOptions alignment, Color color)
        {
            text.text = value;
            text.font = Theme.Font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }
    }

    public sealed class UiState<T>
    {
        private T _value;

        internal UiState(T initialValue) => _value = initialValue;

        public event Action<T> Changed;

        public T Value
        {
            get { return _value; }
            set
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
                Changed?.Invoke(value);
            }
        }

        public static implicit operator T(UiState<T> state) => state.Value;
    }

    internal sealed class UiLifetime : MonoBehaviour
    {
        private readonly List<Action> _cleanup = new List<Action>();

        internal void Add(Action action)
        {
            if (action != null) _cleanup.Add(action);
        }

        private void OnDestroy()
        {
            for (int index = _cleanup.Count - 1; index >= 0; index--)
            {
                try { _cleanup[index](); }
                catch (Exception) { }
            }
            _cleanup.Clear();
        }
    }

    internal sealed class UiBindingHost : MonoBehaviour
    {
        private sealed class Binding
        {
            internal Action Tick;
        }

        private readonly List<Binding> _bindings = new List<Binding>();
        internal ManualLogSource Log { get; set; }

        internal Action Add(Action tick)
        {
            var binding = new Binding { Tick = tick };
            _bindings.Add(binding);
            return () => _bindings.Remove(binding);
        }

        private void Update()
        {
            Binding[] snapshot = _bindings.ToArray();
            foreach (Binding binding in snapshot)
            {
                if (!_bindings.Contains(binding)) continue;
                try { binding.Tick(); }
                catch (Exception exception)
                {
                    _bindings.Remove(binding);
                    Log?.LogError("UI binding disabled after an error: " + exception);
                }
            }
        }
    }

    public enum ButtonTone
    {
        Normal,
        Primary,
        Danger
    }

    public sealed class MenuTheme
    {
        internal static MenuTheme Detect(ManualLogSource log)
        {
            TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            TMP_Text inventoryText = texts.FirstOrDefault(candidate => candidate != null &&
                candidate.font != null && candidate.gameObject.activeInHierarchy &&
                (candidate.text ?? "").IndexOf("INVENTORY", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? texts.FirstOrDefault(candidate => candidate != null && candidate.font != null &&
                    (candidate.text ?? "").IndexOf("INVENTORY", StringComparison.OrdinalIgnoreCase) >= 0);
            TMP_FontAsset font = inventoryText?.font
                ?? texts.FirstOrDefault(candidate => candidate != null && candidate.font != null)?.font
                ?? TMP_Settings.defaultFontAsset;
            log?.LogInfo("MenuApi theme ready; font=" + (font == null ? "fallback" : font.name) +
                (inventoryText == null ? " (fallback selection)" : " (native inventory)"));
            return new MenuTheme(font);
        }

        private MenuTheme(TMP_FontAsset font) => Font = font;

        public TMP_FontAsset Font { get; }
        public Color Backdrop { get; } = new Color32(0, 0, 0, 80);
        public Color Panel { get; } = new Color32(10, 10, 8, 225);
        public Color Inset { get; } = new Color32(0, 0, 0, 130);
        public Color Control { get; } = new Color32(0, 0, 0, 155);
        public Color Input { get; } = new Color32(0, 0, 0, 135);
        public Color Border { get; } = new Color32(255, 255, 235, 105);
        public Color Accent { get; } = new Color32(30, 170, 100, 220);
        public Color AccentBright { get; } = new Color32(215, 218, 205, 235);
        public Color Danger { get; } = new Color32(125, 48, 42, 220);
        public Color Text { get; } = Color.white;
        public Color DarkText { get; } = Color.white;
        public Color Muted { get; } = new Color32(160, 158, 142, 255);
    }

    public sealed class ModOverlay
    {
        private readonly GameObject _root;

        internal ModOverlay(string name, RectTransform parent)
        {
            Root = MenuApi.CreateRect(name, parent);
            MenuApi.Stretch(Root);
            Root.SetAsLastSibling();
            _root = Root.gameObject;
        }

        public RectTransform Root { get; }
        public bool Active => _root != null && _root.activeSelf;
        public void SetActive(bool active)
        {
            if (_root != null) _root.SetActive(active);
        }
        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
    }

    public enum UiAnchor
    {
        TopLeft,
        Top,
        TopRight,
        Left,
        Center,
        Right,
        BottomLeft,
        Bottom,
        BottomRight
    }

    public sealed class ModButton
    {
        private readonly MenuApi _ui;
        private readonly TMP_FontAsset _font;
        private readonly float _fontSize;
        private readonly Color _border;
        private readonly float _roundedEdges;
        private readonly Sprite _texture;
        private readonly float _borderThickness;
        private Vector2 _size;
        private Color _background;
        private string _text;
        private UnityAction _clicked;
        private UnityEngine.UI.Image _fill;
        private readonly List<Action> _cleanup = new List<Action>();
        private bool _destroyed;

        internal ModButton(MenuApi ui, string text, TMP_FontAsset font, float fontSize,
            Vector2 size, Color? border, float roundedEdges, Sprite texture, Color? background,
            float borderThickness)
        {
            _ui = ui;
            _text = text ?? "";
            _font = font;
            _fontSize = fontSize;
            _size = size;
            _border = border ?? ui.Theme.Border;
            _roundedEdges = roundedEdges;
            _texture = texture;
            _background = background ?? ui.Theme.Control;
            _borderThickness = Mathf.Max(0f, borderThickness);
        }

        public RectTransform Rect { get; private set; }
        public UnityEngine.UI.Button Component { get; private set; }
        public TextMeshProUGUI Label { get; private set; }
        public bool IsVisible => Rect != null && Rect.gameObject.activeSelf;

        public ModButton Show(UiAnchor anchor = UiAnchor.Center, Vector2? offset = null)
        {
            return Show(AnchorPoint(anchor), offset);
        }

        public ModButton Show(Vector2 anchor, Vector2? offset = null)
        {
            EnsureCreated(null);
            anchor.x = Mathf.Clamp01(anchor.x);
            anchor.y = Mathf.Clamp01(anchor.y);
            Rect.anchorMin = Rect.anchorMax = anchor;
            Rect.pivot = anchor;
            Rect.sizeDelta = _size;
            Rect.anchoredPosition = offset ?? Vector2.zero;
            Rect.gameObject.SetActive(true);
            return this;
        }

        /// <summary>Adds the button to a row or column controlled by a layout group.</summary>
        public ModButton AddTo(Transform parent, float preferredHeight = -1f)
        {
            EnsureCreated(parent);
            Rect.SetParent(parent, false);
            if (preferredHeight >= 0f)
                _ui.SetLayout(Rect, -1f, preferredHeight, 1f, 0f);
            return this;
        }

        public ModButton Hide()
        {
            if (Rect != null) Rect.gameObject.SetActive(false);
            return this;
        }

        public ModButton VisibleWhen(Func<bool> condition, UiAnchor anchor = UiAnchor.Center,
            Vector2? offset = null)
        {
            bool last = false;
            bool first = true;
            Action remove = _ui.Bindings.Add(delegate
            {
                bool visible = condition != null && condition();
                if (!first && visible == last) return;
                first = false;
                last = visible;
                if (visible) Show(anchor, offset);
                else Hide();
            });
            _cleanup.Add(remove);
            return this;
        }

        public ModButton Hotkey(Key key)
        {
            Action remove = _ui.Bindings.Add(delegate
            {
                Keyboard keyboard = Keyboard.current;
                if (IsVisible && keyboard != null && keyboard[key].wasPressedThisFrame)
                {
                    EnsureCreated(null);
                    Component.onClick.Invoke();
                }
            });
            _cleanup.Add(remove);
            return this;
        }

        public ModButton BindText(UiState<string> state)
        {
            if (state == null) return this;
            Action<string> changed = SetTextFromState;
            state.Changed += changed;
            Action unsubscribe = () => state.Changed -= changed;
            _cleanup.Add(unsubscribe);
            if (Rect != null) MenuApi.WhenDestroyed(Rect.gameObject, unsubscribe);
            SetText(state.Value);
            return this;
        }

        public ModButton OnClick(UnityAction action)
        {
            if (Component != null && _clicked != null) Component.onClick.RemoveListener(_clicked);
            _clicked = action;
            if (Component != null && _clicked != null) Component.onClick.AddListener(_clicked);
            return this;
        }

        public ModButton SetText(string text)
        {
            _text = text ?? "";
            if (Label != null) Label.text = _text;
            return this;
        }

        public ModButton SetBackground(Color color)
        {
            _background = color;
            if (_fill != null) _fill.color = color;
            return this;
        }

        public ModButton SetSize(Vector2 size)
        {
            _size = size;
            if (Rect != null) Rect.sizeDelta = size;
            return this;
        }

        public void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            for (int index = _cleanup.Count - 1; index >= 0; index--) _cleanup[index]();
            _cleanup.Clear();
            if (Rect != null) UnityEngine.Object.Destroy(Rect.gameObject);
            Rect = null;
            Component = null;
            Label = null;
            _fill = null;
        }

        private void EnsureCreated(Transform parent)
        {
            if (_destroyed) throw new InvalidOperationException("This button was destroyed.");
            if (Rect != null) return;
            Rect = MenuApi.CreateBordered("Button", parent ?? _ui.ControlRoot, _border, _background,
                _borderThickness, out _fill);
            Rect.GetComponent<UnityEngine.UI.Image>().sprite =
                MenuApi.RoundedSprite(_roundedEdges);
            if (_texture == null)
                _fill.sprite = MenuApi.RoundedSprite(_roundedEdges);
            else
            {
                _fill.sprite = _texture;
                _fill.type = UnityEngine.UI.Image.Type.Simple;
            }
            _fill.raycastTarget = false;

            Component = Rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            Component.targetGraphic = _fill;
            Component.navigation = new UnityEngine.UI.Navigation
            {
                mode = UnityEngine.UI.Navigation.Mode.None
            };
            ColorBlock colors = Component.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(.72f, .72f, .72f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(.55f, .55f, .55f, .55f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = .06f;
            Component.colors = colors;
            if (_clicked != null) Component.onClick.AddListener(_clicked);

            RectTransform textRect = MenuApi.CreateRect("Text", _fill.rectTransform);
            MenuApi.Stretch(textRect, 12f, 12f, 4f, 4f);
            Label = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            _ui.ApplyText(Label, _text, _fontSize, TextAlignmentOptions.Center, _ui.Theme.Text);
            if (_font != null) Label.font = _font;
            Label.raycastTarget = false;
        }

        private void SetTextFromState(string value) => SetText(value);

        internal static Vector2 AnchorPoint(UiAnchor anchor)
        {
            switch (anchor)
            {
                case UiAnchor.TopLeft: return new Vector2(0f, 1f);
                case UiAnchor.Top: return new Vector2(.5f, 1f);
                case UiAnchor.TopRight: return new Vector2(1f, 1f);
                case UiAnchor.Left: return new Vector2(0f, .5f);
                case UiAnchor.Right: return new Vector2(1f, .5f);
                case UiAnchor.BottomLeft: return new Vector2(0f, 0f);
                case UiAnchor.Bottom: return new Vector2(.5f, 0f);
                case UiAnchor.BottomRight: return new Vector2(1f, 0f);
                default: return new Vector2(.5f, .5f);
            }
        }
    }

    public sealed class ModText
    {
        private readonly MenuApi _ui;
        private readonly TMP_FontAsset _font;
        private readonly float _fontSize;
        private readonly Color _color;
        private readonly TextAlignmentOptions _alignment;
        private Vector2 _size;
        private string _text;
        private readonly List<Action> _cleanup = new List<Action>();
        private bool _destroyed;

        internal ModText(MenuApi ui, string text, TMP_FontAsset font, float fontSize,
            Vector2 size, Color? color, TextAlignmentOptions alignment)
        {
            _ui = ui;
            _text = text ?? "";
            _font = font;
            _fontSize = fontSize;
            _size = size;
            _color = color ?? ui.Theme.Text;
            _alignment = alignment;
        }

        public RectTransform Rect { get; private set; }
        public TextMeshProUGUI Component { get; private set; }
        public bool IsVisible => Rect != null && Rect.gameObject.activeSelf;

        public ModText Show(UiAnchor anchor = UiAnchor.Center, Vector2? offset = null)
        {
            return Show(ModButton.AnchorPoint(anchor), offset);
        }

        public ModText Show(Vector2 anchor, Vector2? offset = null)
        {
            EnsureCreated(null);
            anchor.x = Mathf.Clamp01(anchor.x);
            anchor.y = Mathf.Clamp01(anchor.y);
            Rect.anchorMin = Rect.anchorMax = anchor;
            Rect.pivot = anchor;
            Rect.sizeDelta = _size;
            Rect.anchoredPosition = offset ?? Vector2.zero;
            Rect.gameObject.SetActive(true);
            return this;
        }

        /// <summary>Adds the text to a row or column controlled by a layout group.</summary>
        public ModText AddTo(Transform parent, float preferredHeight = -1f)
        {
            EnsureCreated(parent);
            Rect.SetParent(parent, false);
            if (preferredHeight >= 0f)
                _ui.SetLayout(Rect, -1f, preferredHeight, 1f, 0f);
            return this;
        }

        public ModText Hide()
        {
            if (Rect != null) Rect.gameObject.SetActive(false);
            return this;
        }

        public ModText SetText(string text)
        {
            _text = text ?? "";
            if (Component != null) Component.text = _text;
            return this;
        }

        public ModText Bind(UiState<string> state)
        {
            if (state == null) return this;
            Action<string> changed = SetTextFromState;
            state.Changed += changed;
            Action unsubscribe = () => state.Changed -= changed;
            _cleanup.Add(unsubscribe);
            if (Rect != null) MenuApi.WhenDestroyed(Rect.gameObject, unsubscribe);
            SetText(state.Value);
            return this;
        }

        public ModText Bind<T>(UiState<T> state, Func<T, string> format)
        {
            if (state == null || format == null) return this;
            Action<T> changed = value => SetText(format(value));
            state.Changed += changed;
            Action unsubscribe = () => state.Changed -= changed;
            _cleanup.Add(unsubscribe);
            if (Rect != null) MenuApi.WhenDestroyed(Rect.gameObject, unsubscribe);
            SetText(format(state.Value));
            return this;
        }

        public ModText SetSize(Vector2 size)
        {
            _size = size;
            if (Rect != null) Rect.sizeDelta = size;
            return this;
        }

        public void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            for (int index = _cleanup.Count - 1; index >= 0; index--) _cleanup[index]();
            _cleanup.Clear();
            if (Rect != null) UnityEngine.Object.Destroy(Rect.gameObject);
            Rect = null;
            Component = null;
        }

        private void EnsureCreated(Transform parent)
        {
            if (_destroyed) throw new InvalidOperationException("This text was destroyed.");
            if (Rect != null) return;
            Rect = MenuApi.CreateRect("Text", parent ?? _ui.ControlRoot);
            Component = Rect.gameObject.AddComponent<TextMeshProUGUI>();
            _ui.ApplyText(Component, _text, _fontSize, _alignment, _color);
            if (_font != null) Component.font = _font;
            Component.raycastTarget = false;
        }

        private void SetTextFromState(string value) => SetText(value);
    }

    public sealed class ModInput
    {
        internal ModInput(TMP_InputField component) => Component = component;

        public TMP_InputField Component { get; }

        public string Text
        {
            get { return Component.text; }
            set { Component.text = value ?? ""; }
        }

        public int IntValue
        {
            get
            {
                int value;
                return int.TryParse(Component.text, out value) ? value : 0;
            }
            set { Component.text = value.ToString(); }
        }

        public ModInput Integer()
        {
            Component.contentType = TMP_InputField.ContentType.IntegerNumber;
            return this;
        }

        public ModInput OnChanged(UnityAction<string> action)
        {
            if (action != null) Component.onValueChanged.AddListener(action);
            return this;
        }

        public ModInput Bind(UiState<string> state)
        {
            if (state == null) return this;
            bool updating = false;
            UnityAction<string> fromInput = value =>
            {
                if (updating) return;
                state.Value = value;
            };
            Action<string> fromState = value =>
            {
                if (Component == null || Component.text == (value ?? "")) return;
                updating = true;
                Component.text = value ?? "";
                updating = false;
            };
            Component.onValueChanged.AddListener(fromInput);
            state.Changed += fromState;
            MenuApi.WhenDestroyed(Component.gameObject, delegate
            {
                state.Changed -= fromState;
                if (Component != null) Component.onValueChanged.RemoveListener(fromInput);
            });
            fromState(state.Value);
            return this;
        }

        public ModInput Bind(UiState<int> state)
        {
            if (state == null) return this;
            Integer();
            bool updating = false;
            UnityAction<string> fromInput = value =>
            {
                int parsed;
                if (!updating && int.TryParse(value, out parsed)) state.Value = parsed;
            };
            Action<int> fromState = value =>
            {
                string text = value.ToString();
                if (Component == null || Component.text == text) return;
                updating = true;
                Component.text = text;
                updating = false;
            };
            Component.onValueChanged.AddListener(fromInput);
            state.Changed += fromState;
            MenuApi.WhenDestroyed(Component.gameObject, delegate
            {
                state.Changed -= fromState;
                if (Component != null) Component.onValueChanged.RemoveListener(fromInput);
            });
            fromState(state.Value);
            return this;
        }
    }

    /// <summary>
    /// A composable row or column. It keeps common menu code declarative while exposing Rect for
    /// direct uGUI access whenever a mod needs something more specialized.
    /// </summary>
    public sealed class ModContainer
    {
        private readonly MenuApi _ui;

        internal ModContainer(MenuApi ui, RectTransform rect)
        {
            _ui = ui;
            Rect = rect;
        }

        public RectTransform Rect { get; }

        public ModContainer Fill(float left = 0f, float right = 0f, float top = 0f,
            float bottom = 0f)
        {
            _ui.Fill(Rect, left, right, top, bottom);
            return this;
        }

        public ModContainer Layout(float preferredHeight = -1f, float preferredWidth = -1f,
            float flexibleWidth = 1f, float flexibleHeight = 0f)
        {
            _ui.SetLayout(Rect, preferredWidth, preferredHeight, flexibleWidth, flexibleHeight);
            return this;
        }

        public ModContainer Column(string name = "Column", float spacing = 12f,
            RectOffset padding = null)
        {
            return new ModContainer(_ui, _ui.AddVertical(Rect, name, spacing, padding));
        }

        public ModContainer Row(string name = "Row", float spacing = 12f,
            RectOffset padding = null, bool expandHeight = false)
        {
            RectTransform row = _ui.AddHorizontal(Rect, name, spacing, padding);
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = expandHeight;
            return new ModContainer(_ui, row);
        }

        public ModText Text(string text, float fontSize = 24f, float height = -1f,
            TMP_FontAsset font = null, Color? color = null,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
        {
            Vector2 size = new Vector2(240f, height >= 0f ? height : fontSize + 10f);
            return _ui.Text(text, font, fontSize, size, color, alignment).AddTo(Rect,
                height >= 0f ? height : fontSize + 10f);
        }

        public ModInput Input(string placeholder, float height = 48f,
            UnityAction<string> changed = null)
        {
            TMP_InputField input = _ui.AddInput(Rect, placeholder, height);
            if (changed != null) input.onValueChanged.AddListener(changed);
            return new ModInput(input);
        }

        public TMP_InputField RawInput(string placeholder, float height = 48f)
        {
            return _ui.AddInput(Rect, placeholder, height);
        }

        public ModScrollList ScrollList(string name = "Items", float height = -1f)
        {
            return _ui.AddScrollList(Rect, name, height);
        }

        public ModButton Button(string text, UnityAction clicked = null,
            ButtonTone tone = ButtonTone.Normal, float height = 48f, float fontSize = 19f,
            TMP_FontAsset font = null, float roundedEdges = 7f, Sprite texture = null,
            float borderThickness = 2f)
        {
            Color background = tone == ButtonTone.Primary ? _ui.Theme.Accent :
                tone == ButtonTone.Danger ? _ui.Theme.Danger : _ui.Theme.Control;
            Color border = tone == ButtonTone.Primary ? _ui.Theme.AccentBright : _ui.Theme.Border;
            return _ui.Button(text, font, fontSize, new Vector2(180f, height), border,
                    roundedEdges, texture, background, borderThickness)
                .OnClick(clicked)
                .AddTo(Rect, height);
        }
    }

    public sealed class ModMenu
    {
        private readonly string _title;
        private readonly Vector2 _size;
        private readonly MenuTheme _theme;
        private readonly MenuApi _ui;
        private GameObject _root;
        private Key? _closeKey;

        internal ModMenu(string title, Vector2 size, MenuApi ui)
        {
            _title = title;
            _size = size;
            _ui = ui;
            _theme = ui.Theme;
        }

        /// <summary>The area inside the frame. Only valid while the menu is open.</summary>
        public RectTransform Content { get; private set; }
        public bool IsOpen => _root != null;

        /// <summary>Fires every time the menu opens, after the frame exists. Fill the content here.</summary>
        public event Action<RectTransform> Build;
        public event Action Opened;
        public event Action Closed;

        public void Toggle() => SetOpen(!IsOpen);

        public ModMenu CloseOn(Key key)
        {
            _closeKey = key;
            return this;
        }

        public void SetOpen(bool open)
        {
            if (IsOpen == open) return;
            if (!open)
            {
                Close();
                return;
            }
            try
            {
                Create();
                Build?.Invoke(Content);
                LayoutRebuilder.ForceRebuildLayoutImmediate(Content);
                Opened?.Invoke();
            }
            catch (Exception)
            {
                Close();
                throw;
            }
        }

        public void Destroy() => Close();

        public void Close()
        {
            if (_root == null) return;
            GameObject root = _root;
            _root = null;
            Content = null;
            root.SetActive(false);
            UnityEngine.Object.Destroy(root);
            // Don't leave a (now destroyed) mod button as the selected object for the game's UI.
            if (UnityEngine.EventSystems.EventSystem.current != null)
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
            Closed?.Invoke();
        }

        private void Create()
        {
            RectTransform rootRect = MenuApi.CreateRect(_title + " (Mod UI)", _ui.NativeRoot);
            MenuApi.Stretch(rootRect);
            rootRect.SetAsLastSibling();
            _root = rootRect.gameObject;
            if (_closeKey.HasValue)
            {
                var hotkey = _root.AddComponent<ModMenuHotkey>();
                hotkey.Menu = this;
                hotkey.Key = _closeKey.Value;
            }
            RectTransform overlay = MenuApi.CreateRect("Overlay", rootRect);
            MenuApi.Stretch(overlay);
            var dim = overlay.gameObject.AddComponent<UnityEngine.UI.Image>();
            dim.color = _theme.Backdrop;

            UnityEngine.UI.Image frameFill;
            RectTransform frame = MenuApi.CreateBordered("Frame", overlay, _theme.Border,
                _theme.Panel, 1f, out frameFill);
            frame.anchorMin = frame.anchorMax = new Vector2(.5f, .5f);
            frame.pivot = new Vector2(.5f, .5f);
            frame.sizeDelta = _size;
            RectTransform inner = frameFill.rectTransform;

            RectTransform header = MenuApi.CreateRect("Header", inner);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = Vector2.one;
            header.pivot = new Vector2(.5f, 1f);
            header.offsetMin = new Vector2(18f, -50f);
            header.offsetMax = new Vector2(-18f, -10f);
            var titleText = _ui.AddLabel(header, _title.ToUpperInvariant(), 20f,
                TextAlignmentOptions.MidlineLeft, _theme.Text);
            MenuApi.Stretch(titleText.rectTransform, 0f, 42f, 0f, 0f);

            RectTransform closeHolder = MenuApi.CreateRect("Close Holder", header);
            closeHolder.anchorMin = closeHolder.anchorMax = new Vector2(1f, .5f);
            closeHolder.pivot = new Vector2(1f, .5f);
            closeHolder.sizeDelta = new Vector2(30f, 30f);
            var close = _ui.AddButton(closeHolder, "X", Toggle, ButtonTone.Normal, 30f,
                TextAlignmentOptions.Center, 14f);
            MenuApi.Stretch((RectTransform)close.transform);

            RectTransform divider = MenuApi.CreateRect("Divider", inner);
            divider.anchorMin = new Vector2(0f, 1f);
            divider.anchorMax = new Vector2(1f, 1f);
            divider.offsetMin = new Vector2(18f, -58f);
            divider.offsetMax = new Vector2(-18f, -57f);
            var dividerImage = divider.gameObject.AddComponent<UnityEngine.UI.Image>();
            dividerImage.color = _theme.Border;
            dividerImage.raycastTarget = false;

            Content = MenuApi.CreateRect("Content", inner);
            Content.anchorMin = Vector2.zero;
            Content.anchorMax = Vector2.one;
            Content.offsetMin = new Vector2(18f, 16f);
            Content.offsetMax = new Vector2(-18f, -68f);
        }

    }

    internal sealed class ModMenuHotkey : MonoBehaviour
    {
        internal ModMenu Menu { get; set; }
        internal Key Key { get; set; }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[Key].wasPressedThisFrame) Menu?.Close();
        }
    }

    public sealed class ModScrollList
    {
        private readonly MenuApi _ui;
        internal ModScrollList(MenuApi ui, RectTransform content, ScrollRect scroll)
        {
            _ui = ui;
            Content = content;
            Scroll = scroll;
        }

        public RectTransform Content { get; }
        public ScrollRect Scroll { get; }

        /// <summary>Sets mouse-wheel and trackpad scroll speed. The SDK default is 8.</summary>
        public ModScrollList WheelSpeed(float sensitivity)
        {
            Scroll.scrollSensitivity = Mathf.Max(.1f, sensitivity);
            return this;
        }

        /// <summary>Scroll position in pixels from the top. Save before a rebuild, restore after.</summary>
        public float Offset
        {
            get { return Content.anchoredPosition.y; }
            set
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(Content);
                RectTransform viewport = Content.parent as RectTransform;
                float max = viewport == null ? 0f :
                    Mathf.Max(0f, Content.rect.height - viewport.rect.height);
                Content.anchoredPosition = new Vector2(0f, Mathf.Clamp(value, 0f, max));
            }
        }

        public void Clear(bool resetScroll = true)
        {
            for (int index = Content.childCount - 1; index >= 0; index--)
            {
                GameObject child = Content.GetChild(index).gameObject;
                child.SetActive(false); // inactive children are ignored by layout immediately
                UnityEngine.Object.Destroy(child);
            }
            if (resetScroll) Scroll.verticalNormalizedPosition = 1f;
        }

        public UnityEngine.UI.Button AddButton(string text, UnityAction clicked,
            ButtonTone tone = ButtonTone.Normal, float height = 44f) =>
            _ui.AddButton(Content, text, clicked, tone, height, TextAlignmentOptions.MidlineLeft);

        public UnityEngine.UI.Button AddItem(string title, string metadata, Sprite icon,
            bool selected, UnityAction clicked, float height = 48f) =>
            _ui.AddListItem(Content, title, metadata, icon, selected, clicked, height);
    }
}
