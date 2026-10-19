using System;
using System.Security.Cryptography;
using System.Text;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// DER SubjectPublicKeyInfo for an RSA-2048 key with e = 65537, built and parsed by hand because the runtime's
    /// ExportSubjectPublicKeyInfo is not supported. Only the one canonical 294-byte layout is accepted:
    /// 30 82 01 22 30 0D 06 09 2A 86 48 86 F7 0D 01 01 01 05 00 03 82 01 0F 00 30 82 01 0A 02 82 01 01 00
    /// + 256-byte modulus (top bit set) + 02 03 01 00 01.
    /// </summary>
    public static class Spki
    {
        public const int Length = 294;
        public const int ModulusLength = 256;

        static readonly byte[] Prefix =
        {
            0x30, 0x82, 0x01, 0x22, 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01,
            0x05, 0x00, 0x03, 0x82, 0x01, 0x0F, 0x00, 0x30, 0x82, 0x01, 0x0A, 0x02, 0x82, 0x01, 0x01, 0x00,
        };
        static readonly byte[] Suffix = { 0x02, 0x03, 0x01, 0x00, 0x01 };

        public static byte[] Build(RSAParameters key)
        {
            if (key.Modulus == null || key.Modulus.Length != ModulusLength || (key.Modulus[0] & 0x80) == 0)
                throw new ArgumentException("Only 2048-bit RSA keys are supported.");
            if (key.Exponent == null || key.Exponent.Length != 3 || key.Exponent[0] != 1 || key.Exponent[1] != 0 || key.Exponent[2] != 1)
                throw new ArgumentException("Only the public exponent 65537 is supported.");
            var spki = new byte[Length];
            Buffer.BlockCopy(Prefix, 0, spki, 0, Prefix.Length);
            Buffer.BlockCopy(key.Modulus, 0, spki, Prefix.Length, ModulusLength);
            Buffer.BlockCopy(Suffix, 0, spki, Prefix.Length + ModulusLength, Suffix.Length);
            return spki;
        }

        /// <summary>False for anything that is not exactly the canonical layout.</summary>
        public static bool TryParse(byte[] spki, out RSAParameters key)
        {
            key = default;
            if (spki == null || spki.Length != Length) return false;
            for (int i = 0; i < Prefix.Length; i++)
                if (spki[i] != Prefix[i]) return false;
            for (int i = 0; i < Suffix.Length; i++)
                if (spki[Prefix.Length + ModulusLength + i] != Suffix[i]) return false;
            if ((spki[Prefix.Length] & 0x80) == 0) return false;
            var modulus = new byte[ModulusLength];
            Buffer.BlockCopy(spki, Prefix.Length, modulus, 0, ModulusLength);
            key = new RSAParameters { Modulus = modulus, Exponent = new byte[] { 1, 0, 1 } };
            return true;
        }

        /// <summary>16 characters: RFC 4648 base32 (no padding, uppercase) of SHA-256(spki)[0..10].</summary>
        public static string Fingerprint(byte[] spki)
        {
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(spki);
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var sb = new StringBuilder(16);
            int buffer = 0, bits = 0;
            for (int i = 0; i < 10; i++)
            {
                buffer = (buffer << 8) | hash[i];
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
                buffer &= (1 << bits) - 1;
            }
            return sb.ToString();
        }
    }
}
