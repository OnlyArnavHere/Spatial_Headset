using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The one DTLS fingerprint in an SDP, as uppercase hex with colons (handshake.sdp_fingerprint). Rules in order:
    /// no control character (Cc) or U+2028/U+2029 except line ends (CRLF or bare LF); every line starting with
    /// <c>a=fingerprint:</c> must be exactly <c>a=fingerprint:sha-256 </c> plus 32 colon-joined hex pairs; exactly one
    /// distinct value overall.
    /// </summary>
    public static class SdpFingerprint
    {
        const string Prefix = "a=fingerprint:";

        static readonly Regex Line = new Regex(@"\Aa=fingerprint:sha-256 ([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){31})\z",
            RegexOptions.CultureInvariant);

        public static string Extract(string sdp)
        {
            CheckChars(sdp);
            var found = new HashSet<string>();
            foreach (string raw in sdp.Split('\n'))
            {
                string line = raw.EndsWith("\r") ? raw.Substring(0, raw.Length - 1) : raw;
                if (!line.StartsWith(Prefix, System.StringComparison.Ordinal)) continue;
                var m = Line.Match(line);
                if (!m.Success)
                {
                    string rest = line.Substring(Prefix.Length);
                    int space = rest.IndexOf(' ');
                    if (space > 0 && rest.Substring(0, space) != "sha-256")
                        throw new LinkException("unsupported fingerprint algorithm");
                    throw new LinkException("malformed fingerprint");
                }
                found.Add(m.Groups[1].Value.ToUpperInvariant());
            }
            if (found.Count != 1)
                throw new LinkException("sdp needs exactly one fingerprint");
            foreach (string value in found) return value;
            return null;
        }

        static void CheckChars(string sdp)
        {
            int n = sdp.Length;
            for (int i = 0; i < n; i++)
            {
                char ch = sdp[i];
                if (ch == '\n') continue;
                if (ch == '\r')
                {
                    if (i + 1 < n && sdp[i + 1] == '\n') continue;
                    throw new LinkException("malformed sdp");
                }
                if (ch == '\u2028' || ch == '\u2029' || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Control)
                    throw new LinkException("malformed sdp");
            }
        }
    }
}
