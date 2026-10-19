using System;
using System.IO;
using System.Text;
using UnityEngine;
#if SPATIAL_LAUNCHER_WEBRTC
using SpatialLauncher.SpatialWindows;
#endif

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
#if SPATIAL_LAUNCHER_WEBRTC
        /// <summary>Answers the laptop's offers (WebRTC); the picker tells it which laptop to expect.</summary>
        public LinkAnswerer Answerer { get; private set; }
#endif

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
                ILinkAnswerer answerer = null;
#if SPATIAL_LAUNCHER_WEBRTC
                Answerer = new LinkAnswerer(queue, this, () => Host.Listener.Acquire());
                answerer = Answerer;
#endif
                Host = new LinkHost(StoreDirectory, identity, queue, CreateBonjour(), new ConsentPairConfirmer(queue), name,
                    pbkdf2: CreatePbkdf2(), answerer: answerer);
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
#if SPATIAL_LAUNCHER_WEBRTC
            Answerer?.Shutdown();
#endif
            Host?.Dispose();
            Host = null;
            if (Current == this) Current = null;
        }

        /// <summary>The Keychain identity on iOS, the file identity in the editor.</summary>
        public static ILinkIdentity CreateIdentity()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new IosKeychainIdentity();
#else
            return new EditorFileIdentity();
#endif
        }

        /// <summary>The native Bonjour advertiser on iOS; the editor advertises nothing.</summary>
        public static IBonjourAdvertiser CreateBonjour()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new IosBonjour();
#else
            return new NullBonjour();
#endif
        }

        /// <summary>CommonCrypto PBKDF2 on iOS, the managed one elsewhere.</summary>
        public static IPbkdf2 CreatePbkdf2()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new IosPbkdf2();
#else
            return new Pbkdf2Sha256();
#endif
        }
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
