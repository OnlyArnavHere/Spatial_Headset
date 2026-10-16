using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Shows a remote screen (a <see cref="RemoteStreamSource"/>) at its aspect ratio, with a "Not connected"
    /// placeholder while there is no picture. Pointer move, press, release and scroll on the screen go to the
    /// source's <see cref="IRemoteInputSink"/> in remote-screen coordinates.
    ///
    /// <see cref="InitConnected"/> adds the real connection to the laptop agent (Tools/LaptopAgent) for one target
    /// picked in the Laptop picker (a window or the whole desktop): video over WebRTC (<c>WebRtcStreamSource</c>) from
    /// the target's own stream path, input over its own WebSocket (<see cref="WebSocketRemoteInputSink"/>), so several
    /// of these windows can show different laptop windows at once. A connect panel under the screen has host, port and
    /// token fields, a Connect button, a Keys button (opens the virtual keyboard: <see cref="KeyboardRequested"/>) and a
    /// status line. The title bar shows the input round-trip time; the status line shows the laptop's clock as estimated
    /// over the input link, to compare with Tools/LaptopAgent/clock.py. Keyboard input reaches the laptop through
    /// <see cref="KeyboardSink"/> (see <see cref="RemoteKeyboard"/>).
    ///
    /// Connect asks <see cref="ConsentGate"/> first: video and input connect only after the user approves, every
    /// session. Deny or a timeout leaves both disconnected; without a gate in the scene nothing connects.
    /// </summary>
    public class RemoteStreamContent : WindowContent, IRemoteSession
    {
        static readonly Color PlaceholderColor = new Color(0.07f, 0.08f, 0.10f);
        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
        static readonly Color FieldColor = new Color(0.20f, 0.21f, 0.25f);
        static readonly Color ConnectColor = new Color(0.20f, 0.55f, 0.35f);
        static readonly Color DisconnectColor = new Color(0.60f, 0.25f, 0.25f);
        static readonly Color KeysColor = new Color(0.32f, 0.34f, 0.40f);

        const float RowHeight = 0.04f;
        const float StatusHeight = 0.03f;
        const float Gap = 0.01f;
        const float PanelHeight = RowHeight + StatusHeight + Gap;
        const float ButtonWidth = 0.11f;
        const float KeysButtonWidth = 0.07f;
        const float PortWidth = 0.08f;
        const float TextInset = 0.008f;

        /// <summary>Extra window height the connect panel needs below the screen, in metres.</summary>
        public const float ConnectPanelHeight = PanelHeight + Gap;

        public RemoteStreamSource Source { get; private set; }
        public ContentSurface Surface { get; private set; }
        public Vector2 DisplaySize => Surface != null ? Surface.DisplaySize : Vector2.zero;
        /// <summary>The placeholder text currently shown, or empty when the stream is showing.</summary>
        public string StatusText => statusLabel != null && statusLabel.gameObject.activeSelf ? statusLabel.text : "";

        // Connected mode only.
        /// <summary>The video source in connected mode; null without SPATIAL_LAUNCHER_WEBRTC (input still works).</summary>
        public IRemoteVideoSource Video { get; private set; }
        public WebSocketRemoteInputSink InputLink { get; private set; }
        public WindowTextField HostField { get; private set; }
        public WindowTextField PortField { get; private set; }
        public WindowTextField TokenField { get; private set; }
        public WindowButton ConnectButton { get; private set; }
        /// <summary>Opens the virtual keyboard for this window.</summary>
        public WindowButton KeysButton { get; private set; }
        /// <summary>The Keys button was clicked.</summary>
        public event Action KeyboardRequested;
        /// <summary>Where keyboard input for this screen goes: the input sink, if it takes keys (null otherwise).</summary>
        public IRemoteKeyboardSink KeyboardSink => Source?.Sink as IRemoteKeyboardSink;
        public string StatusLine => statusLine != null ? statusLine.text : "";
        /// <summary>True between Connect and Disconnect (whether or not the connection has succeeded yet).</summary>
        public bool IsActive { get; private set; }
        /// <summary>What this window streams from the laptop; null if it wasn't opened from the picker.</summary>
        public LaptopTarget Target { get; private set; }
        /// <summary>Waiting for the user to approve (<see cref="ConsentGate"/>); nothing is connected yet.</summary>
        public bool AwaitingConsent { get; private set; }
        /// <summary>How the last approval request ended: "approved", "denied", "timed out"... or "" before any.</summary>
        public string ConsentOutcome { get; private set; } = "";

        // IRemoteSession: what the consent question and the live indicator name.
        public string SourceName => Target != null && !string.IsNullOrEmpty(Target.title) ? Target.title : "Laptop";
        public string Address => connectedHost ?? (HostField != null ? HostField.Text.Trim() : "");
        /// <summary>"full control": the laptop window takes pointer and keyboard input.</summary>
        public virtual string ModeLabel => "full control";

        RemoteConnectionSettings settings;
        string connectedHost;
        int connectedPort;
        TextMeshPro statusLabel, statusLine, connectLabel, keysLabel;
        Transform panel;
        readonly List<(WindowTextField field, TextMeshPro label)> fields = new List<(WindowTextField, TextMeshPro)>();

        /// <summary>A screen with no connection: input goes to <paramref name="sink"/> (may be null).</summary>
        /// <param name="expectedSize">The remote screen's resolution (aspect until a stream reports its own).</param>
        public void Init(SpatialWindow window, Material baseMaterial, IRemoteInputSink sink, Vector2Int expectedSize)
        {
            Attach(window, baseMaterial);
            Build(new RemoteStreamSource(sink, expectedSize));
        }

        /// <summary>
        /// A screen connected to the laptop agent for <paramref name="target"/>, with a connect panel. Values typed in
        /// the panel are remembered on this device. <paramref name="inputOverride"/> replaces the WebSocket input (for
        /// debugging); usually null.
        /// </summary>
        public void InitConnected(SpatialWindow window, Material baseMaterial, RemoteConnectionSettings connectionSettings,
            IRemoteInputSink inputOverride = null, LaptopTarget target = null)
        {
            Attach(window, baseMaterial);
            settings = connectionSettings;
            Target = target;
            var expected = target != null && target.width > 0 && target.height > 0
                ? new Vector2Int(target.width, target.height)
                : settings != null ? settings.expectedResolution : new Vector2Int(1920, 1080);
            Video = CreateVideo(expected);
            InputLink = new WebSocketRemoteInputSink();
            InputLink.StatusChanged += RefreshStatus;
            InputLink.StatusChanged += FollowStreamPath;
            Build(new RemoteStreamSource(Video, inputOverride ?? InputLink, expected));
            BuildPanel();
            RefreshStatus();
            Relayout();
        }

        IRemoteVideoSource CreateVideo(Vector2Int expected)
        {
#if SPATIAL_LAUNCHER_WEBRTC
            return new WebRtcStreamSource(this, expected) { User = settings != null ? settings.viewerUser : "viewer" };
#else
            return null;
#endif
        }

        void Build(RemoteStreamSource source)
        {
            Source = source;
            Surface = CreateSurface("RemoteScreen", transform, Source);
            Surface.EmptyColor = PlaceholderColor;

            statusLabel = CreateLabel("Status", transform, "", 0.4f, TextAlignmentOptions.Center);
            statusLabel.transform.localPosition = new Vector3(0f, 0f, -0.002f);
            statusLabel.color = new Color(0.75f, 0.77f, 0.82f);
            statusLabel.textWrappingMode = TextWrappingModes.Normal;

            Source.Changed += RefreshStatus;
            Source.Start();
            RefreshStatus();
            Relayout();
        }

        void BuildPanel()
        {
            panel = CreateQuad("ConnectPanel", transform, CreateMaterial(PanelColor));
            HostField = CreateField("Host", "Laptop address");
            PortField = CreateField("Port", "Port");
            TokenField = CreateField("Token", "Pairing token");
            PortField.DigitsOnly = true;
            PortField.MaxLength = 5;
            TokenField.Masked = true;

            var group = new List<WindowTextField> { HostField, PortField, TokenField };
            foreach (var (field, _) in fields)
            {
                field.Group = group;
                field.Submitted += _ => Connect();
            }

            HostField.SetText(RemoteConnectionPrefs.Host(settings));
            PortField.SetText(RemoteConnectionPrefs.VideoPort(settings).ToString());
            TokenField.SetText(RemoteConnectionPrefs.Token());

            ConnectButton = CreateControl<WindowButton>("ConnectButton", transform, CreateMaterial(ConnectColor));
            connectLabel = CreateLabel("ConnectLabel", transform, "Connect", 0.22f, TextAlignmentOptions.Center);
            ConnectButton.Clicked += () =>
            {
                if (IsActive) Disconnect();
                else Connect();
            };

            KeysButton = CreateControl<WindowButton>("KeysButton", transform, CreateMaterial(KeysColor));
            keysLabel = CreateLabel("KeysLabel", transform, "Keys", 0.22f, TextAlignmentOptions.Center);
            KeysButton.Clicked += () => KeyboardRequested?.Invoke();

            statusLine = CreateLabel("StatusLine", transform, "", 0.17f, TextAlignmentOptions.MidlineLeft);
            statusLine.color = new Color(0.75f, 0.77f, 0.82f);
        }

        WindowTextField CreateField(string fieldName, string placeholder)
        {
            var material = CreateMaterial(FieldColor);
            var field = CreateControl<WindowTextField>($"{fieldName}Field", transform, material);
            var label = CreateLabel($"{fieldName}Text", transform, "", 0.2f, TextAlignmentOptions.MidlineLeft);
            field.Init(label, material, placeholder);
            fields.Add((field, label));
            return field;
        }

        /// <summary>Fills in the panel and connects (used when the picker opens this window).</summary>
        public void ConnectWith(LaptopConnection connection)
        {
            if (InputLink == null || connection == null) return;
            HostField.SetText(connection.Host);
            PortField.SetText(connection.VideoPort.ToString());
            TokenField.SetText(connection.Token);
            Connect();
        }

        /// <summary>Saves the panel's values and connects video and input. Shows what's missing instead if incomplete.</summary>
        public void Connect()
        {
            if (InputLink == null) return;
            WindowTextField.BlurAll();
            string host = HostField.Text.Trim();
            string token = TokenField.Text.Trim();
            if (Target == null)
            {
                statusLine.text = "Open laptop windows from the Laptop picker (the Laptop icon)";
                return;
            }
            if (host.Length == 0 || !int.TryParse(PortField.Text, out int port) || port <= 0 || port > 65535 || token.Length == 0)
            {
                statusLine.text = "Enter the laptop's address, port and the token printed by agent.py";
                return;
            }

            RemoteConnectionPrefs.Save(host, port, token);
            Disconnect();
            connectedHost = host;
            connectedPort = port;
            var gate = ConsentGate.Current;
            if (gate == null)
            {
                ConsentOutcome = "no consent gate";
                statusLine.text = "Not connected: nothing can show here without the headset's approval (no consent gate in this scene).";
                return;
            }
            AwaitingConsent = true;
            statusLine.text = $"Waiting for approval on the headset ({gate.TimeoutSeconds:0} s)...";
            gate.Request(this, () =>
            {
                AwaitingConsent = false;
                ConsentOutcome = "approved";
                if (this == null || InputLink == null) return;
                StartVideo(Target.path, token);
                InputLink.Connect(host, settings != null ? settings.inputPort : 8765, token, Target.id);
                IsActive = true;
                gate.BeginLive(this);
                RefreshStatus();
            }, reason =>
            {
                AwaitingConsent = false;
                ConsentOutcome = reason;
                if (this == null || statusLine == null) return;
                statusLine.text = $"Not allowed ({reason}). Nothing was connected.";
                RefreshStatus();
            });
        }

        /// <summary>Panic stop: disconnects video and input at once (the gate closes the window).</summary>
        public void StopForPanic() => Disconnect();

        void StartVideo(string path, string token)
        {
            if (Video == null) return;
            Video.Stop();
            Video.Url = $"http://{connectedHost}:{connectedPort}/{path}/whep";
            Video.Password = token;
            Video.Start();
        }

        // The agent names stream paths when it lists windows; if it restarted since, the window may have a new path.
        void FollowStreamPath()
        {
            string path = InputLink.StreamPath;
            if (!IsActive || Video == null || string.IsNullOrEmpty(path) || Target == null || path == Target.path) return;
            Target.path = path;
            StartVideo(path, TokenField.Text.Trim());
        }

        public void Disconnect()
        {
            if (InputLink == null) return;
            var gate = ConsentGate.Current;
            if (gate != null)
            {
                if (AwaitingConsent) gate.Withdraw(this);
                gate.EndLive(this);
            }
            AwaitingConsent = false;
            Video?.Stop();
            InputLink.Disconnect();
            IsActive = false;
            RefreshStatus();
        }

        void Update()
        {
            if (InputLink == null) return;
            Video?.Tick();
            InputLink.Tick();

            double? rtt = InputLink.IsConnected ? InputLink.RoundTripMs : null;
            Window.SetTitleDetail(rtt.HasValue ? $"{rtt.Value:0} ms" : null);
            RefreshStatusLine();
        }

        void RefreshStatus()
        {
            if (statusLabel == null || Source == null) return;
            bool showPlaceholder = !Source.IsConnected || Source.Texture == null;
            statusLabel.gameObject.SetActive(showPlaceholder);
            statusLabel.text = Source.StatusText;
            if (connectLabel != null)
            {
                connectLabel.text = IsActive ? "Disconnect" : "Connect";
                ConnectButton.GetComponent<MeshRenderer>().sharedMaterial.color = IsActive ? DisconnectColor : ConnectColor;
            }
            RefreshStatusLine();
        }

        void RefreshStatusLine()
        {
            if (statusLine == null) return;
            if (!IsActive)
            {
                if (!statusLine.text.StartsWith("Enter") && !statusLine.text.StartsWith("Waiting") && !statusLine.text.StartsWith("Not "))
                    statusLine.text = "Start agent.py on the laptop, then enter its address and token.";
                return;
            }
            string clock = InputLink.LaptopNow is System.DateTime now
                ? $"   Laptop clock {now.ToLocalTime():HH:mm:ss.fff}"
                : "";
            string rtt = InputLink.BestRoundTripMs is double best ? $" ({best:0} ms)" : "";
            string video = Video != null ? Video.StatusText : "needs WebRTC (SPATIAL_LAUNCHER_WEBRTC)";
            string throttled = InputLink.ThrottledCount > 0 ? " (keys throttled)" : "";
            statusLine.text = $"Video: {video}   Input: {InputLink.StatusText}{throttled}{rtt}{clock}";
        }

        protected override void Layout(Vector2 size)
        {
            if (panel == null)
            {
                Surface.Fit(size);
                Surface.transform.localPosition = Vector3.zero;
                statusLabel.transform.localPosition = new Vector3(0f, 0f, -0.002f);
                statusLabel.rectTransform.sizeDelta = Surface.DisplaySize;
                return;
            }

            // Screen above, connect panel below.
            float screenHeight = Mathf.Max(0.01f, size.y - ConnectPanelHeight);
            float screenY = size.y * 0.5f - screenHeight * 0.5f;
            Surface.Fit(new Vector2(size.x, screenHeight));
            Surface.transform.localPosition = new Vector3(0f, screenY, 0f);
            statusLabel.transform.localPosition = new Vector3(0f, screenY, -0.002f);
            statusLabel.rectTransform.sizeDelta = Surface.DisplaySize * 0.9f;

            float panelTop = -size.y * 0.5f + PanelHeight;
            panel.localPosition = new Vector3(0f, -size.y * 0.5f + PanelHeight * 0.5f, -0.001f);
            panel.localScale = new Vector3(size.x, PanelHeight, 1f);

            float rowY = panelTop - Gap * 0.5f - RowHeight * 0.5f;
            float left = -size.x * 0.5f + Gap;
            float textWidth = Mathf.Max(0.04f, (size.x - ButtonWidth - KeysButtonWidth - PortWidth - Gap * 6f) * 0.5f);
            float[] widths = { textWidth, PortWidth, textWidth };
            for (int i = 0; i < fields.Count; i++)
            {
                var (field, label) = fields[i];
                float w = widths[i];
                field.transform.localPosition = new Vector3(left + w * 0.5f, rowY, -0.002f);
                field.transform.localScale = new Vector3(w, RowHeight, 1f);
                label.transform.localPosition = new Vector3(left + w * 0.5f, rowY, -0.003f);
                label.rectTransform.sizeDelta = new Vector2(w - TextInset * 2f, RowHeight);
                left += w + Gap;
            }
            ConnectButton.transform.localPosition = new Vector3(left + ButtonWidth * 0.5f, rowY, -0.002f);
            ConnectButton.transform.localScale = new Vector3(ButtonWidth, RowHeight, 1f);
            connectLabel.transform.localPosition = new Vector3(left + ButtonWidth * 0.5f, rowY, -0.003f);
            connectLabel.rectTransform.sizeDelta = new Vector2(ButtonWidth, RowHeight);
            left += ButtonWidth + Gap;
            KeysButton.transform.localPosition = new Vector3(left + KeysButtonWidth * 0.5f, rowY, -0.002f);
            KeysButton.transform.localScale = new Vector3(KeysButtonWidth, RowHeight, 1f);
            keysLabel.transform.localPosition = new Vector3(left + KeysButtonWidth * 0.5f, rowY, -0.003f);
            keysLabel.rectTransform.sizeDelta = new Vector2(KeysButtonWidth, RowHeight);

            float statusY = -size.y * 0.5f + Gap * 0.5f + StatusHeight * 0.5f;
            statusLine.transform.localPosition = new Vector3(0f, statusY, -0.003f);
            statusLine.rectTransform.sizeDelta = new Vector2(size.x - Gap * 2f, StatusHeight);
        }

        protected override void OnDestroy()
        {
            var gate = ConsentGate.Current;
            if (gate != null)
            {
                gate.Withdraw(this);
                gate.EndLive(this);
            }
            if (InputLink != null)
            {
                InputLink.StatusChanged -= RefreshStatus;
                InputLink.StatusChanged -= FollowStreamPath;
                InputLink.Dispose();
            }
            Video?.Stop();
            if (Source != null)
            {
                Source.Changed -= RefreshStatus;
                Source.Stop();
            }
            base.OnDestroy();
        }
    }
}
