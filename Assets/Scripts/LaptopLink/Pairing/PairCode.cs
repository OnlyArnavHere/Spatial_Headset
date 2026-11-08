using System.Security.Cryptography;
using System.Text;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The one-time pairing code: 8 characters of Crockford base32 (40 bits), shown as XXXX-XXXX. 40 bits is enough
    /// because a code lasts 120 s and allows 3 wrong tries (the reasoning is in pair_protocol.py). When typed, spaces
    /// and dashes are dropped, case is ignored, I and L read as 1, O reads as 0, and U or anything else outside the
    /// alphabet is refused; a 12-character code from an older laptop app or headset is still accepted. The canonical
    /// form (uppercase, no dashes) feeds the key derivation.
    /// </summary>
    public static class PairCode
    {
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const int Length = 8;
        public const int LegacyLength = 12;

        public static string New()
        {
            var bytes = new byte[Length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(Length);
            foreach (byte b in bytes) sb.Append(Alphabet[b & 31]); // 32 symbols: no modulo bias
            return sb.ToString();
        }

        /// <summary>ABCDEFGH to ABCD-EFGH (a 12-character code gets three groups).</summary>
        public static string Format(string canonical)
        {
            var sb = new StringBuilder(canonical.Length + 2);
            for (int i = 0; i < canonical.Length; i += 4)
            {
                if (i > 0) sb.Append('-');
                sb.Append(canonical, i, System.Math.Min(4, canonical.Length - i));
            }
            return sb.ToString();
        }

        /// <summary>The code inside a laptop's pairing QR: 20 characters (100 bits), never typed.</summary>
        public const int QrLength = 20;
        public const string QrPrefix = "SLPAIR";

        /// <summary>
        /// Reads a scanned QR's text, "SLPAIR:&lt;code&gt;:&lt;laptop fingerprint&gt;" (pairing.parse_qr in the agent). False for
        /// anything else.
        /// </summary>
        public static bool TryParseQr(string text, out string code, out string fingerprint)
        {
            code = fingerprint = null;
            if (text == null) return false;
            string[] parts = text.Trim().Split(':');
            if (parts.Length != 3 || parts[0] != QrPrefix || parts[1].Length != QrLength || parts[2].Length != 16) return false;
            foreach (char c in parts[1])
                if (Alphabet.IndexOf(c) < 0) return false;
            foreach (char c in parts[2])
                if (!((c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7'))) return false;
            code = parts[1];
            fingerprint = parts[2];
            return true;
        }

        /// <summary>16 lowercase hex characters: the first 8 bytes of SHA-256("sl-qr-id:" + code). Names a QR code without revealing it.</summary>
        public static string QrId(string code)
        {
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.ASCII.GetBytes("sl-qr-id:" + code));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>The Bonjour instance name a headset armed with this QR code advertises, so the laptop finds it.</summary>
        public static string QrInstance(string code) { return "SL-" + QrId(code); }

        /// <summary>The canonical code for what the user typed, or null when it is unusable.</summary>
        public static string Parse(string typed)
        {
            if (typed == null) return null;
            var sb = new StringBuilder(LegacyLength);
            foreach (char raw in typed)
            {
                if (char.IsWhiteSpace(raw) || raw == '-') continue;
                char c = char.ToUpperInvariant(raw);
                if (c == 'I' || c == 'L') c = '1';
                else if (c == 'O') c = '0';
                if (Alphabet.IndexOf(c) < 0) return null;
                sb.Append(c);
                if (sb.Length > LegacyLength) return null;
            }
            return sb.Length == Length || sb.Length == LegacyLength ? sb.ToString() : null;
        }
    }
}
