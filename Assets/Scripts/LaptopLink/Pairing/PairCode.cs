using System.Security.Cryptography;
using System.Text;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The one-time pairing code: 12 characters of Crockford base32 (60 bits), shown as XXXX-XXXX-XXXX. When typed,
    /// spaces and dashes are dropped, case is ignored, I and L read as 1, O reads as 0, and U or anything else
    /// outside the alphabet is refused. The canonical form (12 uppercase characters, no dashes) feeds the key derivation.
    /// </summary>
    public static class PairCode
    {
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const int Length = 12;

        public static string New()
        {
            var bytes = new byte[Length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(Length);
            foreach (byte b in bytes) sb.Append(Alphabet[b & 31]); // 32 symbols: no modulo bias
            return sb.ToString();
        }

        /// <summary>ABCDEFGHJKMN to ABCD-EFGH-JKMN.</summary>
        public static string Format(string canonical)
        {
            return canonical.Substring(0, 4) + "-" + canonical.Substring(4, 4) + "-" + canonical.Substring(8, 4);
        }

        /// <summary>The canonical code for what the user typed, or null when it is unusable.</summary>
        public static string Parse(string typed)
        {
            if (typed == null) return null;
            var sb = new StringBuilder(Length);
            foreach (char raw in typed)
            {
                if (char.IsWhiteSpace(raw) || raw == '-') continue;
                char c = char.ToUpperInvariant(raw);
                if (c == 'I' || c == 'L') c = '1';
                else if (c == 'O') c = '0';
                if (Alphabet.IndexOf(c) < 0) return null;
                sb.Append(c);
                if (sb.Length > Length) return null;
            }
            return sb.Length == Length ? sb.ToString() : null;
        }
    }
}
