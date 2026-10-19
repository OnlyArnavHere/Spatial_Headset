using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SpatialLauncher.LaptopLink;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test (no Play mode) for the laptop link's protocol core: the known answers of the agent's Python tests
    /// (fingerprints, a signature, the pairing key schedule and MACs), the canonical SPKI and RSA key parsing, the strict
    /// JSON, base64, name, SDP fingerprint and framing rules, the pairing code, its authority (120 s, single use, 3
    /// strikes) and the pair store (in a temp folder). Results use a [LaptopLinkCoreTest] prefix.
    /// </summary>
    static class LaptopLinkCoreTest
    {
        [MenuItem("Tools/Spatial Windows/Test Laptop Link Core")]
        static void Run() => EditorTestRunner.Run("LaptopLinkCoreTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Laptop Link Core", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        // TEST_SPKI_PEM of Tools/LaptopAgent/test_identity.py (the PEM body is the DER SubjectPublicKeyInfo in base64).
        const string SpkiA64 =
            "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAu1wr5MR/nvELy6ve8lbVOGQTdl7JKq+XCEey8ihCEpGcCBvEL/TItLFEzxsN6zHc5D2gVYRFOfurvoJw3qIH" +
            "bNFrwF3JDoyz66PAvD5eWTlrM+I1y2bYCn9NxWjVUqaZzforfSg21kPfoPo9v1jtDMw5PoQil2dt6K0epVn6z7PEUybGl4lFjyMekIgF90habmUzc4NGubb/Oh+HNi12" +
            "R8DAFKSeXAASLVI42PIQ8NjqGkIthVEL0BGsR2LajpY7DXV93fPd1dfBadJjcsuX73M0kIHfdXNhV5R0K1ZmAYvrtxR0QbaQNAeYVPVJTOd5VeF0dsGlfsGtfnnCZQIj" +
            "NwIDAQAB";
        // TEST_SPKI_H_PEM of test_pair_protocol.py.
        const string SpkiH64 =
            "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEApKJ8LajE25E9xSKiZMV6kJXwvrM/OS8WPFPRMh4bcKJNGtGcITuPJ5/+QbLqFCoziIo4J+cW/VebqtUsKU8Z" +
            "61fT1hhDrac3ds1s6E0lKHfkClonOezlJsX3oBaR+YAmWOEdF+YL2rIw4IFOgSSi3RmxTMvh1CnfZAKZa65D+h325m8UePMPtz+Le1x+uP+NvVrNgNC2jLpr29uVA30t" +
            "3nhwV1eNAWH/tZA2P0GSLqMO6KfLTbk4dbR8ZQKJXQEgr/zlYptUce+zitbBuxgVXqiSV3JKIXUFrv8TqrvlCxPhNF4KxEGa+Nu+Ar8AobXxj50rIlLL0P5BxU0ojSUg" +
            "5wIDAQAB";
        // KAT_SPKI_PEM of test_identity.py, with its signature over "spatial-launcher identity KAT v1".
        const string SpkiKat64 =
            "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA4c97Nn56mpOuYQR+ev4f/lGlu800GpiU9xqaFib0RXrDgRhyoke2ki0rxzgUydmY3QHlCwgOwewEQq0gX3B/" +
            "kI/CpFk7/b4nLdxcPVBUJP2RxuBTJiF9Je4XySMljBu+U3eAU1vogRR/hGd0VNOOSOKeam8CGMtm2oNXsOFeihz+waLVVS+UmxeMZk12j6SNIT7SfElikTDSNEcboK+I" +
            "xlAsylJLRZhoG2ssNAYVUu7uboAGrpycV34wgolZ4hMzZsDBpDePA57h/nYoArD0nkx0d9Yt0AHhb7fwSK9cW73hwDurxTL6yc770AHjL4D90Cly4iKrghwjr2k78n+N" +
            "uQIDAQAB";
        const string KatSigHex =
            "9986a4a83dbd6abdb6e39f9fede80c5dbee9eb7b58748dc6ca02191bb44038f30717425ed420401db5229e9c48dcc9b0b3638a7abf5e23768f32eada7331070e" +
            "0adcc3c5edde75cacb4f80d2385066f77f02b3a641b952b42a10919cf28f37f1d18fbe932c4134be8a09ec49e3949091644fb6d9c678b22c472e03f3284414e8" +
            "a7190a0f6f9b50e5de0ffdeb93b90d58c5222082d22ee970c0ca60c73450ea8735638987c7dd0f2b1c7ece84405ec54824be4618873eca654e3d1a3ce2823d0b" +
            "d442b21d65847579386c3c710c86a5c5630ec84c2bcd8633aae585b3b99256cde6f84e3d38134027e3e4cb2da044c183fd75543aa8357d5004f3bd718c5330da";

        internal static byte[] SpkiA => Convert.FromBase64String(SpkiA64);
        internal static byte[] SpkiH => Convert.FromBase64String(SpkiH64);
        internal static byte[] SpkiKat => Convert.FromBase64String(SpkiKat64);

        static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
        static byte[] FromHex(string h) => Enumerable.Range(0, h.Length / 2).Select(i => Convert.ToByte(h.Substring(i * 2, 2), 16)).ToArray();
        static string Sha(byte[] b) { using (var s = SHA256.Create()) return Hex(s.ComputeHash(b)); }

        /// <summary>The reason of the LinkException the action throws, "other:Type" for anything else, null for no exception.</summary>
        static string Reason(Action action)
        {
            try { action(); }
            catch (LinkException e) { return e.Reason; }
            catch (Exception e) { return "other:" + e.GetType().Name; }
            return null;
        }

        static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return true; }
            catch (Exception) { return false; }
            return false;
        }

        static bool Malformed(string json) => Reason(() => LinkJson.ParseObject(json)) == "malformed frame";

        static IEnumerator Steps()
        {
            Known();
            SpkiChecks();
            JsonChecks();
            Base64AndNames();
            SdpAndFraming();
            PairCodeChecks();
            Authority();
            Store();
            yield break;
        }

        static void Known()
        {
            Check(Spki.Fingerprint(new byte[0]) == "4OYMIQUY7QOBJGX3", "fingerprint of the empty input is 4OYMIQUY7QOBJGX3");
            Check(Spki.Fingerprint(SpkiA) == "QBM5P7AOIJGXGGFP", "TEST_SPKI fingerprint is QBM5P7AOIJGXGGFP");
            Check(Spki.Fingerprint(SpkiKat) == "Z5KLLXYQCYMIHZMJ", "the signature vector's key fingerprint is Z5KLLXYQCYMIHZMJ");

            byte[] msg = Encoding.ASCII.GetBytes("spatial-launcher identity KAT v1");
            byte[] sig = FromHex(KatSigHex);
            Check(sig.Length == 256 && RsaPkcs1.Verify(SpkiKat, sig, msg), "the signature known answer verifies");
            bool anyFlip = false;
            foreach (int bit in new[] { 0, 7, 100, 2047 })
            {
                var bad = (byte[])sig.Clone();
                bad[bit / 8] ^= (byte)(1 << (bit % 8));
                anyFlip |= RsaPkcs1.Verify(SpkiKat, bad, msg);
            }
            Check(!anyFlip && !RsaPkcs1.Verify(SpkiKat, sig, Encoding.ASCII.GetBytes("spatial-launcher identity KAT v2"))
                  && !RsaPkcs1.Verify(SpkiA, sig, msg) && !RsaPkcs1.Verify(SpkiKat, sig.Take(255).ToArray(), msg),
                "flipped signature bits, another message or key, and a short signature all fail");

            byte[] na = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] nh = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
            Transcripts.DeriveKeys(new Pbkdf2Sha256(), "ABCDEFGHJKMN", na, nh, Transcripts.PairIterations, out var k, out var ka, out var kh);
            byte[] tp = Transcripts.Pair(na, nh, SpkiA, SpkiH, "laptop", "headset");
            Check(Hex(k) == "f373e4b15247fe94b91aef750bc3efddde8777910380df1bcd60e08b8c3f46a0", "pairing key K matches the Python vector");
            Check(Hex(ka) == "473162332210bd9eca3d99dfe3a507ac33e133a7c33289610fddb08e14953b63" &&
                  Hex(kh) == "26227287f32fdb5fa2d87b1f05a34081f039ffce910bfb2c790ec8f592df57df", "K_A and K_H match the Python vectors");
            Check(Sha(tp) == "37060e2d64a5907633130129001cc4f607fd19c8904b3f4879a35f5570c38675", "T_P hashes to the Python vector");
            Check(Hex(Transcripts.AgentMac(ka, tp)) == "3b12a278a24e055ffb01b67dc139ba7a5a0db2a067c4cc899a331aceb9c34907", "MAC_A matches the Python vector");
            Check(Hex(Transcripts.HeadsetMac(kh, tp)) == "6f6408b29b2d7fcd4ad41714dd3b57827901530cbb82bbe79096211c3a0eebea", "MAC_H matches the Python vector");
        }

        // PKCS#1 RSAPublicKey: SEQUENCE { INTEGER n, exponent TLV }; "lead" puts the 00 byte in front of the modulus.
        static byte[] Pkcs1(byte[] modulus, byte[] exponent = null, bool lead = true)
        {
            int n = modulus.Length + (lead ? 1 : 0);
            var body = new List<byte> { 0x02, 0x82, (byte)(n >> 8), (byte)n };
            if (lead) body.Add(0);
            body.AddRange(modulus);
            body.AddRange(exponent ?? new byte[] { 0x02, 0x03, 0x01, 0x00, 0x01 });
            var der = new List<byte> { 0x30, 0x82, (byte)(body.Count >> 8), (byte)body.Count };
            der.AddRange(body);
            return der.ToArray();
        }

        static void SpkiChecks()
        {
            var spki = SpkiA;
            Check(spki.Length == Spki.Length && Spki.TryParse(spki, out var key) && Spki.Build(key).SequenceEqual(spki), "a canonical SPKI parses and rebuilds to the same bytes");
            bool rejected = !Spki.TryParse(null, out _) && !Spki.TryParse(spki.Take(293).ToArray(), out _) && !Spki.TryParse(spki.Concat(new byte[] { 0 }).ToArray(), out _);
            foreach (int at in new[] { 0, 5, 20, 32, 293 })
            {
                var bad = (byte[])spki.Clone();
                bad[at] ^= 0x01;
                rejected &= !Spki.TryParse(bad, out _);
            }
            var lowTop = (byte[])spki.Clone();
            lowTop[33] &= 0x7F;
            Check(rejected && !Spki.TryParse(lowTop, out _), "an SPKI that is short, long, altered in the layout or exponent, or has a clear top modulus bit is refused");
            Check(Throws<ArgumentException>(() => Spki.Build(new RSAParameters { Modulus = new byte[128], Exponent = new byte[] { 1, 0, 1 } })) &&
                  Throws<ArgumentException>(() => Spki.Build(new RSAParameters { Modulus = Enumerable.Repeat((byte)0xFF, 256).ToArray(), Exponent = new byte[] { 3 } })),
                "Build refuses a 1024-bit modulus and an exponent other than 65537");

            var modulus = spki.Skip(33).Take(256).ToArray();
            Check(RsaPublicKeyDer.Parse(Pkcs1(modulus)).Modulus.SequenceEqual(modulus), "a strict PKCS#1 public key parses");
            var lowModulus = (byte[])modulus.Clone();
            lowModulus[0] &= 0x7F;
            Check(Throws<LinkIdentityException>(() => RsaPublicKeyDer.Parse(null)) &&
                  Throws<LinkIdentityException>(() => RsaPublicKeyDer.Parse(Pkcs1(modulus).Concat(new byte[] { 0 }).ToArray())) &&
                  Throws<LinkIdentityException>(() => RsaPublicKeyDer.Parse(Pkcs1(modulus.Take(255).ToArray()))) &&
                  Throws<LinkIdentityException>(() => RsaPublicKeyDer.Parse(Pkcs1(modulus, new byte[] { 0x02, 0x03, 0x01, 0x00, 0x03 }))) &&
                  Throws<LinkIdentityException>(() => RsaPublicKeyDer.Parse(Pkcs1(modulus, null, false))) &&
                  Throws<LinkIdentityException>(() => RsaPublicKeyDer.Parse(Pkcs1(lowModulus))) &&
                  Throws<LinkIdentityException>(() => RsaPublicKeyDer.Parse(Pkcs1(modulus).Take(100).ToArray())),
                "RSA public key DER: null, trailing bytes, 2040-bit, other exponent, negative or non-minimal modulus and truncation are refused");
        }

        static string Nest(int depth)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < depth; i++) sb.Append("{\"a\":");
            sb.Append("{}");
            for (int i = 1; i < depth; i++) sb.Append('}');
            return sb.ToString();
        }

        static void JsonChecks()
        {
            var one = LinkJson.ParseObject("{\"v\":1}");
            var onePointZero = LinkJson.ParseObject("{\"v\":1.0}");
            Check(LinkJson.IsInteger(one["v"], 1) && !LinkJson.IsInteger(onePointZero["v"], 1) && onePointZero["v"] is double &&
                  !LinkJson.IsInteger(LinkJson.ParseObject("{\"v\":\"1\"}")["v"], 1) && !LinkJson.IsInteger(LinkJson.ParseObject("{\"v\":true}")["v"], 1),
                "v:1 is the integer 1; 1.0, \"1\" and true are not");
            Check(Malformed(Nest(65)) && !Malformed(Nest(64)), "nesting depth 64 parses, 65 is malformed");
            Check(Malformed("{\"a\":NaN}") && Malformed("{\"a\":Infinity}") && Malformed("{\"a\":1e999}") && Malformed("{\"a\":-Infinity}"), "NaN, Infinity and overflowing numbers are malformed");
            Check(!Malformed("{\"a\":" + new string('9', 4300) + "}") && Malformed("{\"a\":" + new string('9', 4301) + "}") && Malformed("{\"a\":-" + new string('9', 4301) + "}"),
                "an integer of 4300 digits parses, 4301 is malformed");
            Check((long)LinkJson.ParseObject("{\"a\":1,\"a\":2}")["a"] == 2, "duplicate keys: the last wins");
            var badUtf8 = new byte[] { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D };
            Check(Reason(() => LinkJson.ParseObject(badUtf8)) == "malformed frame", "bad UTF-8 is malformed");
            Check(Malformed("[1]") && Malformed("{} x") && Malformed("{\"a\":\"\t\"}") && Malformed("{\"a\":01}") && Malformed("{\"a\":1,}") && Malformed("") && Malformed("{\"a\":\"\\x\"}"),
                "arrays at the top, trailing text, raw control characters, leading zeros and trailing commas are malformed");
            var text = LinkJson.ParseObject("{\"s\":\"a\\u00e9\\n\\\"\",\"n\":null,\"l\":[1,2.5,{\"k\":false}]}");
            Check((string)text["s"] == "a\u00e9\n\"" && text["n"] == null && ((List<object>)text["l"]).Count == 3 &&
                  LinkJson.Serialize(LinkJson.ParseObject(LinkJson.Serialize(text))) == LinkJson.Serialize(text),
                "escapes, null, arrays and nested objects parse, and serialising round-trips");
        }

        static void Base64AndNames()
        {
            byte[] data;
            Check(StrictBase64.TryDecode("AAEC", out data) && data.SequenceEqual(new byte[] { 0, 1, 2 }) && StrictBase64.TryDecode("", out data) &&
                  StrictBase64.TryDecode("AA==", out data) && StrictBase64.TryDecode("AAE=", out data), "canonical base64 decodes");
            bool bad = false;
            foreach (var s in new[] { "AAE", "AA E", "AAEC\n", "AB==", "AAF=", "-_8=", "A===", "=AAA", "AA=A", "AAEC=", " AAEC", null })
                bad |= StrictBase64.TryDecode(s, out _);
            Check(!bad, "base64 without padding, with whitespace, URL-safe letters, bad padding or non-zero trailing bits is refused");

            Check(NameRules.IsValid("Laptop 1") && NameRules.IsValid("") && NameRules.IsValid(new string('a', 32)) && !NameRules.IsValid(new string('a', 33)) && !NameRules.IsValid(null),
                "names: ordinary, empty and 32 characters pass; 33 and null do not");
            string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 32));
            Check(NameRules.IsValid(emoji) && !NameRules.IsValid(emoji + "a"), "a surrogate pair counts as one character");
            Check(!NameRules.IsValid("a\u0000b") && !NameRules.IsValid("a\u200Bb") && !NameRules.IsValid("a\u00A0b") && !NameRules.IsValid("a\ud800b") &&
                  !NameRules.IsValid("a\u2028b") && !NameRules.IsValid("a\ue000b") && !NameRules.IsValid("a\tb") && NameRules.IsValid("a b"),
                "control, format, no-break space, lone surrogate, line separator and private-use characters are refused; a plain space is not");
            Check(NameRules.Strip("  x y  ") == "x y" && NameRules.DefaultName("QBM5P7AOIJGXGGFP") == "headset-QBM5" &&
                  NameRules.NameOrDefault("   ", "QBM5P7AOIJGXGGFP") == "headset-QBM5" && NameRules.NameOrDefault(" Den ", "QBM5P7AOIJGXGGFP") == "Den" &&
                  Reason(() => NameRules.Validate("a\u0001")) == "bad name", "names: strip, default name and validation");
        }

        static string Fp(string v) => "a=fingerprint:sha-256 " + v;
        static readonly string Hex32 = string.Join(":", Enumerable.Repeat("ab", 32));
        static readonly string Hex32Other = string.Join(":", Enumerable.Repeat("CD", 32));

        static void SdpAndFraming()
        {
            string good = "v=0\r\n" + Fp(Hex32) + "\r\nm=video 9 UDP\r\n";
            string upper = Hex32.ToUpperInvariant();
            Check(SdpFingerprint.Extract(good) == upper && SdpFingerprint.Extract("v=0\n" + Fp(Hex32) + "\n") == upper && SdpFingerprint.Extract(good + Fp(upper) + "\r\n") == upper,
                "the SDP fingerprint is extracted and upper-cased, and the same one twice is fine");
            Check(Reason(() => SdpFingerprint.Extract("v=0\r\n")) == "sdp needs exactly one fingerprint" &&
                  Reason(() => SdpFingerprint.Extract(good + Fp(Hex32Other) + "\r\n")) == "sdp needs exactly one fingerprint", "no fingerprint, or two different ones, is refused");
            Check(Reason(() => SdpFingerprint.Extract("a=fingerprint:sha-1 " + Hex32 + "\r\n")) == "unsupported fingerprint algorithm" &&
                  Reason(() => SdpFingerprint.Extract(Fp(Hex32.Substring(3)) + "\r\n")) == "malformed fingerprint" && Reason(() => SdpFingerprint.Extract(Fp(Hex32) + " \r\n")) == "malformed fingerprint" &&
                  Reason(() => SdpFingerprint.Extract(Fp(Hex32.Replace("ab", "zz")) + "\r\n")) == "malformed fingerprint", "another algorithm, a short, padded or non-hex fingerprint is refused");
            Check(Reason(() => SdpFingerprint.Extract("v=0\r" + Fp(Hex32) + "\n")) == "malformed sdp" && Reason(() => SdpFingerprint.Extract(good + "a=x\t\r\n")) == "malformed sdp" &&
                  Reason(() => SdpFingerprint.Extract(good + "a=x\u2028\r\n")) == "malformed sdp", "a bare CR, a control character or U+2028 is refused");

            Check(LinkFraming.ParseLength(new byte[] { 0, 0, 0, 1 }) == 1 && LinkFraming.ParseLength(new byte[] { 0, 1, 0, 0 }) == 65536 &&
                  Reason(() => LinkFraming.ParseLength(new byte[] { 0, 0, 0, 0 })) == "bad frame length" && Reason(() => LinkFraming.ParseLength(new byte[] { 0, 1, 0, 1 })) == "bad frame length" &&
                  Reason(() => LinkFraming.ParseLength(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })) == "bad frame length" && Reason(() => LinkFraming.ParseLength(new byte[3])) == "bad frame length",
                "a frame length of 1 to 64 KiB passes; 0, 64 KiB + 1, 4 GiB and a short header do not");
            Check(LinkFraming.Frame(new byte[] { 7, 8 }).SequenceEqual(new byte[] { 0, 0, 0, 2, 7, 8 }) && Throws<ArgumentException>(() => LinkFraming.Frame(new byte[0])) &&
                  Throws<ArgumentException>(() => LinkFraming.Frame(new byte[65537])) && LinkFraming.Frame(new byte[65536]).Length == 65540 &&
                  LinkFraming.Enc("hi").SequenceEqual(new byte[] { 0, 0, 0, 2, (byte)'h', (byte)'i' }) && LinkFraming.Enc(new byte[0]).Length == 4,
                "frames and enc() have the documented layout and caps");
        }

        static void PairCodeChecks()
        {
            Check(PairCode.Parse("abcd-efgh-jkmn") == "ABCDEFGHJKMN" && PairCode.Parse(" ABCD EFGH JKMN ") == "ABCDEFGHJKMN" && PairCode.Format("ABCDEFGHJKMN") == "ABCD-EFGH-JKMN",
                "a code parses ignoring case, spaces and dashes, and formats as XXXX-XXXX-XXXX");
            Check(PairCode.Parse("ILO000000000") == "110000000000" && PairCode.Parse("il0o00000000") == "110000000000", "I and L read as 1, O reads as 0");
            Check(PairCode.Parse("UBCDEFGHJKMN") == null && PairCode.Parse("ABCDEFGHJKM") == null && PairCode.Parse("ABCDEFGHJKMNP") == null && PairCode.Parse(null) == null &&
                  PairCode.Parse("ABCDEFGHJKM!") == null && PairCode.Parse("") == null, "U, other characters, 11 or 13 characters and null are refused");
            string fresh = PairCode.New();
            Check(fresh.Length == PairCode.Length && fresh.All(c => PairCode.Alphabet.IndexOf(c) >= 0) && PairCode.Parse(PairCode.Format(fresh)) == fresh && PairCode.New() != fresh,
                "a new code has 12 alphabet characters and survives format and parse");
        }

        static void Authority()
        {
            double t = 0;
            var auth = new PairCodeAuthority(() => t, 120);
            var ended = new List<PairCodeEnd>();
            auth.CodeEnded += (id, why) => ended.Add(why);
            Check(auth.Active == null && auth.TryApplyProof(1, true) == PairProofOutcome.NotPairing, "with no code, a proof is \"not pairing\"");
            var code = auth.Issue();
            t = 119.5;
            bool still = auth.Active != null && auth.RemainingSeconds > 0;
            t = 120;
            Check(still && auth.Active == null && ended.SequenceEqual(new[] { PairCodeEnd.Expired }) && auth.TryApplyProof(code.Id, true) == PairProofOutcome.NotPairing,
                "a code lasts 120 s, then it has expired");

            code = auth.Issue();
            t = 130;
            Check(auth.TryApplyProof(code.Id, true) == PairProofOutcome.Accepted && auth.Active == null && auth.TryApplyProof(code.Id, true) == PairProofOutcome.NotPairing,
                "a right proof spends the code: it works once");

            code = auth.Issue();
            Check(auth.TryApplyProof(code.Id, false) == PairProofOutcome.WrongCode && auth.Strikes == 1 && auth.TryApplyProof(code.Id, false) == PairProofOutcome.WrongCode &&
                  auth.TryApplyProof(code.Id, false) == PairProofOutcome.WrongCodeStruckOut && auth.Active == null && auth.TryApplyProof(code.Id, true) == PairProofOutcome.NotPairing,
                "3 wrong proofs kill the code, and then even the right one is refused");

            var first = auth.Issue();
            auth.TryApplyProof(first.Id, false);
            ended.Clear();
            var second = auth.Issue();
            Check(ended.SequenceEqual(new[] { PairCodeEnd.Replaced }) && second.Id != first.Id && auth.Strikes == 0 && auth.TryApplyProof(first.Id, true) == PairProofOutcome.NotPairing &&
                  auth.Active == second && auth.TryApplyProof(second.Id, true) == PairProofOutcome.Accepted,
                "issuing replaces the old code (its strikes go with it) and the old id no longer works");
            var third = auth.Issue();
            auth.Cancel();
            Check(auth.Active == null && auth.TryApplyProof(third.Id, true) == PairProofOutcome.NotPairing, "cancel kills the code");
        }

        static void Store()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SLCore-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PairStore(dir);
                int changes = 0;
                store.Changed += () => changes++;
                Check(store.List().Count == 0 && store.FindByKey(SpkiA) == null, "an absent file is an empty store");
                Check(store.Add("Laptop", SpkiA, out var pair) == PairAddResult.Added && pair.Name == "Laptop" && pair.Fingerprint == "QBM5P7AOIJGXGGFP" && store.List().Count == 1 && changes == 1,
                    "a laptop is added");
                Check(store.Add("laptop", SpkiA, out pair) == PairAddResult.Updated && pair.Name == "Laptop" && store.List().Count == 1 && changes == 2,
                    "the same name and key updates, ignoring case, keeping the first spelling");
                Check(store.Add("Other", SpkiA, out pair) == PairAddResult.KeyPairedUnderOtherName && pair == null && store.Add("LAPTOP", SpkiH, out _) == PairAddResult.NameHasDifferentKey &&
                      store.List().Count == 1 && changes == 2, "the same key under another name, or the same name with another key, is refused and nothing changes");
                Check(store.CheckCanAdd("Laptop", SpkiA) && store.CheckCanAdd("New", SpkiH) && !store.CheckCanAdd("Other", SpkiA) && !store.CheckCanAdd("Laptop", SpkiH) && !store.CheckCanAdd("", SpkiH) &&
                      !store.CheckCanAdd("New", new byte[10]) && store.List().Count == 1, "CheckCanAdd agrees with Add and writes nothing");
                Check(Throws<ArgumentException>(() => store.Add("  ", SpkiH, out _)) && Throws<ArgumentException>(() => store.Add("X", SpkiH.Take(100).ToArray(), out _)),
                    "Add refuses an empty name and a non-canonical key");
                store.Add("Second", SpkiKat, out _);
                var again = new PairStore(dir);
                Check(again.List().Count == 2 && again.FindByKey(SpkiKat).Name == "Second" && again.FindByKey(SpkiH) == null && Directory.GetFiles(dir).Length == 1,
                    "pairs persist across instances, and no temporary file is left");

                string path = Path.Combine(dir, PairStore.FileName);
                File.WriteAllText(path, File.ReadAllText(path).Replace("QBM5P7AOIJGXGGFP", "AAAAAAAAAAAAAAAA"));
                Check(new PairStore(dir).FindByKey(SpkiA).Fingerprint == "QBM5P7AOIJGXGGFP", "the fingerprint is recomputed from the key, never read back");

                changes = 0;
                Check(store.Remove(" laptop ") && !store.Remove("Laptop") && store.List().Count == 1 && store.FindByKey(SpkiA) == null && changes == 1,
                    "Remove drops a pair (ignoring case) and says false when there is none");

                File.WriteAllText(path, "garbage");
                Check(Throws<InvalidDataException>(() => store.List()) && Throws<InvalidDataException>(() => store.Add("N", SpkiH, out _)) &&
                      Throws<InvalidDataException>(() => store.FindByKey(SpkiA)) && File.ReadAllText(path) == "garbage", "a corrupt file throws and is left untouched");
                File.WriteAllText(path, "{\"version\":2,\"pairs\":[]}");
                Check(Throws<InvalidDataException>(() => store.List()), "an unknown version is refused");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
