using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>One hand as the recognizers see it this frame: its state plus timers and the hand-scale depth.</summary>
    public sealed class HandFeatures
    {
        public readonly Handedness Hand;
        public readonly HandDepthEstimator Estimator = new HandDepthEstimator();

        /// <summary>The last state seen while tracked (kept through drops shorter than the tracking grace).</summary>
        public HandState State;
        /// <summary>Tracked now, or lost for less than <see cref="GestureConfig.trackingGrace"/>.</summary>
        public bool Tracked;
        /// <summary>Tracked this frame (not a held state).</summary>
        public bool Fresh;
        public float LostFor;
        /// <summary>Estimated distance from the head (from the hand's apparent size) and its speed (positive = away).</summary>
        public float Depth, DepthVelocity;
        /// <summary>
        /// The in/out speed of largest magnitude over the last <see cref="GestureConfig.pushTransitionTime"/>: a thrust
        /// is fastest while the hand opens, and has slowed by the time the hand has been open long enough to count.
        /// </summary>
        public float PeakVelocity;
        internal readonly Queue<(float time, float velocity)> RecentVelocities = new Queue<(float, float)>();
        /// <summary>From the head towards the palm (reliable even without depth).</summary>
        public Vector3 Direction;
        /// <summary>Head + <see cref="Direction"/> × <see cref="Depth"/>.</summary>
        public Vector3 Position;
        public bool Fist, Open, Pinched;
        public float FistFor, OpenFor, PinchFor, NotPinchedFor;
        /// <summary>When the last held fist (≥ fistHoldTime) opened, and the window it pointed at when it closed.</summary>
        public float FistEndedAt = float.NegativeInfinity;
        public SpatialWindow FistTarget;
        public bool FistTargetUsed;

        public HandFeatures(Handedness hand) => Hand = hand;

        public void Reset()
        {
            Tracked = Fresh = Fist = Open = Pinched = false;
            FistFor = OpenFor = PinchFor = NotPinchedFor = PeakVelocity = 0f;
            RecentVelocities.Clear();
            Estimator.Reset();
        }
    }

    public struct GestureFrame
    {
        public HandFeatures Left, Right;
        public Pose Head;
        public float DeltaTime, Time;
        public HandFeatures Get(Handedness hand) => hand == Handedness.Left ? Left : Right;
    }

    /// <summary>A gesture recognizer run by <see cref="GestureRecognizers"/> once per frame.</summary>
    public abstract class GestureRecognizer
    {
        protected GestureRecognizers Host { get; private set; }
        protected GestureConfig Config => Host.Config;

        public abstract string Name { get; }
        public bool Enabled { get; set; } = true;
        /// <summary>What it is doing now, for the debug overlay ("Idle", "Grabbing 'Video' (right)"...).</summary>
        public string State { get; protected set; } = "Idle";
        public string LastEvent { get; private set; } = "";
        public float LastEventTime { get; private set; } = float.NegativeInfinity;

        internal void Attach(GestureRecognizers host) => Host = host;

        public abstract void Tick(in GestureFrame frame);

        /// <summary>Stops whatever it is doing (disabled, hand input off, source swapped).</summary>
        public abstract void Cancel();

        protected void Event(string text)
        {
            LastEvent = text;
            LastEventTime = Host.Now;
            Host.Raise(this, text);
        }
    }

    /// <summary>
    /// Hand gestures on top of the hand source (<see cref="IHandSource"/>), next to <see cref="HandWindowInput"/>'s
    /// point-and-pinch: (1) <see cref="FistGrabRecognizer"/>, (2) <see cref="PalmPushRecognizer"/>,
    /// (3) <see cref="TwoHandStretchRecognizer"/>, (4) <see cref="CrossSweepRecognizer"/>, (5) <see cref="PinchHoldRecognizer"/>
    /// (the app switcher). Every threshold is in one
    /// <see cref="GestureConfig"/> asset. Each frame the host turns both hands into <see cref="HandFeatures"/> (tracking
    /// grace for half-lost tracking, fist/open/pinch timers, depth from the hand's apparent size, since the iPhone 16 has
    /// no LiDAR) and ticks the recognizers. A recognizer claims the hands it uses; a claimed hand's pointer in
    /// <see cref="HandWindowInput"/> is paused so the two never fight over a window. Runs only while hand input is the
    /// active window input. In the editor the hands come from <see cref="SimulatedHandSource"/> (G makes a fist).
    /// </summary>
    public class GestureRecognizers : MonoBehaviour
    {
        public static readonly Handedness[] Hands = { Handedness.Left, Handedness.Right };

        [SerializeField] GestureConfig config;
        [Tooltip("A component implementing IHandSource. Defaults to the HandWindowInput's source.")]
        [SerializeField] MonoBehaviour handSource;
        [Tooltip("The user's head. Defaults to Camera.main.")]
        [SerializeField] Transform head;
        [Tooltip("Run only while HandWindowInput is the active window input.")]
        [SerializeField] bool onlyWithHandInput = true;

        public GestureConfig Config
        {
            get
            {
                if (config == null)
                {
                    config = ScriptableObject.CreateInstance<GestureConfig>();
                    config.name = "GestureConfig (defaults)";
                }
                return config;
            }
            set => config = value;
        }

        public IHandSource Source { get; private set; }
        /// <summary>The user's hand length for hand-size depth (Hand Setup); 0 uses the config's.</summary>
        public float HandLength { get; set; }
        /// <summary>Stops recognizing (as when hand input is off), for example while Hand Setup measures poses.</summary>
        public bool Paused { get; set; }
        public HandFeatures Left { get; } = new HandFeatures(Handedness.Left);
        public HandFeatures Right { get; } = new HandFeatures(Handedness.Right);
        public FistGrabRecognizer FistGrab { get; private set; }
        public PalmPushRecognizer PalmPush { get; private set; }
        public TwoHandStretchRecognizer Stretch { get; private set; }
        public CrossSweepRecognizer CrossSweep { get; private set; }
        public PinchHoldRecognizer PinchHold { get; private set; }
        /// <summary>The point-and-pinch input the recognizers run beside (its pointer rays), or null.</summary>
        public HandWindowInput HandInput => hands;
        public IReadOnlyList<GestureRecognizer> Recognizers => recognizers;
        public WindowManager Manager { get; private set; }
        public bool IsRunning { get; private set; }
        public float Now => Time.time;
        public Pose HeadPose { get; private set; }

        /// <summary>A recognizer reported something ("grab 'Video' (right)", "hide all"...).</summary>
        public event Action<GestureRecognizer, string> GestureEvent;

        readonly List<GestureRecognizer> recognizers = new List<GestureRecognizer>();
        readonly GestureRecognizer[] owners = new GestureRecognizer[2];
        HandWindowInput hands;
        LauncherWindow launcher;

        void Awake()
        {
            Manager = GetComponent<WindowManager>();
            hands = GetComponent<HandWindowInput>();
            launcher = GetComponent<LauncherWindow>();
            FistGrab = Add(new FistGrabRecognizer());
            PalmPush = Add(new PalmPushRecognizer());
            Stretch = Add(new TwoHandStretchRecognizer());
            CrossSweep = Add(new CrossSweepRecognizer());
            PinchHold = Add(new PinchHoldRecognizer());
        }

        T Add<T>(T recognizer) where T : GestureRecognizer
        {
            recognizer.Attach(this);
            recognizers.Add(recognizer);
            return recognizer;
        }

        /// <summary>Uses this hand source (for example scripted hands in a test); null goes back to the default.</summary>
        public void SetSource(IHandSource source)
        {
            CancelAll();
            Source = source;
        }

        public HandFeatures Features(Handedness hand) => hand == Handedness.Left ? Left : Right;
        public GestureRecognizer Owner(Handedness hand) => owners[(int)hand];

        /// <summary>Takes a hand for a recognizer (false if another has it); pauses that hand's pointer.</summary>
        public bool Claim(Handedness hand, GestureRecognizer recognizer)
        {
            var owner = owners[(int)hand];
            if (owner != null && owner != recognizer) return false;
            owners[(int)hand] = recognizer;
            if (hands != null) hands.SetSuppressed(hand, true);
            return true;
        }

        public void Release(Handedness hand, GestureRecognizer recognizer)
        {
            if (owners[(int)hand] != recognizer) return;
            owners[(int)hand] = null;
            if (hands != null) hands.SetSuppressed(hand, false);
        }

        /// <summary>The window a ray points at, if any (curved windows included).</summary>
        public SpatialWindow PickWindow(Ray ray) =>
            WindowRaycast.TryHit(ray, 20f, ~0, out _, out var part) ? part.Window : null;

        /// <summary>Hides every window but the launcher, or shows them again.</summary>
        public void ToggleHideAll()
        {
            if (Manager == null) return;
            if (Manager.WindowsHidden) Manager.ShowAll();
            else Manager.HideAll(launcher != null ? launcher.Launcher : null);
        }

        internal void Raise(GestureRecognizer recognizer, string text) => GestureEvent?.Invoke(recognizer, text);

        void Update()
        {
            if (Source == null) Source = handSource as IHandSource ?? (hands != null ? hands.HandSource : null) ?? GetComponent<IHandSource>();
            if (head == null && Camera.main != null) head = Camera.main.transform;
            bool active = !Paused && Source != null && head != null && (!onlyWithHandInput || (hands != null && hands.isActiveAndEnabled));
            if (!active)
            {
                if (IsRunning) CancelAll();
                IsRunning = false;
                return;
            }
            IsRunning = true;

            float dt = Time.deltaTime;
            HeadPose = new Pose(head.position, head.rotation);
            UpdateFeatures(Left, Source.GetHand(Handedness.Left), dt);
            UpdateFeatures(Right, Source.GetHand(Handedness.Right), dt);

            var frame = new GestureFrame { Left = Left, Right = Right, Head = HeadPose, DeltaTime = dt, Time = Now };
            // The panic stop (cross and sweep while a remote screen is live) works even with that gesture switched off.
            bool panicArmed = ConsentGate.Current != null && ConsentGate.Current.IsActive;
            foreach (var recognizer in recognizers)
            {
                if (recognizer.Enabled || (recognizer == CrossSweep && panicArmed)) recognizer.Tick(frame);
                else recognizer.Cancel();
            }
        }

        void UpdateFeatures(HandFeatures f, HandState state, float dt)
        {
            var c = Config;
            if (state.IsTracked)
            {
                f.State = state;
                f.Fresh = f.Tracked = true;
                f.LostFor = 0f;
                f.Depth = f.Estimator.Update(state.ApparentSize, dt, c, HandLength > 0f ? HandLength : c.handLength);
                f.DepthVelocity = f.Estimator.Velocity;
                f.RecentVelocities.Enqueue((Now, f.DepthVelocity));
                while (f.RecentVelocities.Count > 0 && f.RecentVelocities.Peek().time < Now - c.pushTransitionTime)
                    f.RecentVelocities.Dequeue();
                f.PeakVelocity = 0f;
                foreach (var (_, velocity) in f.RecentVelocities)
                    if (Mathf.Abs(velocity) > Mathf.Abs(f.PeakVelocity)) f.PeakVelocity = velocity;
                var toPalm = state.PalmCenter - HeadPose.position;
                if (toPalm.sqrMagnitude > 1e-8f) f.Direction = toPalm.normalized;
                f.Position = HeadPose.position + f.Direction * f.Depth;
            }
            else
            {
                f.Fresh = false;
                f.LostFor += dt;
                if (f.Tracked && f.LostFor >= c.trackingGrace) f.Reset();
                if (!f.Tracked) return;
            }

            var s = f.State;
            bool fist = s.Grab || s.Openness <= c.fistOpenness;
            bool open = !s.Grab && s.Openness >= c.openPalmOpenness;
            if (f.Fist && !fist && f.FistFor >= c.fistHoldTime) f.FistEndedAt = Now;
            if (fist && f.FistFor < c.fistHoldTime && f.FistFor + dt >= c.fistHoldTime)
            {
                f.FistTarget = PickWindow(s.PointingRay); // what the fist closed on
                f.FistTargetUsed = false;
            }
            f.FistFor = fist ? f.FistFor + dt : 0f;
            f.OpenFor = open ? f.OpenFor + dt : 0f;
            f.Fist = fist;
            f.Open = open;
            f.Pinched = s.IsPinched;
            f.PinchFor = f.Pinched ? f.PinchFor + dt : 0f;
            f.NotPinchedFor = f.Pinched ? 0f : f.NotPinchedFor + dt;
        }

        void CancelAll()
        {
            foreach (var recognizer in recognizers) recognizer.Cancel();
            Left.Reset();
            Right.Reset();
        }

        void OnDisable()
        {
            CancelAll();
            IsRunning = false;
        }
    }
}
