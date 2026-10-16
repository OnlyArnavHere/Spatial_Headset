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
    /// again cancels it. Character labels follow Shift.
    /// </summary>
    public class VirtualKeyboardContent : WindowContent
    {
        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
        static readonly Color CharacterColor = new Color(0.24f, 0.25f, 0.30f);
        static readonly Color SpecialColor = new Color(0.32f, 0.34f, 0.40f);
        static readonly Color ModifierColor = new Color(0.32f, 0.34f, 0.40f);
        static readonly Color ModifierOnColor = new Color(0.16f, 0.36f, 0.95f);
        static readonly Color StatusColor = new Color(0.75f, 0.77f, 0.82f);

        const float StatusHeight = 0.03f;
        const float KeyGap = 0.004f;

        /// <summary>Modifiers that apply to the next key.</summary>
        public KeyModifiers Modifiers { get; private set; }
        public string StatusText => status != null ? status.text : "";

        Func<IReadOnlyList<KeyStroke>, bool> send;
        Func<RemoteStreamContent> target;
        TextMeshPro status;
        Transform panel;
        readonly List<(VirtualKey key, WindowButton button, TextMeshPro label, Material material)> keys =
            new List<(VirtualKey, WindowButton, TextMeshPro, Material)>();

        /// <param name="sendStrokes">Sends what a key press means; false if there is no window to send to.</param>
        /// <param name="currentTarget">The stream window keys go to now (for the status line).</param>
        public void Init(SpatialWindow window, Material baseMaterial, Func<IReadOnlyList<KeyStroke>, bool> sendStrokes,
            Func<RemoteStreamContent> currentTarget)
        {
            Attach(window, baseMaterial);
            send = sendStrokes;
            target = currentTarget;

            panel = CreateQuad("KeyboardPanel", transform, CreateMaterial(PanelColor));
            status = CreateLabel("Status", transform, "", 0.17f, TextAlignmentOptions.MidlineLeft);
            status.color = StatusColor;

            var characterMaterial = CreateMaterial(CharacterColor);
            var specialMaterial = CreateMaterial(SpecialColor);
            foreach (var key in VirtualKeyboardLayout.Keys)
            {
                var material = key.Kind == VirtualKeyKind.Modifier ? CreateMaterial(ModifierColor)
                    : key.Kind == VirtualKeyKind.Character ? characterMaterial : specialMaterial;
                var button = CreateControl<WindowButton>($"Key {key.Id}", transform, material);
                var label = CreateLabel($"KeyLabel {key.Id}", transform, key.Id == "Space" ? "Space" : key.Label, 0.18f,
                    TextAlignmentOptions.Center);
                var pressed = key;
                button.Clicked += () => Press(pressed);
                keys.Add((key, button, label, material));
            }
            RefreshKeys();
            RefreshStatus();
            Relayout();
        }

        /// <summary>The button for a key id from <see cref="VirtualKeyboardLayout"/> (e.g. "A", "Enter", "Shift").</summary>
        public WindowButton Button(string id)
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
            var stream = target?.Invoke();
            string text;
            if (stream == null) text = "Open a laptop window to type into it";
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
