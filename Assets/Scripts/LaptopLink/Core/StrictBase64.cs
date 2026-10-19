using System;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// RFC 4648 standard base64 as the link uses it: alphabet A-Z a-z 0-9 + /, '=' padding, no whitespace or other
    /// characters, and the canonical re-encoding of the decoded bytes must equal the text (no non-zero trailing bits).
    /// </summary>
    public static class StrictBase64
    {
        public static string Encode(byte[] data)
        {
            return Convert.ToBase64String(data);
        }

        public static bool TryDecode(string text, out byte[] data)
        {
            data = null;
            if (text == null || text.Length % 4 != 0)
                return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/'
                          || (c == '=' && i >= text.Length - 2);
                if (!ok) return false;
                // '=' may only be followed by another '='.
                if (c == '=' && i == text.Length - 2 && text[i + 1] != '=') return false;
            }
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return false;
            }
            if (!string.Equals(Convert.ToBase64String(decoded), text, StringComparison.Ordinal))
                return false;
            data = decoded;
            return true;
        }
    }
}
