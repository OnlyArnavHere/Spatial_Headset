using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A floating QWERTY keyboard (<see cref="VirtualKeyboardLayout"/>) for laptop stream windows: a status line naming
    /// the window it types into, then five rows of keys. Each key is a <see cref="WindowButton"/>, so the mouse and a
    /// pinch both press it. Shift, Ctrl and Alt are one-shot toggles that light up and apply to the next key; tapping one
    /// again cancels it. Character labels follow Shift. Talk is push to talk (<see cref="VoiceTyping"/>): hold it, speak,
    /// let go; the status line shows what is being heard.
    /// </summary>
    public class VirtualKeyboardContent : WindowContent
    {
        /// <summary>No panel behind the keys: the window's glass shows through.</summary>
        public static readonly Color PanelColor = new Color(0f, 0f, 0f, 0f);
        public static readonly Color KeyColor = UITheme.KeyFill;
        public static readonly Color ModifierOnColor = UITheme.AccentFill;
        public static readonly Color ReturnColor = UITheme.AccentFill;
        public static readonly Color TalkColor = UITheme.KeyFill;
        public static readonly Color ListeningColor = UITheme.DestructiveFill;

        /// <summary>Key label size (pt): 17 pt counts as large text, so white on KeyFill needs 4.5:1.</summary>
        public const float LabelPt = 17f;

        /// <summary>The icon drawn on a key instead of its text (arrows, Home), or null.</summary>
        public static string KeyIcon(string id)
        {
            switch (id)
            {
                case "Up": return UIIcons.KeyboardArrowUp;
                case "Down": return UIIcons.KeyboardArrowDown;
                case "Left": return UIIcons.KeyboardArrowLeft;
                case "Right": return UIIcons.KeyboardArrowRight;
                case "Home": return UIIcons.Home;
                default: return null;
            }
        }

        const float StatusHeight = 0.03f;
        /// <summary>Gap between keys (6 pt: the keyboard's exception to the 8 mm rule, ui-design section 9).</summary>
        public const float KeyGap = 0.006f;
        /// <summary>Window content size that gives 44 pt keys: 15 key units across and a status line plus five rows.</summary>
        public static readonly Vector2 PreferredSize = new Vector2(0.79f, 0.32f);
        /// <summary>The window size for <see cref="PreferredSize"/> content.</summary>
        public static Vector2 WindowSize => PreferredSize + Vector2.one * (Padding * 2f);

        /// <summary>Modifiers that apply to the next key.</summary>
        public KeyModifiers Modifiers { get; private set; }
        public string StatusText => status != null ? status.text : "";

        Func<IReadOnlyList<KeyStroke>, bool> send;
        Func<RemoteStreamContent> target;
        VoiceTyping voice;
        Material talkMaterial;
        TextMeshPro status;
        Transform panel;
        readonly List<(VirtualKey key, WindowControl button, TextMeshPro label, Material material)> keys =
            new List<(VirtualKey, WindowControl, TextMeshPro, Material)>();

        /// <param name="sendStrokes">Sends what a key press means; false if there is no window to send to.</param>
        /// <param name="currentTarget">The stream window keys go to now (for the status line).</param>
        /// <param name="voiceTyping">Voice typing for the Talk key; null leaves the key inactive.</param>
        public void Init(SpatialWindow window, Material baseMaterial, Func<IReadOnlyList<KeyStroke>, bool> sendStrokes,
            Func<RemoteStreamContent> currentTarget, VoiceTyping voiceTyping = null)
        {
            Attach(window, baseMaterial);
            send = sendStrokes;
            target = currentTarget;
            voice = voiceTyping;

            panel = CreateQuad("KeyboardPanel", transform, CreateRounded(PanelColor, UITheme.ControlRadius));
            status = CreateLabel("Status", transform, "", TextRole.Secondary, TextAlignmentOptions.MidlineLeft);

            var characterMaterial = CreateRounded(KeyColor, UITheme.ControlRadius);
            var specialMaterial = CreateRounded(KeyColor, UITheme.ControlRadius);
            foreach (var key in VirtualKeyboardLayout.Keys)
            {
                var material = key.Kind == VirtualKeyKind.Modifier ? CreateRounded(KeyColor, UITheme.ControlRadius)
                    : key.Kind == VirtualKeyKind.Voice ? talkMaterial = CreateRounded(TalkColor, UITheme.ControlRadius)
                    : key.Id == "Enter" ? CreateRounded(ReturnColor, UITheme.ControlRadius)
                    : key.Kind == VirtualKeyKind.Character ? characterMaterial : specialMaterial;
                var icon = KeyIcon(key.Id);
                var label = CreateLabel($"KeyLabel {key.Id}", transform, icon ?? (key.Id == "Space" ? "Space" : key.Label),
                    icon != null ? TextRole.Icon : TextRole.Headline, TextAlignmentOptions.Center);
                // Labels are large text (Headline, 17 pt); arrows and Home are icons (20 pt) so they fit a one-unit key.
                WindowControl control;
                if (key.Kind == VirtualKeyKind.Voice)
                {
                    var talk = CreateControl<WindowHoldButton>($"Key {key.Id}", transform, material);
                    talk.Pressed += () => voice?.StartListening();
                    talk.Released += () => voice?.StopListening();
                    control = talk;
                }
                else
                {
                    var button = CreateControl<WindowButton>($"Key {key.Id}", transform, material);
                    var pressed = key;
                    button.Clicked += () => Press(pressed);
                    control = button;
                }
                control.BindLabel(label);
                keys.Add((key, control, label, material));
            }
            RefreshKeys();
            RefreshStatus();
            Relayout();
        }

        /// <summary>The control for a key id from <see cref="VirtualKeyboardLayout"/> (e.g. "A", "Enter", "Shift", "Talk").</summary>
        public WindowControl Button(string id)
        {
            foreach (var entry in keys)
                if (entry.key.Id == id) return entry.button;
            return null;
        }

        /// <summary>What clicking a key does: toggles a modifier, or sends the key with the active modifiers and clears them.</summary>
        public void Press(VirtualKey key)
        {
            if (key.Kind == VirtualKeyKind.Modifier)
            {
                Modifiers ^= key.Modifier;
            }
            else
            {
                send?.Invoke(VirtualKeyboardLayout.Resolve(key, Modifiers));
                Modifiers = KeyModifiers.None;
            }
            RefreshKeys();
            RefreshStatus();
        }

        void Update() => RefreshStatus();

        void RefreshStatus()
        {
            if (status == null) return;
            bool listening = voice != null && voice.IsListening;
            if (talkMaterial != null) talkMaterial.color = listening ? ListeningColor : TalkColor;
            var stream = target?.Invoke();
            string text;
            if (listening) text = $"{voice.Status}: {voice.Partial}";
            else if (stream == null) text = "Open a laptop window to type into it";
#if SPATIAL_LAUNCHER_WEBRTC
            else if (stream.IsLinked && !stream.IsActive) text = $"Typing into: {stream.Window.Title} (not connected)";
            else if (stream.LinkInput != null && !stream.LinkInput.KeyboardAllowed)
                text = $"Typing into: {stream.Window.Title} (view only: request control to type)";
#endif
            else text = $"Typing into: {stream.Window.Title}";
            if (status.text != text) status.text = text;
        }

        void RefreshKeys()
        {
            bool shift = (Modifiers & KeyModifiers.Shift) != 0;
            foreach (var (key, _, label, material) in keys)
            {
                if (key.Kind == VirtualKeyKind.Modifier) material.color = (Modifiers & key.Modifier) != 0 ? ModifierOnColor : KeyColor;
                else if (key.Kind == VirtualKeyKind.Character && key.Id != "Space") label.text = shift ? key.ShiftLabel : key.Label;
            }
        }

        protected override void Layout(Vector2 size)
        {
            panel.localPosition = new Vector3(0f, 0f, -0.0005f);
            panel.localScale = new Vector3(size.x, size.y, 1f);

            status.transform.localPosition = new Vector3(0f, size.y * 0.5f - StatusHeight * 0.5f, -0.003f);
            status.rectTransform.sizeDelta = new Vector2(size.x - KeyGap * 2f, StatusHeight);

            var rows = VirtualKeyboardLayout.Rows;
            float unit = size.x / VirtualKeyboardLayout.RowUnits;
            float rowHeight = (size.y - StatusHeight) / rows.Count;
            float top = size.y * 0.5f - StatusHeight;
            int index = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                float y = top - rowHeight * (r + 0.5f);
                float left = -size.x * 0.5f;
                foreach (var key in rows[r])
                {
                    float width = key.Width * unit;
                    if (key.Kind != VirtualKeyKind.Spacer)
                    {
                        var (_, button, label, _) = keys[index++];
                        var position = new Vector3(left + width * 0.5f, y, -0.002f);
                        var keySize = new Vector2(Mathf.Max(0.005f, width - KeyGap), Mathf.Max(0.005f, rowHeight - KeyGap));
                        button.transform.localPosition = position;
                        button.transform.localScale = new Vector3(keySize.x, keySize.y, 1f);
                        label.transform.localPosition = position + new Vector3(0f, 0f, -0.001f);
                        label.rectTransform.sizeDelta = keySize;
                    }
                    left += width;
                }
            }
        }
    }
}
