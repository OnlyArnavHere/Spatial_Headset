#if SPATIAL_LAUNCHER_WEBRTC
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpatialLauncher.LaptopLink;
using Unity.WebRTC;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Answers the laptop's WebRTC offer (the headset is the answerer). A verified offer is answered only from the
    /// laptop the picker is waiting for (<see cref="ExpectLaptop"/>): tapping Connect on a paired laptop is the view-only
    /// consent, so there is no prompt on dial-in. Anything else, or any offer while a session or another answer is live,
    /// is refused with "no answer". The peer connection is built on the main thread (a coroutine on the host): data
    /// channels control (id 0) and input-k (id 1+k), the offer applied, every transceiver set to RecvOnly, the answer
    /// created and returned after ICE gathering (2 s cap). Commit keeps the connection as a <see cref="LinkSession"/>
    /// holding a listener lease; Abort throws everything away. Both are idempotent.
    /// </summary>
    public sealed class LinkAnswerer : ILinkAnswerer
    {
        const float GatheringTimeout = 2f;
        const int MaxSlots = 8;

        internal sealed class Attempt
        {
            public TaskCompletionSource<LinkAnswer> Tcs = new TaskCompletionSource<LinkAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            public LinkSession Session;
            public LinkOffer Offer;
            public volatile bool Cancelled;
            public bool Committed;
        }

        readonly MainThreadQueue queue;
        readonly Action<IEnumerator> run;
        readonly Func<IDisposable> acquireLease;
        readonly object gate = new object();
        byte[] expected;
        Attempt current;
        LinkSession live;
        bool panicked;
        ConsentGate hooked;

        /// <summary>The live session, or null.</summary>
        public LinkSession Session { get { lock (gate) return live != null && !live.IsEnded ? live : null; } }
        /// <summary>A session started (main thread).</summary>
        public event Action<LinkSession> SessionStarted;

        /// <param name="acquireLease">Takes the LinkHost listener lease the session holds while it lives.</param>
        /// <param name="startCoroutine">Runs a coroutine on the main thread; null runs it on <paramref name="host"/>.</param>
        public LinkAnswerer(MainThreadQueue queue, MonoBehaviour host, Func<IDisposable> acquireLease, Action<IEnumerator> startCoroutine = null)
        {
            this.queue = queue;
            run = startCoroutine ?? (e => host.StartCoroutine(e));
            this.acquireLease = acquireLease;
        }

        /// <summary>The picker is waiting for this laptop (its SPKI); only its offers are answered.</summary>
        public void ExpectLaptop(byte[] spki)
        {
            lock (gate) { expected = spki == null ? null : (byte[])spki.Clone(); panicked = false; }
            var consent = ConsentGate.Current;
            if (consent != hooked)
            {
                if (hooked != null) hooked.Panicked -= OnPanic;
                hooked = consent;
                if (hooked != null) hooked.Panicked += OnPanic; // a panic stop refuses offers until the next ExpectLaptop
            }
        }

        public void StopExpecting()
        {
            lock (gate) expected = null;
        }

        // Panic stop: refuse further offers, cancel an answer in progress and refuse to commit one already accepted.
        // A live session is ended by its LiveEntry.StopForPanic.
        void OnPanic()
        {
            Attempt attempt;
            lock (gate)
            {
                expected = null;
                panicked = true;
                attempt = current;
                current = null;
            }
            if (attempt == null) return;
            attempt.Cancelled = true;
            attempt.Tcs.TrySetResult(null);
            queue.Enqueue(() => { if (!attempt.Committed) attempt.Session?.Dispose(); });
        }

        public async Task<LinkAnswer> AnswerAsync(LinkOffer offer, CancellationToken ct)
        {
            Attempt attempt;
            lock (gate)
            {
                bool wanted = expected != null && offer?.LaptopSpki != null && expected.AsSpan().SequenceEqual(offer.LaptopSpki);
                if (!wanted || current != null || (live != null && !live.IsEnded))
                {
                    return null;
                }
                attempt = current = new Attempt { Offer = offer };
            }
            int lines = SdpLines.VideoMids(offer.Sdp).Count;
            if (lines == 0 || lines > MaxSlots) return null; // Abort clears the attempt

            using (ct.Register(() =>
            {
                attempt.Cancelled = true;
                attempt.Tcs.TrySetCanceled();
                queue.Enqueue(() => { if (!attempt.Committed) attempt.Session?.Dispose(); });
            }))
            {
                queue.Enqueue(() => { if (!attempt.Cancelled) run(Build(attempt)); });
                return await attempt.Tcs.Task.ConfigureAwait(false);
            }
        }

        public void Commit(LinkOffer offer, LinkAnswer answer)
        {
            queue.Enqueue(() =>
            {
                Attempt attempt;
                lock (gate)
                {
                    attempt = current;
                    if (attempt == null || !ReferenceEquals(attempt.Offer, offer)) return;
                    current = null;
                    if (panicked) { attempt.Cancelled = true; attempt.Session?.Dispose(); return; }
                }
                if (attempt.Session == null || attempt.Session.IsEnded) return;
                var session = attempt.Session;
                try
                {
                    var lease = acquireLease();
                    attempt.Committed = true;
                    lock (gate) live = session;
                    session.Begin(lease);
                    run(Pump(session));
                    BeginLive(session);
                    SessionStarted?.Invoke(session);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Link] couldn't start the session: " + e.Message);
                    lock (gate) { if (ReferenceEquals(live, session)) live = null; }
                    session.Dispose();
                }
            });
        }

        // The session owns its live entry, so the indicator and the panic stop cover it with no windows open.
        static void BeginLive(LinkSession session)
        {
            var consent = ConsentGate.Current;
            if (consent == null) return;
            var entry = new LiveEntry(session);
            consent.BeginLive(entry);
            session.Ended += _ => consent.EndLive(entry);
        }

        sealed class LiveEntry : IRemoteSession
        {
            readonly LinkSession session;
            public LiveEntry(LinkSession session) { this.session = session; }
            public string SourceName => session.LaptopName ?? "Laptop";
            public string Address => session.Address ?? "";
            public string ModeLabel => session.Control.State == LinkSessionState.Control || session.Control.State == LinkSessionState.Requested
                ? "full control" : "view-only";
            public SpatialWindow Window => null;
            public void StopForPanic() => session.StopForPanic();
        }

        public void Abort(LinkOffer offer, string reason)
        {
            Attempt attempt;
            lock (gate)
            {
                attempt = current;
                if (attempt == null || !ReferenceEquals(attempt.Offer, offer)) return;
                current = null;
            }
            attempt.Cancelled = true;
            attempt.Tcs.TrySetResult(null);
            queue.Enqueue(() => { if (!attempt.Committed) attempt.Session?.Dispose(); });
        }

        /// <summary>Ends the live session and any answer in progress (the service is going away).</summary>
        public void Shutdown()
        {
            LinkSession session;
            lock (gate)
            {
                session = live;
                live = null;
                if (current != null)
                {
                    current.Cancelled = true;
                    current.Tcs.TrySetResult(null);
                    current.Session?.Dispose();
                    current = null;
                }
            }
            if (hooked != null) hooked.Panicked -= OnPanic;
            hooked = null;
            session?.Dispose();
        }

        IEnumerator Pump(LinkSession session)
        {
            while (!session.IsEnded || session.ClosePending)
            {
                session.Tick(Time.realtimeSinceStartup);
                yield return null;
            }
        }

        /// <summary>Builds the connection and the answer on the main thread; completes <c>attempt.Tcs</c>.</summary>
        internal IEnumerator Build(Attempt attempt)
        {
            var offer = attempt.Offer;
            var mids = SdpLines.VideoMids(offer.Sdp);
            string error = Setup(attempt, mids);
            if (error != null) { Fail(attempt, error); yield break; }
            var pc = attempt.Session.Peer;

            var remote = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = offer.Sdp };
            var setRemote = pc.SetRemoteDescription(ref remote);
            yield return setRemote;
            if (attempt.Cancelled) yield break;
            if (setRemote.IsError) { Fail(attempt, "couldn't apply the offer (" + setRemote.Error.message + ")"); yield break; }

            error = ReceiveOnly(pc, mids);
            if (error != null) { Fail(attempt, error); yield break; }

            var create = pc.CreateAnswer();
            yield return create;
            if (attempt.Cancelled) yield break;
            if (create.IsError) { Fail(attempt, "couldn't create the answer (" + create.Error.message + ")"); yield break; }
            var description = create.Desc;
            var setLocal = pc.SetLocalDescription(ref description);
            yield return setLocal;
            if (attempt.Cancelled) yield break;
            if (setLocal.IsError) { Fail(attempt, "couldn't apply the answer (" + setLocal.Error.message + ")"); yield break; }

            // No trickle ICE: the answer is sent once it carries the candidates.
            float deadline = Time.realtimeSinceStartup + GatheringTimeout;
            while (pc.GatheringState != RTCIceGatheringState.Complete && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                if (attempt.Cancelled) yield break;
            }

            LinkAnswer answer;
            try
            {
                string sdp = pc.LocalDescription.sdp;
                var accepted = SdpLines.AcceptedVideoMids(sdp);
                if (accepted.Count == 0)
                {
                    Debug.LogWarning("[Link] this device can't decode H.264: every video m-line was rejected");
                    Fail(attempt, "no answer");
                    yield break;
                }
                var usable = new List<string>();
                foreach (string mid in mids) usable.Add(accepted.Contains(mid) ? mid : null);
                attempt.Session.SetMids(usable);
                answer = new LinkAnswer(sdp, SdpFingerprint.Extract(sdp));
            }
            catch (Exception e)
            {
                Fail(attempt, e.Message);
                yield break;
            }
            attempt.Tcs.TrySetResult(answer);
        }

        // Creates the connection and its data channels (wrapped in the session); returns an error text or null.
        string Setup(Attempt attempt, List<string> mids)
        {
            if (!WebRtcUpdater.IsUsable) return "WebRTC is not usable (scripts were reloaded during Play mode)";
            RTCPeerConnection pc = null;
            try
            {
                var configuration = new RTCConfiguration { iceServers = Array.Empty<RTCIceServer>() }; // same network: no STUN/TURN
                pc = new RTCPeerConnection(ref configuration);
                attempt.Session = new LinkSession(pc, mids.Count, attempt.Offer.LaptopName, attempt.Offer.Address);
                return null;
            }
            catch (Exception e)
            {
                if (attempt.Session == null) pc?.Dispose();
                return "couldn't create the connection (" + e.Message + ")";
            }
        }

        // The offer created the transceivers: do not add any; set the ones of our slots to RecvOnly.
        static string ReceiveOnly(RTCPeerConnection pc, List<string> mids)
        {
            try
            {
                foreach (var transceiver in pc.GetTransceivers())
                    if (transceiver.Mid != null && mids.Contains(transceiver.Mid))
                        transceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;
                return null;
            }
            catch (Exception e)
            {
                return "couldn't set the transceivers receive-only (" + e.Message + ")";
            }
        }

        void Fail(Attempt attempt, string reason)
        {
            Debug.LogWarning("[Link] no answer: " + reason);
            attempt.Tcs.TrySetResult(null);
            if (!attempt.Committed) attempt.Session?.Dispose();
        }
    }

    /// <summary>The m=video sections of an SDP, for slot mapping and for noticing rejected m-lines.</summary>
    static class SdpLines
    {
        /// <summary>The mid of every m=video section, in m-line order (null for one without a=mid).</summary>
        public static List<string> VideoMids(string sdp) => Sections(sdp, false);

        /// <summary>The mids of the m=video sections that were not rejected (port 0).</summary>
        public static HashSet<string> AcceptedVideoMids(string sdp)
        {
            var result = new HashSet<string>();
            foreach (string mid in Sections(sdp, true))
                if (mid != null) result.Add(mid);
            return result;
        }

        static List<string> Sections(string sdp, bool acceptedOnly)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(sdp)) return result;
            bool inVideo = false, accepted = false;
            int index = -1;
            foreach (string raw in sdp.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("m=", StringComparison.Ordinal))
                {
                    inVideo = line.StartsWith("m=video ", StringComparison.Ordinal);
                    if (!inVideo) continue;
                    var parts = line.Split(' ');
                    accepted = parts.Length > 1 && parts[1] != "0";
                    if (!acceptedOnly || accepted) { result.Add(null); index = result.Count - 1; }
                    else index = -1;
                }
                else if (inVideo && index >= 0 && line.StartsWith("a=mid:", StringComparison.Ordinal))
                    result[index] = line.Substring(6).Trim();
            }
            return result;
        }
    }
}
#endif
