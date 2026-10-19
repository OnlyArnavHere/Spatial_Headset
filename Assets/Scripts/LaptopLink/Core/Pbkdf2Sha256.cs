using System.Security.Cryptography;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>PBKDF2-HMAC-SHA256. The pairing code is stretched with it; callers run it off the main thread.</summary>
    public interface IPbkdf2
    {
        byte[] Derive(byte[] password, byte[] salt, int iterations, int length);
    }

    /// <summary>Managed implementation (the editor and tests). The iOS build gets a CommonCrypto one later.</summary>
    public sealed class Pbkdf2Sha256 : IPbkdf2
    {
        public byte[] Derive(byte[] password, byte[] salt, int iterations, int length)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }
    }
}
