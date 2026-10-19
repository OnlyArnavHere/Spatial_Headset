using System;
using System.Security.Cryptography;
using UnityEngine;

namespace SpatialLauncher.LaptopLink
{
    // Shapes of the SpatialLink.mm exports. The iOS classes bind the real ones; the editor checks bind fakes.
    public delegate int LoadKeyFn(string tag, out IntPtr pub, out int len);
    public delegate int SignFn(byte[] msg, int len, out IntPtr sig, out int sigLen);
    public delegate int Pbkdf2Fn(byte[] pw, int pwLen, byte[] salt, int saltLen, int iterations, byte[] output, int outLen);
    public delegate int BonjourRegisterFn(string instance, int port, int pairing);
    public delegate void VoidFn();
    public delegate void FreeFn(IntPtr p);

    /// <summary>Copies a native malloc'd buffer into managed memory and always frees it.</summary>
    public static class NativeBuffers
    {
        public static byte[] Take(int status, IntPtr ptr, int len, FreeFn free, string what)
        {
            try
            {
                if (status != 0) throw new LinkIdentityException(what + " failed (OSStatus " + status + ").");
                if (ptr == IntPtr.Zero || len <= 0) throw new LinkIdentityException(what + " returned no data.");
                var bytes = new byte[len];
                System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, len);
                return bytes;
            }
            finally
            {
                if (ptr != IntPtr.Zero) free(ptr);
            }
        }
    }

    /// <summary>
    /// Strict PKCS#1 RSAPublicKey DER: SEQUENCE { INTEGER n, INTEGER e }. Minimal lengths, no trailing bytes, a 2048-bit
    /// modulus and e = 65537; anything else is rejected.
    /// </summary>
    public static class RsaPublicKeyDer
    {
        public static RSAParameters Parse(byte[] der)
        {
            if (der == null) throw new LinkIdentityException("The public key is missing.");
            int pos = 0;
            int seqLen = ReadHeader(der, ref pos, 0x30, der.Length);
            if (pos + seqLen != der.Length) throw Bad("trailing or missing bytes");

            int nLen = ReadHeader(der, ref pos, 0x02, der.Length);
            if (nLen < 1) throw Bad("empty modulus");
            if ((der[pos] & 0x80) != 0) throw Bad("negative modulus");
            int start = pos, count = nLen;
            if (der[start] == 0x00)
            {
                if (count < 2 || (der[start + 1] & 0x80) == 0) throw Bad("non-minimal modulus");
                start++;
                count--;
            }
            if (count != Spki.ModulusLength || (der[start] & 0x80) == 0) throw Bad("modulus is not 2048 bits");
            var modulus = new byte[count];
            Buffer.BlockCopy(der, start, modulus, 0, count);
            pos += nLen;

            int eLen = ReadHeader(der, ref pos, 0x02, der.Length);
            if (eLen != 3 || der[pos] != 1 || der[pos + 1] != 0 || der[pos + 2] != 1) throw Bad("exponent is not 65537");
            pos += eLen;
            if (pos != der.Length) throw Bad("trailing bytes");
            return new RSAParameters { Modulus = modulus, Exponent = new byte[] { 1, 0, 1 } };
        }

        // Reads tag + minimal DER length at pos; leaves pos at the content and checks the content fits before limit.
        static int ReadHeader(byte[] d, ref int pos, byte tag, int limit)
        {
            if (pos + 2 > limit || d[pos] != tag) throw Bad("unexpected tag");
            pos++;
            int b = d[pos++];
            int len;
            if (b < 0x80) len = b;
            else if (b == 0x81)
            {
                if (pos + 1 > limit) throw Bad("truncated");
                len = d[pos++];
                if (len < 0x80) throw Bad("non-minimal length");
            }
            else if (b == 0x82)
            {
                if (pos + 2 > limit) throw Bad("truncated");
                len = (d[pos] << 8) | d[pos + 1];
                pos += 2;
                if (len < 0x100) throw Bad("non-minimal length");
            }
            else throw Bad("unsupported length");
            if (len > limit - pos) throw Bad("truncated");
            return len;
        }

        static LinkIdentityException Bad(string why)
        {
            return new LinkIdentityException("The stored public key is not a valid RSA-2048 PKCS#1 key (" + why + ").");
        }
    }

    /// <summary>The Keychain identity over injected natives. Loads (or creates, once) the key in the constructor.</summary>
    public sealed class KeychainIdentityCore : ILinkIdentity
    {
        readonly SignFn sign;
        readonly FreeFn free;

        public byte[] Spki { get { return (byte[])spki.Clone(); } }
        public string Fingerprint { get; }
        readonly byte[] spki;

        public KeychainIdentityCore(string tag, LoadKeyFn load, SignFn sign, FreeFn free)
        {
            this.sign = sign;
            this.free = free;
            IntPtr pub;
            int len;
            int status = load(tag, out pub, out len);
            byte[] der = NativeBuffers.Take(status, pub, len, free, "Loading the identity key");
            spki = SpatialLauncher.LaptopLink.Spki.Build(RsaPublicKeyDer.Parse(der));
            Fingerprint = SpatialLauncher.LaptopLink.Spki.Fingerprint(spki);
        }

        public byte[] Sign(byte[] message)
        {
            if (message == null) throw new ArgumentNullException("message");
            IntPtr ptr;
            int len;
            // An empty array may marshal as null, which the native refuses: pass a spare byte with length 0.
            int status = sign(message.Length == 0 ? new byte[1] : message, message.Length, out ptr, out len);
            byte[] signature = NativeBuffers.Take(status, ptr, len, free, "Signing");
            if (signature.Length != SpatialLauncher.LaptopLink.Spki.ModulusLength)
                throw new LinkIdentityException("Signing returned " + signature.Length + " bytes.");
            return signature;
        }
    }

    public sealed class NativePbkdf2Core : IPbkdf2
    {
        readonly Pbkdf2Fn derive;

        public NativePbkdf2Core(Pbkdf2Fn derive) { this.derive = derive; }

        public byte[] Derive(byte[] password, byte[] salt, int iterations, int length)
        {
            if (password == null) throw new ArgumentNullException("password");
            if (salt == null) throw new ArgumentNullException("salt");
            if (iterations <= 0) throw new ArgumentOutOfRangeException("iterations");
            if (length <= 0) throw new ArgumentOutOfRangeException("length");
            var output = new byte[length];
            int status = derive(password.Length == 0 ? new byte[1] : password, password.Length,
                salt.Length == 0 ? new byte[1] : salt, salt.Length, iterations, output, length);
            if (status != 0) throw new InvalidOperationException("PBKDF2 failed (" + status + ").");
            return output;
        }
    }

    /// <summary>Bonjour over injected natives: one registration at a time, unregistering twice is a no-op.</summary>
    public sealed class NativeBonjourCore : IBonjourAdvertiser
    {
        readonly BonjourRegisterFn register;
        readonly VoidFn unregister;
        bool registered;

        public bool Registered { get { return registered; } }
        public int LastError { get; private set; }

        public NativeBonjourCore(BonjourRegisterFn register, VoidFn unregister)
        {
            this.register = register;
            this.unregister = unregister;
        }

        public void Register(string instance, int port, bool pairing)
        {
            Unregister();
            LastError = register(instance, port, pairing ? 1 : 0);
            registered = LastError == 0;
            if (!registered) Debug.LogWarning("[Link] Bonjour registration failed: " + LastError);
        }

        public void Unregister()
        {
            if (!registered) return;
            registered = false;
            unregister();
        }
    }
}
