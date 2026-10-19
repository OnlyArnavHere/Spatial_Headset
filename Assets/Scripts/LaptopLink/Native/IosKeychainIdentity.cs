#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>The headset's RSA-2048 identity in the iOS Keychain (SpatialLink.mm); the private key never leaves it.</summary>
    public sealed class IosKeychainIdentity : ILinkIdentity
    {
        const string Tag = "com.spatiallauncher.link.identity";

        [DllImport("__Internal")] static extern int SpatialLink_LoadOrCreateKey(string tag, out IntPtr pub, out int len);
        [DllImport("__Internal")] static extern int SpatialLink_Sign(byte[] msg, int len, out IntPtr sig, out int sigLen);
        [DllImport("__Internal")] static extern void SpatialLink_Free(IntPtr p);

        readonly KeychainIdentityCore core;

        public IosKeychainIdentity()
        {
            core = new KeychainIdentityCore(Tag, SpatialLink_LoadOrCreateKey, SpatialLink_Sign, SpatialLink_Free);
        }

        public byte[] Spki { get { return core.Spki; } }
        public string Fingerprint { get { return core.Fingerprint; } }
        public byte[] Sign(byte[] message) { return core.Sign(message); }
    }
}
#endif
