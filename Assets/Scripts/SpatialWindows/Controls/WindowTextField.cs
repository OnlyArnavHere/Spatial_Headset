using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A one-line text field on a quad. Click (or pinch) it to focus, then type with the keyboard: Backspace deletes,
    /// Ctrl+V pastes, Tab moves to the next field of the same group, Enter submits, Esc leaves the field.
    /// On iOS focusing opens the on-screen keyboard (usable in Mono mode, where the screen can be touched).
    /// While any field has focus, <see cref="IsTyping"/> is true so single-key shortcuts elsewhere can stand down.
    /// </summary>
    public class WindowTextField : WindowControl
    {
        /// <summary>The raised glass overlay over the dark fallback, like the round controls.</summary>
        public static readonly Color NormalColor = UITheme.CompositeOver(UITheme.GlassRaised, UITheme.GlassFallback);
        /// <summary>The accent over the normal fill, so a focused field stands out from its neighbours.</summary>
        public static readonly Color FocusedColor = UITheme.CompositeOver(new Color(UITheme.Accent.r, UITheme.Accent.g, UITheme.Accent.b, 0.45f), NormalColor);

        /// <summary>The field that has keyboard focus, if any.</summary>
        public static WindowTextField Focused { get; private set; }
        /// <summary>True while the user is typing into a field: keyboard shortcuts should be ignored.</summary>
        public static bool IsTyping => Focused != null;

        public string Text { get; private set; } = "";
        /// <summary>Shows asterisks instead of the text (for tokens).</summary>
        public bool Masked { get; set; }
        public int MaxLength { get; set; } = 128;
        /// <summary>Accepts only digits (ports).</summary>
        public bool DigitsOnly { get; set; }
        /// <summary>Fields sharing a group cycle with Tab, in the order they were created.</summary>
        public List<WindowTextField> Group { get; set; }

        public event Action<WindowTextField> Submitted;
        public event Action<WindowTextField> TextChanged;

        TextMeshPro label;
        Material background;
        string placeholder = "";
        bool caretVisible;
        float caretTimer;
#if UNITY_IOS && !UNITY_EDITOR
        TouchScreenKeyboard touchKeyboard;
#endif

        public void Init(TextMeshPro textLabel, Material backgroundMaterial, string placeholderText)
        {
            label = textLabel;
            background = backgroundMaterial;
            placeholder = placeholderText;
            Refresh();
        }

        public void SetText(string text)
        {
            text ??= "";
            if (DigitsOnly) text = Filter(text);
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);
            if (text == Text) return;
            Text = text;
            Refresh();
            TextChanged?.Invoke(this);
        }

        public void Focus()
        {
            if (Focused == this) return;
            if (Focused != null) Focused.Blur();
            Focused = this;
            caretVisible = true;
            caretTimer = 0f;
            if (Keyboard.current != null) Keyboard.current.onTextInput += OnTextInput;
#if UNITY_IOS && !UNITY_EDITOR
            touchKeyboard = TouchScreenKeyboard.Open(Text, DigitsOnly ? TouchScreenKeyboardType.NumberPad : TouchScreenKeyboardType.URL,
                false, false, Masked);
#endif
            Refresh();
        }

        public void Blur()
        {
            if (Focused != this) return;
            Focused = null;
            if (Keyboard.current != null) Keyboard.current.onTextInput -= OnTextInput;
#if UNITY_IOS && !UNITY_EDITOR
            if (touchKeyboard != null) touchKeyboard.active = false;
            touchKeyboard = null;
#endif
            Refresh();
        }

        public static void BlurAll() => Focused?.Blur();

        public override void Release(bool inside)
        {
            if (inside) Focus();
        }

        void OnTextInput(char character)
        {
            if (char.IsControl(character)) return; // Backspace, Tab, Enter and Esc are handled as keys
            SetText(Text + character);
        }

        void Update()
        {
            if (Focused != this) return;

            caretTimer += Time.unscaledDeltaTime;
            if (caretTimer >= 0.5f)
            {
                caretTimer = 0f;
                caretVisible = !caretVisible;
                Refresh();
            }

#if UNITY_IOS && !UNITY_EDITOR
            if (touchKeyboard != null)
            {
                SetText(touchKeyboard.text);
                if (touchKeyboard.status == TouchScreenKeyboard.Status.Done) { Blur(); Submitted?.Invoke(this); }
                else if (touchKeyboard.status != TouchScreenKeyboard.Status.Visible) Blur();
                return;
            }
#endif
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.backspaceKey.wasPressedThisFrame && Text.Length > 0) SetText(Text.Substring(0, Text.Length - 1));
            bool control = keyboard.ctrlKey.isPressed || keyboard.leftCommandKey.isPressed || keyboard.rightCommandKey.isPressed;
            if (control && keyboard.vKey.wasPressedThisFrame) SetText(Text + Clean(GUIUtility.systemCopyBuffer));
            if (keyboard.escapeKey.wasPressedThisFrame) Blur();
            else if (keyboard.tabKey.wasPressedThisFrame) FocusNext(keyboard.shiftKey.isPressed ? -1 : 1);
            else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                Blur();
                Submitted?.Invoke(this);
            }
        }

        void FocusNext(int step)
        {
            if (Group == null || Group.Count < 2) return;
            int index = Group.IndexOf(this);
            Group[(index + step + Group.Count) % Group.Count].Focus();
        }

        void Refresh()
        {
            if (label == null) return;
            bool focused = Focused == this;
            string shown = Masked ? new string('*', Text.Length) : Text;
            if (Text.Length == 0 && !focused)
            {
                label.text = placeholder;
                label.color = UITheme.TextSecondary;
            }
            else
            {
                label.text = shown + (focused && caretVisible ? "|" : "");
                label.color = Color.white;
            }
            if (background != null) background.color = focused ? FocusedColor : NormalColor;
        }

        string Filter(string text)
        {
            var digits = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
                if (char.IsDigit(c)) digits.Append(c);
            return digits.ToString();
        }

        // Pasted tokens often carry a trailing newline or spaces from the console.
        static string Clean(string pasted) => (pasted ?? "").Trim().Replace("\r", "").Replace("\n", "");

        void OnDisable() => Blur();
    }
}
