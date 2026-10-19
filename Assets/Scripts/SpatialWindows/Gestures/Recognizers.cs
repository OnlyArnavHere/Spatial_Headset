using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// (1) A fist closed while pointing at a window grabs it; the window then follows the fist in 3D: it turns about the
    /// head with the fist's direction (so it stays "under" the fist) and moves in and out with the fist's estimated depth
    /// (× <see cref="GestureConfig.grabDepthGain"/>), so pulling the fist back pulls the window in. Opening the hand
    /// releases it, or hands it to <see cref="PalmPushRecognizer"/> when the hand is moving in or out fast.
    /// </summary>
    public sealed class FistGrabRecognizer : GestureRecognizer
    {
        sealed class Grab
        {
            public SpatialWindow Window;
            public Vector3 WindowDirection, HandDirection;
            public float WindowDistance, HandDepth;
            public bool Consumed; // this fist already tried to grab
        }

        readonly Grab[] grabs = { new Grab(), new Grab() };

        public override string Name => "Fist grab";
        public SpatialWindow Grabbed(Handedness hand) => grabs[(int)hand].Window;

        public override void Tick(in GestureFrame frame)
        {
            foreach (var hand in GestureRecognizers.Hands)
            {
                var f = frame.Get(hand);
                var g = grabs[(int)hand];
                if (!f.Fist || !f.Tracked) g.Consumed = false;

                if (g.Window != null)
                {
                    if (!f.Tracked) End(hand, "lost tracking");
                    else if (f.OpenFor >= Config.openHoldTime) Released(hand, f);
                    else Follow(g, f, frame.Head);
                }
                else if (!g.Consumed && f.Fresh && f.Fist && f.FistFor >= Config.fistHoldTime && Host.Owner(hand) == null)
                {
                    g.Consumed = true;
                    var window = Host.PickWindow(f.State.PointingRay);
                    if (window != null && Host.Claim(hand, this)) Begin(hand, g, window, f, frame.Head);
                }
            }
            State = grabs[1].Window != null ? $"Grabbing '{grabs[1].Window.Title}' (right)"
                : grabs[0].Window != null ? $"Grabbing '{grabs[0].Window.Title}' (left)" : "Idle";
        }

        void Begin(Handedness hand, Grab g, SpatialWindow window, HandFeatures f, Pose head)
        {
            g.Window = window;
            var offset = window.transform.position - head.position;
            g.WindowDistance = offset.magnitude;
            g.WindowDirection = offset.normalized;
            g.HandDirection = f.Direction;
            g.HandDepth = f.Depth;
            Host.Manager?.Focus(window);
            Event($"grab '{window.Title}' ({hand.ToString().ToLowerInvariant()})");
        }

        void Follow(Grab g, HandFeatures f, Pose head)
        {
            var turn = Quaternion.FromToRotation(g.HandDirection, f.Direction);
            float distance = Mathf.Clamp(g.WindowDistance + (f.Depth - g.HandDepth) * Config.grabDepthGain,
                Config.minWindowDistance, Config.maxWindowDistance);
            g.Window.MoveTo(head.position + turn * g.WindowDirection * distance);
        }

        void Released(Handedness hand, HandFeatures f)
        {
            var window = grabs[(int)hand].Window;
            End(hand, "release");
            var push = Host.PalmPush;
            if (push.Enabled && Mathf.Abs(f.PeakVelocity) >= Config.pushStartSpeed) push.TryBegin(hand, window, f);
        }

        void End(Handedness hand, string why)
        {
            var g = grabs[(int)hand];
            if (g.Window == null) return;
            string title = g.Window != null ? g.Window.Title : "?";
            g.Window = null;
            Host.Release(hand, this);
            Event($"{why} '{title}' ({hand.ToString().ToLowerInvariant()})");
        }

        public override void Cancel()
        {
            foreach (var hand in GestureRecognizers.Hands)
            {
                End(hand, "cancel");
                grabs[(int)hand].Consumed = false;
            }
            State = "Idle";
        }
    }

    /// <summary>
    /// (2) Opening a fist into a palm while moving the hand in or out pushes the window it held (or pointed at) away,
    /// or pulls it back for a backward motion: while the palm stays open, the window's distance follows the palm's
    /// estimated depth × <see cref="GestureConfig.pushGain"/>, both ways. Ends when the palm stops, closes, or is lost.
    /// </summary>
    public sealed class PalmPushRecognizer : GestureRecognizer
    {
        sealed class Push
        {
            public SpatialWindow Window;
            public Vector3 Direction;
            public float StartDistance, StartDepth, StillFor, Elapsed;
        }

        readonly Push[] pushes = { new Push(), new Push() };

        public override string Name => "Palm push";
        public SpatialWindow Pushing(Handedness hand) => pushes[(int)hand].Window;

        /// <summary>Starts pushing a window with this hand (from a grab's release, or on its own).</summary>
        public bool TryBegin(Handedness hand, SpatialWindow window, HandFeatures f)
        {
            if (window == null || !Enabled || !Host.Claim(hand, this)) return false;
            var p = pushes[(int)hand];
            var offset = window.transform.position - Host.HeadPose.position;
            p.Window = window;
            p.Direction = offset.normalized;
            p.StartDistance = offset.magnitude;
            p.StartDepth = f.Depth;
            p.StillFor = p.Elapsed = 0f;
            f.FistTargetUsed = true;
            Event($"{(f.PeakVelocity >= 0f ? "push" : "pull")} '{window.Title}' ({hand.ToString().ToLowerInvariant()})");
            return true;
        }

        public override void Tick(in GestureFrame frame)
        {
            foreach (var hand in GestureRecognizers.Hands)
            {
                var f = frame.Get(hand);
                var p = pushes[(int)hand];
                if (p.Window != null)
                {
                    if (!f.Tracked) End(hand, "lost tracking");
                    else if (f.Fist && f.FistFor >= Config.fistHoldTime) End(hand, "closed");
                    else
                    {
                        p.Elapsed += frame.DeltaTime;
                        float distance = Mathf.Clamp(p.StartDistance + (f.Depth - p.StartDepth) * Config.pushGain,
                            Config.minWindowDistance, Config.maxWindowDistance);
                        p.Window.MoveTo(frame.Head.position + p.Direction * distance);
                        p.StillFor = Mathf.Abs(f.DepthVelocity) < Config.pushStopSpeed ? p.StillFor + frame.DeltaTime : 0f;
                        if (p.StillFor >= Config.pushStopTime || p.Elapsed >= Config.pushMaxTime) End(hand, "stopped");
                    }
                }
                else if (f.Fresh && f.Open && Host.Owner(hand) == null && !f.FistTargetUsed && f.FistTarget != null
                         && frame.Time - f.FistEndedAt <= Config.pushTransitionTime && Mathf.Abs(f.PeakVelocity) >= Config.pushStartSpeed)
                {
                    TryBegin(hand, f.FistTarget, f); // a fist that didn't grab (fist grab off)
                }
            }
            State = pushes[1].Window != null ? $"Pushing '{pushes[1].Window.Title}' (right)"
                : pushes[0].Window != null ? $"Pushing '{pushes[0].Window.Title}' (left)" : "Idle";
        }

        void End(Handedness hand, string why)
        {
            var p = pushes[(int)hand];
            if (p.Window == null) return;
            float distance = Vector3.Distance(p.Window.transform.position, Host.HeadPose.position);
            string title = p.Window.Title;
            p.Window = null;
            Host.Release(hand, this);
            Event($"{why}: '{title}' at {distance:F2} m ({hand.ToString().ToLowerInvariant()})");
        }

        public override void Cancel()
        {
            End(Handedness.Left, "cancel");
            End(Handedness.Right, "cancel");
            State = "Idle";
        }
    }

    /// <summary>
    /// (3) Both hands pinching the same window (the second within <see cref="GestureConfig.stretchStartWindow"/> of the
    /// first) stretch it: the change in the distance between the hands scales it, and the change in their horizontal /
    /// vertical ratio changes its aspect (unless the window has an aspect preset). The window stays centred. Ends when
    /// either pinch opens for longer than <see cref="GestureConfig.pinchReleaseGrace"/>, or a hand is lost.
    /// </summary>
    public sealed class TwoHandStretchRecognizer : GestureRecognizer
    {
        public SpatialWindow Window { get; private set; }
        /// <summary>The hands' (|dx|, |dy|) in head space when the stretch began, and in the last update.</summary>
        public Vector2 StartSeparation => startSeparation;
        public Vector2 LastSeparation { get; private set; }
        public Vector2 StartSize => startSize;
        Vector2 startSeparation, startSize;
        bool consumed;

        public override string Name => "Two-hand stretch";

        /// <summary>The size for a stretch: separations are the hands' (|dx|, |dy|) in head space.</summary>
        public static Vector2 StretchedSize(Vector2 startSize, Vector2 startSeparation, Vector2 separation, GestureConfig c)
        {
            Vector2 Floor(Vector2 v) => new Vector2(Mathf.Max(Mathf.Abs(v.x), c.stretchMinComponent), Mathf.Max(Mathf.Abs(v.y), c.stretchMinComponent));
            var s0 = Floor(startSeparation);
            var s1 = Floor(separation);
            float scale = s1.magnitude / s0.magnitude;
            float aspect = Mathf.Clamp(s1.x / s1.y / (s0.x / s0.y), 1f / c.maxAspectChange, c.maxAspectChange);
            float root = Mathf.Sqrt(aspect);
            return new Vector2(startSize.x * scale * root, startSize.y * scale / root);
        }

        public override void Tick(in GestureFrame frame)
        {
            var l = frame.Left;
            var r = frame.Right;
            if (!l.Pinched || !r.Pinched) consumed = false;

            if (Window != null)
            {
                if (!l.Tracked || !r.Tracked) End("lost a hand");
                else if (l.NotPinchedFor > Config.pinchReleaseGrace || r.NotPinchedFor > Config.pinchReleaseGrace) End("release");
                else
                {
                    LastSeparation = Separation(l, r, frame.Head);
                    Host.Manager?.Resize(Window, StretchedSize(startSize, startSeparation, LastSeparation, Config));
                }
            }
            else if (!consumed && l.Fresh && r.Fresh && l.Pinched && r.Pinched
                     && Mathf.Abs(l.PinchFor - r.PinchFor) <= Config.stretchStartWindow)
            {
                consumed = true;
                var window = Host.PickWindow(l.State.PointingRay);
                if (window != null && window == Host.PickWindow(r.State.PointingRay)
                    && Host.Owner(Handedness.Left) == null && Host.Owner(Handedness.Right) == null)
                {
                    Host.Claim(Handedness.Left, this);
                    Host.Claim(Handedness.Right, this);
                    Window = window;
                    startSize = window.Size;
                    startSeparation = Separation(l, r, frame.Head);
                    Host.Manager?.Focus(window);
                    Event($"stretch '{window.Title}'");
                }
            }
            State = Window != null ? $"Stretching '{Window.Title}' to {Window.Size.x:F2} x {Window.Size.y:F2} m" : "Idle";
        }

        static Vector2 Separation(HandFeatures l, HandFeatures r, Pose head)
        {
            var d = Quaternion.Inverse(head.rotation) * (r.State.IndexTipPosition - l.State.IndexTipPosition);
            return new Vector2(Mathf.Abs(d.x), Mathf.Abs(d.y));
        }

        void End(string why)
        {
            if (Window == null) return;
            string text = $"{why}: '{Window.Title}' at {Window.Size.x:F2} x {Window.Size.y:F2} m";
            Window = null;
            Host.Release(Handedness.Left, this);
            Host.Release(Handedness.Right, this);
            Event(text);
        }

        public override void Cancel()
        {
            End("cancel");
            consumed = false;
            State = "Idle";
        }
    }

    /// <summary>
    /// (4) Crossing the wrists (left wrist right of the right one, at about the same height) for
    /// <see cref="GestureConfig.crossHoldTime"/>, then sweeping them apart by <see cref="GestureConfig.sweepDistance"/>
    /// within <see cref="GestureConfig.sweepTime"/>, hides every window but the launcher; the same gesture (or the
    /// launcher's Show button) brings them back. Both hands must be tracked throughout (short drops within the tracking
    /// grace are tolerated), so one stray hand can't trigger it. While a remote screen is live or asking for approval
    /// (<see cref="ConsentGate.IsActive"/>) the same gesture is the panic stop instead: it disconnects every remote screen
    /// and returns to Home. It runs then even if Settings switched the gesture off.
    /// </summary>
    public sealed class CrossSweepRecognizer : GestureRecognizer
    {
        enum Phase { Idle, Crossing, Armed }

        Phase phase;
        float crossedFor, uncrossedAt, cooldownUntil;

        public override string Name => "Cross and sweep";

        public override void Tick(in GestureFrame frame)
        {
            var c = Config;
            if (frame.Time < cooldownUntil)
            {
                State = "Cooling down";
                return;
            }
            var l = frame.Left;
            var r = frame.Right;
            if (!l.Tracked || !r.Tracked)
            {
                if (phase != Phase.Idle) Event("cancelled: a hand was lost");
                Reset();
                return;
            }

            var inverse = Quaternion.Inverse(frame.Head.rotation);
            var lw = inverse * (l.State.WristPosition - frame.Head.position);
            var rw = inverse * (r.State.WristPosition - frame.Head.position);
            bool crossed = lw.x - rw.x > c.crossMargin && Mathf.Abs(lw.y - rw.y) < c.crossMaxHeightGap;

            switch (phase)
            {
                case Phase.Idle:
                    if (crossed)
                    {
                        phase = Phase.Crossing;
                        crossedFor = 0f;
                    }
                    break;
                case Phase.Crossing:
                    if (!crossed) Reset();
                    else if ((crossedFor += frame.DeltaTime) >= c.crossHoldTime)
                    {
                        phase = Phase.Armed;
                        uncrossedAt = float.NaN;
                    }
                    break;
                case Phase.Armed:
                    if (crossed)
                    {
                        uncrossedAt = float.NaN;
                        break;
                    }
                    if (float.IsNaN(uncrossedAt)) uncrossedAt = frame.Time;
                    if (rw.x - lw.x >= c.sweepDistance)
                    {
                        var gate = ConsentGate.Current;
                        if (gate != null && gate.IsActive)
                        {
                            gate.PanicStop("panic stop (cross and sweep)");
                            Event("panic stop: remote screens disconnected");
                        }
                        else
                        {
                            bool hiding = Host.Manager != null && !Host.Manager.WindowsHidden;
                            Host.ToggleHideAll();
                            Event(hiding ? "hide all windows" : "show all windows");
                        }
                        Reset();
                        cooldownUntil = frame.Time + c.crossCooldown;
                    }
                    else if (frame.Time - uncrossedAt > c.sweepTime)
                    {
                        Event("cancelled: sweep too slow or too short");
                        Reset();
                    }
                    break;
            }
            State = phase == Phase.Crossing ? $"Crossed {crossedFor:F2} / {c.crossHoldTime:F2} s"
                : phase == Phase.Armed ? "Armed: sweep apart" : "Idle";
        }

        void Reset()
        {
            phase = Phase.Idle;
            crossedFor = 0f;
            State = "Idle";
        }

        public override void Cancel() => Reset();
    }
}
