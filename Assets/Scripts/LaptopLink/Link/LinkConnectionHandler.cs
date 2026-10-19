using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// One accepted TCP connection on the link port. Applies the per-IP gate (a refused source is dropped without a
    /// reply), reads the first frame and routes it: v2 "pair" to <see cref="PairProtocolHeadset"/> (only while a pairing
    /// code is active, otherwise "not pairing"), v1 "hello" to <see cref="HandshakeHeadset"/>, anything else is an error
    /// and a close. All work runs on the calling worker thread; <see cref="Finished"/> is raised on the main thread when
    /// a <see cref="MainThreadQueue"/> was given.
    /// </summary>
    public sealed class LinkConnectionHandler
    {
        static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

        readonly ILinkIdentity identity;
        readonly PairStore store;
        readonly PairCodeAuthority authority;
        readonly IPbkdf2 pbkdf2;
        readonly IPairConfirmer confirmer;
        readonly ILinkAnswerer answerer;
        readonly HeadsetGate gate;
        readonly MainThreadQueue queue;
        readonly Func<string> deviceName;

        /// <summary>Raised once per handled connection (not for sources the gate refused).</summary>
        public event Action<LinkResult> Finished;

        public LinkConnectionHandler(ILinkIdentity identity, PairStore store, PairCodeAuthority authority, IPbkdf2 pbkdf2,
            IPairConfirmer confirmer, ILinkAnswerer answerer, HeadsetGate gate, MainThreadQueue queue, Func<string> deviceName)
        {
            this.identity = identity;
            this.store = store;
            this.authority = authority;
            this.pbkdf2 = pbkdf2 ?? new Pbkdf2Sha256();
            this.confirmer = confirmer ?? new DecliningConfirmer();
            this.answerer = answerer ?? new RefusingAnswerer();
            this.gate = gate ?? new HeadsetGate();
            this.queue = queue;
            this.deviceName = deviceName ?? (() => "");
        }

        /// <summary>Handles an accepted client; the client is closed when this returns.</summary>
        public Task<LinkResult> HandleAsync(TcpClient client, CancellationToken ct)
        {
            string address = "?";
            try
            {
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote != null) address = remote.Address.ToString();
            }
            catch (Exception)
            {
            }
            Stream stream;
            try { stream = client.GetStream(); }
            catch (Exception)
            {
                client.Close();
                return Task.FromResult<LinkResult>(null);
            }
            return HandleAsync(stream, () => client.Close(), address, ct);
        }

        /// <summary>Handles a connection given as a stream; <paramref name="close"/> closes the underlying socket.</summary>
        public async Task<LinkResult> HandleAsync(Stream stream, Action close, string address, CancellationToken ct)
        {
            var since = Stopwatch.StartNew();
            if (!gate.TryEnter(address))
            {
                try { stream.Dispose(); } catch (Exception) { }
                try { close?.Invoke(); } catch (Exception) { }
                return null;
            }
            LinkResult result;
            try
            {
                result = await RouteAsync(new NetworkFrameChannel(stream, close, 0, 8), address, since, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Leave(address);
            }
            Post(result);
            return result;
        }

        async Task<LinkResult> RouteAsync(NetworkFrameChannel channel, string address, Stopwatch since, CancellationToken ct)
        {
            Dictionary<string, object> first = null;
            LinkException error = null;
            bool cancelled = false;
            using (var clock = new ExchangeClock(ct, since, FirstFrameTimeout))
            {
                try
                {
                    first = await channel.ReadFrameAsync(Timeout.InfiniteTimeSpan, clock.Token).ConfigureAwait(false);
                }
                catch (LinkException e) { error = e; }
                catch (OperationCanceledException)
                {
                    if (clock.TimedOut) error = new LinkException("handshake timeout");
                    else cancelled = true;
                }
                catch (Exception e) { error = new LinkException("internal error: " + e.GetType().Name); }
            }

            int version = 1;
            LinkKind kind = LinkKind.Unknown;
            if (first != null)
            {
                string type = LinkJson.GetString(first, "type");
                if (type == "pair") { version = 2; kind = LinkKind.Pairing; }
                else if (type == "hello") { version = 1; kind = LinkKind.Handshake; }
                else error = type == "error" ? new LinkException("peer refused", remote: true) : new LinkException("unexpected message");
                if (error == null)
                {
                    try
                    {
                        NetworkFrameChannel.Validate(first, version);
                        if (kind == LinkKind.Pairing && authority.Active == null) error = new LinkException("not pairing");
                    }
                    catch (LinkException e) { error = e; }
                }
            }

            if (error != null || cancelled || first == null)
            {
                channel.Version = version;
                if (error != null && !error.Remote && !cancelled && error.Reason != "connection closed"
                    && !error.Reason.StartsWith("internal error", StringComparison.Ordinal))
                    await channel.SendErrorAsync(error.Reason).ConfigureAwait(false);
                channel.Close();
                return new LinkResult(kind, false, cancelled ? "cancelled" : error?.Reason, null, address);
            }

            channel.Version = version;
            channel.MaxFrames = kind == LinkKind.Pairing ? PairProtocolHeadset.MaxFrames : HandshakeHeadset.MaxFrames;
            var replay = new ReplayChannel(channel, first);
            string name = SafeName();
            if (kind == LinkKind.Pairing)
                return await PairProtocolHeadset.RunAsync(replay, address, since, identity, name, store, authority, pbkdf2, confirmer, ct).ConfigureAwait(false);
            return await HandshakeHeadset.RunAsync(replay, address, since, identity, name, store, answerer, ct).ConfigureAwait(false);
        }

        string SafeName()
        {
            try { return deviceName(); }
            catch (Exception) { return ""; }
        }

        void Post(LinkResult result)
        {
            var handler = Finished;
            if (handler == null) return;
            if (queue != null) queue.Enqueue(() => handler(result));
            else handler(result);
        }
    }
}
