using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The headset side of the paired-mode link handshake, wire v1, M1-M5, exactly as Tools/LaptopAgent/handshake.py
    /// (headset_handshake). The headset answers only a laptop whose key is pinned ("unknown laptop"), verifies the
    /// offer's signature before it calls the answerer, and calls Commit only after the laptop's M5 verified. Until M3
    /// verifies the whole handshake has 5 s; after that 30 s from the start.
    /// Stream transport (USB without a network): when M1 asks for "transport":"stream", the connection came through the
    /// USB tunnel (it arrives from the phone's loopback address, <see cref="IsUsbTunnel"/>) and the answerer is an
    /// <see cref="IStreamLinkAnswerer"/>, the mode is "paired-stream", M3 and M4 carry <see cref="StreamDescription"/>
    /// instead of SDP with empty fingerprints, and after M5 the connection is handed to the answerer instead of closed.
    /// Otherwise the mode stays "paired" (the laptop then refuses with "unsupported mode").
    /// </summary>
    public static class HandshakeHeadset
    {
        public const int Version = 1;
        public const int MaxFrames = 6;
        public const string Mode = "paired";
        public const string ModeStream = "paired-stream";
        public const string TransportStream = "stream";
        public const string StreamDescription = "spatiallauncher-stream-v1";
        public static readonly TimeSpan PreAuthTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan AuthedTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Runs M1-M5 on <paramref name="channel"/> (a ReplayChannel when M1 was already read) and closes it.
        /// <paramref name="since"/> is running from the moment the connection was accepted.
        /// </summary>
        public static Task<LinkResult> RunAsync(IFrameChannel channel, string address, Stopwatch since, ILinkIdentity identity,
            string headsetName, PairStore store, ILinkAnswerer answerer, CancellationToken ct)
        {
            var clock = new ExchangeClock(ct, since, PreAuthTimeout);
            return ExchangeRunner.RunAsync(LinkKind.Handshake, channel, clock, address,
                () => BodyAsync(channel, clock, address, identity, headsetName, store, answerer));
        }

        /// <summary>
        /// Whether a connection came through Apple's USB multiplexer: on the phone those arrive from the loopback address.
        /// Nothing on a network can reach loopback, so the unencrypted stream transport never crosses Wi-Fi.
        /// </summary>
        public static bool IsUsbTunnel(string address)
        {
            if (!System.Net.IPAddress.TryParse(address ?? "", out var ip)) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return System.Net.IPAddress.IsLoopback(ip);
        }

        /// <summary>The name the headset sends: the configured one stripped, or "headset-xxxx" when empty or invalid.</summary>
        internal static string ResolveName(string configured, string fingerprint)
        {
            if (configured == null || !NameRules.IsValid(configured)) return NameRules.DefaultName(fingerprint);
            return NameRules.NameOrDefault(configured, fingerprint);
        }

        static async Task<LinkResult> BodyAsync(IFrameChannel ch, ExchangeClock clock, string address, ILinkIdentity identity,
            string headsetName, PairStore store, ILinkAnswerer answerer)
        {
            var tok = clock.Token;
            string name = ResolveName(headsetName, identity.Fingerprint);
            byte[] spkiH = identity.Spki;

            var m1 = LinkMessages.Require(await ch.ReadFrameAsync(Timeout.InfiniteTimeSpan, tok).ConfigureAwait(false), "hello");
            byte[] na = LinkMessages.Bytes(m1, "nonce", exact: LinkMessages.NonceLength);
            byte[] spkiA = LinkMessages.PublicKey(m1);
            PairedLaptop laptop = store.FindByKey(spkiA);
            if (laptop == null) throw new LinkException("unknown laptop");

            var streamAnswerer = answerer as IStreamLinkAnswerer;
            bool stream = streamAnswerer != null && IsUsbTunnel(address)
                && LinkJson.GetString(m1, "transport") == TransportStream;
            string mode = stream ? ModeStream : Mode;

            var nh = new byte[LinkMessages.NonceLength];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nh);
            if (Transcripts.MacEquals(na, nh)) throw new LinkException("replayed nonce", auth: true);
            await ch.WriteFrameAsync(LinkMessages.Frame(Version, "hello", "nonce", LinkMessages.B64(nh),
                "spki", LinkMessages.B64(spkiH), "mode", mode, "name", name), tok).ConfigureAwait(false);

            var m3 = LinkMessages.Require(await ch.ReadFrameAsync(Timeout.InfiniteTimeSpan, tok).ConfigureAwait(false), "offer");
            string offerSdp = LinkMessages.Sdp(m3);
            byte[] sigA = LinkMessages.Bytes(m3, "sig", exact: RsaPkcs1.SignatureLength);
            if (stream && offerSdp != StreamDescription) throw new LinkException("malformed sdp");
            string fpO = stream ? "" : SdpFingerprint.Extract(offerSdp);
            byte[] tA = Transcripts.Agent(na, nh, spkiA, spkiH, mode, name, offerSdp, fpO);
            if (!RsaPkcs1.Verify(spkiA, sigA, tA)) throw new LinkException("bad signature", auth: true);
            clock.Extend(AuthedTimeout); // the laptop is authenticated: ICE gathering and M5 may take until 30 s

            var linkOffer = new LinkOffer(offerSdp, fpO, laptop.Name, spkiA, address, stream);
            bool asked = false;
            try
            {
                asked = true;
                string answerSdp, fpA;
                if (stream)
                {
                    if (!streamAnswerer.AcceptStream(linkOffer)) throw new LinkException("no answer");
                    answerSdp = StreamDescription;
                    fpA = "";
                }
                else
                {
                    LinkAnswer given = await answerer.AnswerAsync(linkOffer, tok).ConfigureAwait(false);
                    if (given == null || given.Sdp == null) throw new LinkException("no answer");
                    answerSdp = LinkMessages.CheckSdp(given.Sdp);
                    fpA = SdpFingerprint.Extract(answerSdp);
                }
                byte[] tH = Transcripts.Headset(na, nh, spkiA, spkiH, mode, name, offerSdp, answerSdp, fpO, fpA);
                await ch.WriteFrameAsync(LinkMessages.Frame(Version, "answer", "sdp", answerSdp,
                    "sig", LinkMessages.B64(identity.Sign(tH))), tok).ConfigureAwait(false);

                var m5 = LinkMessages.Require(await ch.ReadFrameAsync(Timeout.InfiniteTimeSpan, tok).ConfigureAwait(false), "done");
                byte[] sigDone = LinkMessages.Bytes(m5, "sig", exact: RsaPkcs1.SignatureLength);
                if (!RsaPkcs1.Verify(spkiA, sigDone, Transcripts.AgentDoneMessage(tH)))
                    throw new LinkException("bad signature", auth: true);

                if (stream)
                {
                    if (!(ch is IDetachableFrameChannel detachable) || !detachable.TryDetach(out var connection, out var close))
                        throw new LinkException("connection closed");
                    asked = false; // from here the answerer owns the outcome and the connection
                    streamAnswerer.CommitStream(linkOffer, connection, close);
                    return new LinkResult(LinkKind.Handshake, true, null, laptop.Name, address);
                }
                asked = false; // from here the answerer owns the outcome
                answerer.Commit(linkOffer, new LinkAnswer(answerSdp, fpA));
                return new LinkResult(LinkKind.Handshake, true, null, laptop.Name, address);
            }
            catch (Exception e)
            {
                if (asked) AbortQuietly(answerer, linkOffer, clock, e);
                throw;
            }
        }

        static void AbortQuietly(ILinkAnswerer answerer, LinkOffer offer, ExchangeClock clock, Exception e)
        {
            string reason;
            if (e is LinkException le) reason = le.Reason;
            else if (e is OperationCanceledException) reason = clock.TimedOut ? "handshake timeout" : "cancelled";
            else reason = "internal error";
            try { answerer.Abort(offer, reason); }
            catch (Exception) { }
        }
    }
}
