using System.Security.Cryptography;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256 verification against a canonical SPKI (what identity.verify does).</summary>
    public static class RsaPkcs1
    {
        public const int SignatureLength = 256;

        /// <summary>False for a malformed key, a signature that is not exactly 256 bytes, or a bad signature.</summary>
        public static bool Verify(byte[] spki, byte[] signature, byte[] message)
        {
            if (signature == null || signature.Length != SignatureLength || message == null) return false;
            if (!Spki.TryParse(spki, out var key)) return false;
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(key);
                    return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }
}
