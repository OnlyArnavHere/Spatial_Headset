using System.Globalization;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// Device names on the link: a JSON string of at most 32 Unicode code points (a surrogate pair counts once) with no
    /// character in the general categories Cc, Cf, Cs, Co, Cn, Zl, Zp, or Zs other than U+0020. After validation only
    /// U+0020 can be stripped. Otherwise "bad name".
    /// </summary>
    public static class NameRules
    {
        public const int MaxRunes = 32;

        /// <summary>True if <paramref name="name"/> satisfies the wire rules.</summary>
        public static bool IsValid(string name)
        {
            if (name == null) return false;
            int runes = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool pair = char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]);
                if (char.IsSurrogate(c) && !pair) return false;
                if (c != ' ' && IsRefused(CharUnicodeInfo.GetUnicodeCategory(name, i))) return false;
                if (pair) i++;
                if (++runes > MaxRunes) return false;
            }
            return true;
        }

        /// <summary>The name as given when valid; throws LinkException("bad name") otherwise.</summary>
        public static string Validate(string name)
        {
            if (!IsValid(name)) throw new LinkException("bad name");
            return name;
        }

        /// <summary>A valid name with only U+0020 stripped from both ends.</summary>
        public static string Strip(string validName)
        {
            return validName.Trim(' ');
        }

        /// <summary>"headset-" plus the first four fingerprint characters (handshake.default_name).</summary>
        public static string DefaultName(string fingerprint)
        {
            return "headset-" + fingerprint.Substring(0, 4);
        }

        /// <summary>The name the headset sends: the stripped name, or the default when empty. Validates the input.</summary>
        public static string NameOrDefault(string name, string fingerprint)
        {
            string stripped = Strip(Validate(name));
            return stripped.Length > 0 ? stripped : DefaultName(fingerprint);
        }

        static bool IsRefused(UnicodeCategory category)
        {
            switch (category)
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    return true;
                default:
                    return false;
            }
        }
    }
}
