using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The headset side of first-time pairing, wire v2, P1-P6, exactly as Tools/LaptopAgent/pair_protocol.py
    /// (headset_pair). P3's signature is checked before the PBKDF2 runs (a bad signature costs no strike). The
    /// derivation and the strike-or-consume step run as one task on the thread pool that the exchange's token does not
    /// cancel, so a closed socket or a timeout during the derivation still counts the proof. The laptop's key is pinned
    /// only after P6 verified. Until P3's mac verifies the exchange has 5 s; after that 75 s from the start.
    /// </summary>
    public static class PairProtocolHeadset
    {
        public const int Version = 2;
        public const int MaxFrames = 8;
        public static readonly TimeSpan PreAuthTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(75);
        public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Runs P1-P6 on <paramref name="channel"/> (a ReplayChannel when P1 was already read) and closes it.</summary>
        public static Task<LinkResult> RunAsync(IFrameChannel channel, string address, Stopwatch since, ILinkIdentity identity,
            string headsetName, PairStore store, PairCodeAuthority authority, IPbkdf2 pbkdf2, IPairConfirmer confirmer,
            CancellationToken ct, int iterations = Transcripts.PairIterations)
        {
            var clock = new ExchangeClock(ct, since, PreAuthTimeout);
            return ExchangeRunner.RunAsync(LinkKind.Pairing, channel, clock, address,
                () => BodyAsync(channel, clock, address, identity, headsetName, store, authority, pbkdf2, confirmer, iterations));
        }

        static async Task<LinkResult> BodyAsync(IFrameChannel ch, ExchangeClock clock, string address, ILinkIdentity identity,
            string headsetName, PairStore store, PairCodeAuthority authority, IPbkdf2 pbkdf2, IPairConfirmer confirmer, int iterations)
        {
            var tok = clock.Token;
            byte[] spkiH = identity.Spki;
            string nameH = HandshakeHeadset.ResolveName(headsetName, identity.Fingerprint);

            var p1 = LinkMessages.Require(await ch.ReadFrameAsync(Timeout.InfiniteTimeSpan, tok).ConfigureAwait(false), "pair");
            byte[] na = LinkMessages.Bytes(p1, "nonce", exact: LinkMessages.NonceLength);
            byte[] spkiA = LinkMessages.PublicKey(p1);
            string nameA = NameRules.Validate(LinkJson.GetString(p1, "name"));
            if (NameRules.Strip(nameA).Length == 0) throw new LinkException("bad name"); // before "not pairing": no strike
            if (authority.Active == null) throw new LinkException("not pairing");
            if (!store.CheckCanAdd(NameRules.Strip(nameA), spkiA)) throw new LinkException("name taken"); // no strike, code untouched

            var nh = new byte[LinkMessages.NonceLength];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nh);
            await ch.WriteFrameAsync(LinkMessages.Frame(Version, "pair-hello", "nonce", LinkMessages.B64(nh),
                "spki", LinkMessages.B64(spkiH), "name", nameH), tok).ConfigureAwait(false);
            byte[] tp = Transcripts.Pair(na, nh, spkiA, spkiH, nameA, nameH);

            var p3 = LinkMessages.Require(await ch.ReadFrameAsync(Timeout.InfiniteTimeSpan, tok).ConfigureAwait(false), "pair-proof");
            byte[] sig = LinkMessages.Bytes(p3, "sig", exact: RsaPkcs1.SignatureLength);
            byte[] mac = LinkMessages.Bytes(p3, "mac", exact: 32);
            IssuedPairCode issued = authority.Active;
            if (issued == null) throw new LinkException("not pairing");
            if (!RsaPkcs1.Verify(spkiA, sig, Transcripts.PairAgentSigMessage(tp)))
                throw new LinkException("bad signature", auth: true); // cheap, and before PBKDF2: no strike

            // Not tied to the exchange token: once the signature is good the proof is counted whatever happens to the socket.
            Task<Tuple<PairProofOutcome, byte[]>> proof = Task.Run(() =>
            {
                Transcripts.DeriveKeys(pbkdf2, issued.Code, na, nh, iterations, out _, out byte[] kAgent, out byte[] kHeadset);
                bool macOk = Transcripts.MacEquals(mac, Transcripts.AgentMac(kAgent, tp));
                return Tuple.Create(authority.TryApplyProof(issued.Id, macOk), kHeadset);
            });
            _ = proof.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            var cancelled = new TaskCompletionSource<bool>();
            using (tok.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(proof, cancelled.Task).ConfigureAwait(false) != proof)
                    throw new OperationCanceledException(tok);
            }
            var applied = await proof.ConfigureAwait(false);
            switch (applied.Item1)
            {
                case PairProofOutcome.Accepted:
                    break;
                case PairProofOutcome.NotPairing:
                    throw new LinkException("not pairing");
                default:
                    throw new LinkException("wrong code", auth: true);
            }
            clock.Extend(TotalTimeout);

            await ch.WriteFrameAsync(LinkMessages.Frame(Version, "pair-proof",
                "sig", LinkMessages.B64(identity.Sign(Transcripts.PairHeadsetSigMessage(tp))),
                "mac", LinkMessages.B64(Transcripts.HeadsetMac(applied.Item2, tp))), tok).ConfigureAwait(false);

            bool ok = await AskAsync(confirmer, nameA, spkiA, tok).ConfigureAwait(false);
            await ch.WriteFrameAsync(LinkMessages.Frame(Version, "pair-confirm", "ok", ok,
                "sig", LinkMessages.B64(identity.Sign(Transcripts.PairConfirmMessage(ok, tp)))), tok).ConfigureAwait(false);
            if (!ok) throw new LinkException("declined", remote: true); // the laptop has been told already

            var p6 = LinkMessages.Require(await ch.ReadFrameAsync(Timeout.InfiniteTimeSpan, tok).ConfigureAwait(false), "pair-done");
            byte[] sigDone = LinkMessages.Bytes(p6, "sig", exact: RsaPkcs1.SignatureLength);
            if (!RsaPkcs1.Verify(spkiA, sigDone, Transcripts.PairPinnedMessage(tp)))
                throw new LinkException("bad signature", auth: true);
            if (store.Add(NameRules.Strip(nameA), spkiA, out _) == PairAddResult.KeyPairedUnderOtherName
                || store.FindByKey(spkiA) == null)
                throw new LinkException("name taken");
            return new LinkResult(LinkKind.Pairing, true, null, NameRules.Strip(nameA), address);
        }

        /// <summary>The user's answer within 60 s; a timeout or a failing confirmer is "no". Only the exchange's own cancellation propagates.</summary>
        static async Task<bool> AskAsync(IPairConfirmer confirmer, string nameA, byte[] spkiA, CancellationToken tok)
        {
            string fp = Spki.Fingerprint(spkiA);
            string shown = fp.Substring(0, 4) + "-" + fp.Substring(4, 4) + "-" + fp.Substring(8, 4) + "-" + fp.Substring(12, 4);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(tok))
            {
                cts.CancelAfter(ConfirmTimeout);
                Task<bool> ask;
                try { ask = confirmer.ConfirmAsync(NameRules.Strip(nameA), shown, ConfirmTimeout, cts.Token); }
                catch (Exception) { return false; }
                var cancelled = new TaskCompletionSource<bool>();
                using (cts.Token.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(ask, cancelled.Task).ConfigureAwait(false) != ask)
                    {
                        _ = ask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        tok.ThrowIfCancellationRequested();
                        return false;
                    }
                }
                try { return await ask.ConfigureAwait(false); }
                catch (Exception) { tok.ThrowIfCancellationRequested(); return false; }
            }
        }
    }
}
