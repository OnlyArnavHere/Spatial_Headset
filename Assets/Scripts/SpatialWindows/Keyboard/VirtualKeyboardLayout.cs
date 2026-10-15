using System;
using System.Collections.Generic;

namespace SpatialLauncher.SpatialWindows
{
    [Flags]
    public enum KeyModifiers { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }

    public enum VirtualKeyKind
    {
        /// <summary>Types a character (<see cref="VirtualKey.Label"/>, or <see cref="VirtualKey.ShiftLabel"/> with Shift).</summary>
        Character,
        /// <summary>A key with no character: Enter, Backspace, arrows...</summary>
        Special,
        /// <summary>Shift, Ctrl or Alt: toggles a modifier for the next key.</summary>
        Modifier,
        /// <summary>An empty gap in the row.</summary>
        Spacer,
    }

    /// <summary>One key of the virtual keyboard.</summary>
    public sealed class VirtualKey
    {
        /// <summary>Unique within the layout (the key's name in the scene, e.g. "Key A").</summary>
        public string Id;
        public string Label;
        public string ShiftLabel;
        /// <summary>The protocol key name (<see cref="RemoteKeyProtocol"/>); null for spacers.</summary>
        public string Code;
        public VirtualKeyKind Kind;
        /// <summary>Width in key units; a row is <see cref="VirtualKeyboardLayout.RowUnits"/> units wide.</summary>
        public float Width = 1f;
        /// <summary>The modifier this key toggles (Modifier keys only).</summary>
        public KeyModifiers Modifier;
    }

    /// <summary>What a key press sends: text, or a key going down or up.</summary>
    public readonly struct KeyStroke : IEquatable<KeyStroke>
    {
        public enum Type { Text, Down, Up }

        public readonly Type Kind;
        /// <summary>The text for <see cref="Type.Text"/>, else the key name.</summary>
        public readonly string Value;

        public KeyStroke(Type kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        public static KeyStroke Text(string text) => new KeyStroke(Type.Text, text);
        public static KeyStroke Down(string code) => new KeyStroke(Type.Down, code);
        public static KeyStroke Up(string code) => new KeyStroke(Type.Up, code);

        public void SendTo(IRemoteKeyboardSink sink)
        {
            if (Kind == Type.Text) sink.Text(Value);
            else sink.Key(Value, Kind == Type.Down);
        }

        public bool Equals(KeyStroke other) => Kind == other.Kind && Value == other.Value;
        public override bool Equals(object obj) => obj is KeyStroke other && Equals(other);
        public override int GetHashCode() => ((int)Kind * 397) ^ (Value?.GetHashCode() ?? 0);
        public override string ToString() => Kind == Type.Text ? $"text \"{Value}\"" : $"{Value} {Kind.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// The virtual keyboard's QWERTY (US) layout, and what each key sends. Modifiers are sticky one-shot toggles, since a
    /// pointer or a pinch presses one key at a time: tap Shift, then a letter, for a capital. Without Ctrl or Alt a
    /// character key types its character as text (so the laptop's own layout doesn't matter); with Ctrl or Alt it is
    /// sent as key presses wrapped in the modifiers (Ctrl+C), and so are special keys (Shift+Left selects).
    /// </summary>
    public static class VirtualKeyboardLayout
    {
        public const float RowUnits = 15f;

        static List<IReadOnlyList<VirtualKey>> rows;

        /// <summary>The rows from top to bottom, each <see cref="RowUnits"/> wide.</summary>
        public static IReadOnlyList<IReadOnlyList<VirtualKey>> Rows => rows ??= Build();

        /// <summary>Every key that isn't a spacer, row by row.</summary>
        public static IEnumerable<VirtualKey> Keys
        {
            get
            {
                foreach (var row in Rows)
                foreach (var key in row)
                    if (key.Kind != VirtualKeyKind.Spacer) yield return key;
            }
        }

        public static VirtualKey Find(string id)
        {
            foreach (var key in Keys)
                if (key.Id == id) return key;
            return null;
        }

        /// <summary>What pressing <paramref name="key"/> sends with the given modifiers active. Modifier keys send nothing.</summary>
        public static List<KeyStroke> Resolve(VirtualKey key, KeyModifiers modifiers)
        {
            var strokes = new List<KeyStroke>();
            if (key == null || key.Kind == VirtualKeyKind.Modifier || key.Kind == VirtualKeyKind.Spacer) return strokes;

            bool shift = (modifiers & KeyModifiers.Shift) != 0;
            bool chord = (modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0;
            if (key.Kind == VirtualKeyKind.Character && !chord)
            {
                strokes.Add(KeyStroke.Text(shift ? key.ShiftLabel : key.Label));
                return strokes;
            }

            var held = new List<string>();
            if ((modifiers & KeyModifiers.Ctrl) != 0) held.Add("Ctrl");
            if ((modifiers & KeyModifiers.Alt) != 0) held.Add("Alt");
            if (shift) held.Add("Shift");
            foreach (var code in held) strokes.Add(KeyStroke.Down(code));
            strokes.Add(KeyStroke.Down(key.Code));
            strokes.Add(KeyStroke.Up(key.Code));
            for (int i = held.Count - 1; i >= 0; i--) strokes.Add(KeyStroke.Up(held[i]));
            return strokes;
        }

        static List<IReadOnlyList<VirtualKey>> Build()
        {
            return new List<IReadOnlyList<VirtualKey>>
            {
                Row(Char("`~", "Backquote"), Char("1!", "1"), Char("2@", "2"), Char("3#", "3"), Char("4$", "4"),
                    Char("5%", "5"), Char("6^", "6"), Char("7&", "7"), Char("8*", "8"), Char("9(", "9"), Char("0)", "0"),
                    Char("-_", "Minus"), Char("=+", "Equals"), Special("Backspace", "Bksp", 2f)),
                Row(Special("Tab", "Tab", 1.5f), Letters("qwertyuiop"), Char("[{", "LeftBracket"), Char("]}", "RightBracket"),
                    Char("\\|", "Backslash", 1.5f)),
                Row(Special("Escape", "Esc", 1.75f), Letters("asdfghjkl"), Char(";:", "Semicolon"), Char("'\"", "Quote"),
                    Special("Enter", "Enter", 2.25f)),
                Row(Modifier("Shift", KeyModifiers.Shift, 2.25f), Letters("zxcvbnm"), Char(",<", "Comma"), Char(".>", "Period"),
                    Char("/?", "Slash"), Special("Up", "^", 1f), Special("Delete", "Del", 1.75f)),
                Row(Modifier("Ctrl", KeyModifiers.Ctrl, 1.5f), Modifier("Alt", KeyModifiers.Alt, 1.5f),
                    new VirtualKey { Id = "Space", Label = " ", ShiftLabel = " ", Code = "Space", Kind = VirtualKeyKind.Character, Width = 6.25f },
                    Special("Home", "Home", 1f), Special("End", "End", 1f),
                    Special("Left", "<", 1f), Special("Down", "v", 1f), Special("Right", ">", 1f),
                    new VirtualKey { Id = "Spacer", Kind = VirtualKeyKind.Spacer, Width = 0.75f }),
            };
        }

        // Each part is a key or a sequence of keys.
        static IReadOnlyList<VirtualKey> Row(params object[] parts)
        {
            var row = new List<VirtualKey>();
            foreach (var part in parts)
            {
                if (part is VirtualKey key) row.Add(key);
                else row.AddRange((IEnumerable<VirtualKey>)part);
            }
            return row;
        }

        static IEnumerable<VirtualKey> Letters(string letters)
        {
            foreach (char letter in letters)
            {
                string lower = letter.ToString(), upper = lower.ToUpperInvariant();
                yield return new VirtualKey { Id = upper, Label = lower, ShiftLabel = upper, Code = upper, Kind = VirtualKeyKind.Character };
            }
        }

        // glyphs: the character without and with Shift, e.g. "1!".
        static VirtualKey Char(string glyphs, string code, float width = 1f) => new VirtualKey
        {
            Id = code, Label = glyphs[0].ToString(), ShiftLabel = glyphs[1].ToString(), Code = code,
            Kind = VirtualKeyKind.Character, Width = width,
        };

        static VirtualKey Special(string code, string label, float width) =>
            new VirtualKey { Id = code, Label = label, ShiftLabel = label, Code = code, Kind = VirtualKeyKind.Special, Width = width };

        static VirtualKey Modifier(string code, KeyModifiers modifier, float width) =>
            new VirtualKey { Id = code, Label = code, ShiftLabel = code, Code = code, Kind = VirtualKeyKind.Modifier, Width = width, Modifier = modifier };
    }
}
