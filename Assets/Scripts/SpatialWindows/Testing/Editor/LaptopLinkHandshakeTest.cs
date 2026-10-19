using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SpatialLauncher.LaptopLink;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test (no Play mode) for the headset's side of the laptop link, against a fake laptop written here from
    /// the same Core pieces (P1-P6 pairing and M1-M5 handshake) over loopback TCP, through <see cref="LinkConnectionHandler"/>:
    /// pairing with a right and wrong code (strikes, the 4th is "not pairing", cancel and close during the key derivation
    /// still count, a bad signature costs nothing and skips PBKDF2, declined, name taken), the paired handshake (unknown
    /// laptop, tampered transcript, Commit only after M5, Abort on every failure with the offer), oversize length headers,
    /// the rate limit, one exchange per IP and the gate released on every path. PBKDF2 is swapped for a 1000-iteration
    /// spy. Results use a [LaptopLinkHandshakeTest] prefix.
    /// </summary>
    static class LaptopLinkHandshakeTest
    {
        [MenuItem("Tools/Spatial Windows/Test Laptop Link Handshake")]
        static void Run() => EditorTestRunner.Run("LaptopLinkHandshakeTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Laptop Link Handshake", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        const string Addr = "10.0.0.1";
        static readonly string Hex32 = string.Join(":", Enumerable.Repeat("AB", 32));
        static readonly string Hex32Other = string.Join(":", Enumerable.Repeat("CD", 32));
        static readonly string OfferSdp = "v=0\r\na=fingerprint:sha-256 " + Hex32 + "\r\nm=video 9 UDP\r\n";
        static readonly string AnswerSdp = "v=0\r\na=fingerprint:sha-256 " + Hex32Other + "\r\nm=video 9 UDP\r\n";

        // --- Fakes ---------------------------------------------------------------------------------------------------------

        /// <summary>PBKDF2 at 1000 iterations that counts calls, signals when it starts and can be held.</summary>
        sealed class SpyKdf : IPbkdf2
        {
            public int Calls;
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public ManualResetEventSlim Hold;
            public byte[] Derive(byte[] password, byte[] salt, int iterations, int length)
            {
                Interlocked.Increment(ref Calls);
                Entered.Set();
                Hold?.Wait(10000);
                return new Pbkdf2Sha256().Derive(password, salt, 1000, length);
            }
        }

        sealed class FakeConfirmer : IPairConfirmer
        {
            public bool Answer = true;
            public readonly List<string> Names = new List<string>();
            public Task<bool> ConfirmAsync(string laptopName, string fingerprint, TimeSpan timeout, CancellationToken ct)
            {
                lock (Names) Names.Add(laptopName);
                return Task.FromResult(Answer);
            }
        }

        sealed class FakeAnswerer : ILinkAnswerer
        {
            public readonly List<LinkOffer> Answered = new List<LinkOffer>();
            public readonly List<KeyValuePair<LinkOffer, LinkAnswer>> Commits = new List<KeyValuePair<LinkOffer, LinkAnswer>>();
            public readonly List<KeyValuePair<LinkOffer, string>> Aborts = new List<KeyValuePair<LinkOffer, string>>();
            public Func<LinkOffer, CancellationToken, Task<LinkAnswer>> Behaviour;
            public int CommitCount { get { lock (Commits) return Commits.Count; } }

            public Task<LinkAnswer> AnswerAsync(LinkOffer offer, CancellationToken ct)
            {
                lock (Answered) Answered.Add(offer);
                return Behaviour != null ? Behaviour(offer, ct) : Task.FromResult(new LinkAnswer(AnswerSdp, Hex32Other));
            }

            public void Commit(LinkOffer offer, LinkAnswer answer) { lock (Commits) Commits.Add(new KeyValuePair<LinkOffer, LinkAnswer>(offer, answer)); }
            public void Abort(LinkOffer offer, string reason) { lock (Aborts) Aborts.Add(new KeyValuePair<LinkOffer, string>(offer, reason)); }
        }

        sealed class Conn
        {
            public Task<LinkResult> Headset;
            public Task Agent;
            public Exception AgentError;
            public LinkResult Result;
            public string AgentReason => (AgentError as LinkException)?.Reason;
        }

        /// <summary>One headset: its pair store, code authority, handler and fakes. Connections come in over loopback TCP.</summary>
        sealed class Rig
        {
            public readonly PairStore Store;
            public readonly PairCodeAuthority Auth = new PairCodeAuthority();
            public readonly SpyKdf Kdf = new SpyKdf();
            public readonly FakeConfirmer Confirmer = new FakeConfirmer();
            public readonly FakeAnswerer Answerer = new FakeAnswerer();
            public readonly HeadsetGate Gate;
            readonly LinkConnectionHandler handler;

            public Rig(string root, ILinkIdentity headset, HeadsetGate gate = null)
            {
                Store = new PairStore(Path.Combine(root, "store-" + Guid.NewGuid().ToString("N")));
                Gate = gate ?? new HeadsetGate(null, 1000, 60);
                handler = new LinkConnectionHandler(headset, Store, Auth, Kdf, Confirmer, Answerer, Gate, null, () => "Phone");
            }

            public Conn Connect(string address, int version, Func<NetworkFrameChannel, Stream, Task> agent, CancellationToken ct = default(CancellationToken))
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var client = new TcpClient();
                var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = listener.AcceptTcpClient();
                connecting.Wait(10000);
                listener.Stop();
                var conn = new Conn();
                conn.Headset = Task.Run(() => handler.HandleAsync(server.GetStream(), () => server.Close(), address, ct));
                var stream = client.GetStream();
                var channel = new NetworkFrameChannel(stream, () => client.Close(), version, 8);
                conn.Agent = Task.Run(async () =>
                {
                    try { await agent(channel, stream); }
                    catch (Exception e) { conn.AgentError = e; }
                    finally { channel.Close(); }
                });
                return conn;
            }

            /// <summary>Waits for both ends; fills <see cref="Conn.Result"/>.</summary>
            public Conn Finish(Conn c)
            {
                try
                {
                    Task.WaitAll(new Task[] { c.Headset, c.Agent }, 20000);
                    c.Result = c.Headset.IsCompleted ? c.Headset.Result : null;
                }
                catch (Exception e) { Debug.LogError("[LaptopLinkHandshakeTest] " + e); }
                return c;
            }

            public Conn Run(string address, int version, Func<NetworkFrameChannel, Stream, Task> agent, CancellationToken ct = default(CancellationToken)) =>
                Finish(Connect(address, version, agent, ct));
        }

        // --- The fake laptop ------------------------------------------------------------------------------------------------

        sealed class PairAgent
        {
            public ILinkIdentity Id, Stranger;
            public string Name = "Laptop", Code;
            public bool BadSig, StopAfterProof;
            public Action AfterProof;
            public bool HeadsetVerified, ConfirmOk, ConfirmSigOk, PinnedHeadset;
        }

        sealed class HandshakeAgent
        {
            public ILinkIdentity Id, Stranger;
            public bool BadSig, BadM5, CloseBeforeM5;
            public string SignMode, SignName, SignSdp;
            public bool AnswerVerified;
            public int CommitsBeforeM5 = -1;
            public FakeAnswerer Answerer;
        }

        static byte[] Random32() { var b = new byte[32]; using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b); return b; }
        static Task<Dictionary<string, object>> Read(NetworkFrameChannel ch) => ch.ReadFrameAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        static string B64(byte[] b) => LinkMessages.B64(b);
        static string Wrong(string code) => (code[0] == '0' ? "1" : "0") + code.Substring(1);

        static async Task RunPairAgent(NetworkFrameChannel ch, PairAgent o)
        {
            byte[] na = Random32();
            await ch.WriteFrameAsync(LinkMessages.Frame(2, "pair", "nonce", B64(na), "spki", B64(o.Id.Spki), "name", o.Name));
            var p2 = LinkMessages.Require(await Read(ch), "pair-hello");
            byte[] nh = LinkMessages.Bytes(p2, "nonce", exact: 32);
            byte[] spkiH = LinkMessages.PublicKey(p2);
            string nameH = LinkJson.GetString(p2, "name");
            byte[] tp = Transcripts.Pair(na, nh, o.Id.Spki, spkiH, o.Name, nameH);
            Transcripts.DeriveKeys(new SpyKdf(), o.Code, na, nh, Transcripts.PairIterations, out _, out var kAgent, out var kHeadset);
            var signer = o.BadSig ? o.Stranger : o.Id;
            await ch.WriteFrameAsync(LinkMessages.Frame(2, "pair-proof", "sig", B64(signer.Sign(Transcripts.PairAgentSigMessage(tp))),
                "mac", B64(Transcripts.AgentMac(kAgent, tp))));
            o.AfterProof?.Invoke();
            if (o.StopAfterProof) return;
            var p4 = LinkMessages.Require(await Read(ch), "pair-proof");
            o.HeadsetVerified = RsaPkcs1.Verify(spkiH, LinkMessages.Bytes(p4, "sig", exact: 256), Transcripts.PairHeadsetSigMessage(tp))
                                && Transcripts.MacEquals(LinkMessages.Bytes(p4, "mac", exact: 32), Transcripts.HeadsetMac(kHeadset, tp));
            var p5 = LinkMessages.Require(await Read(ch), "pair-confirm");
            o.ConfirmOk = p5.TryGetValue("ok", out var ok) && ok is bool b && b;
            o.ConfirmSigOk = RsaPkcs1.Verify(spkiH, LinkMessages.Bytes(p5, "sig", exact: 256), Transcripts.PairConfirmMessage(o.ConfirmOk, tp));
            if (!o.ConfirmOk) return;
            await ch.WriteFrameAsync(LinkMessages.Frame(2, "pair-done", "sig", B64(o.Id.Sign(Transcripts.PairPinnedMessage(tp)))));
            o.PinnedHeadset = o.HeadsetVerified && o.ConfirmSigOk;
        }

        static async Task RunHandshakeAgent(NetworkFrameChannel ch, HandshakeAgent o)
        {
            byte[] na = Random32();
            await ch.WriteFrameAsync(LinkMessages.Frame(1, "hello", "nonce", B64(na), "spki", B64(o.Id.Spki)));
            var m2 = LinkMessages.Require(await Read(ch), "hello");
            byte[] nh = LinkMessages.Bytes(m2, "nonce", exact: 32);
            byte[] spkiH = LinkMessages.PublicKey(m2);
            string nameH = LinkJson.GetString(m2, "name");
            string fpO = SdpFingerprint.Extract(OfferSdp);
            byte[] tA = Transcripts.Agent(na, nh, o.Id.Spki, spkiH, o.SignMode ?? "paired", o.SignName ?? nameH, o.SignSdp ?? OfferSdp, fpO);
            await ch.WriteFrameAsync(LinkMessages.Frame(1, "offer", "sdp", OfferSdp, "sig", B64((o.BadSig ? o.Stranger : o.Id).Sign(tA))));
            var m4 = LinkMessages.Require(await Read(ch), "answer");
            string answerSdp = LinkMessages.Sdp(m4);
            byte[] tH = Transcripts.Headset(na, nh, o.Id.Spki, spkiH, "paired", nameH, OfferSdp, answerSdp, fpO, SdpFingerprint.Extract(answerSdp));
            o.AnswerVerified = RsaPkcs1.Verify(spkiH, LinkMessages.Bytes(m4, "sig", exact: 256), tH);
            o.CommitsBeforeM5 = o.Answerer.CommitCount;
            if (o.CloseBeforeM5) return;
            byte[] done = o.Id.Sign(Transcripts.AgentDoneMessage(tH));
            if (o.BadM5) done[10] ^= 1;
            await ch.WriteFrameAsync(LinkMessages.Frame(1, "done", "sig", B64(done)));
            try { await Read(ch); } catch (LinkException) { } // the headset closes when it is done
        }

        // --- The test --------------------------------------------------------------------------------------------------------

        static IEnumerator Steps()
        {
            string root = Path.Combine(Path.GetTempPath(), "SLHandshake-" + Guid.NewGuid().ToString("N"));
            EditorFileIdentity headset = null, agent = null, stranger = null;
            try
            {
                headset = new EditorFileIdentity(Path.Combine(root, "headset"));
                agent = new EditorFileIdentity(Path.Combine(root, "agent"));
                stranger = new EditorFileIdentity(Path.Combine(root, "stranger"));
                Pairing(root, headset, agent, stranger);
                Handshake(root, headset, agent, stranger);
                Limits(root, headset);
            }
            finally
            {
                headset?.Dispose();
                agent?.Dispose();
                stranger?.Dispose();
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
            yield break;
        }

        static void GateFree(Rig rig, string address, string after)
        {
            bool free = rig.Gate.TryEnter(address);
            if (free) rig.Gate.Leave(address);
            Check(free, "the per-IP gate is released after " + after);
        }

        static bool WaitFor(Func<bool> condition, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (!condition())
            {
                if (DateTime.UtcNow > until) return false;
                Thread.Sleep(10);
            }
            return true;
        }

        static PairAgent NewPair(ILinkIdentity agent, ILinkIdentity stranger, string code) => new PairAgent { Id = agent, Stranger = stranger, Code = code };

        static void Pairing(string root, ILinkIdentity headset, ILinkIdentity agent, ILinkIdentity stranger)
        {
            // A right code pins both sides.
            var rig = new Rig(root, headset);
            var code = rig.Auth.Issue();
            var o = NewPair(agent, stranger, code.Code);
            var c = rig.Run(Addr, 2, (ch, _) => RunPairAgent(ch, o));
            var pinned = rig.Store.FindByKey(agent.Spki);
            Check(c.Result != null && c.Result.Ok && c.Result.Kind == LinkKind.Pairing && c.Result.LaptopName == "Laptop" && c.AgentError == null,
                $"the right code pairs ({c.Result}; agent: {c.AgentError?.Message})");
            Check(pinned != null && pinned.Name == "Laptop" && o.PinnedHeadset && o.ConfirmOk && rig.Confirmer.Names.SequenceEqual(new[] { "Laptop" }),
                "both sides pin the other's key: the headset stores the laptop, the laptop verified the headset's proof and confirmation");
            Check(rig.Auth.Active == null && rig.Auth.Strikes == 0, "the code is spent and cost no strike");
            GateFree(rig, Addr, "a successful pairing");

            // Wrong codes are strikes; three kill the code; a fourth is "not pairing".
            rig = new Rig(root, headset);
            code = rig.Auth.Issue();
            var reasons = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var w = NewPair(agent, stranger, Wrong(code.Code));
                c = rig.Run(Addr, 2, (ch, _) => RunPairAgent(ch, w));
                reasons.Add(c.Result?.Reason + "/" + c.AgentReason + "/" + rig.Auth.Strikes);
            }
            Check(reasons.SequenceEqual(new[] { "wrong code/peer refused: wrong code/1", "wrong code/peer refused: wrong code/2", "wrong code/peer refused: wrong code/3" }) &&
                  rig.Auth.Active == null && rig.Store.List().Count == 0 && rig.Confirmer.Names.Count == 0, $"a wrong code is a strike, three kill the code ({string.Join("; ", reasons)})");
            var fourth = NewPair(agent, stranger, code.Code);
            c = rig.Run(Addr, 2, (ch, _) => RunPairAgent(ch, fourth));
            Check(c.Result != null && c.Result.Reason == "not pairing" && c.AgentReason == "peer refused: not pairing" && rig.Kdf.Calls == 3 && rig.Store.List().Count == 0,
                $"a 4th attempt, even with the right code, gets \"not pairing\" without a derivation ({c.Result?.Reason}, {rig.Kdf.Calls} derivations)");
            GateFree(rig, Addr, "wrong codes");

            // A cancelled exchange during the derivation still counts the strike.
            rig = new Rig(root, headset);
            code = rig.Auth.Issue();
            rig.Kdf.Hold = new ManualResetEventSlim(false);
            var cts = new CancellationTokenSource();
            var wrong = NewPair(agent, stranger, Wrong(code.Code));
            c = rig.Connect(Addr, 2, (ch, _) => RunPairAgent(ch, wrong), cts.Token);
            bool entered = rig.Kdf.Entered.Wait(10000);
            cts.Cancel();
            rig.Finish(c);
            bool noStrikeYet = rig.Auth.Strikes == 0;
            rig.Kdf.Hold.Set();
            Check(entered && c.Result != null && c.Result.Reason == "cancelled" && noStrikeYet && WaitFor(() => rig.Auth.Strikes == 1, 5000) && rig.Auth.Active != null,
                "a cancelled exchange during the key derivation still counts the wrong proof once it finishes");
            GateFree(rig, Addr, "a cancelled pairing");

            // A closed channel during the derivation: the wrong proof counts; a right one still spends the code and pins nothing.
            foreach (bool right in new[] { false, true })
            {
                rig = new Rig(root, headset);
                code = rig.Auth.Issue();
                rig.Kdf.Hold = new ManualResetEventSlim(false);
                var gone = NewPair(agent, stranger, right ? code.Code : Wrong(code.Code));
                gone.StopAfterProof = true;
                gone.AfterProof = () => rig.Kdf.Entered.Wait(10000);
                c = rig.Connect(Addr, 2, (ch, _) => RunPairAgent(ch, gone));
                Thread.Sleep(300);
                rig.Kdf.Hold.Set();
                rig.Finish(c);
                Check(c.Result != null && !c.Result.Ok && rig.Store.List().Count == 0 && (right ? rig.Auth.Active == null && rig.Auth.Strikes == 0 : rig.Auth.Strikes == 1 && rig.Auth.Active != null),
                    $"the laptop closing during the derivation: a {(right ? "right code spends the code, pins nothing" : "wrong code counts a strike")} ({c.Result?.Reason})");
                GateFree(rig, Addr, "a closed pairing");
            }

            // A bad P3 signature costs no strike and never reaches PBKDF2.
            rig = new Rig(root, headset);
            code = rig.Auth.Issue();
            var forged = NewPair(agent, stranger, code.Code);
            forged.BadSig = true;
            c = rig.Run(Addr, 2, (ch, _) => RunPairAgent(ch, forged));
            Check(c.Result != null && c.Result.Reason == "bad signature" && c.AgentReason == "peer refused: bad signature" && rig.Kdf.Calls == 0 && rig.Auth.Strikes == 0 && rig.Auth.Active != null,
                $"a bad P3 signature is refused with no PBKDF2 and no strike ({c.Result?.Reason}, {rig.Kdf.Calls} derivations)");
            GateFree(rig, Addr, "a bad signature");

            // The user declines: nothing is pinned.
            rig = new Rig(root, headset);
            code = rig.Auth.Issue();
            rig.Confirmer.Answer = false;
            var declined = NewPair(agent, stranger, code.Code);
            c = rig.Run(Addr, 2, (ch, _) => RunPairAgent(ch, declined));
            Check(c.Result != null && !c.Result.Ok && c.Result.Reason == "declined" && rig.Store.List().Count == 0 && declined.HeadsetVerified && !declined.ConfirmOk && declined.ConfirmSigOk && !declined.PinnedHeadset,
                $"a declined confirmation pins nothing, and the laptop is told with a signed \"no\" ({c.Result?.Reason})");
            GateFree(rig, Addr, "a declined pairing");

            // A name that is taken (or blank) is refused before P2: no strike, the code stays.
            rig = new Rig(root, headset);
            code = rig.Auth.Issue();
            rig.Store.Add("Laptop", stranger.Spki, out _);
            var taken = NewPair(agent, stranger, code.Code);
            c = rig.Run(Addr, 2, (ch, _) => RunPairAgent(ch, taken));
            bool takenOk = c.Result != null && c.Result.Reason == "name taken" && c.AgentReason == "peer refused: name taken" && rig.Auth.Strikes == 0 && rig.Auth.Active != null && rig.Kdf.Calls == 0;
            var blank = NewPair(agent, stranger, code.Code);
            blank.Name = " ";
            c = rig.Run(Addr, 2, (ch, _) => RunPairAgent(ch, blank));
            Check(takenOk && c.Result != null && c.Result.Reason == "bad name" && rig.Auth.Strikes == 0 && rig.Auth.Active != null && rig.Store.List().Count == 1,
                "a taken or blank name is refused before any key derivation, with no strike");
            GateFree(rig, Addr, "a refused name");
        }

        static void Handshake(string root, ILinkIdentity headset, ILinkIdentity agent, ILinkIdentity stranger)
        {
            // A pinned laptop is answered; Commit comes only after M5.
            var rig = new Rig(root, headset);
            rig.Store.Add("Laptop", agent.Spki, out _);
            var o = new HandshakeAgent { Id = agent, Stranger = stranger, Answerer = rig.Answerer };
            var c = rig.Run(Addr, 1, (ch, _) => RunHandshakeAgent(ch, o));
            var offer = rig.Answerer.Answered.FirstOrDefault();
            Check(c.Result != null && c.Result.Ok && c.Result.Kind == LinkKind.Handshake && c.Result.LaptopName == "Laptop" && c.AgentError == null && o.AnswerVerified,
                $"a pinned laptop completes M1-M5 and the laptop verifies the answer ({c.Result}; agent: {c.AgentError?.Message})");
            Check(rig.Answerer.Answered.Count == 1 && offer.Sdp == OfferSdp && offer.FpO == Hex32 && offer.LaptopName == "Laptop" && offer.LaptopSpki.SequenceEqual(agent.Spki) && offer.Address == Addr,
                "the answerer gets the verified offer: SDP, fingerprint, the pinned name and key, and the source address");
            Check(o.CommitsBeforeM5 == 0 && rig.Answerer.CommitCount == 1 && ReferenceEquals(rig.Answerer.Commits[0].Key, offer) && rig.Answerer.Commits[0].Value.Sdp == AnswerSdp && rig.Answerer.Aborts.Count == 0,
                "Commit is called once, with the same offer, and only after the laptop's M5 (none before it was sent); no Abort");
            GateFree(rig, Addr, "a handshake");

            // An unpinned laptop.
            rig = new Rig(root, headset);
            rig.Store.Add("Someone", stranger.Spki, out _);
            o = new HandshakeAgent { Id = agent, Stranger = stranger, Answerer = rig.Answerer };
            c = rig.Run(Addr, 1, (ch, _) => RunHandshakeAgent(ch, o));
            Check(c.Result != null && c.Result.Reason == "unknown laptop" && c.AgentReason == "peer refused: unknown laptop" && rig.Answerer.Answered.Count == 0,
                "a laptop whose key is not pinned gets \"unknown laptop\" and the answerer is never called");
            GateFree(rig, Addr, "an unknown laptop");

            // A tampered field of the signed transcript, or a signature by another key.
            var tampers = new Dictionary<string, Action<HandshakeAgent>>
            {
                { "mode", t => t.SignMode = "other" }, { "name", t => t.SignName = "evil" },
                { "offer SDP", t => t.SignSdp = OfferSdp + "a=x\r\n" }, { "signing key", t => t.BadSig = true },
            };
            var results = new List<string>();
            foreach (var tamper in tampers)
            {
                rig = new Rig(root, headset);
                rig.Store.Add("Laptop", agent.Spki, out _);
                o = new HandshakeAgent { Id = agent, Stranger = stranger, Answerer = rig.Answerer };
                tamper.Value(o);
                c = rig.Run(Addr, 1, (ch, _) => RunHandshakeAgent(ch, o));
                bool refused = c.Result != null && c.Result.Reason == "bad signature" && rig.Answerer.Answered.Count == 0 && rig.Answerer.Aborts.Count == 0 && rig.Answerer.CommitCount == 0;
                if (!refused) results.Add(tamper.Key);
            }
            Check(results.Count == 0, "a tampered mode, name or offer SDP, or another signing key, is \"bad signature\" before the answerer is called" + (results.Count > 0 ? " (failed: " + string.Join(", ", results) + ")" : ""));

            // Abort on every failure after AnswerAsync, with the offer that was answered, and never a Commit.
            var cases = new List<KeyValuePair<string, KeyValuePair<Action<Rig, HandshakeAgent>, string>>>();
            Action<string, string, Action<Rig, HandshakeAgent>> add = (name, reason, setup) =>
                cases.Add(new KeyValuePair<string, KeyValuePair<Action<Rig, HandshakeAgent>, string>>(name, new KeyValuePair<Action<Rig, HandshakeAgent>, string>(setup, reason)));
            add("no answer", "no answer", (r, a) => r.Answerer.Behaviour = (offerIn, ct) => Task.FromResult<LinkAnswer>(null));
            add("answerer throws", "internal error", (r, a) => r.Answerer.Behaviour = (offerIn, ct) => { throw new InvalidOperationException("boom"); });
            add("answer without fingerprint", "sdp needs exactly one fingerprint", (r, a) => r.Answerer.Behaviour = (offerIn, ct) => Task.FromResult(new LinkAnswer("v=0\r\n", "x")));
            add("bad M5", "bad signature", (r, a) => a.BadM5 = true);
            add("closed before M5", "connection closed", (r, a) => a.CloseBeforeM5 = true);
            foreach (var test in cases)
            {
                rig = new Rig(root, headset);
                rig.Store.Add("Laptop", agent.Spki, out _);
                o = new HandshakeAgent { Id = agent, Stranger = stranger, Answerer = rig.Answerer };
                test.Value.Key(rig, o);
                c = rig.Run(Addr, 1, (ch, _) => RunHandshakeAgent(ch, o));
                bool aborted = rig.Answerer.Answered.Count == 1 && rig.Answerer.Aborts.Count == 1 && ReferenceEquals(rig.Answerer.Aborts[0].Key, rig.Answerer.Answered[0]) &&
                               rig.Answerer.Aborts[0].Value == test.Value.Value && rig.Answerer.CommitCount == 0 && c.Result != null && !c.Result.Ok;
                Check(aborted, $"{test.Key}: Abort(\"{test.Value.Value}\") with the answered offer, never Commit (got {string.Join(",", rig.Answerer.Aborts.Select(a => a.Value))}; {c.Result?.Reason})");
                GateFree(rig, Addr, test.Key);
            }
            rig = new Rig(root, headset);
            rig.Store.Add("Laptop", agent.Spki, out _);
            var cancelled = new CancellationTokenSource();
            rig.Answerer.Behaviour = async (offerIn, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
            o = new HandshakeAgent { Id = agent, Stranger = stranger, Answerer = rig.Answerer };
            c = rig.Connect(Addr, 1, (ch, _) => RunHandshakeAgent(ch, o), cancelled.Token);
            WaitFor(() => rig.Answerer.Answered.Count == 1, 10000);
            cancelled.Cancel();
            rig.Finish(c);
            Check(c.Result != null && c.Result.Reason == "cancelled" && rig.Answerer.Aborts.Count == 1 && rig.Answerer.Aborts[0].Value == "cancelled" &&
                  ReferenceEquals(rig.Answerer.Aborts[0].Key, rig.Answerer.Answered[0]) && rig.Answerer.CommitCount == 0, "a cancelled exchange while the answerer works is Abort(\"cancelled\") with the offer");
            GateFree(rig, Addr, "a cancelled handshake");
        }

        static void Limits(string root, ILinkIdentity headset)
        {
            // An oversized length header is refused before anything is allocated or read.
            var reasons = new List<string>();
            foreach (var header in new[] { new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, new byte[] { 0, 1, 0, 1 }, new byte[] { 0, 0, 0, 0 } })
            {
                var channel = new NetworkFrameChannel(new MemoryStream(header), null, 0, 8);
                try { channel.ReadFrameAsync(TimeSpan.FromSeconds(5), CancellationToken.None).Wait(10000); reasons.Add("no error"); }
                catch (AggregateException e) { reasons.Add((e.InnerException as LinkException)?.Reason ?? "other"); }
            }
            Check(reasons.All(r => r == "bad frame length"), $"a length header of 0, 64 KiB + 1 or up to 4 GiB is \"bad frame length\" (not \"connection closed\") ({string.Join(", ", reasons)})");
            var rig = new Rig(root, headset);
            var c = rig.Run(Addr, 1, async (ch, raw) =>
            {
                raw.Write(new byte[] { 0x40, 0, 0, 0 }, 0, 4);
                raw.Flush();
                await Read(ch);
            });
            Check(c.Result != null && c.Result.Reason == "bad frame length" && c.AgentReason == "peer refused: bad frame length", "the listener refuses a 1 GiB length header and tells the peer");
            GateFree(rig, Addr, "an oversized header");

            // The rate limit: 5 attempts per minute per address, then dropped without a reply.
            double now = 0;
            rig = new Rig(root, headset, new HeadsetGate(() => now, 5, 60));
            Func<NetworkFrameChannel, Stream, Task> junk = async (ch, _) =>
            {
                await ch.WriteFrameAsync(LinkMessages.Frame(1, "nope"));
                await Read(ch);
            };
            var outcomes = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                c = rig.Run("10.9.9.9", 1, junk);
                outcomes.Add(c.Result == null ? "dropped(" + c.AgentReason + ")" : c.Result.Reason);
            }
            c = rig.Run("10.9.9.8", 1, junk);
            now = 13;
            var later = rig.Run("10.9.9.9", 1, junk);
            Check(outcomes.Take(5).All(r => r == "unexpected message") && outcomes[5] == "dropped(connection closed)" && c.Result != null && later.Result != null,
                $"the 6th attempt from one address in a minute is dropped without a reply; another address and a later minute are fine ({string.Join(", ", outcomes)})");

            // One exchange per IP at a time, and Leave frees it.
            var gate = new HeadsetGate(() => 0, 100, 60);
            bool first = gate.TryEnter("1.1.1.1"), second = gate.TryEnter("1.1.1.1"), other = gate.TryEnter("2.2.2.2");
            gate.Leave("1.1.1.1");
            Check(first && !second && other && gate.TryEnter("1.1.1.1"), "the gate lets one exchange per IP in at a time; Leave frees the address");
            double t = 0;
            var limited = new HeadsetGate(() => t, 5, 60);
            int allowed = 0;
            for (int i = 0; i < 8; i++)
                if (limited.TryEnter("3.3.3.3")) { allowed++; limited.Leave("3.3.3.3"); }
            t = 12.5;
            bool refilled = limited.TryEnter("3.3.3.3");
            Check(allowed == 5 && refilled, $"the gate allows 5 attempts per minute per IP, then one more every 12 s ({allowed})");
        }
    }
}
