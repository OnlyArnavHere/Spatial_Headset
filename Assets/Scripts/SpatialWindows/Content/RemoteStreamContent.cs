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
    /// Laptop windows are <see cref="InitLinked"/> windows (below). The title bar shows the control round-trip time. Keyboard
    /// input reaches the laptop through <see cref="KeyboardSink"/> (see <see cref="RemoteKeyboard"/>).
    ///
    /// Cast mode (<see cref="IsCast"/>, the launcher's Cast icon): view-only. The screen takes no pointer input, no
    /// keyboard reaches it and there is no Keys button; a bar of media controls (previous, seek back, play/pause, seek
    /// forward, next, volume down/up, mute) sends only media actions, which an agent started with --cast-only accepts
    /// while dropping everything else. The consent question says "view-only".
    ///
    /// <see cref="InitLinked"/> (needs SPATIAL_LAUNCHER_WEBRTC): a window on one slot of the headset's link session. The
    /// video is the slot's SlotVideoSource and input goes through its DataChannelInputSink. The session starts view-only;
    /// a badge shows View only / Requesting / Control / Control revoked / Denied, and Request control asks
    /// <see cref="ConsentGate"/> for "full control": only its approve callback lets the control client send the request.
    /// Closing the window sends close{slot}. Cast windows have only media buttons and never offer control.
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
        const float KeysButtonWidth = 0.07f;
        const float LinkedButtonWidth = 0.16f;
        const float TextInset = 0.008f;
        const float CastBarRow = 0.04f;
        static readonly Color MediaColor = new Color(0.24f, 0.26f, 0.32f);
        static readonly string[] MediaLabels = { "Prev", "Back", "Play/Pause", "Fwd", "Next", "Vol -", "Vol +", "Mute" };

        /// <summary>Extra window height a Cast window's media bar needs, on top of <see cref="ConnectPanelHeight"/>.</summary>
        public const float CastBarHeight = CastBarRow + Gap;

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
        public WindowButton ConnectButton { get; private set; }
        /// <summary>Opens the virtual keyboard for this window.</summary>
        public WindowButton KeysButton { get; private set; }
        /// <summary>The Keys button was clicked.</summary>
        public event Action KeyboardRequested;
        /// <summary>Where keyboard input for this screen goes: the input sink, if it takes keys (null otherwise).</summary>
        public IRemoteKeyboardSink KeyboardSink => IsCast ? null : Source?.Sink as IRemoteKeyboardSink;
        /// <summary>A view-only Cast window with media controls instead of pointer and keyboard input.</summary>
        public bool IsCast { get; private set; }
        /// <summary>Cast mode: the media buttons by action name (RemoteInputEncoding.MediaActions).</summary>
        public IReadOnlyDictionary<string, WindowButton> MediaButtons => mediaButtons;
        /// <summary>Cast mode: the last media button pressed, and whether it was sent.</summary>
        public string LastMediaAction { get; private set; }
        public bool LastMediaSent { get; private set; }
        public string StatusLine => statusLine != null ? statusLine.text : "";
        /// <summary>True while a linked window's session is running.</summary>
        public bool IsActive { get; private set; }
        /// <summary>What this window streams from the laptop; null if it wasn't opened from the picker.</summary>
        public LaptopTarget Target { get; private set; }
        /// <summary>Waiting for the user to approve (<see cref="ConsentGate"/>); nothing is connected yet.</summary>
        public bool AwaitingConsent { get; private set; }
        /// <summary>How the last approval request ended: "approved", "denied", "timed out"... or "" before any.</summary>
        public string ConsentOutcome { get; private set; } = "";

        // IRemoteSession: what the consent question and the live indicator name.
        public string SourceName => Target != null && !string.IsNullOrEmpty(Target.title) ? Target.title : "Laptop";
        public string Address => LinkedAddress ?? "";
        /// <summary>
        /// "full control" (pointer and keyboard reach the laptop window) or "view-only" (Cast). A linked window says
        /// "full control" only while control is granted or being asked for, otherwise "view-only".
        /// </summary>
        public string ModeLabel => IsCast ? "view-only" : linked && !ControlGrantedOrAsking ? "view-only" : "full control";

        bool linked;
        bool asking;
        string denial;
        TextMeshPro badgeLabel;
        string LinkedAddress =>
#if SPATIAL_LAUNCHER_WEBRTC
            Link != null ? Link.Address :
#endif
            null;
        bool ControlGrantedOrAsking =>
#if SPATIAL_LAUNCHER_WEBRTC
            Link != null && !Link.IsEnded && (asking || Link.Control.State == LinkSessionState.Control || Link.Control.State == LinkSessionState.Requested);
#else
            false;
#endif
        /// <summary>The control badge of a linked window: View only, Requesting…, Control, Control revoked, Denied: ...</summary>
        public string Badge { get; private set; } = "";
        public bool IsLinked => linked;

        RemoteConnectionSettings settings;
        TextMeshPro statusLabel, statusLine, connectLabel, keysLabel;
        Transform panel;
        readonly Dictionary<string, WindowButton> mediaButtons = new Dictionary<string, WindowButton>();
        readonly List<TextMeshPro> mediaLabels = new List<TextMeshPro>();

        /// <summary>A screen with no connection: input goes to <paramref name="sink"/> (may be null).</summary>
        /// <param name="expectedSize">The remote screen's resolution (aspect until a stream reports its own).</param>
        public void Init(SpatialWindow window, Material baseMaterial, IRemoteInputSink sink, Vector2Int expectedSize)
        {
            Attach(window, baseMaterial);
            Build(new RemoteStreamSource(sink, expectedSize));
        }

#if SPATIAL_LAUNCHER_WEBRTC
        /// <summary>The link session this window shows a slot of (linked windows only).</summary>
        public LinkSession Link { get; private set; }
        public int Slot { get; private set; } = -1;
        public DataChannelInputSink LinkInput { get; private set; }

        /// <summary>
        /// A window on slot <paramref name="slot"/> of <paramref name="session"/>, already opened on the laptop for
        /// <paramref name="target"/>. View-only until the user asks for control (not offered for a cast).
        /// </summary>
        public void InitLinked(SpatialWindow window, Material baseMaterial, RemoteConnectionSettings connectionSettings,
            LinkSession session, int slot, LaptopTarget target, bool cast = false)
        {
            Attach(window, baseMaterial);
            settings = connectionSettings;
            linked = true;
            Link = session;
            Slot = slot;
            Target = target;
            IsCast = cast;
            var expected = target != null && target.width > 0 && target.height > 0
                ? new Vector2Int(target.width, target.height)
                : settings != null ? settings.expectedResolution : new Vector2Int(1920, 1080);
            var video = session.Slots[slot];
            LinkInput = session.Inputs[slot];
            Video = video;
            Build(new RemoteStreamSource(video, cast ? null : LinkInput, expected) { ViewOnly = cast });
            BuildPanel();
            video.Start();
            session.Control.StateChanged += RefreshStatus;
            session.Ended += OnLinkEnded;
            IsActive = !session.IsEnded;
            if (IsActive) ConsentGate.Current?.BeginLive(this);
            RefreshStatus();
            Relayout();
        }

        void OnLinkEnded(string reason)
        {
            if (this == null) return;
            IsActive = false;
            ConsentGate.Current?.EndLive(this);
            RefreshStatus();
        }

        // The Request/Release control button.
        void ToggleControl()
        {
            if (Link == null || IsCast || Link.IsEnded) return;
            var control = Link.Control;
            var gate = ConsentGate.Current;
            if (control.State == LinkSessionState.Control || control.State == LinkSessionState.Requested)
            {
                if (asking && gate != null) gate.Withdraw(this);
                asking = false;
                AwaitingConsent = false;
                control.ReleaseControl();
                RefreshStatus();
                return;
            }
            if (control.State != LinkSessionState.ViewOnly) return;
            denial = null;
            if (gate == null)
            {
                ConsentOutcome = "no consent gate";
                denial = "no consent gate";
                RefreshStatus();
                return;
            }
            asking = true;
            AwaitingConsent = true;
            gate.Request(this, () =>
            {
                asking = false;
                AwaitingConsent = false;
                ConsentOutcome = "approved";
                if (this == null || Link == null || Link.IsEnded) return;
                // The only place that approves a control request; it sends control-request itself.
                Link.Control.ApproveControlFromConsent();
                RefreshStatus();
            }, reason =>
            {
                asking = false;
                AwaitingConsent = false;
                ConsentOutcome = reason;
                if (this == null) return;
                denial = reason;
                RefreshStatus();
            });
            RefreshStatus();
        }

        string ComputeBadge()
        {
            if (Link.IsEnded) return "Session ended";
            if (IsCast) return "View only";
            if (denial != null) return "Denied: " + denial;
            switch (Link.Control.State)
            {
                case LinkSessionState.ViewOnly:
                    return Link.Control.ControlDeniedReason != null ? "Denied: " + Link.Control.ControlDeniedReason : asking ? "Waiting for approval…" : "View only";
                case LinkSessionState.Requested: return "Requesting…";
                case LinkSessionState.Control: return "Control";
                case LinkSessionState.Revoked: return "Control revoked";
                default: return "Connecting…";
            }
        }

        void RefreshLinked()
        {
            var state = Link.Control.State;
            Badge = ComputeBadge();
            badgeLabel.text = Badge;
            bool offer = !IsCast && !Link.IsEnded
                && (state == LinkSessionState.ViewOnly || state == LinkSessionState.Requested || state == LinkSessionState.Control);
            ConnectButton.gameObject.SetActive(offer);
            connectLabel.gameObject.SetActive(offer);
            bool release = state == LinkSessionState.Requested || state == LinkSessionState.Control;
            connectLabel.text = release ? "Release control" : "Request control";
            ConnectButton.GetComponent<MeshRenderer>().sharedMaterial.color = release ? DisconnectColor : ConnectColor;
            RefreshLinkedLine();
        }

        void RefreshLinkedLine()
        {
            if (statusLine == null) return;
            var control = Link.Control;
            string rtt = control.BestRoundTripMs is double best ? $"   {best:0} ms" : "";
            string throttled = LinkInput.ThrottledCount > 0 ? " (keys throttled)" : "";
            string wait = asking && ConsentGate.Current != null ? $"   Waiting for approval on the headset ({ConsentGate.Current.TimeoutSeconds:0} s)..." : "";
            statusLine.text = $"{Link.LaptopName}   Video: {Link.Slots[Slot].StatusText}{throttled}{rtt}{wait}";
        }
#endif

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
            badgeLabel = CreateLabel("Badge", transform, "", 0.22f, TextAlignmentOptions.MidlineLeft);
            badgeLabel.richText = false;

            ConnectButton = CreateControl<WindowButton>("ConnectButton", transform, CreateMaterial(ConnectColor));
            connectLabel = CreateLabel("ConnectLabel", transform, "Request control", 0.22f, TextAlignmentOptions.Center);
            ConnectButton.Clicked += () =>
            {
#if SPATIAL_LAUNCHER_WEBRTC
                if (linked) ToggleControl();
#endif
            };

            KeysButton = CreateControl<WindowButton>("KeysButton", transform, CreateMaterial(KeysColor));
            keysLabel = CreateLabel("KeysLabel", transform, "Keys", 0.22f, TextAlignmentOptions.Center);
            KeysButton.Clicked += () => KeyboardRequested?.Invoke();
            if (IsCast)
            {
                // No keyboard for a cast; media controls instead.
                KeysButton.gameObject.SetActive(false);
                keysLabel.gameObject.SetActive(false);
                for (int i = 0; i < RemoteInputEncoding.MediaActions.Length; i++)
                {
                    string action = RemoteInputEncoding.MediaActions[i];
                    var button = CreateControl<WindowButton>($"Media {action}", transform, CreateMaterial(MediaColor));
                    var label = CreateLabel($"MediaLabel {action}", transform, MediaLabels[i], 0.18f, TextAlignmentOptions.Center);
                    button.Clicked += () => SendMedia(action);
                    mediaButtons[action] = button;
                    mediaLabels.Add(label);
                }
            }

            statusLine = CreateLabel("StatusLine", transform, "", 0.17f, TextAlignmentOptions.MidlineLeft);
            statusLine.color = new Color(0.75f, 0.77f, 0.82f);
        }

        /// <summary>Cast mode: sends a media control (a media button does this). False if it couldn't be sent.</summary>
        public bool SendMedia(string action)
        {
#if SPATIAL_LAUNCHER_WEBRTC
            if (linked)
            {
                if (!IsCast) return false;
                LastMediaAction = action;
                LastMediaSent = IsActive && LinkInput.SendMedia(action);
                if (!LastMediaSent && statusLine != null)
                    statusLine.text = !IsActive ? "Not sent: the session ended" : "Not sent: start the agent with --cast-only for media controls";
                return LastMediaSent;
            }
#endif
            return false;
        }

        /// <summary>Panic stop: disconnects video and input at once (the gate closes the window).</summary>
        public void StopForPanic()
        {
#if SPATIAL_LAUNCHER_WEBRTC
            if (linked)
            {
                Link?.StopForPanic();
                return;
            }
#endif
        }

        void Update()
        {
#if SPATIAL_LAUNCHER_WEBRTC
            if (linked)
            {
                Video?.Tick();
                LinkInput.Tick();
                double? linkRtt = IsActive ? Link.Control.RoundTripMs : null;
                Window.SetTitleDetail(linkRtt.HasValue ? $"{linkRtt.Value:0} ms" : null);
                RefreshLinkedLine();
                return;
            }
#endif
        }

        void RefreshStatus()
        {
            if (statusLabel == null || Source == null) return;
            bool showPlaceholder = !Source.IsConnected || Source.Texture == null;
            statusLabel.gameObject.SetActive(showPlaceholder);
            statusLabel.text = Source.StatusText;
#if SPATIAL_LAUNCHER_WEBRTC
            if (linked && Link != null && connectLabel != null)
            {
                RefreshLinked();
                return;
            }
#endif
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

            // Screen above, (cast: media bar,) connect panel below.
            float barHeight = IsCast ? CastBarHeight : 0f;
            float screenHeight = Mathf.Max(0.01f, size.y - ConnectPanelHeight - barHeight);
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
            float connectWidth;
            connectWidth = LinkedButtonWidth;
            float badgeWidth = Mathf.Max(0.04f, size.x - connectWidth - (IsCast ? 0f : KeysButtonWidth) - Gap * 4f);
            badgeLabel.transform.localPosition = new Vector3(left + badgeWidth * 0.5f, rowY, -0.003f);
            badgeLabel.rectTransform.sizeDelta = new Vector2(badgeWidth - TextInset * 2f, RowHeight);
            left += badgeWidth + Gap;
            ConnectButton.transform.localPosition = new Vector3(left + connectWidth * 0.5f, rowY, -0.002f);
            ConnectButton.transform.localScale = new Vector3(connectWidth, RowHeight, 1f);
            connectLabel.transform.localPosition = new Vector3(left + connectWidth * 0.5f, rowY, -0.003f);
            connectLabel.rectTransform.sizeDelta = new Vector2(connectWidth, RowHeight);
            left += connectWidth + Gap;
            KeysButton.transform.localPosition = new Vector3(left + KeysButtonWidth * 0.5f, rowY, -0.002f);
            KeysButton.transform.localScale = new Vector3(KeysButtonWidth, RowHeight, 1f);
            keysLabel.transform.localPosition = new Vector3(left + KeysButtonWidth * 0.5f, rowY, -0.003f);
            keysLabel.rectTransform.sizeDelta = new Vector2(KeysButtonWidth, RowHeight);

            if (IsCast)
            {
                float barY = panelTop + Gap * 0.5f + CastBarRow * 0.5f;
                int count = RemoteInputEncoding.MediaActions.Length;
                float cell = (size.x - Gap * (count + 1)) / count;
                for (int i = 0; i < count; i++)
                {
                    float x = -size.x * 0.5f + Gap + cell * (i + 0.5f) + Gap * i;
                    var button = mediaButtons[RemoteInputEncoding.MediaActions[i]].transform;
                    button.localPosition = new Vector3(x, barY, -0.002f);
                    button.localScale = new Vector3(cell, CastBarRow, 1f);
                    mediaLabels[i].transform.localPosition = new Vector3(x, barY, -0.003f);
                    mediaLabels[i].rectTransform.sizeDelta = new Vector2(cell, CastBarRow);
                }
            }

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
#if SPATIAL_LAUNCHER_WEBRTC
            if (Link != null)
            {
                Link.Control.StateChanged -= RefreshStatus;
                Link.Ended -= OnLinkEnded;
            }
#endif
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
