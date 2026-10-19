using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SpatialLauncher.LaptopLink
{
    public sealed class PairedLaptop
    {
        public string Name { get; }
        public string Fingerprint { get; }
        public byte[] PublicKey { get; }
        /// <summary>UTC, ISO-8601 with a trailing Z.</summary>
        public string Added { get; }

        public PairedLaptop(string name, byte[] publicKey, string added)
        {
            Name = name;
            PublicKey = publicKey;
            Fingerprint = Spki.Fingerprint(publicKey);
            Added = added;
        }
    }

    public enum PairAddResult { Added, Updated, KeyPairedUnderOtherName, NameHasDifferentKey }

    /// <summary>
    /// The laptops this headset trusts, in pairs.json. A laptop is trusted only if its full SPKI equals a stored key;
    /// the fingerprint is recomputed on load and never read back as proof. Names compare ignoring case. Writes are atomic.
    /// A file that cannot be read throws and is left untouched.
    /// </summary>
    public sealed class PairStore
    {
        public const string FileName = "pairs.json";

        readonly object gate = new object();
        readonly string path;

        /// <summary>Raised (outside the lock) after the stored pairs changed.</summary>
        public event Action Changed;

        public PairStore(string directory)
        {
            path = Path.Combine(directory, FileName);
        }

        public IReadOnlyList<PairedLaptop> List()
        {
            lock (gate) return Read();
        }

        /// <summary>The pair whose full key equals <paramref name="spki"/>, or null.</summary>
        public PairedLaptop FindByKey(byte[] spki)
        {
            lock (gate)
            {
                foreach (var p in Read())
                    if (Transcripts.MacEquals(p.PublicKey, spki)) return p;
                return null;
            }
        }

        /// <summary>
        /// Same name and same key updates; the same key under another name, or the same name with a different key, is
        /// refused (nothing is written).
        /// </summary>
        public PairAddResult Add(string name, byte[] spki, out PairedLaptop pair)
        {
            pair = null;
            name = name == null ? "" : name.Trim();
            if (name.Length == 0) throw new ArgumentException("A pair needs a name.", nameof(name));
            if (!Spki.TryParse(spki, out _)) throw new ArgumentException("Not a canonical RSA-2048 public key.", nameof(spki));
            PairAddResult result;
            lock (gate)
            {
                var pairs = Read();
                var refused = Conflict(pairs, name, spki);
                if (refused.HasValue) return refused.Value;
                string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                var fresh = new PairedLaptop(name, (byte[])spki.Clone(), now);
                int at = pairs.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    fresh = new PairedLaptop(pairs[at].Name, fresh.PublicKey, now);
                    pairs[at] = fresh;
                    result = PairAddResult.Updated;
                }
                else
                {
                    pairs.Add(fresh);
                    result = PairAddResult.Added;
                }
                Write(pairs);
                pair = fresh;
            }
            Changed?.Invoke();
            return result;
        }

        static PairAddResult? Conflict(List<PairedLaptop> pairs, string name, byte[] spki)
        {
            foreach (var e in pairs)
            {
                bool sameName = string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase);
                bool sameKey = Transcripts.MacEquals(e.PublicKey, spki);
                if (sameKey && !sameName) return PairAddResult.KeyPairedUnderOtherName;
                if (sameName && !sameKey) return PairAddResult.NameHasDifferentKey;
            }
            return null;
        }

        /// <summary>Read-only: true when <see cref="Add"/> would accept this name and key (nothing is written).</summary>
        public bool CheckCanAdd(string name, byte[] spki)
        {
            name = name == null ? "" : name.Trim();
            if (name.Length == 0 || !Spki.TryParse(spki, out _)) return false;
            lock (gate) return !Conflict(Read(), name, spki).HasValue;
        }

        public bool Remove(string name)
        {
            lock (gate)
            {
                var pairs = Read();
                int removed = pairs.RemoveAll(p => string.Equals(p.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (removed == 0) return false;
                Write(pairs);
            }
            Changed?.Invoke();
            return true;
        }

        List<PairedLaptop> Read()
        {
            var pairs = new List<PairedLaptop>();
            if (!File.Exists(path)) return pairs;
            try
            {
                var doc = LinkJson.ParseObject(File.ReadAllBytes(path));
                if (!LinkJson.IsInteger(doc.TryGetValue("version", out var v) ? v : null, 1) || !(doc["pairs"] is List<object> list))
                    throw new InvalidDataException("unsupported version");
                foreach (object item in list)
                {
                    var o = item as Dictionary<string, object> ?? throw new InvalidDataException("bad pair");
                    string name = LinkJson.GetString(o, "name");
                    if (string.IsNullOrEmpty(name) || !StrictBase64.TryDecode(LinkJson.GetString(o, "public_key"), out var key))
                        throw new InvalidDataException("bad pair");
                    pairs.Add(new PairedLaptop(name, key, LinkJson.GetString(o, "added") ?? ""));
                }
            }
            catch (Exception e)
            {
                throw new InvalidDataException(path + " can't be read (" + e.Message + "); fix or delete it. It was not changed.", e);
            }
            return pairs;
        }

        void Write(List<PairedLaptop> pairs)
        {
            var items = new List<object>();
            foreach (var p in pairs)
                items.Add(new Dictionary<string, object>
                {
                    ["name"] = p.Name,
                    ["fingerprint"] = p.Fingerprint,
                    ["public_key"] = StrictBase64.Encode(p.PublicKey),
                    ["added"] = p.Added,
                });
            AtomicFile.Write(path, LinkJson.ToBytes(new Dictionary<string, object> { ["version"] = 1L, ["pairs"] = items }));
        }
    }
}
