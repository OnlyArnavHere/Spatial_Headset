using System;
using System.Collections.Generic;
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
        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
        static readonly Color CharacterColor = new Color(0.24f, 0.25f, 0.30f);
        static readonly Color SpecialColor = new Color(0.32f, 0.34f, 0.40f);
        static readonly Color ModifierColor = new Color(0.32f, 0.34f, 0.40f);
        static readonly Color ModifierOnColor = new Color(0.16f, 0.36f, 0.95f);
        static readonly Color StatusColor = new Color(0.75f, 0.77f, 0.82f);
        static readonly Color TalkColor = new Color(0.20f, 0.55f, 0.35f);
        static readonly Color ListeningColor = new Color(0.85f, 0.25f, 0.25f);

        const float StatusHeight = 0.03f;
        const float KeyGap = 0.004f;

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

            panel = CreateQuad("KeyboardPanel", transform, CreateMaterial(PanelColor));
            status = CreateLabel("Status", transform, "", 0.17f, TextAlignmentOptions.MidlineLeft);
            status.color = StatusColor;

            var characterMaterial = CreateMaterial(CharacterColor);
            var specialMaterial = CreateMaterial(SpecialColor);
            foreach (var key in VirtualKeyboardLayout.Keys)
            {
                var material = key.Kind == VirtualKeyKind.Modifier ? CreateMaterial(ModifierColor)
                    : key.Kind == VirtualKeyKind.Voice ? talkMaterial = CreateMaterial(TalkColor)
                    : key.Kind == VirtualKeyKind.Character ? characterMaterial : specialMaterial;
                var label = CreateLabel($"KeyLabel {key.Id}", transform, key.Id == "Space" ? "Space" : key.Label, 0.18f,
                    TextAlignmentOptions.Center);
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
            else if (stream.InputLink != null && !stream.InputLink.IsConnected) text = $"Typing into: {stream.Window.Title} (not connected)";
            else if (stream.InputLink != null && !stream.InputLink.KeyboardAllowed)
                text = $"Typing into: {stream.Window.Title} (keyboard off: start agent.py with --input --keyboard)";
            else text = $"Typing into: {stream.Window.Title}";
            if (status.text != text) status.text = text;
        }

        void RefreshKeys()
        {
            bool shift = (Modifiers & KeyModifiers.Shift) != 0;
            foreach (var (key, _, label, material) in keys)
            {
                if (key.Kind == VirtualKeyKind.Modifier) material.color = (Modifiers & key.Modifier) != 0 ? ModifierOnColor : ModifierColor;
                else if (key.Kind == VirtualKeyKind.Character && key.Id != "Space") label.text = shift ? key.ShiftLabel : key.Label;
            }
        }

        protected override void Layout(Vector2 size)
        {
            panel.localPosition = new Vector3(0f, 0f, -0.001f);
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
                        label.fontSize = Mathf.Min(0.18f, keySize.y * 4f);
                    }
                    left += width;
                }
            }
        }
    }
}
