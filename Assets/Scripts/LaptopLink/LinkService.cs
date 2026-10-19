using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The scene-side owner of the headset's link (<see cref="LinkHost"/>): created next to the other services by
    /// <c>LauncherWindow</c>, found through <see cref="Current"/>. Builds the identity, stores under
    /// persistentDataPath/LaptopLink, the main-thread queue and the confirmer, pumps the host and disposes it.
    /// </summary>
    public class LinkService : MonoBehaviour
    {
        public static LinkService Current { get; private set; }

        /// <summary>Null when the identity could not be set up (see <see cref="Problem"/>).</summary>
        public LinkHost Host { get; private set; }
        public string Problem { get; private set; }

        public static string StoreDirectory { get { return Path.Combine(Application.persistentDataPath, "LaptopLink"); } }

        void Awake()
        {
            Current = this;
            try
            {
                var identity = CreateIdentity();
                string fingerprint = identity.Fingerprint;
                var queue = GetComponent<MainThreadQueue>();
                if (queue == null) queue = gameObject.AddComponent<MainThreadQueue>();
                string name = DeviceNames.Safe(SystemInfo.deviceName, fingerprint);
                Host = new LinkHost(StoreDirectory, identity, queue, CreateBonjour(), new ConsentPairConfirmer(queue), name);
            }
            catch (Exception e)
            {
                Problem = e.GetType().Name + ": " + e.Message;
                Debug.LogWarning("[Link] unavailable: " + Problem);
            }
        }

        void Update()
        {
            Host?.Tick();
        }

        void OnDestroy()
        {
            Host?.Dispose();
            Host = null;
            if (Current == this) Current = null;
        }

        /// <summary>The editor's file identity; on iOS the Keychain one comes with 5b-2 (this placeholder refuses).</summary>
        public static ILinkIdentity CreateIdentity()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new UnsupportedIdentity();
#else
            return new EditorFileIdentity();
#endif
        }

        /// <summary>The Bonjour advertiser; the native iOS one comes with 5b-2.</summary>
        public static IBonjourAdvertiser CreateBonjour()
        {
            return new NullBonjour();
        }

#if UNITY_IOS && !UNITY_EDITOR
        sealed class UnsupportedIdentity : ILinkIdentity
        {
            public byte[] Spki { get { throw new NotSupportedException("The iOS identity is not implemented yet."); } }
            public string Fingerprint { get { throw new NotSupportedException("The iOS identity is not implemented yet."); } }
            public byte[] Sign(byte[] message) { throw new NotSupportedException("The iOS identity is not implemented yet."); }
        }
#endif
    }

    /// <summary>The headset's name on the wire: ASCII letters, digits and a few separators, valid under <see cref="NameRules"/>.</summary>
    public static class DeviceNames
    {
        public static string Safe(string raw, string fingerprint)
        {
            var sb = new StringBuilder();
            if (raw != null)
                foreach (char c in raw)
                {
                    if (sb.Length >= NameRules.MaxRunes) break;
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == ' ';
                    if (ok) sb.Append(c);
                }
            string name = sb.ToString().Trim(' ');
            return name.Length > 0 && NameRules.IsValid(name) ? name : NameRules.DefaultName(fingerprint);
        }
    }
}
