using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>The verified offer handed to the answerer (the WebRTC side of commit 6).</summary>
    public sealed class LinkOffer
    {
        public string Sdp { get; }
        /// <summary>The offer's DTLS fingerprint: uppercase hex with colons.</summary>
        public string FpO { get; }
        public string LaptopName { get; }
        public byte[] LaptopSpki { get; }
        /// <summary>The laptop's source IP.</summary>
        public string Address { get; }

        public LinkOffer(string sdp, string fpO, string laptopName, byte[] laptopSpki, string address)
        {
            Sdp = sdp;
            FpO = fpO;
            LaptopName = laptopName;
            LaptopSpki = laptopSpki;
            Address = address;
        }
    }

    public sealed class LinkAnswer
    {
        public string Sdp { get; }
        public string FpA { get; }

        public LinkAnswer(string sdp, string fpA)
        {
            Sdp = sdp;
            FpA = fpA;
        }
    }

    /// <summary>
    /// What the headset does with a verified offer. All three methods are called from worker threads and must marshal to
    /// the main thread themselves. AnswerAsync runs only after the offer's signature verified. After AnswerAsync was
    /// called, exactly one of Commit(offer, answer) (the laptop's M5 verified; the answer carries the SDP that was sent) or Abort(offer, reason)
    /// follows, also when AnswerAsync itself failed or was cancelled.
    /// </summary>
    public interface ILinkAnswerer
    {
        /// <summary>Returns null (or an answer without an SDP) to refuse with "no answer".</summary>
        Task<LinkAnswer> AnswerAsync(LinkOffer offer, CancellationToken ct);
        void Commit(LinkOffer offer, LinkAnswer answer);
        void Abort(LinkOffer offer, string reason);
    }

    /// <summary>The default: every verified offer is refused with "no answer".</summary>
    public sealed class RefusingAnswerer : ILinkAnswerer
    {
        public Task<LinkAnswer> AnswerAsync(LinkOffer offer, CancellationToken ct) { return Task.FromResult<LinkAnswer>(null); }
        public void Commit(LinkOffer offer, LinkAnswer answer) { }
        public void Abort(LinkOffer offer, string reason) { }
    }

    /// <summary>Asks the user whether to pair with a laptop (the UI comes with 5b). Called from a worker thread.</summary>
    public interface IPairConfirmer
    {
        /// <param name="fingerprint">The laptop key's fingerprint, formatted XXXX-XXXX-XXXX-XXXX.</param>
        /// <returns>True if the user agreed. A timeout or cancellation counts as no.</returns>
        Task<bool> ConfirmAsync(string laptopName, string fingerprint, TimeSpan timeout, CancellationToken ct);
    }

    /// <summary>The default: never agrees.</summary>
    public sealed class DecliningConfirmer : IPairConfirmer
    {
        public Task<bool> ConfirmAsync(string laptopName, string fingerprint, TimeSpan timeout, CancellationToken ct) { return Task.FromResult(false); }
    }

    public enum LinkKind { Unknown, Handshake, Pairing }

    /// <summary>How one connection ended.</summary>
    public sealed class LinkResult
    {
        public LinkKind Kind { get; }
        public bool Ok { get; }
        /// <summary>Null when Ok. May be local only ("connection closed", "declined", "peer refused: ...").</summary>
        public string Reason { get; }
        public string LaptopName { get; }
        public string Address { get; }

        public LinkResult(LinkKind kind, bool ok, string reason, string laptopName, string address)
        {
            Kind = kind;
            Ok = ok;
            Reason = reason;
            LaptopName = laptopName;
            Address = address;
        }

        public override string ToString() { return Kind + (Ok ? " ok " : " failed: " + Reason) + " " + LaptopName + " " + Address; }
    }

    /// <summary>
    /// The deadline of one exchange: the first limit from the moment the connection was accepted, which
    /// <see cref="Extend"/> replaces by a longer total once the peer is authenticated. <see cref="Token"/> is cancelled
    /// by the deadline or by the outer token.
    /// </summary>
    public sealed class ExchangeClock : IDisposable
    {
        readonly System.Diagnostics.Stopwatch since;
        readonly CancellationTokenSource deadline = new CancellationTokenSource();
        readonly CancellationTokenSource linked;

        public CancellationToken Token { get { return linked.Token; } }
        public bool TimedOut { get { return deadline.IsCancellationRequested; } }

        public ExchangeClock(CancellationToken outer, System.Diagnostics.Stopwatch sinceAccepted, TimeSpan first)
        {
            since = sinceAccepted;
            linked = CancellationTokenSource.CreateLinkedTokenSource(outer, deadline.Token);
            SetTotal(first);
        }

        /// <summary>Moves the deadline to <paramref name="total"/> after the connection was accepted.</summary>
        public void Extend(TimeSpan total) { SetTotal(total); }

        void SetTotal(TimeSpan total)
        {
            var remaining = total - since.Elapsed;
            deadline.CancelAfter(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
        }

        public void Dispose()
        {
            linked.Dispose();
            deadline.Dispose();
        }
    }

    /// <summary>Runs one exchange body: turns failures into error frames, closes the connection, returns the outcome.</summary>
    public static class ExchangeRunner
    {
        public static async Task<LinkResult> RunAsync(LinkKind kind, IFrameChannel channel, ExchangeClock clock, string address,
            Func<Task<LinkResult>> body)
        {
            LinkResult result = null;
            LinkException error = null;
            bool cancelled = false;
            try
            {
                result = await body().ConfigureAwait(false);
            }
            catch (LinkException e)
            {
                error = e;
            }
            catch (OperationCanceledException)
            {
                if (clock.TimedOut) error = new LinkException("handshake timeout");
                else cancelled = true;
            }
            catch (Exception e)
            {
                error = new LinkException("internal error: " + e.GetType().Name + " " + e.Message);
            }
            if (error != null && !error.Remote && !cancelled && ShouldSend(error.Reason))
                await channel.SendErrorAsync(error.Reason).ConfigureAwait(false);
            channel.Close();
            clock.Dispose();
            if (result != null) return result;
            return new LinkResult(kind, false, cancelled ? "cancelled" : error.Reason, null, address);
        }

        /// <summary>Local-only reasons are never sent.</summary>
        static bool ShouldSend(string reason)
        {
            return reason != "connection closed" && reason != "frame too large" && !reason.StartsWith("internal error", StringComparison.Ordinal);
        }
    }
}
