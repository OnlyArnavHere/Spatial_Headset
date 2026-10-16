using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The keyboard messages of the laptop agent's control protocol (Tools/LaptopAgent/input_server.py, version 3):
    /// <c>{"type":"key","code":"A","down":true}</c> and <c>{"type":"text","text":"..."}</c>. Key names match
    /// Tools/LaptopAgent/keys.py; the agent drops any other name, and there is deliberately no Windows key.
    /// </summary>
    public static class RemoteKeyProtocol
    {
        /// <summary>The agent refuses longer text in one message; <see cref="SplitText"/> cuts text to fit.</summary>
        public const int MaxTextLength = 64;

        static readonly string[] Specials =
        {
            "Enter", "Backspace", "Tab", "Escape", "Space", "Shift", "Ctrl", "Alt",
            "Left", "Up", "Right", "Down", "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
            "Semicolon", "Equals", "Comma", "Minus", "Period", "Slash", "Backquote", "LeftBracket", "Backslash",
            "RightBracket", "Quote",
        };

        static HashSet<string> known;

        /// <summary>Every key name the agent accepts.</summary>
        public static IReadOnlyCollection<string> KnownCodes
        {
            get
            {
                if (known != null) return known;
                known = new HashSet<string>(Specials);
                for (char c = 'A'; c <= 'Z'; c++) known.Add(c.ToString());
                for (int d = 0; d <= 9; d++) known.Add(d.ToString(CultureInfo.InvariantCulture));
                for (int f = 1; f <= 12; f++) known.Add($"F{f}");
                return known;
            }
        }

        public static bool IsKnown(string code) => code != null && ((HashSet<string>)KnownCodes).Contains(code);

        public static string EncodeKey(string code, bool down) =>
            $"{{\"type\":\"key\",\"code\":{Quote(code)},\"down\":{(down ? "true" : "false")}}}";

        public static string EncodeText(string text) => $"{{\"type\":\"text\",\"text\":{Quote(text)}}}";

        /// <summary>A keyboard message as sent: kind "key" (with <see cref="Message.code"/> and <see cref="Message.down"/>) or "text".</summary>
        [Serializable]
        public sealed class Message
        {
            public string type;
            public string code;
            public bool down;
            public string text;
        }

        /// <summary>Reads a keyboard message back (tests, logging); false for anything else.</summary>
        public static bool TryDecode(string json, out Message message)
        {
            message = null;
            try
            {
                message = JsonUtility.FromJson<Message>(json);
            }
            catch (ArgumentException)
            {
                return false;
            }
            return message != null && (message.type == "key" && IsKnown(message.code) || message.type == "text" && message.text != null);
        }

        /// <summary>Cuts text into pieces the agent accepts, never between the two halves of a surrogate pair.</summary>
        public static IEnumerable<string> SplitText(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            int start = 0;
            while (start < text.Length)
            {
                int length = Mathf.Min(MaxTextLength, text.Length - start);
                if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
                yield return text.Substring(start, length);
                start += length;
            }
        }

        /// <summary>A JSON string literal: quotes, backslashes and control characters escaped.</summary>
        public static string Quote(string value)
        {
            var json = new StringBuilder((value?.Length ?? 0) + 2);
            json.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (c < 0x20) json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else json.Append(c);
                        break;
                }
            }
            json.Append('"');
            return json.ToString();
        }
    }
}
