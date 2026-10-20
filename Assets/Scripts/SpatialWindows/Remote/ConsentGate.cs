using System;
using System.Collections.Generic;
using HoloKit;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>A remote screen shown in the headset (a laptop stream now; a phone or device mirror later).</summary>
    public interface IRemoteSession
    {
        /// <summary>What the user knows it as: the laptop window's title, a device name.</summary>
        string SourceName { get; }
        /// <summary>Where it comes from (an IP address or host name).</summary>
        string Address { get; }
        /// <summary>"full control" or "view-only", shown in the approval text and the live indicator.</summary>
        string ModeLabel { get; }
        /// <summary>The window showing it (closed by a panic stop), or null.</summary>
        SpatialWindow Window { get; }
        /// <summary>Disconnects everything at once (panic stop).</summary>
        void StopForPanic();
    }

    /// <summary>
    /// Nothing from another device shows in the headset until the user allows it, every session. A remote screen asks
    /// with <see cref="Request"/> before it connects: a modal window ("Allow &lt;source&gt; (&lt;address&gt;) to show its
    /// screen on this headset? (full control)") with Approve and Deny, in Mono and Stereo alike (it is a window). Approve
    /// with a hand pinch or the mouse on its buttons, HoloKit's stereo bar TRIGGER (it reads ALLOW while asking, instead of
    /// ZOOM), or in Mono a touch on the on-screen Approve button. Deny, or no answer in <see cref="TimeoutSeconds"/> (30 s),
    /// connects nothing. Nothing is remembered: an approval covers that source in that mode while one of its screens stays
    /// live; once none is, or after a restart, it asks again.
    ///
    /// While a remote screen is live a red "LIVE: &lt;source&gt;" line is drawn at the top of the view (in each eye in
    /// Stereo). Panic stop (<see cref="PanicStop"/>) disconnects every remote screen, closes their windows, refuses
    /// anything waiting and brings Home (the launcher) to the front. It is the cross-and-sweep gesture
    /// (<see cref="CrossSweepRecognizer"/>, on even when Settings switches that gesture off), F8, or in Mono a touch on
    /// the indicator's Stop.
    /// </summary>
    public class ConsentGate : MonoBehaviour
    {
        public const float DefaultTimeout = 30f;
        const string AllowLabel = "ALLOW";

        [SerializeField] WindowManager manager;
        [Tooltip("Seconds to answer before it counts as Deny.")]
        [SerializeField] float timeoutSeconds = DefaultTimeout;
        [SerializeField] Key panicKey = Key.F8;
        [SerializeField] Vector2 modalSize = new Vector2(0.56f, 0.24f);

        public static ConsentGate Current { get; private set; }

        public float TimeoutSeconds
        {
            get => timeoutSeconds;
            set => timeoutSeconds = Mathf.Max(0.1f, value);
        }

        /// <summary>A request waits for an answer.</summary>
        public bool HasPending => pending.Count > 0;
        /// <summary>A remote screen is live or waiting: the panic stop is armed.</summary>
        public bool IsActive => HasPending || live.Count > 0;
        public IReadOnlyList<IRemoteSession> Live => live;
        /// <summary>The question being asked, or "".</summary>
        public string Question => HasPending ? pending[0].Text ?? QuestionFor(pending[0].Session) : "";
        /// <summary>Seconds left to answer the current question.</summary>
        public float SecondsLeft => HasPending ? Mathf.Max(0f, LimitOf(pending[0]) - pending[0].Age) : 0f;
        /// <summary>The modal window, while asking.</summary>
        public SpatialWindow Modal => modal;
        /// <summary>The indicator line, or "" while nothing is live.</summary>
        public string LiveText => live.Count == 0 ? "" : LiveTextFor(live);
        /// <summary>Panic stops so far.</summary>
        public int PanicCount { get; private set; }
        /// <summary>A panic stop ran (after it refused everything waiting and stopped everything live).</summary>
        public event Action Panicked;
        /// <summary>The stereo bar's TRIGGER was used to approve this frame (so it doesn't also step the zoom).</summary>
        public bool TriggerUsedThisFrame => triggerFrame == Time.frameCount;
        /// <summary>The Mono on-screen buttons are drawn (asking, or live).</summary>
        public bool TouchButtonsVisible => !Stereo && HasPending;

        sealed class Pending
        {
            public IRemoteSession Session;
            public Action Approved;
            public Action<string> Denied;
            public float Age;
            /// <summary>Replaces the standard question / title / time limit (<see cref="Ask"/>).</summary>
            public string Text, Title;
            public float Limit;
        }

        sealed class AskSession : IRemoteSession
        {
            public string SourceName => "question";
            public string Address => "";
            public string ModeLabel => "";
            public SpatialWindow Window => null;
            public void StopForPanic() { }
        }

        float LimitOf(Pending p) => p.Limit > 0f ? p.Limit : timeoutSeconds;

        readonly List<Pending> pending = new List<Pending>();
        readonly List<IRemoteSession> live = new List<IRemoteSession>();
        HoloKitCameraManager holoKit;
        SpatialWindow modal;
        ConsentContent modalContent;
        int triggerFrame = -1;
        GUIStyle style, buttonStyle;
        Texture2D liveTexture, buttonTexture;
        readonly List<Rect> eyes = new List<Rect>();

        bool Stereo => holoKit != null && holoKit.ScreenRenderMode == ScreenRenderMode.Stereo;

        public static string QuestionFor(IRemoteSession s) =>
            $"Allow {s.SourceName} ({s.Address}) to show its screen on this headset? ({s.ModeLabel})";

        public static string LiveTextFor(IReadOnlyList<IRemoteSession> sessions)
        {
            var first = sessions[0];
            string more = sessions.Count > 1 ? $" +{sessions.Count - 1} more" : "";
            return $"LIVE: {first.SourceName} ({first.Address}, {first.ModeLabel}){more}";
        }

        static bool Same(IRemoteSession a, IRemoteSession b) => a.Address == b.Address && a.ModeLabel == b.ModeLabel;

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
            holoKit = FindFirstObjectByType<HoloKitCameraManager>();
        }

        void OnEnable()
        {
            Current = this;
            if (holoKit != null) holoKit.OnStereoTrigger += OnTrigger;
            if (manager != null) manager.WindowClosed += OnWindowClosed;
        }

        void OnDisable()
        {
            if (holoKit != null) holoKit.OnStereoTrigger -= OnTrigger;
            if (manager != null) manager.WindowClosed -= OnWindowClosed;
            if (Current == this) Current = null;
        }

        /// <summary>
        /// Asks the user whether <paramref name="session"/> may show its screen. <paramref name="approved"/> runs on
        /// Approve (connect then), <paramref name="denied"/> on Deny, timeout, panic stop or <see cref="Withdraw"/>
        /// (with the reason). A view-only source already live is approved at once; full control always asks.
        /// </summary>
        public void Request(IRemoteSession session, Action approved, Action<string> denied)
        {
            if (session == null) return;
            Withdraw(session);
            // Full control always asks, whatever else is live; an other that is still waiting for its own answer covers nothing.
            if (session.ModeLabel != "full control")
                foreach (var other in live)
                    if (other != session && Same(other, session) && !pending.Exists(p => p.Session == other))
                {
                    approved?.Invoke();
                    return;
                }
            pending.Add(new Pending { Session = session, Approved = approved, Denied = denied });
            if (pending.Count == 1) ShowModal();
        }

        /// <summary>
        /// Asks a plain yes/no question in the same modal (pinch, mouse, TRIGGER, touch). <paramref name="answer"/> gets
        /// true on Approve, false on Deny, timeout, panic stop or a closed modal. Returns a token for <see cref="Withdraw"/>
        /// (which drops the question without calling <paramref name="answer"/>).
        /// </summary>
        public IRemoteSession Ask(string question, float timeoutSeconds, Action<bool> answer, string title)
        {
            var token = new AskSession();
            pending.Add(new Pending
            {
                Session = token, Text = question, Title = title, Limit = Mathf.Max(0.1f, timeoutSeconds),
                Approved = () => answer?.Invoke(true), Denied = _ => answer?.Invoke(false),
            });
            if (pending.Count == 1) ShowModal();
            return token;
        }

        /// <summary>The session no longer wants an answer (its window closed or it disconnected first).</summary>
        public void Withdraw(IRemoteSession session)
        {
            int index = pending.FindIndex(p => p.Session == session);
            if (index < 0) return;
            pending.RemoveAt(index);
            if (index == 0) Next();
        }

        /// <summary>Allows the current request (the Approve button, TRIGGER, a touch).</summary>
        public void Approve()
        {
            if (!HasPending) return;
            var request = pending[0];
            pending.RemoveAt(0);
            request.Approved?.Invoke();
            Next();
        }

        /// <summary>Refuses the current request: nothing connects.</summary>
        public void Deny() => Refuse("denied");

        void Refuse(string reason)
        {
            if (!HasPending) return;
            var request = pending[0];
            pending.RemoveAt(0);
            request.Denied?.Invoke(reason);
            Next();
        }

        void Next()
        {
            CloseModal();
            if (HasPending) ShowModal();
        }

        /// <summary>A session connected after approval: it shows in the live indicator until <see cref="EndLive"/>.</summary>
        public void BeginLive(IRemoteSession session)
        {
            if (session != null && !live.Contains(session)) live.Add(session);
        }

        public void EndLive(IRemoteSession session) => live.Remove(session);

        /// <summary>
        /// Disconnects every live remote screen and closes its window, refuses everything waiting, and brings Home to the
        /// front. Safe to call any time.
        /// </summary>
        public void PanicStop(string why = "panic stop")
        {
            PanicCount++;
            while (HasPending) Refuse(why);
            var sessions = new List<IRemoteSession>(live);
            live.Clear();
            foreach (var session in sessions)
            {
                try { session.StopForPanic(); }
                catch (Exception error) { Debug.LogWarning($"[Consent] Stopping {session.SourceName} failed: {error.Message}"); }
                if (session.Window != null && manager != null) manager.Close(session.Window);
            }
            if (manager != null && manager.WindowsHidden) manager.ShowAll();
            if (TryGetComponent<LauncherWindow>(out var launcher)) launcher.OpenLauncher();
            Debug.Log($"[Consent] {why}: {sessions.Count} remote screen(s) disconnected");
            Panicked?.Invoke();
        }

        void ShowModal()
        {
            if (manager == null || !HasPending || modal != null) return;
            modal = manager.Spawn(pending[0].Title ?? "Allow screen?", modalSize);
            if (modal == null) return;
            modal.SetSolid(true);
            modalContent = new GameObject().AddComponent<ConsentContent>();
            modalContent.Init(modal, manager.WindowMaterial, this);
        }

        void CloseModal()
        {
            var closing = modal;
            modal = null;
            modalContent = null;
            if (closing != null && manager != null) manager.Close(closing);
        }

        // Someone closed the modal with its close button: that is a Deny.
        void OnWindowClosed(SpatialWindow window)
        {
            if (window == null || window != modal) return;
            modal = null;
            modalContent = null;
            Refuse("closed");
        }

        void OnTrigger()
        {
            if (!HasPending) return;
            triggerFrame = Time.frameCount;
            Approve();
        }

        void Update()
        {
            if (HasPending)
            {
                pending[0].Age += Time.unscaledDeltaTime;
                if (pending[0].Age >= LimitOf(pending[0]))
                {
                    Refuse("timed out");
                    return;
                }
                // The modal is gone some other way (destroyed): that is a Deny too.
                if (modal == null && manager != null) Refuse("closed");
            }
            live.RemoveAll(s => s == null || (s is UnityEngine.Object o && !o));

            if (holoKit != null && HasPending)
            {
                holoKit.StereoTriggerLabel = AllowLabel;
                holoKit.StereoTriggerPressedLabel = "ALLOWED";
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && !KeyboardShortcuts.Blocked && keyboard[panicKey].wasPressedThisFrame && IsActive) PanicStop("panic stop (key)");
            if (!Stereo) HandleTouch();
        }

        // --- Mono on-screen buttons and the indicator -----------------------------------------------------------------

        /// <summary>Approve and Deny buttons (IMGUI rects, top-left origin) for a screen of this size: bottom centre.</summary>
        public static void ButtonRects(Vector2 screen, out Rect approve, out Rect deny)
        {
            float unit = Mathf.Min(screen.x, screen.y);
            var size = new Vector2(unit * 0.3f, unit * 0.1f);
            float gap = unit * 0.04f, y = screen.y - unit * 0.2f - size.y;
            approve = new Rect(screen.x * 0.5f - gap * 0.5f - size.x, y, size.x, size.y);
            deny = new Rect(screen.x * 0.5f + gap * 0.5f, y, size.x, size.y);
        }

        /// <summary>The indicator's Stop button in Mono (IMGUI rect): right of the live line at the top centre.</summary>
        public static Rect StopRect(Vector2 screen)
        {
            float unit = Mathf.Min(screen.x, screen.y);
            return new Rect(screen.x * 0.5f + unit * 0.45f, unit * 0.03f, unit * 0.16f, unit * 0.07f);
        }

        void HandleTouch()
        {
            if (!HasPending && live.Count == 0) return;
            var screen = new Vector2(Screen.width, Screen.height);
            ButtonRects(screen, out var approve, out var deny);
            var stop = StopRect(screen);
            foreach (var point in Presses())
            {
                if (HasPending && PassthroughZoomControl.Hits(approve, point, screen.y)) { Approve(); return; }
                if (HasPending && PassthroughZoomControl.Hits(deny, point, screen.y)) { Deny(); return; }
                if (live.Count > 0 && PassthroughZoomControl.Hits(stop, point, screen.y)) { PanicStop("panic stop (touch)"); return; }
            }
        }

        static IEnumerable<Vector2> Presses()
        {
            var screen = Touchscreen.current;
            if (screen != null)
                foreach (var touch in screen.touches)
                    if (touch.press.wasPressedThisFrame) yield return touch.position.ReadValue();
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) yield return mouse.position.ReadValue();
        }

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || (!HasPending && live.Count == 0)) return;
            EnsureStyles();
            var screen = new Vector2(Screen.width, Screen.height);
            float unit = Mathf.Min(screen.x, screen.y);
            if (live.Count > 0)
            {
                string text = LiveText + "  - cross arms and sweep to stop";
                if (Stereo)
                {
                    // Top centre of each eye's viewport, small enough to fit it.
                    eyes.Clear();
                    foreach (var camera in Camera.allCameras)
                        if (camera.cullingMask != 0 && camera.rect.width < 0.99f) eyes.Add(camera.pixelRect);
                    style.fontSize = Mathf.RoundToInt(unit * 0.022f);
                    foreach (var eye in eyes)
                    {
                        var size = style.CalcSize(new GUIContent(text));
                        float width = Mathf.Min(size.x, eye.width * 0.9f);
                        GUI.Label(new Rect(eye.center.x - width * 0.5f, screen.y - eye.yMax + unit * 0.02f, width, size.y), text, style);
                    }
                }
                else
                {
                    style.fontSize = Mathf.RoundToInt(unit * 0.03f);
                    var size = style.CalcSize(new GUIContent(text));
                    float width = Mathf.Min(size.x, screen.x * 0.9f);
                    GUI.Label(new Rect(screen.x * 0.5f - width * 0.5f, unit * 0.03f + unit * 0.08f, width, size.y), text, style);
                    buttonStyle.fontSize = Mathf.RoundToInt(unit * 0.03f);
                    GUI.Label(StopRect(screen), "Stop", buttonStyle);
                }
            }
            if (TouchButtonsVisible)
            {
                ButtonRects(screen, out var approve, out var deny);
                buttonStyle.fontSize = Mathf.RoundToInt(unit * 0.035f);
                GUI.Label(approve, "Approve", buttonStyle);
                GUI.Label(deny, $"Deny ({SecondsLeft:0})", buttonStyle);
            }
        }

        void EnsureStyles()
        {
            if (style != null) return;
            liveTexture = Solid(new Color(0.75f, 0.08f, 0.08f, 0.85f), "Live Indicator");
            buttonTexture = Solid(new Color(0.1f, 0.1f, 0.12f, 0.85f), "Consent Button");
            style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };
            style.normal.textColor = Color.white;
            style.normal.background = liveTexture;
            style.padding = new RectOffset(12, 12, 6, 6);
            buttonStyle = new GUIStyle(style);
            buttonStyle.normal.background = buttonTexture;
        }

        static Texture2D Solid(Color color, string name)
        {
            var texture = new Texture2D(1, 1) { name = name };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        void OnDestroy()
        {
            if (liveTexture != null) Destroy(liveTexture);
            if (buttonTexture != null) Destroy(buttonTexture);
        }
    }
}
