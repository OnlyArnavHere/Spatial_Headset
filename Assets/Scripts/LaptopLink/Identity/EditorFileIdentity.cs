#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// DEV KEY, editor only: the identity is a plain, unprotected file at persistentDataPath/LaptopLink/identity.key
    /// (RSAParameters as base64 in JSON). A device build uses the Keychain instead. If the file exists but cannot be
    /// used, the constructor throws; a new key is never made over it (delete the file yourself and re-pair).
    /// </summary>
    public sealed class EditorFileIdentity : ILinkIdentity, IDisposable
    {
        public const string FileName = "identity.key";

        readonly object gate = new object();
        readonly RSACryptoServiceProvider rsa;

        public byte[] Spki { get; }
        public string Fingerprint { get; }

        public static string DefaultDirectory
        {
            get { return Path.Combine(Application.persistentDataPath, "LaptopLink"); }
        }

        public EditorFileIdentity() : this(DefaultDirectory) { }

        public EditorFileIdentity(string directory)
        {
            string path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                rsa = Load(path);
            }
            else
            {
                rsa = new RSACryptoServiceProvider(2048);
                if (rsa.KeySize != 2048) throw new LinkIdentityException("Could not create a 2048-bit RSA key.");
                Save(path, rsa.ExportParameters(true));
            }
            Spki = Spki_Of(rsa);
            Fingerprint = LaptopLink.Spki.Fingerprint(Spki);
        }

        public byte[] Sign(byte[] message)
        {
            lock (gate)
                return rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        public void Dispose()
        {
            lock (gate) rsa.Dispose();
        }

        static byte[] Spki_Of(RSACryptoServiceProvider provider)
        {
            return LaptopLink.Spki.Build(provider.ExportParameters(false));
        }

        static RSACryptoServiceProvider Load(string path)
        {
            string hint = path + " can't be used. It is corrupt; delete it only if you accept re-pairing every laptop.";
            try
            {
                var doc = LinkJson.ParseObject(File.ReadAllBytes(path));
                var p = new RSAParameters
                {
                    Modulus = Field(doc, "modulus"),
                    Exponent = Field(doc, "exponent"),
                    D = Field(doc, "d"),
                    P = Field(doc, "p"),
                    Q = Field(doc, "q"),
                    DP = Field(doc, "dp"),
                    DQ = Field(doc, "dq"),
                    InverseQ = Field(doc, "inverse_q"),
                };
                var provider = new RSACryptoServiceProvider();
                provider.ImportParameters(p);
                if (provider.KeySize != 2048) throw new CryptographicException("not a 2048-bit key");
                var probe = new byte[] { 1, 2, 3 };
                if (!RsaPkcs1.Verify(LaptopLink.Spki.Build(provider.ExportParameters(false)),
                        provider.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), probe))
                    throw new CryptographicException("key does not sign");
                return provider;
            }
            catch (Exception e)
            {
                throw new LinkIdentityException(hint + " (" + e.Message + ")", e);
            }
        }

        static byte[] Field(Dictionary<string, object> doc, string name)
        {
            if (!StrictBase64.TryDecode(LinkJson.GetString(doc, name), out var bytes) || bytes.Length == 0)
                throw new InvalidDataException("missing or malformed " + name);
            return bytes;
        }

        static void Save(string path, RSAParameters p)
        {
            var doc = new Dictionary<string, object>
            {
                ["version"] = 1L,
                ["note"] = "development key, stored unprotected, editor only",
                ["modulus"] = StrictBase64.Encode(p.Modulus),
                ["exponent"] = StrictBase64.Encode(p.Exponent),
                ["d"] = StrictBase64.Encode(p.D),
                ["p"] = StrictBase64.Encode(p.P),
                ["q"] = StrictBase64.Encode(p.Q),
                ["dp"] = StrictBase64.Encode(p.DP),
                ["dq"] = StrictBase64.Encode(p.DQ),
                ["inverse_q"] = StrictBase64.Encode(p.InverseQ),
            };
            AtomicFile.Write(path, LinkJson.ToBytes(doc));
        }
    }
}
#endif
