using System.Security.Cryptography;
using System.Text;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The signed transcripts and the pairing key schedule, byte for byte as handshake.py and pair_protocol.py.
    /// Strings are UTF-8; SDP hashes are over the exact UTF-8 bytes sent.
    /// </summary>
    public static class Transcripts
    {
        public const string HandshakeLabel = "spatiallauncher-link-v1";
        public const string PairLabel = "spatiallauncher-pair-v2";
        public const string PairSaltLabel = "sl-pair-v2";
        public const int PairIterations = 100000;

        static byte[] E(byte[] x) { return LinkFraming.Enc(x); }
        static byte[] E(string x) { return LinkFraming.Enc(x); }

        static byte[] SdpHash(string sdp)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(new UTF8Encoding(false).GetBytes(sdp));
        }

        public static byte[] Agent(byte[] na, byte[] nh, byte[] spkiA, byte[] spkiH, string mode, string name, string offerSdp, string fpOffer)
        {
            return LinkFraming.Concat(E(HandshakeLabel), E("agent"), E(na), E(nh), E(spkiA), E(spkiH), E(mode), E(name),
                E(SdpHash(offerSdp)), E(fpOffer));
        }

        public static byte[] Headset(byte[] na, byte[] nh, byte[] spkiA, byte[] spkiH, string mode, string name, string offerSdp,
            string answerSdp, string fpOffer, string fpAnswer)
        {
            return LinkFraming.Concat(E(HandshakeLabel), E("headset"), E(na), E(nh), E(spkiA), E(spkiH), E(mode), E(name),
                E(SdpHash(offerSdp)), E(SdpHash(answerSdp)), E(fpOffer), E(fpAnswer));
        }

        /// <summary>M5: enc("agent-done") || T_H.</summary>
        public static byte[] AgentDoneMessage(byte[] tHeadset) { return LinkFraming.Concat(E("agent-done"), tHeadset); }

        /// <summary>T_P of the pairing exchange.</summary>
        public static byte[] Pair(byte[] na, byte[] nh, byte[] spkiA, byte[] spkiH, string nameA, string nameH)
        {
            return LinkFraming.Concat(E(PairLabel), E(na), E(nh), E(spkiA), E(spkiH), E(nameA), E(nameH));
        }

        public static byte[] PairAgentSigMessage(byte[] tp) { return LinkFraming.Concat(E("agent"), tp); }
        public static byte[] PairHeadsetSigMessage(byte[] tp) { return LinkFraming.Concat(E("headset"), tp); }
        public static byte[] PairConfirmMessage(bool ok, byte[] tp) { return LinkFraming.Concat(E("headset-confirm"), E(ok ? "yes" : "no"), tp); }
        public static byte[] PairPinnedMessage(byte[] tp) { return LinkFraming.Concat(E("agent-pinned"), tp); }

        /// <summary>K = PBKDF2(code ASCII, enc("sl-pair-v2") || na || nh), then k_a and k_h = HMAC(K, "agent"/"headset").</summary>
        public static void DeriveKeys(IPbkdf2 pbkdf2, string canonicalCode, byte[] na, byte[] nh, int iterations,
            out byte[] k, out byte[] kAgent, out byte[] kHeadset)
        {
            byte[] salt = LinkFraming.Concat(E(PairSaltLabel), na, nh);
            k = pbkdf2.Derive(Encoding.ASCII.GetBytes(canonicalCode), salt, iterations, 32);
            kAgent = Hmac(k, Encoding.ASCII.GetBytes("agent"));
            kHeadset = Hmac(k, Encoding.ASCII.GetBytes("headset"));
        }

        public static byte[] AgentMac(byte[] kAgent, byte[] tp) { return Hmac(kAgent, LinkFraming.Concat(E("pair-mac-agent"), tp)); }
        public static byte[] HeadsetMac(byte[] kHeadset, byte[] tp) { return Hmac(kHeadset, LinkFraming.Concat(E("pair-mac-headset"), tp)); }

        /// <summary>Constant-time comparison of two MACs.</summary>
        public static bool MacEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static byte[] Hmac(byte[] key, byte[] data)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(data);
        }
    }
}
