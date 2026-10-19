#if SPATIAL_LAUNCHER_WEBRTC
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SpatialLauncher.LaptopLink;

namespace SpatialLauncher.SpatialWindows
{
    public enum LinkSessionState { Waiting, Connecting, ViewOnly, Requested, Control, Revoked, Ended }

    /// <summary>One video slot as announced by the agent's welcome: the m-line's mid.</summary>
    public readonly struct SlotInfo
    {
        public readonly int Slot;
        public readonly string Mid;
        public SlotInfo(int slot, string mid) { Slot = slot; Mid = mid; }
    }

    /// <summary>The agent's answer to an open: which slot and mid carry the window, and what it is.</summary>
    public sealed class OpenedWindow
    {
        public int Slot;
        public string Mid, Id, Path, Kind, Description;
        public int Width, Height;
    }

    /// <summary>
    /// The headset end of the "control" data channel (protocol v5, see Tools/LaptopAgent/session.py): welcome, window
    /// list, open and close, ping, and the control states. Plain C# over a send delegate, so it knows nothing of WebRTC.
    /// Receive parses and updates state only (it never blocks); call <see cref="Tick"/> every frame for the ping.
    /// States: Waiting (not started), Connecting, ViewOnly (after welcome), Requested, Control, Revoked (final until the
    /// session ends: the laptop's kill switch or stop), Ended.
    ///
    /// Full control is requested only through <see cref="ApproveControlFromConsent"/>, which the ConsentGate approve
    /// callback alone may call: <see cref="RequestControl"/> sends nothing unless that approval was given for it.
    /// </summary>
    public sealed class ControlChannelClient
    {
        public const int ProtocolVersion = 5;
        /// <summary>Bigger control messages end the session, like on the agent.</summary>
        public const int MaxMessageBytes = 16 * 1024;
        const float PingInterval = 1f;
        const int RttWindow = 10;

        readonly Action<string> send;
        readonly Func<double> unixMs;
        readonly List<(double rtt, double offset)> samples = new List<(double, double)>();
        float nextPing;
        int pingId;
        double? clockOffsetMs;
        bool consentApproved;

        public LinkSessionState State { get; private set; } = LinkSessionState.Waiting;
        public string EndReason { get; private set; }
        public IReadOnlyList<SlotInfo> Slots { get; private set; } = Array.Empty<SlotInfo>();
        /// <summary>The agent accepts input at all (started with --input); false once its kill switch fired.</summary>
        public bool InputAllowed { get; private set; }
        /// <summary>The agent granted keys with the control (it runs with --keyboard).</summary>
        public bool KeyboardAllowed { get; private set; }
        public bool CastOnly { get; private set; }
        public bool MediaAllowed { get; private set; }
        public bool AgentDryRun { get; private set; }
        /// <summary>Why the last control request was denied, or null.</summary>
        public string ControlDeniedReason { get; private set; }
        public string RevokedReason { get; private set; }
        public IReadOnlyList<LaptopTarget> Targets { get; private set; } = Array.Empty<LaptopTarget>();

        public double? RoundTripMs { get; private set; }
        public double? BestRoundTripMs { get; private set; }
        public DateTime? LaptopNow => clockOffsetMs.HasValue ? DateTime.UtcNow.AddMilliseconds(clockOffsetMs.Value) : (DateTime?)null;

        public bool IsLive => State == LinkSessionState.ViewOnly || State == LinkSessionState.Requested
            || State == LinkSessionState.Control || State == LinkSessionState.Revoked;

        public event Action StateChanged;
        public event Action Welcomed;
        public event Action TargetsReceived;
        public event Action<OpenedWindow> Opened;
        /// <summary>An open failed ("gone", "minimized", "no slot", ...): goes back to whoever asked.</summary>
        public event Action<string> OpenFailed;
        /// <summary>A window's stream ended on the laptop (slot, reason); the slot is free again.</summary>
        public event Action<int, string> WindowEnded;
        public event Action<string> Ended;

        public ControlChannelClient(Action<string> send, Func<double> unixMs = null)
        {
            this.send = send ?? (_ => { });
            this.unixMs = unixMs ?? (() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds);
        }

        public void BeginConnecting()
        {
            if (State == LinkSessionState.Waiting) SetState(LinkSessionState.Connecting);
        }

        // --- Requests -------------------------------------------------------------------------------------------------

        public void RequestTargets()
        {
            if (IsLive) send("{\"type\":\"list\"}");
        }

        public void Open(string target)
        {
            if (IsLive && !string.IsNullOrEmpty(target)) send($"{{\"type\":\"open\",\"target\":{RemoteKeyProtocol.Quote(target)}}}");
        }

        public void CloseSlot(int slot)
        {
            if (IsLive) send($"{{\"type\":\"close\",\"slot\":{slot.ToString(CultureInfo.InvariantCulture)}}}");
        }

        /// <summary>
        /// ONLY the ConsentGate approve callback may call this: the user approved full control for this session. It marks
        /// the approval and sends the request.
        /// </summary>
        public void ApproveControlFromConsent()
        {
            consentApproved = true;
            RequestControl();
        }

        /// <summary>
        /// Sends {"type":"control-request","consent":"full control"}, and only right after
        /// <see cref="ApproveControlFromConsent"/>; the approval is used up by the request. False when nothing was sent.
        /// </summary>
        public bool RequestControl()
        {
            if (!consentApproved) return false;
            consentApproved = false;
            if (State != LinkSessionState.ViewOnly) return false;
            ControlDeniedReason = null;
            send("{\"type\":\"control-request\",\"consent\":\"full control\"}");
            SetState(LinkSessionState.Requested);
            return true;
        }

        public void ReleaseControl()
        {
            consentApproved = false;
            if (State != LinkSessionState.Control && State != LinkSessionState.Requested) return;
            send("{\"type\":\"control-release\"}");
            KeyboardAllowed = false;
            SetState(LinkSessionState.ViewOnly);
        }

        /// <summary>Panic stop: tells the agent to revoke control and end the session, then ends it here.</summary>
        public void Stop()
        {
            consentApproved = false;
            if (State == LinkSessionState.Ended) return;
            send("{\"type\":\"stop\"}");
            End("stopped");
        }

        public void End(string reason)
        {
            if (State == LinkSessionState.Ended) return;
            consentApproved = false;
            KeyboardAllowed = false;
            EndReason = reason;
            SetState(LinkSessionState.Ended);
            Ended?.Invoke(reason);
        }

        // --- Incoming -------------------------------------------------------------------------------------------------

        public void Receive(byte[] bytes)
        {
            if (State == LinkSessionState.Ended || bytes == null) return;
            if (bytes.Length > MaxMessageBytes) { End("control message too large"); return; }
            Dictionary<string, object> message;
            try { message = LinkJson.ParseObject(bytes); }
            catch (LinkException) { return; }
            Handle(message);
        }

        public void Receive(string json)
        {
            if (State == LinkSessionState.Ended || json == null) return;
            if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) { End("control message too large"); return; }
            Dictionary<string, object> message;
            try { message = LinkJson.ParseObject(json); }
            catch (LinkException) { return; }
            Handle(message);
        }

        void Handle(Dictionary<string, object> m)
        {
            switch (LinkJson.GetString(m, "type"))
            {
                case "welcome": OnWelcome(m); break;
                case "targets": OnTargets(m); break;
                case "opened": OnOpened(m); break;
                case "error":
                {
                    string reason = LinkJson.GetString(m, "reason") ?? "error";
                    int slot = Int(m, "slot");
                    if (slot >= 0) WindowEnded?.Invoke(slot, reason);
                    else OpenFailed?.Invoke(reason);
                    break;
                }
                case "closed":
                {
                    int slot = Int(m, "slot");
                    if (slot >= 0) WindowEnded?.Invoke(slot, LinkJson.GetString(m, "reason") ?? "closed");
                    break;
                }
                case "pong": OnPong(m); break;
                case "control-granted":
                    if (State == LinkSessionState.Requested)
                    {
                        KeyboardAllowed = Bool(m, "keyboard");
                        SetState(LinkSessionState.Control);
                    }
                    break;
                case "control-denied":
                    if (State == LinkSessionState.Requested)
                    {
                        ControlDeniedReason = LinkJson.GetString(m, "reason") ?? "denied";
                        SetState(LinkSessionState.ViewOnly);
                    }
                    break;
                case "control-revoked":
                    if (State != LinkSessionState.Ended && State != LinkSessionState.Revoked)
                    {
                        RevokedReason = LinkJson.GetString(m, "reason") ?? "revoked";
                        InputAllowed = KeyboardAllowed = false;
                        SetState(LinkSessionState.Revoked);
                    }
                    break;
            }
        }

        void OnWelcome(Dictionary<string, object> m)
        {
            if (State != LinkSessionState.Waiting && State != LinkSessionState.Connecting) return;
            if (!(m.TryGetValue("version", out var v) && v is long version) || version < ProtocolVersion)
            {
                End("laptop agent out of date");
                return;
            }
            var slots = new List<SlotInfo>();
            if (m.TryGetValue("slots", out var s) && s is List<object> list)
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> d)) continue;
                    int slot = Int(d, "slot");
                    string mid = LinkJson.GetString(d, "mid");
                    if (slot >= 0 && mid != null) slots.Add(new SlotInfo(slot, mid));
                }
            Slots = slots;
            bool revoked = LinkJson.GetString(m, "control") == "revoked";
            InputAllowed = Bool(m, "input") && !revoked;
            CastOnly = Bool(m, "cast_only");
            MediaAllowed = Bool(m, "media");
            AgentDryRun = Bool(m, "dry_run");
            nextPing = 0f;
            SetState(revoked ? LinkSessionState.Revoked : LinkSessionState.ViewOnly);
            Welcomed?.Invoke();
        }

        void OnTargets(Dictionary<string, object> m)
        {
            var result = new List<LaptopTarget>();
            if (m.TryGetValue("targets", out var t) && t is List<object> list)
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> d)) continue;
                    result.Add(new LaptopTarget
                    {
                        id = LinkJson.GetString(d, "id"), path = LinkJson.GetString(d, "path"), title = LinkJson.GetString(d, "title"),
                        kind = LinkJson.GetString(d, "kind"), description = LinkJson.GetString(d, "description"),
                        width = Math.Max(0, Int(d, "width")), height = Math.Max(0, Int(d, "height")),
                    });
                }
            Targets = result;
            TargetsReceived?.Invoke();
        }

        void OnOpened(Dictionary<string, object> m)
        {
            int slot = Int(m, "slot");
            if (slot < 0) return;
            var window = new OpenedWindow { Slot = slot, Mid = LinkJson.GetString(m, "mid"), Id = LinkJson.GetString(m, "id"), Path = LinkJson.GetString(m, "path") };
            if (m.TryGetValue("target", out var t) && t is Dictionary<string, object> d)
            {
                window.Kind = LinkJson.GetString(d, "kind");
                window.Description = LinkJson.GetString(d, "description") ?? "";
                window.Width = Math.Max(0, Int(d, "width"));
                window.Height = Math.Max(0, Int(d, "height"));
            }
            Opened?.Invoke(window);
        }

        void OnPong(Dictionary<string, object> m)
        {
            if (!Number(m, "t", out double sentMs) || !Number(m, "agent_ms", out double agentMs)) return;
            double now = unixMs();
            double rtt = now - sentMs;
            if (rtt < 0) return;
            RoundTripMs = rtt;
            // NTP-style: the agent read its clock about halfway through the round trip.
            samples.Add((rtt, agentMs - (sentMs + now) / 2.0));
            if (samples.Count > RttWindow) samples.RemoveAt(0);
            var best = samples[0];
            foreach (var sample in samples)
                if (sample.rtt < best.rtt) best = sample;
            BestRoundTripMs = best.rtt;
            clockOffsetMs = best.offset;
            StateChanged?.Invoke();
        }

        /// <summary>Main-thread pump: the 1 s ping. <paramref name="now"/> is Time.realtimeSinceStartup.</summary>
        public void Tick(float now)
        {
            if (!IsLive || now < nextPing) return;
            nextPing = now + PingInterval;
            send($"{{\"type\":\"ping\",\"id\":{(++pingId).ToString(CultureInfo.InvariantCulture)},\"t\":{unixMs().ToString("0.###", CultureInfo.InvariantCulture)}}}");
        }

        void SetState(LinkSessionState state)
        {
            if (state == State) return;
            State = state;
            StateChanged?.Invoke();
        }

        static int Int(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out var v) && v is long l && l >= 0 && l <= int.MaxValue ? (int)l : -1;

        static bool Bool(Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) && v is bool b && b;

        static bool Number(Dictionary<string, object> o, string key, out double value)
        {
            value = 0;
            if (!o.TryGetValue(key, out var v)) return false;
            if (v is long l) value = l;
            else if (v is double d) value = d;
            else return false;
            return true;
        }
    }
}
#endif
