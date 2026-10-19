using System;
using System.Security.Cryptography;
using System.Text;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// Everything the headset side of the link owns, as plain C# (<see cref="LinkService"/> wraps it in a MonoBehaviour):
    /// the pair store, the pairing-code authority, the listener, the connection handler and the Bonjour advertisement.
    /// Pairing mode (<see cref="BeginPairing"/>) holds a listener lease and advertises m=pair; normal mode
    /// (<see cref="AcquireDiscovery"/>) holds a lease and advertises without it. Every advertisement gets a new random
    /// instance name. Public methods run on the main thread; events are raised there too.
    /// </summary>
    public sealed class LinkHost : IDisposable
    {
        public ILinkIdentity Identity { get; }
        public PairStore Pairs { get; }
        public PairCodeAuthority Codes { get; }
        public LinkListener Listener { get; }
        public LinkConnectionHandler Handler { get; }
        public MainThreadQueue Queue { get; }
        public string DeviceName { get; }
        /// <summary>The Bonjour instance name currently advertised, or null.</summary>
        public string AdvertisedName { get; private set; }

        /// <summary>The pairing code died of expiry, wrong tries or use; pairing mode has been left already.</summary>
        public event Action<PairCodeEnd> PairingEnded;
        /// <summary>One connection finished (pairing or handshake).</summary>
        public event Action<LinkResult> ConnectionFinished;

        readonly IBonjourAdvertiser bonjour;
        IDisposable pairLease;
        long pairCodeId;
        int discovery;
        bool disposed;

        public bool Pairing { get { return pairLease != null; } }

        public LinkHost(string directory, ILinkIdentity identity, MainThreadQueue queue, IBonjourAdvertiser advertiser,
            IPairConfirmer confirmer, string deviceName, int port = LinkListener.LinkPort)
        {
            Identity = identity;
            Queue = queue;
            DeviceName = deviceName;
            bonjour = advertiser ?? new NullBonjour();
            Pairs = new PairStore(directory);
            Codes = new PairCodeAuthority();
            Handler = new LinkConnectionHandler(identity, Pairs, Codes, null, confirmer, null, new HeadsetGate(), queue, () => DeviceName);
            Handler.Finished += r => ConnectionFinished?.Invoke(r);
            Listener = new LinkListener((client, ct) => Handler.HandleAsync(client, ct), port);
            Codes.CodeEnded += OnCodeEnded;
        }

        /// <summary>Issues a pairing code (replacing any), listens and advertises m=pair.</summary>
        public IssuedPairCode BeginPairing()
        {
            if (disposed) return null;
            var code = Codes.Issue();
            pairCodeId = code.Id;
            if (pairLease == null) pairLease = Listener.Acquire();
            Advertise();
            return code;
        }

        /// <summary>Kills the code, stops advertising m=pair and gives the listener lease back. Safe any time.</summary>
        public void EndPairing()
        {
            if (pairLease == null) return;
            pairCodeId = 0;
            Codes.Cancel();
            var lease = pairLease;
            pairLease = null;
            lease.Dispose();
            Advertise();
        }

        /// <summary>While the Laptop picker is open: listen and advertise without m=pair. Dispose to stop.</summary>
        public IDisposable AcquireDiscovery()
        {
            if (disposed) return null;
            var lease = Listener.Acquire();
            discovery++;
            Advertise();
            return new DiscoveryLease(this, lease);
        }

        /// <summary>Notices a pairing code that ran out (expiry is only seen when asked).</summary>
        public void Tick()
        {
            if (Pairing) { var _ = Codes.Active; }
        }

        void Advertise()
        {
            if (Pairing || discovery > 0)
            {
                AdvertisedName = NewInstanceName();
                bonjour.Register(AdvertisedName, Listener.Port, Pairing);
            }
            else if (AdvertisedName != null)
            {
                AdvertisedName = null;
                bonjour.Unregister();
            }
        }

        /// <summary>"SL-" plus 16 lowercase hex characters from a CSPRNG.</summary>
        public static string NewInstanceName()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder("SL-");
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Raised on any thread (the authority, a worker): hop to the main thread.
        void OnCodeEnded(long id, PairCodeEnd reason)
        {
            if (reason == PairCodeEnd.Replaced) return;
            Action run = () =>
            {
                if (disposed || !Pairing || id != pairCodeId) return;
                EndPairing();
                PairingEnded?.Invoke(reason);
            };
            if (Queue != null) Queue.Enqueue(run);
            else run();
        }

        public void Dispose()
        {
            if (disposed) return;
            EndPairing();
            disposed = true;
            discovery = 0;
            Advertise();
            Listener.Dispose();
        }

        sealed class DiscoveryLease : IDisposable
        {
            LinkHost host;
            readonly IDisposable lease;

            public DiscoveryLease(LinkHost owner, IDisposable listenerLease) { host = owner; lease = listenerLease; }

            public void Dispose()
            {
                var h = host;
                if (h == null) return;
                host = null;
                lease.Dispose();
                if (h.discovery > 0) h.discovery--;
                if (!h.disposed) h.Advertise();
            }
        }
    }
}
