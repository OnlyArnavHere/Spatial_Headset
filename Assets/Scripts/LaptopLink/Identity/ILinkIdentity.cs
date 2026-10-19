using System;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The headset's long-term RSA-2048 identity. Safe to call from any thread. Signatures are RSASSA-PKCS1-v1_5 with
    /// SHA-256.
    /// </summary>
    public interface ILinkIdentity
    {
        /// <summary>Canonical 294-byte DER SubjectPublicKeyInfo.</summary>
        byte[] Spki { get; }
        /// <summary>16-character fingerprint of <see cref="Spki"/>.</summary>
        string Fingerprint { get; }
        byte[] Sign(byte[] message);
    }

    /// <summary>The stored identity exists but cannot be used. It is never regenerated automatically.</summary>
    public sealed class LinkIdentityException : Exception
    {
        public LinkIdentityException(string message, Exception inner = null) : base(message, inner) { }
    }
}
