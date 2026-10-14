using System.Collections;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for HandWindowInput, driven by a scripted hand source (no mouse needed): hover cursor,
    /// pinch to focus, drag in plane and in depth, tremor rejection, resize, close, controls (launcher icon,
    /// video play/pause and seek-bar drag), two hands at once, and losing tracking mid-drag; then HoloKitHandSource's
    /// mapping from hand joints to pinch, grab and pointing ray, on synthetic hands.
    /// Results are logged with a [HandTest] prefix. The previous input source and hand source are restored after.
    /// </summary>
    static class HandInputTest
    {
        const float Settle = 0.7f;   // focus animation
        const float Converge = 0.35f; // ray smoothing

        [MenuItem("Tools/Spatial Windows/Test Hand Input")]
        static void Run() => EditorTestRunner.Run("HandTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Hand Input", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        /// <summary>A hand source the test sets directly.</summary>
        sealed class ScriptedHandSource : IHandSource
        {
            public HandState Left, Right;
            public HandState GetHand(Handedness hand) => hand == Handedness.Left ? Left : Right;
        }

        static IEnumerator Steps()
        {
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var hands = Object.FindFirstObjectByType<HandWindowInput>();
            var tester = Object.FindFirstObjectByType<WindowSpawnTester>();
            var cam = Camera.main;
            if (manager == null || hands == null || tester == null || cam == null)
            {
                Check(false, "scene has a WindowManager, HandWindowInput, WindowSpawnTester and main camera");
                yield break;
            }

            int previousInput = WindowTestUtil.SelectInput<HandWindowInput>(manager);
            Check(previousInput >= 0 && manager.ActiveInput == (IWindowInput)hands, "WindowManager switches to HandWindowInput");
            var mouse = FindMouse(manager);
            Check(mouse == null || !mouse.enabled, "the mouse input is disabled while hands are active");

            var previousSource = hands.HandSource;
            var scripted = new ScriptedHandSource();
            hands.SetHandSource(scripted);

            var body = Body(manager, hands, tester, cam, scripted);
            try
            {
                while (body.MoveNext()) yield return body.Current;
            }
            finally
            {
                hands.SetHandSource(previousSource);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }

            CheckHoloKitMapping();
        }

        /// <summary>
        /// HoloKitHandSource's joints-to-state mapping on synthetic hands (the device source itself needs a phone):
        /// head at the origin looking along +Z, the right hand below-right of it with the fingers pointing forward.
        /// </summary>
        static void CheckHoloKitMapping()
        {
            var head = new Pose(Vector3.zero, Quaternion.identity);
            var wrist = new Vector3(0.15f, -0.25f, 0.35f);
            HandJoints Hand(bool fist, bool foldOthers, Vector3 thumbFromIndexTip)
            {
                Vector3 Tip(Vector3 knuckle, bool curled) => knuckle + (curled ? new Vector3(0f, -0.03f, -0.03f) : new Vector3(0f, 0f, 0.08f));
                var j = new HandJoints
                {
                    Wrist = wrist,
                    IndexMCP = wrist + new Vector3(-0.02f, 0f, 0.09f),
                    MiddleMCP = wrist + new Vector3(0f, 0f, 0.09f),
                    RingMCP = wrist + new Vector3(0.02f, 0f, 0.085f),
                    LittleMCP = wrist + new Vector3(0.04f, 0f, 0.075f),
                };
                j.IndexTip = Tip(j.IndexMCP, fist);
                j.MiddleTip = Tip(j.MiddleMCP, fist || foldOthers);
                j.RingTip = Tip(j.RingMCP, fist || foldOthers);
                j.LittleTip = Tip(j.LittleMCP, fist || foldOthers);
                j.ThumbTip = j.IndexTip + thumbFromIndexTip;
                return j;
            }

            var open = HoloKitHandSource.FromJoints(Hand(false, false, new Vector3(-0.04f, 0f, -0.09f)), Handedness.Right, head, false);
            var pinch = HoloKitHandSource.FromJoints(Hand(false, false, new Vector3(0.005f, 0f, 0f)), Handedness.Right, head, false);
            var halfway = Hand(false, false, new Vector3(0.03f, 0f, 0f)); // 1/3 of the hand's size: between on and off
            var fist = HoloKitHandSource.FromJoints(Hand(true, false, new Vector3(0.005f, 0f, 0f)), Handedness.Right, head, false);
            var folded = HoloKitHandSource.FromJoints(Hand(false, true, new Vector3(0.005f, 0f, 0f)), Handedness.Right, head, false);
            var collapsed = HoloKitHandSource.FromJoints(new HandJoints { Wrist = wrist, MiddleMCP = wrist }, Handedness.Right, head, false);

            Check(open.IsTracked && !open.IsPinched && !open.IsGrabbing && open.PinchStrength < 0.01f, "HoloKit mapping: an open hand is tracked, not pinched or grabbing");
            Check(pinch.IsPinched && !pinch.IsGrabbing && pinch.PinchStrength > 0.99f, "HoloKit mapping: thumb on index tip pinches");
            Check(!HoloKitHandSource.FromJoints(halfway, Handedness.Right, head, false).IsPinched &&
                  HoloKitHandSource.FromJoints(halfway, Handedness.Right, head, true).IsPinched,
                "HoloKit mapping: between the thresholds a pinch neither starts nor ends (hysteresis)");
            Check(fist.IsGrabbing && !fist.IsPinched, "HoloKit mapping: a fist grabs and doesn't pinch");
            Check(folded.IsPinched && !folded.IsGrabbing, "HoloKit mapping: a pinch with the other fingers folded is still a pinch, not a grab");
            Check(!collapsed.IsTracked, "HoloKit mapping: collapsed joints aren't a hand");

            var shoulder = new Vector3(0.15f, -0.18f, 0f);
            var knuckle = wrist + new Vector3(-0.02f, 0f, 0.09f);
            Check(Vector3.Distance(open.PointingRay.origin, knuckle) < 1e-5f &&
                  Vector3.Angle(open.PointingRay.direction, knuckle - shoulder) < 0.01f && Vector3.Dot(open.PointingRay.direction, Vector3.forward) > 0.9f,
                "HoloKit mapping: the ray goes from the shoulder through the index knuckle, forward");
            Check(Vector3.Angle(open.PointingRay.direction, pinch.PointingRay.direction) < 0.01f &&
                  Vector3.Distance(open.PointingRay.origin, pinch.PointingRay.origin) < 1e-5f,
                "HoloKit mapping: pinching doesn't move the ray");
            var leftOpen = HoloKitHandSource.FromJoints(Hand(false, false, new Vector3(-0.04f, 0f, -0.09f)), Handedness.Left, head, false);
            Check(Vector3.Angle(leftOpen.PointingRay.direction, knuckle - new Vector3(-0.15f, -0.18f, 0f)) < 0.01f,
                "HoloKit mapping: the left hand's ray starts from the left shoulder");
        }

        static MonoBehaviour FindMouse(WindowManager manager)
        {
            foreach (var source in manager.InputSources)
                if (source is MouseWindowInput) return source;
            return null;
        }

        static IEnumerator Body(WindowManager manager, HandWindowInput hands, WindowSpawnTester tester, Camera cam,
            ScriptedHandSource scripted)
        {
            var head = cam.transform;

            // Fingertips below and to each side of the head, like SimulatedHandSource.
            Vector3 Tip(Handedness hand, float extraReach = 0f)
            {
                float side = hand == Handedness.Right ? 1f : -1f;
                return head.position + head.right * (0.15f * side) - head.up * 0.2f + head.forward * (0.35f + extraReach);
            }

            HandState Hand(Handedness hand, Vector3 aimAt, bool pinched, float extraReach = 0f)
            {
                var tip = Tip(hand, extraReach);
                return new HandState
                {
                    IsTracked = true,
                    IndexTipPosition = tip,
                    WristPosition = tip - head.forward * 0.09f,
                    Pinch = pinched ? PinchState.Pinched : PinchState.Open,
                    PinchStrength = pinched ? 1f : 0f,
                    PointingRay = new Ray(tip, (aimAt - tip).normalized),
                };
            }

            // Start clean: two placeholder windows, A moved left so part of it is visible beside B.
            manager.CloseAll();
            yield return 0.2f;
            tester.SpawnTestWindow();
            var a = manager.FocusedWindow;
            tester.SpawnTestWindow();
            var b = manager.FocusedWindow;
            a.MoveTo(a.transform.position - head.right * 0.35f);
            yield return Settle;

            // Hover: the cursor lands where the ray hits.
            var bCenter = WindowTestUtil.Part(b, "Body").position;
            scripted.Right = Hand(Handedness.Right, bCenter, false);
            yield return Converge;
            bool cursorShown = hands.TryGetCursor(Handedness.Right, out var cursor);
            Check(cursorShown && Vector3.Distance(cursor, bCenter) < 0.01f,
                $"hover shows the cursor on the window ({(cursorShown ? Vector3.Distance(cursor, bCenter) * 1000f : -1f):F1} mm from the aim point)");

            scripted.Right = HandState.Untracked;
            yield return 0.1f;
            Check(!hands.TryGetCursor(Handedness.Right, out _), "losing tracking hides the cursor");

            // Pinch focuses immediately on press, before release.
            if (!WindowTestUtil.TryFindVisiblePoint(manager, a, Tip(Handedness.Right), out var aPoint))
            {
                Check(false, "found a visible point on window A");
                yield break;
            }
            scripted.Right = Hand(Handedness.Right, aPoint, false);
            yield return Converge;
            scripted.Right = Hand(Handedness.Right, aPoint, true);
            yield return 0.1f;
            Check(manager.FocusedWindow == a, $"pinch press focuses the unfocused window ({manager.FocusedWindow?.Title})");
            scripted.Right = Hand(Handedness.Right, aPoint, false);
            yield return Settle;

            // Drag in the window's plane.
            var title = WindowTestUtil.Part(a, "TitleBar").position;
            scripted.Right = Hand(Handedness.Right, title, false);
            yield return Converge;
            scripted.Right = Hand(Handedness.Right, title, true);
            yield return 0.1f;
            Check(hands.GetActiveWindow(Handedness.Right) == a, "pinching the title bar grabs the window");

            // Axes of the window's plane at grab time (billboarding turns the window as it moves).
            var normal = a.transform.forward;
            var sideways = a.transform.right;
            var start = a.transform.position;
            const float lateral = 0.2f;
            for (int i = 1; i <= 10; i++)
            {
                scripted.Right = Hand(Handedness.Right, title + sideways * (lateral * i / 10f), true);
                yield return 0.05f;
            }
            yield return Converge;
            var moved = a.transform.position - start;
            Check(Mathf.Abs(Vector3.Dot(moved, sideways) - lateral) < 0.03f && Mathf.Abs(Vector3.Dot(moved, normal)) < 0.01f,
                $"drag moves the window {Vector3.Dot(moved, sideways):F3} m sideways (target {lateral}) and " +
                $"{Vector3.Dot(moved, normal) * 1000f:F1} mm off its plane");

            // In/out: move the hand away from the head; the window goes away along its normal.
            var aimNow = title + sideways * lateral;
            float handBefore = Vector3.Distance(Tip(Handedness.Right), head.position);
            float handAfter = Vector3.Distance(Tip(Handedness.Right, 0.06f), head.position);
            float expectedDepth = (handAfter - handBefore - hands.DeadZoneDistance) * hands.DepthGain;
            var beforeDepth = a.transform.position;
            for (int i = 1; i <= 6; i++)
            {
                float extra = 0.06f * i / 6f;
                // Keep the direction: shift the aim point with the hand.
                scripted.Right = Hand(Handedness.Right, aimNow + head.forward * extra, true, extra);
                yield return 0.05f;
            }
            yield return 0.6f;
            float depthMoved = Vector3.Dot(a.transform.position - beforeDepth, normal);
            Check(Mathf.Abs(depthMoved - expectedDepth) < 0.04f,
                $"moving the hand {(handAfter - handBefore) * 100f:F1} cm away pushes the window {depthMoved:F3} m back (expected {expectedDepth:F3})");

            // Tremor: zero-mean noise on a still hand must not jitter the held window. Right after a move the
            // filter's held ray sits at the edge of the dead zone, so the first noise can settle it once by up
            // to the noise amplitude; after that the window must stay put.
            var rng = new System.Random(1234);
            var steadyAim = aimNow + head.forward * 0.06f;
            HandState Noisy()
            {
                var noisy = Hand(Handedness.Right, steadyAim, true, 0.06f);
                var axis = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).normalized;
                noisy.PointingRay = new Ray(
                    noisy.PointingRay.origin + axis * (0.002f * (float)rng.NextDouble()),
                    Quaternion.AngleAxis(0.25f * (float)(rng.NextDouble() * 2 - 1), axis) * noisy.PointingRay.direction);
                return noisy;
            }
            var beforeNoise = a.transform.position;
            for (int i = 0; i < 30; i++)
            {
                scripted.Right = Noisy();
                yield return null;
            }
            float settle = Vector3.Distance(a.transform.position, beforeNoise);
            var restPosition = a.transform.position;
            float maxDeviation = 0f;
            for (int i = 0; i < 60; i++)
            {
                scripted.Right = Noisy();
                yield return null;
                maxDeviation = Mathf.Max(maxDeviation, Vector3.Distance(a.transform.position, restPosition));
            }
            float unfiltered = Vector3.Distance(head.position, restPosition) * Mathf.Tan(0.25f * Mathf.Deg2Rad) * 1000f + 2f;
            Check(maxDeviation < 0.001f,
                $"tremor (±0.25°, 2 mm) jitters the held window by {maxDeviation * 1000f:F2} mm " +
                $"(unfiltered it would be up to ~{unfiltered:F1} mm; one-off settle {settle * 1000f:F1} mm)");

            scripted.Right = Hand(Handedness.Right, steadyAim, false, 0.06f);
            yield return Converge;
            Check(hands.GetActiveWindow(Handedness.Right) == null, "opening the pinch releases the window");

            // Undo the depth push, which left A behind B, so its handle and close button are reachable.
            a.MoveTo(beforeDepth);
            yield return Settle;

            // Resize from the bottom-right handle.
            var handle = WindowTestUtil.Part(a, "ResizeHandle").position;
            var startSize = a.Size;
            scripted.Right = Hand(Handedness.Right, handle, false);
            yield return Converge;
            scripted.Right = Hand(Handedness.Right, handle, true);
            yield return 0.1f;
            var grow = a.transform.right * 0.1f - a.transform.up * 0.06f;
            for (int i = 1; i <= 8; i++)
            {
                scripted.Right = Hand(Handedness.Right, handle + grow * (i / 8f), true);
                yield return 0.05f;
            }
            yield return Converge;
            scripted.Right = Hand(Handedness.Right, handle + grow, false);
            yield return 0.1f;
            var grown = a.Size - startSize;
            Check(Mathf.Abs(grown.x - 0.1f) < 0.02f && Mathf.Abs(grown.y - 0.06f) < 0.02f,
                $"pinch-dragging the resize handle grows the window by {grown.x:F3} x {grown.y:F3} (target 0.100 x 0.060)");
            yield return Settle;

            // Close: pinch and release on the close button.
            int countBeforeClose = manager.Windows.Count;
            var close = WindowTestUtil.Part(a, "CloseButton").position;
            scripted.Right = Hand(Handedness.Right, close, false);
            yield return Converge;
            scripted.Right = Hand(Handedness.Right, close, true);
            yield return 0.1f;
            scripted.Right = Hand(Handedness.Right, close, false);
            yield return 0.2f;
            Check(a == null && manager.Windows.Count == countBeforeClose - 1, "pinching the close button closes the window");

            // Controls: a launcher icon (button), then the video's play/pause button and seek bar (slider).
            var launcherWindow = Object.FindFirstObjectByType<LauncherWindow>();
            if (launcherWindow == null)
            {
                Check(false, "scene has a LauncherWindow for the control checks");
            }
            else
            {
                launcherWindow.OpenLauncher();
                yield return Settle;
                int countBeforeIcon = manager.Windows.Count;
                var videoIcon = launcherWindow.LauncherContent.Buttons[1].transform.position;
                scripted.Right = Hand(Handedness.Right, videoIcon, false);
                yield return Converge;
                scripted.Right = Hand(Handedness.Right, videoIcon, true);
                yield return 0.1f;
                scripted.Right = Hand(Handedness.Right, videoIcon, false);
                yield return Settle;
                var videoWindow = manager.FocusedWindow;
                Check(manager.Windows.Count == countBeforeIcon + 1 && videoWindow != null && videoWindow.Title == "Video",
                    "pinching the launcher's Video icon opens a Video window");

                var video = videoWindow != null ? videoWindow.GetComponentInChildren<VideoContent>() : null;
                if (video != null)
                {
                    double deadline = EditorTestRunner.Now + 5.0;
                    while (!video.IsPlaying && EditorTestRunner.Now < deadline) yield return 0.1f;

                    var playPause = video.PlayPauseButton.transform.position;
                    scripted.Right = Hand(Handedness.Right, playPause, false);
                    yield return Converge;
                    scripted.Right = Hand(Handedness.Right, playPause, true);
                    yield return 0.1f;
                    scripted.Right = Hand(Handedness.Right, playPause, false);
                    yield return 0.3f;
                    Check(!video.IsPlaying, "pinching play/pause pauses the video");

                    // Pinch the seek bar at 25% and drag to 75% before letting go.
                    var seek = video.SeekBar.transform;
                    var from = seek.TransformPoint(new Vector3(-0.25f, 0f, 0f));
                    var to = seek.TransformPoint(new Vector3(0.25f, 0f, 0f));
                    scripted.Right = Hand(Handedness.Right, from, false);
                    yield return Converge;
                    scripted.Right = Hand(Handedness.Right, from, true);
                    yield return 0.1f;
                    for (int i = 1; i <= 8; i++)
                    {
                        scripted.Right = Hand(Handedness.Right, Vector3.Lerp(from, to, i / 8f), true);
                        yield return 0.05f;
                    }
                    yield return Converge;
                    scripted.Right = Hand(Handedness.Right, to, false);
                    yield return 0.3f;
                    double target = 0.75 * video.Player.length;
                    Check(System.Math.Abs(video.Player.time - target) < 0.5,
                        $"pinch-dragging the seek bar scrubs to {video.Player.time:F2}s (target {target:F2}s)");
                }
                else
                {
                    Check(false, "Video window has VideoContent");
                }
            }

            // Two hands: right drags B by its title bar while the left pinches another window,
            // moved aside first so part of it is visible.
            var other = manager.FocusedWindow != b ? manager.FocusedWindow : manager.Windows[manager.Windows.Count - 2];
            other.MoveTo(other.transform.position - head.right * 0.45f);
            manager.Focus(b);
            yield return Settle;
            var bTitle = WindowTestUtil.Part(b, "TitleBar").position;
            scripted.Right = Hand(Handedness.Right, bTitle, false);
            yield return Converge;
            scripted.Right = Hand(Handedness.Right, bTitle, true);
            yield return 0.1f;

            bool otherVisible = WindowTestUtil.TryFindVisiblePoint(manager, other, Tip(Handedness.Left), out var otherPoint);
            if (otherVisible)
            {
                scripted.Left = Hand(Handedness.Left, otherPoint, false);
                yield return Converge;
                Check(hands.TryGetCursor(Handedness.Left, out _) && hands.TryGetCursor(Handedness.Right, out _),
                    "both hands show cursors at once");
                scripted.Left = Hand(Handedness.Left, otherPoint, true);
                yield return 0.1f;
                Check(manager.FocusedWindow == other && hands.GetActiveWindow(Handedness.Right) == b,
                    $"left pinch focuses {other.Title} while the right hand keeps holding {b.Title}");

                var bStart = b.transform.position;
                var bUp = b.transform.up;
                for (int i = 1; i <= 6; i++)
                {
                    scripted.Right = Hand(Handedness.Right, bTitle + bUp * (0.12f * i / 6f), true);
                    yield return 0.05f;
                }
                yield return Converge;
                float raised = Vector3.Dot(b.transform.position - bStart, bUp);
                Check(Mathf.Abs(raised - 0.12f) < 0.03f, $"the right hand still drags {b.Title} ({raised:F3} m up, target 0.12)");
                scripted.Left = HandState.Untracked;
            }
            else
            {
                Check(false, $"found a visible point on {other.Title} for the left hand");
            }

            // Losing tracking mid-drag lets go.
            scripted.Right = HandState.Untracked;
            yield return 0.1f;
            Check(hands.GetActiveWindow(Handedness.Right) == null && !hands.TryGetCursor(Handedness.Right, out _),
                "losing tracking mid-drag releases the window and hides the cursor");
        }
    }
}
