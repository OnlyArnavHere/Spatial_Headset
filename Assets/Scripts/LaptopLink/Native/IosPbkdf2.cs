#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>PBKDF2-HMAC-SHA256 through CommonCrypto (SpatialLink.mm).</summary>
    public sealed class IosPbkdf2 : IPbkdf2
    {
        [DllImport("__Internal")] static extern int SpatialLink_Pbkdf2(byte[] pw, int pwLen, byte[] salt, int saltLen, int iterations, byte[] output, int outLen);

        readonly NativePbkdf2Core core = new NativePbkdf2Core(SpatialLink_Pbkdf2);

        public byte[] Derive(byte[] password, byte[] salt, int iterations, int length) { return core.Derive(password, salt, iterations, length); }
    }
}
#endif
