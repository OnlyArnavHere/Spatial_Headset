using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for the gesture recognizers, with hands driven by scripted joint data: each frame the test's hands
    /// are built as joints (wrist, knuckles, tips), jittered by 2 mm noise, sometimes dropped (half-lost tracking), and
    /// mapped to <see cref="HandState"/> by <see cref="HoloKitHandSource.FromJoints"/>, the device mapping. Covers the
    /// hand features, the hand-scale depth estimate and the 2D reconstruction, then fist grab, palm push and pull,
    /// two-hand stretch and cross-and-sweep, each with the noisy and half-lost cases and what must not trigger, the debug
    /// overlay and the settings toggles. Results use a [GestureTest] prefix.
    /// </summary>
    static class GestureTest
    {
        const float Settle = 0.5f;
        const float Noise = 0.002f;

        [MenuItem("Tools/Spatial Windows/Test Gestures")]
        static void Run() => EditorTestRunner.Run("GestureTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Gestures", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        enum Shape { Open, Fist, Pinch }

        /// <summary>One hand for a frame: where its wrist is, which way the fingers point, and its shape.</summary>
        struct Pose
        {
            public Vector3 Wrist;
            public Shape Shape;
            public bool Lost;
        }

        sealed class PanicSession : IRemoteSession
        {
            public string SourceName => "Gesture test stream";
            public string Address => "127.0.0.1";
            public string ModeLabel => "full control";
            public SpatialWindow Window { get; set; }
            public int Stops;
            public void StopForPanic() => Stops++;
        }

        /// <summary>Scripted hands: joints from <see cref="Pose"/>s, with noise and random dropped frames, through FromJoints.</summary>
        sealed class ScriptedHands : IHandSource
        {
            public Pose Left = new Pose { Lost = true }, Right = new Pose { Lost = true };
            public float DropRate;
            readonly System.Random random = new System.Random(7);
            readonly Transform head;
            int frame = -1;
            HandState left, right;

            public ScriptedHands(Transform head) => this.head = head;

            public HandState GetHand(Handedness hand)
            {
                if (frame != Time.frameCount)
                {
                    frame = Time.frameCount;
                    left = Build(Left, Handedness.Left, left);
                    right = Build(Right, Handedness.Right, right);
                }
                return hand == Handedness.Left ? left : right;
            }

            HandState Build(Pose pose, Handedness hand, HandState previous)
            {
                if (pose.Lost || random.NextDouble() < DropRate) return HandState.Untracked;
                var joints = Joints(pose, hand, head, () => Jitter());
                return HoloKitHandSource.FromJoints(joints, hand, new UnityEngine.Pose(head.position, head.rotation), previous.IsPinched);
            }

            Vector3 Jitter() => new Vector3(Gauss(), Gauss(), Gauss()) * Noise;

            float Gauss()
            {
                double u1 = 1.0 - random.NextDouble(), u2 = random.NextDouble();
                return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }
        }

        /// <summary>A hand with the fingers pointing up and the palm facing away from the head.</summary>
        static HandJoints Joints(Pose pose, Handedness hand, Transform head, Func<Vector3> jitter = null)
        {
            jitter ??= () => Vector3.zero;
            var up = head.up;
            var forward = head.forward;
            var thumbSide = hand == Handedness.Right ? -head.right : head.right;
            var w = pose.Wrist;
            var middle = w + up * 0.09f;
            var index = middle + thumbSide * 0.02f;
            var ring = middle - thumbSide * 0.02f - up * 0.005f;
            var little = middle - thumbSide * 0.04f - up * 0.015f;
            bool curled = pose.Shape == Shape.Fist;
            Vector3 Tip(Vector3 knuckle, bool curl) => curl ? knuckle - up * 0.03f - forward * 0.03f : knuckle + up * 0.08f;
            var indexTip = Tip(index, curled);
            var thumb = pose.Shape == Shape.Pinch ? indexTip + thumbSide * 0.004f : index + thumbSide * 0.05f - up * 0.06f;
            return new HandJoints
            {
                Wrist = w + jitter(), ThumbTip = thumb + jitter(),
                IndexMCP = index + jitter(), IndexTip = indexTip + jitter(),
                MiddleMCP = middle + jitter(), MiddleTip = Tip(middle, curled) + jitter(),
                RingMCP = ring + jitter(), RingTip = Tip(ring, curled) + jitter(),
                LittleMCP = little + jitter(), LittleTip = Tip(little, curled) + jitter(),
            };
        }

        /// <summary>A hand whose pointing ray (shoulder through index knuckle) aims at a world point, the knuckle this far from the shoulder.</summary>
        static Pose Aiming(Handedness hand, Transform head, Vector3 target, float reach, Shape shape)
        {
            float side = hand == Handedness.Right ? 1f : -1f;
            var shoulder = head.position + head.rotation * new Vector3(0.15f * side, -0.18f, 0f);
            var index = shoulder + (target - shoulder).normalized * reach;
            var thumbSide = hand == Handedness.Right ? -head.right : head.right;
            return new Pose { Wrist = index - thumbSide * 0.02f - head.up * 0.09f, Shape = shape };
        }

        /// <summary>The same hand moved straight towards (negative) or away from (positive) the head.</summary>
        static Pose Radial(Pose pose, Transform head, float metres, Shape shape) =>
            new Pose { Wrist = pose.Wrist + (pose.Wrist - head.position).normalized * metres, Shape = shape };

        /// <summary>A hand with its wrist at a point in head space (x right, y up, z forward).</summary>
        static Pose At(Transform head, Vector3 local, Shape shape) => new Pose { Wrist = head.TransformPoint(local), Shape = shape };

        // Runs a pose change over time: set(t) with t from 0 to 1.
        static IEnumerator Over(float seconds, Action<float> set)
        {
            double start = EditorTestRunner.Now;
            while (true)
            {
                float t = Mathf.Clamp01((float)((EditorTestRunner.Now - start) / seconds));
                set(t);
                if (t >= 1f) yield break;
                yield return null;
            }
        }

        static IEnumerator Steps()
        {
            var manager = UnityEngine.Object.FindFirstObjectByType<WindowManager>();
            var hands = UnityEngine.Object.FindFirstObjectByType<HandWindowInput>();
            var gestures = UnityEngine.Object.FindFirstObjectByType<GestureRecognizers>();
            var launcher = UnityEngine.Object.FindFirstObjectByType<LauncherWindow>();
            var cam = Camera.main;
            if (manager == null || hands == null || gestures == null || launcher == null || cam == null)
            {
                Check(false, "scene has a WindowManager, HandWindowInput, GestureRecognizers, LauncherWindow and main camera");
                yield break;
            }

            PureChecks(gestures.Config, cam.transform);

            int previousInput = WindowTestUtil.SelectInput<HandWindowInput>(manager);
            var previousSource = hands.HandSource;
            var scripted = new ScriptedHands(cam.transform);
            hands.SetHandSource(scripted);
            gestures.SetSource(scripted);
            var events = new List<string>();
            Action<GestureRecognizer, string> record = (r, text) => events.Add(text);
            gestures.GestureEvent += record;
            var flags = gestures.Recognizers.Select(r => r.Enabled).ToArray();
            foreach (var r in gestures.Recognizers) r.Enabled = true;
            var opened = new List<SpatialWindow>();
            try
            {
                yield return FistGrab(manager, hands, gestures, scripted, cam.transform, events, opened);
                yield return PalmPush(manager, gestures, scripted, cam.transform, events, opened);
                yield return Stretch(manager, hands, gestures, scripted, cam.transform, events, opened);
                yield return CrossSweep(manager, launcher, gestures, scripted, cam.transform, events, opened);
                Overlay(gestures);
                SettingsToggles(gestures, launcher);
            }
            finally
            {
                gestures.GestureEvent -= record;
                if (manager.WindowsHidden) manager.ShowAll();
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                for (int i = 0; i < flags.Length; i++) gestures.Recognizers[i].Enabled = flags[i];
                gestures.SetSource(null);
                hands.SetHandSource(previousSource);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static void PureChecks(GestureConfig c, Transform head)
        {
            var hp = new UnityEngine.Pose(head.position, head.rotation);
            var open = HoloKitHandSource.FromJoints(Joints(At(head, new Vector3(0.1f, -0.1f, 0.5f), Shape.Open), Handedness.Right, head), Handedness.Right, hp, false);
            var fist = HoloKitHandSource.FromJoints(Joints(At(head, new Vector3(0.1f, -0.1f, 0.5f), Shape.Fist), Handedness.Right, head), Handedness.Right, hp, false);
            float distance = Vector3.Distance(head.position, head.TransformPoint(new Vector3(0.1f, -0.1f, 0.5f)));
            Check(open.Openness > 0.95f && fist.Openness < 0.05f && fist.Grab && !open.Grab,
                $"hand features: an open hand has openness {open.Openness:F2}, a fist {fist.Openness:F2}");
            float estimate = HandDepthEstimator.DepthFor(open.ApparentSize, c);
            Check(Mathf.Abs(estimate - distance) < 0.03f,
                $"hand features: a hand {distance:F2} m away looks {open.ApparentSize * Mathf.Rad2Deg:F1}° big, so depth {estimate:F2} m");

            // The estimator: noise smoothed, a one-frame spike limited, motion away shows as positive speed.
            var estimator = new HandDepthEstimator();
            var random = new System.Random(3);
            float noisyMax = 0f;
            for (int i = 0; i < 120; i++)
            {
                float jitter = 1f + ((float)random.NextDouble() - 0.5f) * 0.1f; // ±5% size noise
                estimator.Update(c.handLength / 0.5f * jitter, 1f / 60f, c);
                if (i > 60) noisyMax = Mathf.Max(noisyMax, Mathf.Abs(estimator.Depth - 0.5f));
            }
            float before = estimator.Depth;
            estimator.Update(c.handLength / 0.25f, 1f / 60f, c); // one bad frame says 0.25 m
            float jump = Mathf.Abs(estimator.Depth - before);
            for (int i = 0; i < 30; i++) estimator.Update(c.handLength / (0.5f + i * 0.005f), 1f / 60f, c); // moving away 0.3 m/s
            Check(noisyMax < 0.02f && jump <= c.maxDepthSpeed / 60f + 1e-4f && estimator.Velocity > 0.1f,
                $"depth estimate: ±5% size noise stays within {noisyMax * 1000f:F0} mm, a bad frame moves it {jump * 1000f:F0} mm, " +
                $"moving away reads {estimator.Velocity:+0.00} m/s");

            // The same push speed reads the same whatever the frame rate.
            var speeds = new List<string>();
            bool tracked = true;
            foreach (int fps in new[] { 30, 60, 240 })
            {
                var e = new HandDepthEstimator();
                float dt = 1f / fps;
                for (int i = 0; i <= fps / 2; i++) e.Update(c.handLength / (0.4f + 0.6f * i * dt), dt, c); // 0.6 m/s for 0.5 s
                speeds.Add($"{fps} fps {e.Velocity:F2}");
                tracked &= e.Velocity > 0.45f && e.Velocity < 0.65f && Mathf.Abs(e.Depth - 0.7f) < 0.08f;
            }
            Check(tracked, $"depth estimate: a hand moving away at 0.6 m/s reads about that at any frame rate ({string.Join(", ", speeds)} m/s)");

            // No LiDAR: joints from their viewing directions at the estimated depth classify the same.
            var truth = Joints(At(head, new Vector3(0.05f, -0.1f, 0.45f), Shape.Pinch), Handedness.Right, head);
            HandJoints Dir(HandJoints j) => new HandJoints
            {
                Wrist = j.Wrist - head.position, ThumbTip = j.ThumbTip - head.position, IndexMCP = j.IndexMCP - head.position,
                IndexTip = j.IndexTip - head.position, MiddleMCP = j.MiddleMCP - head.position, MiddleTip = j.MiddleTip - head.position,
                RingMCP = j.RingMCP - head.position, RingTip = j.RingTip - head.position, LittleMCP = j.LittleMCP - head.position,
                LittleTip = j.LittleTip - head.position,
            };
            var directions = Dir(truth);
            float depth = HandDepthEstimator.DepthFor(HoloKitHandSource.ApparentSizeOf(directions), c);
            var rebuilt = HoloKitHandSource.FromJoints(HoloKitHandSource.JointsAtDepth(directions, head.position, depth), Handedness.Right, hp, false);
            Check(rebuilt.IsPinched && Mathf.Abs(Vector3.Distance(rebuilt.PalmCenter, head.position) - Vector3.Distance(truth.MiddleMCP, head.position)) < 0.05f,
                $"no LiDAR: 2D joints placed at the hand-scale depth ({depth:F2} m) still read as a pinch, palm within 5 cm");

            var mapped = HoloKitHandSource.ImageToViewport(new Vector2(0.2f, 0.7f), c);
            var flipped = ScriptableObject.CreateInstance<GestureConfig>();
            flipped.imageSwapXY = flipped.imageFlipY = true;
            Check(mapped == new Vector2(0.2f, 0.7f) && HoloKitHandSource.ImageToViewport(new Vector2(0.2f, 0.7f), flipped) == new Vector2(0.7f, 0.8f),
                "no LiDAR: Vision's image points map to the viewport with the config's swap and flips");
            UnityEngine.Object.Destroy(flipped);

            var stretched = TwoHandStretchRecognizer.StretchedSize(new Vector2(0.6f, 0.4f), new Vector2(0.2f, 0.1f), new Vector2(0.3f, 0.1f), c);
            Check(Mathf.Abs(stretched.x - 1.039f) < 0.002f && Mathf.Abs(stretched.y - 0.462f) < 0.002f,
                $"stretch maths: hands 1.41x further apart and 1.5x wider in ratio give {stretched.x:F3} x {stretched.y:F3} (expected 1.039 x 0.462)");
            var capped = TwoHandStretchRecognizer.StretchedSize(new Vector2(0.6f, 0.4f), new Vector2(0.2f, 0.2f), new Vector2(0.4f, 0.001f), c);
            Check(capped.x / capped.y <= 1.5f * c.maxAspectChange + 1e-3f, $"stretch maths: a flat hand pair can't blow the aspect up ({capped.x / capped.y:F2})");
        }

        static SpatialWindow Spawn(WindowManager manager, string title, List<SpatialWindow> opened)
        {
            var window = manager.Spawn(title, new Vector2(0.5f, 0.32f));
            opened.Add(window);
            return window;
        }

        static float DistanceFromHead(SpatialWindow w, Transform head) => Vector3.Distance(w.transform.position, head.position);

        static IEnumerator FistGrab(WindowManager manager, HandWindowInput hands, GestureRecognizers gestures, ScriptedHands scripted,
            Transform head, List<string> events, List<SpatialWindow> opened)
        {
            var window = Spawn(manager, "Grab me", opened);
            yield return Settle;
            var center = window.transform.position;
            scripted.DropRate = 0.08f; // noisy and half-lost: ~1 frame in 12 missing
            scripted.Right = Aiming(Handedness.Right, head, center, 0.4f, Shape.Open);
            yield return 0.3f;
            events.Clear();
            scripted.Right = Aiming(Handedness.Right, head, center, 0.4f, Shape.Fist);
            yield return 0.4f;
            Check(gestures.FistGrab.Grabbed(Handedness.Right) == window && events.Any(e => e.StartsWith("grab 'Grab me'")) && hands.IsSuppressed(Handedness.Right),
                $"a fist pointing at a window grabs it, and that hand's pointer pauses ({string.Join("; ", events)})");

            float startDistance = DistanceFromHead(window, head);
            var startPalm = gestures.Right.Direction;
            float startDepth = gestures.Right.Depth;
            float startTrue = Vector3.Distance(head.position, gestures.Right.State.PalmCenter);
            int startFrame = Time.frameCount;
            var aimEnd = center + head.right * 0.25f;
            // Swing right and bring the fist 5 cm towards the head (pull), over 0.8 s, with a 0.12 s tracking drop on the way.
            yield return Over(0.8f, t =>
            {
                var target = Vector3.Lerp(center, aimEnd, t);
                var pose = Aiming(Handedness.Right, head, target, 0.4f - 0.05f * t, Shape.Fist);
                pose.Lost = t > 0.4f && t < 0.55f;
                scripted.Right = pose;
            });
            yield return 0.5f;
            float pulled = startDistance - DistanceFromHead(window, head);
            float turned = Vector3.Angle(startPalm, gestures.Right.Direction);
            float windowTurn = Vector3.Angle(center - head.position, window.transform.position - head.position);
            Check(gestures.FistGrab.Grabbed(Handedness.Right) == window, "a short tracking drop (0.12 s) doesn't drop the window");
            float depthChange = startDepth - gestures.Right.Depth;
            float trueChange = startTrue - Vector3.Distance(head.position, gestures.Right.State.PalmCenter);
            Check(Mathf.Abs(windowTurn - turned) < 2f && Mathf.Abs(pulled - trueChange * gestures.Config.grabDepthGain) < 0.04f,
                $"the window follows the fist: turned {windowTurn:F1}° with the fist's {turned:F1}°, pulled in {pulled:F2} m for the fist's " +
                $"{trueChange * 100f:F1} cm × gain {gestures.Config.grabDepthGain} (estimated {depthChange * 100f:F1} cm; {Time.frameCount - startFrame} frames)");

            var held = window.transform.position;
            scripted.Right = Aiming(Handedness.Right, head, aimEnd, 0.35f, Shape.Open); // open, without moving
            yield return 0.5f;
            Check(gestures.FistGrab.Grabbed(Handedness.Right) == null && !hands.IsSuppressed(Handedness.Right)
                  && events.Any(e => e.StartsWith("release 'Grab me'")) && Vector3.Distance(window.transform.position, held) < 0.03f,
                $"opening the hand still releases it where it is ({Vector3.Distance(window.transform.position, held) * 1000f:F0} mm), and the pointer is back");

            // Lost for good: released.
            scripted.Right = Aiming(Handedness.Right, head, window.transform.position, 0.35f, Shape.Fist);
            yield return 0.4f;
            bool grabbedAgain = gestures.FistGrab.Grabbed(Handedness.Right) == window;
            scripted.Right = new Pose { Lost = true };
            yield return 0.6f;
            Check(grabbedAgain && gestures.FistGrab.Grabbed(Handedness.Right) == null && events.Any(e => e.StartsWith("lost tracking")),
                "losing the hand for longer than the grace period releases the window");

            // A fist pointing at nothing (far to the left and down) grabs nothing.
            scripted.Right = At(head, new Vector3(-0.3f, -0.35f, 0.3f), Shape.Open);
            yield return 0.2f;
            scripted.Right = At(head, new Vector3(-0.3f, -0.35f, 0.3f), Shape.Fist);
            yield return 0.4f;
            Check(gestures.FistGrab.Grabbed(Handedness.Right) == null, "a fist pointing away from every window grabs nothing");
            scripted.Right = new Pose { Lost = true };
            scripted.DropRate = 0f;
            yield return 0.4f;
        }

        static IEnumerator PalmPush(WindowManager manager, GestureRecognizers gestures, ScriptedHands scripted, Transform head,
            List<string> events, List<SpatialWindow> opened)
        {
            var window = Spawn(manager, "Push me", opened);
            yield return Settle;
            var center = window.transform.position;
            scripted.DropRate = 0.05f;
            var hand = Aiming(Handedness.Right, head, center, 0.35f, Shape.Open);
            scripted.Right = hand;
            yield return 0.3f;
            scripted.Right = Radial(hand, head, 0f, Shape.Fist);
            yield return 0.4f;
            events.Clear();
            float start = DistanceFromHead(window, head);

            // A thrust: 15 cm forward in 0.25 s, opening the fist on the way, then 5 cm more with the palm open.
            float maxSpeed = 0f, depthBefore = gestures.Right.Depth, gameStart = Time.time;
            string before = $"before: estimate {gestures.Right.Depth:F2}, raw {gestures.Right.Estimator.RawDepth:F2}, " +
                            $"size {gestures.Right.State.ApparentSize * Mathf.Rad2Deg:F2}°, true {Vector3.Distance(head.position, gestures.Right.State.PalmCenter):F2} m, " +
                            $"fresh {gestures.Right.Fresh}, tracked {gestures.Right.Tracked}";
            int frameStart = Time.frameCount;
            double realStart = EditorTestRunner.Now;
            yield return Over(0.25f, t =>
            {
                scripted.Right = Radial(hand, head, 0.15f * t, t > 0.6f ? Shape.Open : Shape.Fist);
                maxSpeed = Mathf.Max(maxSpeed, gestures.Right.DepthVelocity);
            });
            yield return Over(0.2f, t =>
            {
                scripted.Right = Radial(hand, head, 0.15f + 0.05f * t, Shape.Open);
                maxSpeed = Mathf.Max(maxSpeed, gestures.Right.DepthVelocity);
            });
            string trace = $"max speed {maxSpeed:F2} m/s, depth {depthBefore:F2} -> {gestures.Right.Depth:F2} m in {Time.time - gameStart:F2} s game " +
                           $"/ {EditorTestRunner.Now - realStart:F2} s real, {Time.frameCount - frameStart} frames; {before}; after: true " +
                           $"{Vector3.Distance(head.position, gestures.Right.State.PalmCenter):F2} m";
            yield return 0.1f;
            float pushed = DistanceFromHead(window, head);
            Check(events.Any(e => e.StartsWith("push 'Push me'")) && gestures.PalmPush.Pushing(Handedness.Right) == window && pushed > start + 0.15f,
                $"opening the fist while thrusting forward pushes the window away ({start:F2} -> {pushed:F2} m; {string.Join("; ", events)}; " +
                $"{trace})");

            // Pull the open palm back 8 cm: the window comes back.
            yield return Over(0.3f, t => scripted.Right = Radial(hand, head, 0.20f - 0.08f * t, Shape.Open));
            yield return 0.15f;
            float pulled = DistanceFromHead(window, head);
            Check(pulled < pushed - 0.15f, $"moving the palm back pulls it back ({pushed:F2} -> {pulled:F2} m)");

            // Hold still: the push ends and the window stays.
            yield return 0.8f;
            Check(gestures.PalmPush.Pushing(Handedness.Right) == null && events.Any(e => e.StartsWith("stopped")),
                $"holding the palm still ends the push ({events.LastOrDefault()})");

            // A slow open doesn't push.
            scripted.Right = Aiming(Handedness.Right, head, window.transform.position, 0.4f, Shape.Fist);
            yield return 0.4f;
            events.Clear();
            yield return Over(0.6f, t => scripted.Right = Aiming(Handedness.Right, head, window.transform.position, 0.4f + 0.03f * t, t > 0.3f ? Shape.Open : Shape.Fist));
            yield return 0.3f;
            Check(!events.Any(e => e.StartsWith("push")) && events.Any(e => e.StartsWith("release")),
                $"opening slowly just releases, no push ({string.Join("; ", events)})");
            scripted.Right = new Pose { Lost = true };
            scripted.DropRate = 0f;
            yield return 0.4f;
        }

        static IEnumerator Stretch(WindowManager manager, HandWindowInput hands, GestureRecognizers gestures, ScriptedHands scripted,
            Transform head, List<string> events, List<SpatialWindow> opened)
        {
            var window = Spawn(manager, "Stretch me", opened);
            yield return Settle;
            var center = window.transform.position;
            var startSize = window.Size;
            Vector2 Separation()
            {
                var d = Quaternion.Inverse(head.rotation) * (gestures.Right.State.IndexTipPosition - gestures.Left.State.IndexTipPosition);
                return new Vector2(Mathf.Abs(d.x), Mathf.Abs(d.y));
            }
            scripted.DropRate = 0.05f;
            var leftAim = center - head.right * 0.08f - head.up * 0.04f;
            var rightAim = center + head.right * 0.08f + head.up * 0.04f;
            scripted.Left = Aiming(Handedness.Left, head, leftAim, 0.4f, Shape.Open);
            scripted.Right = Aiming(Handedness.Right, head, rightAim, 0.4f, Shape.Open);
            yield return 0.3f;
            events.Clear();
            scripted.Left = Aiming(Handedness.Left, head, leftAim, 0.4f, Shape.Pinch);
            yield return 0.1f;
            scripted.Right = Aiming(Handedness.Right, head, rightAim, 0.4f, Shape.Pinch);
            yield return 0.3f;
            Check(gestures.Stretch.Window == window && hands.IsSuppressed(Handedness.Left) && hands.IsSuppressed(Handedness.Right),
                $"both hands pinching one window start a stretch, pausing both pointers ({string.Join("; ", events)})");
            var startSeparation = Separation();

            // Spread the hands apart horizontally only: bigger and wider. A 0.06 s pinch flicker on the way doesn't end it.
            yield return Over(0.6f, t =>
            {
                scripted.Left = Aiming(Handedness.Left, head, leftAim - head.right * 0.4f * t, 0.4f, t > 0.5f && t < 0.6f ? Shape.Open : Shape.Pinch);
                scripted.Right = Aiming(Handedness.Right, head, rightAim + head.right * 0.4f * t, 0.4f, Shape.Pinch);
            });
            yield return 0.2f;
            var size = window.Size;
            var separation = gestures.Stretch.LastSeparation;
            // The recognizer's own start and latest separations: the joints are noisy, so the test's samples would differ.
            var expected = AspectPresets.Clamp(TwoHandStretchRecognizer.StretchedSize(gestures.Stretch.StartSize, gestures.Stretch.StartSeparation,
                separation, gestures.Config), manager.MinSize, manager.MaxSize);
            startSeparation = gestures.Stretch.StartSeparation;
            float aspectBefore = startSize.x / startSize.y, aspectAfter = size.x / size.y;
            Check(gestures.Stretch.Window == window && size.x > startSize.x * 1.15f && aspectAfter > aspectBefore * 1.1f
                  && Vector2.Distance(size, expected) < 0.002f && Vector3.Distance(window.transform.position, center) < 0.01f,
                $"spreading the hands sideways ({startSeparation.x * 100f:F1} -> {separation.x * 100f:F1} cm apart) makes it bigger and wider " +
                $"about its centre ({startSize.x:F2} x {startSize.y:F2} -> {size.x:F2} x {size.y:F2}, expected {expected.x:F2} x {expected.y:F2}; " +
                $"aspect {aspectBefore:F2} -> {aspectAfter:F2})");

            scripted.Right = Aiming(Handedness.Right, head, rightAim + head.right * 0.4f, 0.4f, Shape.Open);
            yield return 0.4f;
            Check(gestures.Stretch.Window == null && !hands.IsSuppressed(Handedness.Left) && events.Any(e => e.StartsWith("release")),
                "opening one pinch ends the stretch and frees both pointers");

            // One hand alone pinching does nothing here.
            Check(!events.Skip(1).Any(e => e.StartsWith("stretch")), "no second stretch started by the flicker");
            scripted.Left = scripted.Right = new Pose { Lost = true };
            scripted.DropRate = 0f;
            yield return 0.4f;
        }

        static IEnumerator Cross(ScriptedHands scripted, Transform head, float hold, float sweepTime, bool loseLeft = false)
        {
            yield return Over(0.15f, t =>
            {
                scripted.Left = At(head, new Vector3(Mathf.Lerp(-0.15f, 0.08f, t), -0.15f, 0.4f), Shape.Open);
                scripted.Right = At(head, new Vector3(Mathf.Lerp(0.15f, -0.08f, t), -0.15f, 0.4f), Shape.Open);
            });
            yield return Over(hold, t =>
            {
                var left = At(head, new Vector3(0.08f, -0.15f, 0.4f), Shape.Open);
                left.Lost = loseLeft && t > 0.2f;
                scripted.Left = left;
            });
            yield return Over(sweepTime, t =>
            {
                scripted.Left = At(head, new Vector3(Mathf.Lerp(0.08f, -0.25f, t), -0.15f, 0.4f), Shape.Open);
                scripted.Right = At(head, new Vector3(Mathf.Lerp(-0.08f, 0.25f, t), -0.15f, 0.4f), Shape.Open);
            });
            yield return 0.2f;
        }

        static IEnumerator CrossSweep(WindowManager manager, LauncherWindow launcher, GestureRecognizers gestures, ScriptedHands scripted,
            Transform head, List<string> events, List<SpatialWindow> opened)
        {
            launcher.OpenLauncher();
            var window = Spawn(manager, "Hide me", opened);
            yield return Settle;
            scripted.DropRate = 0.05f;
            events.Clear();
            yield return Cross(scripted, head, 0.45f, 0.3f);
            Check(manager.WindowsHidden && !window.gameObject.activeSelf && launcher.Launcher.gameObject.activeSelf
                  && events.Contains("hide all windows") && launcher.LauncherContent.EntryLabel(8) == "Show all",
                $"crossing the wrists, holding, then sweeping apart hides every window but the launcher, whose icon says Show all ({string.Join("; ", events)})");

            yield return gestures.Config.crossCooldown;
            yield return Cross(scripted, head, 0.45f, 0.3f);
            Check(!manager.WindowsHidden && window.gameObject.activeSelf && events.Contains("show all windows"),
                "the same gesture brings them back");

            yield return gestures.Config.crossCooldown;
            events.Clear();
            yield return Cross(scripted, head, 0.12f, 0.3f);
            Check(!manager.WindowsHidden && !events.Contains("hide all windows"), "crossing too briefly does nothing");

            yield return Cross(scripted, head, 0.6f, 0.3f, loseLeft: true);
            Check(!manager.WindowsHidden && events.Any(e => e.StartsWith("cancelled: a hand was lost")),
                "losing a hand while crossed cancels it");

            yield return Cross(scripted, head, 0.45f, 2f);
            Check(!manager.WindowsHidden, "a sweep that's too slow does nothing");

            gestures.CrossSweep.Enabled = false;
            yield return Cross(scripted, head, 0.45f, 0.3f);
            Check(!manager.WindowsHidden, "turned off, the gesture does nothing");

            // While a remote screen is live the gesture is the panic stop, even switched off.
            var gate = ConsentGate.Current;
            if (gate != null)
            {
                yield return gestures.Config.crossCooldown;
                var stream = Spawn(manager, "Live stream", opened);
                var live = new PanicSession { Window = stream };
                gate.BeginLive(live);
                events.Clear();
                yield return Cross(scripted, head, 0.45f, 0.3f);
                Check(live.Stops == 1 && stream == null && !gate.IsActive && !manager.WindowsHidden &&
                      events.Contains("panic stop: remote screens disconnected") && manager.FocusedWindow == launcher.Launcher,
                    $"with a remote screen live, cross and sweep is the panic stop even when switched off: it disconnects, closes the stream and returns Home ({string.Join("; ", events)})");
                gate.EndLive(live);
            }
            else Check(false, "the launcher has a consent gate");
            gestures.CrossSweep.Enabled = true;

            // The launcher's Hide all icon does the same.
            launcher.ToggleHideAll();
            bool hiddenByButton = manager.WindowsHidden && !window.gameObject.activeSelf;
            launcher.ToggleHideAll();
            Check(hiddenByButton && !manager.WindowsHidden && window.gameObject.activeSelf, "the launcher's Hide all / Show all button does the same");
            scripted.Left = scripted.Right = new Pose { Lost = true };
            scripted.DropRate = 0f;
            yield return 0.3f;
        }

        static void Overlay(GestureRecognizers gestures)
        {
            string text = GestureDebugOverlay.Describe(gestures);
            Check(text.Contains("Fist grab") && text.Contains("Palm push") && text.Contains("Two-hand stretch") && text.Contains("Cross and sweep")
                  && text.Contains("Left") && text.Contains("Right") && text.Contains("fist <="),
                $"the debug overlay shows both hands, every recognizer and the thresholds ({text.Split('\n').Length} lines)");
        }

        static void SettingsToggles(GestureRecognizers gestures, LauncherWindow launcher)
        {
            var settings = SettingsStore.Current.Clone();
            settings.hands.fistGrab = false;
            settings.hands.crossSweepHide = false;
            settings.hands.debugOverlay = true;
            launcher.Settings.Apply(settings);
            var overlay = gestures.GetComponent<GestureDebugOverlay>();
            bool applied = !gestures.FistGrab.Enabled && !gestures.CrossSweep.Enabled && gestures.PalmPush.Enabled && overlay.Visible;
            launcher.Settings.Apply(SettingsStore.Current);
            Check(applied, "Settings > Hands turns each gesture and the debug overlay on and off");
        }
    }
}
