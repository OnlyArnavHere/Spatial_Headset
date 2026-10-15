using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The connection to the laptop agent's control server (Tools/LaptopAgent) over a WebSocket, authenticated with
    /// the pairing token. It can ask for the list of windows the laptop can stream (<see cref="RequestTargets"/>);
    /// connected with a target id, it opens that stream (<see cref="StreamPath"/> says where to watch it) and sends the
    /// window's pointer input to it, so each streamed window has its own link. It measures the round-trip time with a
    /// ping every second and from the same pings estimates the laptop's clock (<see cref="LaptopNow"/>), so the window
    /// can show it next to the streamed test clock.
    ///
    /// Moves are coalesced to at most one per <see cref="MoveInterval"/>; presses, releases and scrolls are sent at once
    /// (after any pending move, so the order is kept). Socket work runs on background tasks; state, events and sending
    /// happen on the main thread in <see cref="Tick"/>, which the owner must call every frame.
    /// </summary>
    public class WebSocketRemoteInputSink : IRemoteInputSink, IConnectionStatus, IDisposable
    {
        public enum State { Disconnected, Connecting, Connected, Rejected, Retrying }

        const int ProtocolVersion = 2;
        const float MoveInterval = 1f / 60f;
        const float PingInterval = 1f;
        const int ConnectTimeoutMs = 5000;
        const float MaxRetryDelay = 10f;
        const int RttWindow = 10;

        public State CurrentState { get; private set; } = State.Disconnected;
        public bool IsConnected => CurrentState == State.Connected;
        public string StatusText { get; private set; } = "Not connected";

        /// <summary>Latest round-trip time in milliseconds, or null before the first pong.</summary>
        public double? RoundTripMs { get; private set; }
        /// <summary>Lowest round-trip time among the last few pings: the least noisy delay figure.</summary>
        public double? BestRoundTripMs { get; private set; }
        /// <summary>The laptop's clock now, estimated from the ping with the lowest round-trip time; null until known.</summary>
        public DateTime? LaptopNow => clockOffsetMs.HasValue ? DateTime.UtcNow.AddMilliseconds(clockOffsetMs.Value) : (DateTime?)null;
        /// <summary>The target this link opens after connecting, or null for a list-only link (the picker).</summary>
        public string TargetId { get; private set; }
        /// <summary>The stream path the agent opened for <see cref="TargetId"/>; null until it answers.</summary>
        public string StreamPath { get; private set; }
        /// <summary>What the agent streams for this link, e.g. "window 'Notepad'".</summary>
        public string TargetDescription { get; private set; } = "";
        /// <summary>False when the agent runs without --input: the stream can be watched but not clicked.</summary>
        public bool InputAllowed { get; private set; }
        public bool AgentDryRun { get; private set; }
        /// <summary>The last window list from <see cref="RequestTargets"/>: the whole desktop first, then the windows.</summary>
        public IReadOnlyList<LaptopTarget> Targets { get; private set; } = Array.Empty<LaptopTarget>();
        /// <summary>The last dry-run acknowledgement: (screen x, screen y) the agent mapped an event to.</summary>
        public Vector2Int? LastAck { get; private set; }
        /// <summary>The stream path of the last acknowledged event: the window the agent routed it to.</summary>
        public string LastAckPath { get; private set; }
        public int AckCount { get; private set; }

        public event Action StatusChanged;
        /// <summary>A new window list arrived (<see cref="Targets"/>).</summary>
        public event Action TargetsReceived;

        readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<string> outbox = new ConcurrentQueue<string>();
        readonly SemaphoreSlim outboxSignal = new SemaphoreSlim(0);
        readonly List<(double rtt, double offset)> samples = new List<(double, double)>();

        ClientWebSocket socket;
        CancellationTokenSource cancel;
        string host, token;
        int port;
        bool wanted;              // the user asked to be connected
        volatile bool socketOpen; // set by the background task
        volatile string socketError;
        float nextPing, nextRetry, retryDelay = 1f, lastMoveSent = -1f;
        Vector2? pendingMove;
        int pingId;
        double? clockOffsetMs;

        /// <param name="streamTarget">The target to open (from the agent's list); null to only list windows.</param>
        public void Connect(string laptopHost, int inputPort, string pairingToken, string streamTarget = null)
        {
            Disconnect();
            host = laptopHost;
            port = inputPort;
            token = pairingToken;
            TargetId = streamTarget;
            StreamPath = null;
            wanted = true;
            retryDelay = 1f;
            Open();
        }

        /// <summary>Asks the agent which windows it can stream; <see cref="TargetsReceived"/> fires with the answer.</summary>
        public void RequestTargets()
        {
            if (IsConnected) Send("{\"type\":\"list\"}");
        }

        public void Disconnect()
        {
            wanted = false;
            Close();
            SetState(State.Disconnected, "Not connected");
        }

        public void Dispose() => Disconnect();

        // --- IRemoteInputSink: positions are normalized, top-left origin -------------------------------------------

        public void PointerMove(Vector2 position)
        {
            if (IsConnected) pendingMove = position;
        }

        public void PointerDown(Vector2 position) => SendButton("down", position);
        public void PointerUp(Vector2 position) => SendButton("up", position);

        public void Scroll(Vector2 position, float delta)
        {
            if (!IsConnected) return;
            FlushMove();
            Send($"{{\"type\":\"scroll\",{Position(position)},\"delta\":{F(delta)}}}");
        }

        void SendButton(string kind, Vector2 position)
        {
            if (!IsConnected) return;
            pendingMove = null; // the button event carries its own position
            Send($"{{\"type\":\"{kind}\",{Position(position)},\"button\":\"left\"}}");
        }

        // --- Main-thread pump ---------------------------------------------------------------------------------------

        public void Tick()
        {
            while (inbox.TryDequeue(out var message)) Handle(message);

            if (socketError != null)
            {
                string error = socketError;
                socketError = null;
                Close();
                if (CurrentState == State.Rejected || !wanted) return;
                nextRetry = Time.realtimeSinceStartup + retryDelay;
                SetState(State.Retrying, $"{error}. Retrying in {Mathf.CeilToInt(retryDelay)} s");
                retryDelay = Mathf.Min(MaxRetryDelay, retryDelay * 2f);
            }

            if (wanted && CurrentState == State.Retrying && Time.realtimeSinceStartup >= nextRetry) Open();

            if (!IsConnected) return;
            if (pendingMove.HasValue && Time.realtimeSinceStartup - lastMoveSent >= MoveInterval) FlushMove();
            if (Time.realtimeSinceStartup >= nextPing)
            {
                nextPing = Time.realtimeSinceStartup + PingInterval;
                Send($"{{\"type\":\"ping\",\"id\":{++pingId},\"t\":{F(UnixMs())}}}");
            }
        }

        void FlushMove()
        {
            if (!pendingMove.HasValue) return;
            Send($"{{\"type\":\"move\",{Position(pendingMove.Value)}}}");
            pendingMove = null;
            lastMoveSent = Time.realtimeSinceStartup;
        }

        void Handle(string json)
        {
            var message = JsonUtility.FromJson<AgentMessage>(json);
            switch (message.type)
            {
                case "welcome":
                    if (message.version < ProtocolVersion)
                    {
                        Stop("The laptop agent is out of date: update Tools/LaptopAgent");
                        break;
                    }
                    retryDelay = 1f;
                    AgentDryRun = message.dry_run;
                    InputAllowed = message.input;
                    nextPing = 0f;
                    if (TargetId != null) Send($"{{\"type\":\"open\",\"target\":\"{Escape(TargetId)}\"}}");
                    SetState(State.Connected, !InputAllowed ? "Connected (view only: agent started without --input)"
                        : AgentDryRun ? "Connected (agent in dry-run mode)" : "Connected");
                    break;
                case "opened":
                    StreamPath = message.path;
                    TargetDescription = message.target != null ? message.target.description : "";
                    StatusChanged?.Invoke();
                    break;
                case "targets":
                    Targets = message.targets ?? Array.Empty<LaptopTarget>();
                    TargetsReceived?.Invoke();
                    break;
                case "error":
                    // Don't retry a wrong token: the agent locks a device out after a few wrong tokens.
                    if (message.reason == "bad token") Stop("Wrong token (the agent prints a new one every run)");
                    else if (message.reason == "gone") Stop("That window is no longer open on the laptop");
                    else if (message.reason == "minimized") Stop("That window is minimized on the laptop: restore it, then connect");
                    else if (message.reason == "busy") socketError = "Another headset is connected to the laptop";
                    else socketError = $"Refused: {message.reason}";
                    break;
                case "pong":
                    OnPong(message.t, message.agent_ms);
                    break;
                case "ack":
                    LastAck = new Vector2Int(Mathf.RoundToInt(message.x), Mathf.RoundToInt(message.y));
                    LastAckPath = message.path;
                    AckCount++;
                    break;
            }
        }

        // Ends the connection for a reason retrying can't fix.
        void Stop(string why)
        {
            wanted = false;
            Close();
            SetState(State.Rejected, why);
        }

        void OnPong(double sentMs, double agentMs)
        {
            double now = UnixMs();
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
            StatusChanged?.Invoke();
        }

        // --- Socket (background tasks) -----------------------------------------------------------------------------

        void Open()
        {
            Close();
            SetState(State.Connecting, $"Connecting to {host}:{port}…");
            cancel = new CancellationTokenSource();
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(2);
            var hello = $"{{\"type\":\"hello\",\"token\":\"{Escape(token)}\",\"client\":\"SpatialLauncher\"}}";
            _ = RunAsync(socket, new Uri($"ws://{host}:{port}"), hello, cancel.Token);
        }

        void Close()
        {
            socketOpen = false;
            cancel?.Cancel();
            cancel = null;
            if (socket != null)
            {
                var closing = socket;
                socket = null;
                // A polite close only on an open socket: on one that never connected, Mono's CloseOutputAsync throws.
                // The agent releases held buttons either way when the socket goes away.
                if (closing.State == WebSocketState.Open || closing.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        _ = closing.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                            .ContinueWith(_ => closing.Dispose());
                    }
                    catch (Exception)
                    {
                        closing.Abort();
                        closing.Dispose();
                    }
                }
                else
                {
                    closing.Abort();
                    closing.Dispose();
                }
            }
            while (outbox.TryDequeue(out _)) { }
            pendingMove = null;
            samples.Clear();
            RoundTripMs = BestRoundTripMs = null;
            clockOffsetMs = null;
        }

        void Send(string json)
        {
            if (!socketOpen) return;
            outbox.Enqueue(json);
            outboxSignal.Release();
        }

        async Task RunAsync(ClientWebSocket ws, Uri uri, string hello, CancellationToken token)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(ConnectTimeoutMs);
                    await ws.ConnectAsync(uri, timeout.Token);
                }
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(hello)), WebSocketMessageType.Text, true, token);
                socketOpen = true;
                var sender = SendLoopAsync(ws, token);
                await ReceiveLoopAsync(ws, token);
                await sender;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Closed on purpose.
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                    socketError = error is OperationCanceledException ? $"Can't reach {uri.Host}:{uri.Port}" : Describe(error, uri);
            }
            finally
            {
                socketOpen = false;
            }
        }

        async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[4096];
            var text = new StringBuilder();
            while (!token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    socketError = string.IsNullOrEmpty(result.CloseStatusDescription)
                        ? "The laptop closed the connection"
                        : $"The laptop closed the connection ({result.CloseStatusDescription})";
                    return;
                }
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                inbox.Enqueue(text.ToString());
                text.Clear();
            }
        }

        async Task SendLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await outboxSignal.WaitAsync(token);
                    while (outbox.TryDequeue(out var json))
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { } // the receive loop reports the failure
        }

        void SetState(State state, string text)
        {
            if (state == CurrentState && text == StatusText) return;
            CurrentState = state;
            StatusText = text;
            StatusChanged?.Invoke();
        }

        static string Describe(Exception error, Uri uri)
        {
            for (var e = error; e != null; e = e.InnerException)
                if (e is System.Net.Sockets.SocketException) return $"Can't reach {uri.Host}:{uri.Port} (is the agent running?)";
            return $"Input connection failed ({error.GetBaseException().Message})";
        }

        static double UnixMs() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Position(Vector2 p) => $"\"x\":{F(Mathf.Clamp01(p.x))},\"y\":{F(Mathf.Clamp01(p.y))}";
        static string Escape(string text) => (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        [Serializable]
        sealed class AgentMessage
        {
            public string type;
            public string reason;
            public int version;
            public double t;
            public double agent_ms;
            public float x, y;
            public bool dry_run;
            public bool input;
            public string path;
            public AgentTarget target;
            public LaptopTarget[] targets;
        }

        [Serializable]
        sealed class AgentTarget
        {
            public string kind;
            public string description;
            public int width, height;
        }
    }
}
