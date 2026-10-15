using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Maps the editor's physical keys (Input System <see cref="Key"/>) to <see cref="RemoteKeyProtocol"/> key names.
    /// Keys that type a character are forwarded as text (from the OS text input, so the local layout and Shift apply)
    /// unless Ctrl or Alt is held; the others are forwarded as key presses. Windows/Command keys are never forwarded.
    /// </summary>
    public static class PhysicalKeyMap
    {
        static Dictionary<Key, string> codes;
        static HashSet<Key> typing;

        public static IReadOnlyDictionary<Key, string> Codes
        {
            get
            {
                if (codes == null) Build();
                return codes;
            }
        }

        /// <summary>The protocol name for a physical key; false for keys that aren't forwarded.</summary>
        public static bool TryGetCode(Key key, out string code)
        {
            if (codes == null) Build();
            return codes.TryGetValue(key, out code);
        }

        /// <summary>True for keys that type a character (letters, digits, punctuation, Space).</summary>
        public static bool TypesCharacter(Key key)
        {
            if (typing == null) Build();
            return typing.Contains(key);
        }

        static void Build()
        {
            codes = new Dictionary<Key, string>();
            typing = new HashSet<Key>();
            for (var key = Key.A; key <= Key.Z; key++) AddTyping(key, key.ToString());
            // The Key enum runs Digit1..Digit9, then Digit0.
            for (int d = 1; d <= 9; d++) AddTyping(Key.Digit1 + (d - 1), d.ToString());
            AddTyping(Key.Digit0, "0");
            AddTyping(Key.Space, "Space");
            AddTyping(Key.Semicolon, "Semicolon");
            AddTyping(Key.Equals, "Equals");
            AddTyping(Key.Comma, "Comma");
            AddTyping(Key.Minus, "Minus");
            AddTyping(Key.Period, "Period");
            AddTyping(Key.Slash, "Slash");
            AddTyping(Key.Backquote, "Backquote");
            AddTyping(Key.LeftBracket, "LeftBracket");
            AddTyping(Key.Backslash, "Backslash");
            AddTyping(Key.RightBracket, "RightBracket");
            AddTyping(Key.Quote, "Quote");

            for (int f = 0; f < 12; f++) codes[Key.F1 + f] = $"F{f + 1}";
            codes[Key.Enter] = "Enter";
            codes[Key.NumpadEnter] = "Enter";
            codes[Key.Backspace] = "Backspace";
            codes[Key.Tab] = "Tab";
            codes[Key.Escape] = "Escape";
            codes[Key.LeftShift] = codes[Key.RightShift] = "Shift";
            codes[Key.LeftCtrl] = codes[Key.RightCtrl] = "Ctrl";
            codes[Key.LeftAlt] = codes[Key.RightAlt] = "Alt";
            codes[Key.LeftArrow] = "Left";
            codes[Key.RightArrow] = "Right";
            codes[Key.UpArrow] = "Up";
            codes[Key.DownArrow] = "Down";
            codes[Key.Insert] = "Insert";
            codes[Key.Delete] = "Delete";
            codes[Key.Home] = "Home";
            codes[Key.End] = "End";
            codes[Key.PageUp] = "PageUp";
            codes[Key.PageDown] = "PageDown";
        }

        static void AddTyping(Key key, string code)
        {
            codes[key] = code;
            typing.Add(key);
        }
    }
}
