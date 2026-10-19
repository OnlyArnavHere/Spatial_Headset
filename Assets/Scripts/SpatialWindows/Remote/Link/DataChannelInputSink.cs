#if SPATIAL_LAUNCHER_WEBRTC
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SpatialLauncher.LaptopLink;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Input for one laptop window over the session's "input-k" data channel; the messages are byte for byte those of
    /// <see cref="WebSocketRemoteInputSink"/>. Moves are coalesced to one per <see cref="MoveInterval"/>; presses,
    /// releases, scrolls, keys and text go at once (after a pending move). Until the control state is Control only media
    /// controls are sent, and only when the agent runs cast-only, or accepts media and control was granted; everything
    /// else is dropped here. Acks and throttle notices come back on the same channel (<see cref="ReceiveInput"/>).
    /// The owner calls <see cref="Tick"/> every frame (the session does too).
    /// </summary>
    public sealed class DataChannelInputSink : IRemoteInputSink, IRemoteKeyboardSink
    {
        const float MoveInterval = 1f / 60f;
        const int MaxInputMessageBytes = 4096;

        public int Slot { get; }

        /// <summary>The agent takes input and control was granted for this session.</summary>
        public bool InputAllowed => control.InputAllowed && control.State == LinkSessionState.Control;
        /// <summary>As <see cref="InputAllowed"/>, and the agent granted keys (it runs with --keyboard).</summary>
        public bool KeyboardAllowed => InputAllowed && control.KeyboardAllowed;
        public bool CastOnly => control.CastOnly;
        public bool MediaAllowed => control.MediaAllowed;
        public bool AgentDryRun => control.AgentDryRun;

        public Vector2Int? LastAck { get; private set; }
        public string LastAckPath { get; private set; }
        public int AckCount { get; private set; }
        public RemoteKeyProtocol.Message LastKeyAck { get; private set; }
        public string LastKeyAckPath { get; private set; }
        public int KeyAckCount { get; private set; }
        public int ThrottledCount { get; private set; }
        public string LastMediaSent { get; private set; }
        public string LastMediaAck { get; private set; }
        public int MediaSentCount { get; private set; }
        public int MediaAckCount { get; private set; }

        public static readonly string[] MediaActions = WebSocketRemoteInputSink.MediaActions;

        public event Action StatusChanged;

        readonly ControlChannelClient control;
        readonly Action<string> send;
        Vector2? pendingMove;
        float lastMoveSent = -1f;

        public DataChannelInputSink(int slot, ControlChannelClient control, Action<string> send)
        {
            Slot = slot;
            this.control = control;
            this.send = send ?? (_ => { });
        }

        // --- IRemoteInputSink: positions are normalized, top-left origin -------------------------------------------

        public void PointerMove(Vector2 position)
        {
            if (InputAllowed) pendingMove = position;
        }

        public void PointerDown(Vector2 position) => SendButton("down", position);
        public void PointerUp(Vector2 position) => SendButton("up", position);

        public void Scroll(Vector2 position, float delta)
        {
            if (!InputAllowed) return;
            FlushMove(Time.realtimeSinceStartup);
            send($"{{\"type\":\"scroll\",{Position(position)},\"delta\":{F(delta)}}}");
        }

        // --- IRemoteKeyboardSink -------------------------------------------------------------------------------------

        public void Key(string code, bool down)
        {
            if (!KeyboardAllowed || !RemoteKeyProtocol.IsKnown(code)) return;
            FlushMove(Time.realtimeSinceStartup);
            send(RemoteKeyProtocol.EncodeKey(code, down));
        }

        public void Text(string text)
        {
            if (!KeyboardAllowed) return;
            FlushMove(Time.realtimeSinceStartup);
            foreach (var piece in RemoteKeyProtocol.SplitText(text))
                send(RemoteKeyProtocol.EncodeText(piece));
        }

        /// <summary>{"type":"media","action":...} for one of <see cref="MediaActions"/>, or null for anything else.</summary>
        public static string EncodeMedia(string action) => WebSocketRemoteInputSink.EncodeMedia(action);

        /// <summary>
        /// Sends a media control (Cast windows). False, and nothing sent, unless the agent is cast-only, or takes media
        /// and control was granted, or when the action isn't one of <see cref="MediaActions"/>.
        /// </summary>
        public bool SendMedia(string action)
        {
            string json = EncodeMedia(action);
            bool permitted = control.CastOnly || (control.MediaAllowed && control.State == LinkSessionState.Control && control.State != LinkSessionState.Revoked);
            if (json == null || !control.IsLive || !permitted) return false;
            send(json);
            LastMediaSent = action;
            MediaSentCount++;
            return true;
        }

        void SendButton(string kind, Vector2 position)
        {
            if (!InputAllowed) return;
            pendingMove = null; // the button event carries its own position
            send($"{{\"type\":\"{kind}\",{Position(position)},\"button\":\"left\"}}");
        }

        // --- Pump and replies ----------------------------------------------------------------------------------------

        public void Tick() => Tick(Time.realtimeSinceStartup);

        public void Tick(float now)
        {
            if (pendingMove.HasValue && now - lastMoveSent >= MoveInterval) FlushMove(now);
        }

        void FlushMove(float now)
        {
            if (!pendingMove.HasValue) return;
            send($"{{\"type\":\"move\",{Position(pendingMove.Value)}}}");
            pendingMove = null;
            lastMoveSent = now;
        }

        public void ReceiveInput(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaxInputMessageBytes) return;
            Dictionary<string, object> m;
            try { m = LinkJson.ParseObject(bytes); }
            catch (LinkException) { return; }
            Handle(m);
        }

        public void ReceiveInput(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaxInputMessageBytes) return;
            Dictionary<string, object> m;
            try { m = LinkJson.ParseObject(json); }
            catch (LinkException) { return; }
            Handle(m);
        }

        void Handle(Dictionary<string, object> m)
        {
            switch (LinkJson.GetString(m, "type"))
            {
                case "ack":
                {
                    string kind = LinkJson.GetString(m, "event");
                    string path = LinkJson.GetString(m, "path");
                    if (kind == "media")
                    {
                        LastMediaAck = LinkJson.GetString(m, "action");
                        MediaAckCount++;
                    }
                    else if (kind == "key" || kind == "text")
                    {
                        LastKeyAck = new RemoteKeyProtocol.Message
                        {
                            type = kind, code = LinkJson.GetString(m, "code"),
                            down = m.TryGetValue("down", out var d) && d is bool b && b, text = LinkJson.GetString(m, "text"),
                        };
                        LastKeyAckPath = path;
                        KeyAckCount++;
                    }
                    else
                    {
                        LastAck = new Vector2Int(Mathf.RoundToInt(Num(m, "x")), Mathf.RoundToInt(Num(m, "y")));
                        LastAckPath = path;
                        AckCount++;
                    }
                    StatusChanged?.Invoke();
                    break;
                }
                case "throttled":
                    ThrottledCount++;
                    StatusChanged?.Invoke();
                    break;
            }
        }

        static float Num(Dictionary<string, object> m, string key) =>
            m.TryGetValue(key, out var v) ? (v is long l ? l : v is double d ? (float)d : 0f) : 0f;

        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Position(Vector2 p) => $"\"x\":{F(Mathf.Clamp01(p.x))},\"y\":{F(Mathf.Clamp01(p.y))}";
    }
}
#endif
