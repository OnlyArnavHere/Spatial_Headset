using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for Hand Setup and the relative pointer: the calibrator's math (thresholds between the user's own
    /// poses, LiDAR hand length, refusals), the thresholds reaching HoloKitHandSource.FromJoints and the depth estimate,
    /// the settings (saved, sanitized, applied, Reset), the guided window with scripted hands (pointers and gestures paused
    /// while it measures, Save), Settings > Hands and Gestures, the relative pointer's freeze from pinch onset to click or
    /// drag, and HandWindowInput in relative mode. Results use a [HandSetupTest] prefix.
    /// </summary>
    static class HandSetupTest
    {
        [MenuItem("Tools/Spatial Windows/Test Hand Setup")]
        static void Run() => EditorTestRunner.Run("HandSetupTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Hand Setup", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        sealed class ScriptedHands : IHandSource
        {
            public HandState Left, Right;
            public HandState GetHand(Handedness hand) => hand == Handedness.Left ? Left : Right;
        }

        static HandState Hand(float pinchRatio, float extension, float size = 0.09f, bool metric = false) => new HandState
        {
            IsTracked = true, PinchRatio = pinchRatio, FingerExtension = extension, HandSize = size, MetricDepth = metric,
            PointingRay = new Ray(Vector3.zero, Vector3.forward),
        };

        static IEnumerator Steps()
        {
            var errors = new List<string>();
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && !message.Contains("[HandSetupTest]")) errors.Add(message);
            };
            Application.logMessageReceived += onLog;
            var saved = SettingsStore.Current.hands.calibration.Clone();
            var savedMode = SettingsStore.Current.hands.pointerMode;
            float savedGain = SettingsStore.Current.hands.relativeGain;
            try
            {
                CheckCalibrator();
                CheckThresholdsApply();
                CheckSettingsModel();
                CheckRelativePointer();
                yield return CheckSettingsApplied();
                yield return CheckWindow();
                yield return CheckSettingsRows();
                yield return CheckRelativeInput();
            }
            finally
            {
                Application.logMessageReceived -= onLog;
                SettingsStore.Update(s =>
                {
                    s.hands.calibration = saved;
                    s.hands.pointerMode = savedMode;
                    s.hands.relativeGain = savedGain;
                });
            }
            Check(errors.Count == 0, $"no errors logged during the test ({errors.Count}{(errors.Count > 0 ? ": " + errors[0] : "")})");
        }

        static void Fill(HandCalibrator c, HandCalibrator.Pose pose, float pinch, float extension, float size = 0.09f, bool metric = false)
        {
            var random = new System.Random(3 + (int)pose);
            for (int i = 0; i < HandCalibrator.SamplesNeeded + 5; i++)
            {
                float n() => ((float)random.NextDouble() - 0.5f) * 0.04f;
                c.Add(pose, Hand(pinch + n(), extension + n(), size + n() * 0.1f, metric));
            }
        }

        static void CheckCalibrator()
        {
            var c = new HandCalibrator();
            Fill(c, HandCalibrator.Pose.Open, 0.8f, 1.75f);
            Check(!c.TryCompute(new HandCalibration(), out _, out var missing) && missing.Contains("pinch"),
                $"it refuses until every pose has its samples (\"{missing}\")");
            Check(c.Count(HandCalibrator.Pose.Open) == HandCalibrator.SamplesNeeded && !c.Add(HandCalibrator.Pose.Open, Hand(0.8f, 1.75f)) &&
                  !c.Add(HandCalibrator.Pose.Pinch, HandState.Untracked) && !c.Add(HandCalibrator.Pose.Pinch, Hand(0f, 0f)),
                "a pose stops at its sample count; untracked hands and hands without raw measures are ignored");
            Fill(c, HandCalibrator.Pose.Pinch, 0.12f, 1.5f);
            Fill(c, HandCalibrator.Pose.Fist, 0.7f, 1.0f);
            var previous = new HandCalibration { handLength = 0.1f };
            bool ok = c.TryCompute(previous, out var r, out var problem);
            Check(ok && r.calibrated && Mathf.Abs(r.pinchOn - (r.pinchPinchRatio + 0.3f * (r.openPinchRatio - r.pinchPinchRatio))) < 1e-4f &&
                  Mathf.Abs(r.pinchOn - 0.324f) < 0.02f && Mathf.Abs(r.pinchOff - 0.46f) < 0.02f && r.pinchOff > r.pinchOn,
                $"pinch turns on 30% and off 50% of the way from the user's pinch to their open hand ({r?.pinchOn:F3} / {r?.pinchOff:F3}) {problem}");
            Check(ok && Mathf.Abs(r.curledRatio - 1.1875f) < 0.02f && Mathf.Abs(r.extendedRatio - 1.675f) < 0.02f,
                $"curled at 25% and fully open at 90% of the way from their fist to their open hand ({r?.curledRatio:F3}, {r?.extendedRatio:F3})");
            Check(ok && Mathf.Approximately(r.handLength, 0.1f) && !r.handLengthMeasured,
                "without LiDAR depth the hand length is kept (default or typed)");

            var lidar = new HandCalibrator();
            Fill(lidar, HandCalibrator.Pose.Open, 0.8f, 1.75f, 0.085f, true);
            Fill(lidar, HandCalibrator.Pose.Pinch, 0.12f, 1.5f, 0.085f, true);
            Fill(lidar, HandCalibrator.Pose.Fist, 0.7f, 1.0f, 0.085f, true);
            Check(lidar.TryCompute(previous, out var measured, out _) && measured.handLengthMeasured && Mathf.Abs(measured.handLength - 0.085f) < 0.002f,
                $"with LiDAR the hand length is measured ({measured?.handLength * 100f:F1} cm)");

            var weak = new HandCalibrator();
            Fill(weak, HandCalibrator.Pose.Open, 0.8f, 1.75f);
            Fill(weak, HandCalibrator.Pose.Pinch, 0.6f, 1.5f);
            Fill(weak, HandCalibrator.Pose.Fist, 0.7f, 1.0f);
            Check(!weak.TryCompute(previous, out _, out var notClosed) && notClosed.Contains("pinch didn't close"),
                $"a pinch that didn't close is refused (\"{notClosed}\")");
            var alike = new HandCalibrator();
            Fill(alike, HandCalibrator.Pose.Open, 0.8f, 1.6f);
            Fill(alike, HandCalibrator.Pose.Pinch, 0.12f, 1.5f);
            Fill(alike, HandCalibrator.Pose.Fist, 0.7f, 1.45f);
            Check(!alike.TryCompute(previous, out _, out var same) && same.Contains("too alike"),
                $"a fist that looks like the open hand is refused (\"{same}\")");
        }

        // A right hand at the origin, fingers along +Y, palm facing -Z: open or a fist, thumb tip at a given offset from the index tip.
        static HandJoints Joints(float fingerRatio, Vector3 thumbFromIndexTip)
        {
            var wrist = Vector3.zero;
            Vector3 Knuckle(float x) => new Vector3(x, 0.09f, 0f);
            Vector3 Tip(Vector3 knuckle) => wrist + (knuckle - wrist) * fingerRatio;
            var j = new HandJoints
            {
                Wrist = wrist,
                IndexMCP = Knuckle(0.03f), MiddleMCP = Knuckle(0.01f), RingMCP = Knuckle(-0.01f), LittleMCP = Knuckle(-0.03f),
            };
            j.IndexTip = Tip(j.IndexMCP);
            j.MiddleTip = Tip(j.MiddleMCP);
            j.RingTip = Tip(j.RingMCP);
            j.LittleTip = Tip(j.LittleMCP);
            j.ThumbTip = j.IndexTip + thumbFromIndexTip;
            return j;
        }

        static void CheckThresholdsApply()
        {
            var head = new Pose(new Vector3(0f, 0.2f, -0.5f), Quaternion.identity);
            var j = Joints(1.8f, new Vector3(0.027f, 0f, 0f)); // thumb 2.7 cm from the index tip
            var state = HoloKitHandSource.FromJoints(j, Handedness.Right, head, false);
            float size = Vector3.Distance(j.Wrist, j.MiddleMCP);
            Check(Mathf.Abs(state.PinchRatio - 0.027f / size) < 1e-4f && Mathf.Abs(state.FingerExtension - 1.8f) < 1e-3f &&
                  Mathf.Abs(state.HandSize - size) < 1e-5f && !state.MetricDepth,
                $"FromJoints reports the raw measures (pinch {state.PinchRatio:F3}, fingers {state.FingerExtension:F2}, size {state.HandSize * 100f:F1} cm)");

            var calibrated = new HandThresholds { PinchOn = 0.324f, PinchOff = 0.46f, CurledRatio = 1.19f, ExtendedRatio = 1.68f, HandLength = 0.08f };
            Check(!state.IsPinched && HoloKitHandSource.FromJoints(j, Handedness.Right, head, false, calibrated).IsPinched,
                $"a thumb {state.PinchRatio:F2} of the hand away isn't a pinch by default but is with the user's own threshold (0.32)");
            var curled = Joints(1.17f, new Vector3(0.06f, 0f, 0f));
            Check(!HoloKitHandSource.FromJoints(curled, Handedness.Right, head, false).IsGrabbing &&
                  HoloKitHandSource.FromJoints(curled, Handedness.Right, head, false, calibrated).IsGrabbing,
                "fingers at 1.17 of their knuckle distance aren't a fist by default (1.15) but are with a calibrated 1.19");
            var open = Joints(1.68f, new Vector3(0.06f, 0f, 0f));
            Check(Mathf.Abs(HoloKitHandSource.FromJoints(open, Handedness.Right, head, false, calibrated).Openness - 1f) < 1e-3f &&
                  HoloKitHandSource.FromJoints(open, Handedness.Right, head, false).Openness < 0.99f,
                "openness reaches 1 at the user's own open hand");
            var config = ScriptableObject.CreateInstance<GestureConfig>();
            float defaultDepth = HandDepthEstimator.DepthFor(0.2f, config), ownDepth = HandDepthEstimator.DepthFor(0.2f, config, 0.08f);
            Object.Destroy(config);
            Check(Mathf.Abs(defaultDepth - 0.45f) < 1e-4f && Mathf.Abs(ownDepth - 0.4f) < 1e-4f,
                $"hand-size depth uses the user's hand length ({defaultDepth:F2} m with 9 cm, {ownDepth:F2} m with 8 cm)");
        }

        static void CheckSettingsModel()
        {
            var fresh = new SpatialSettings();
            Check(!fresh.hands.calibration.calibrated && fresh.hands.pointerMode == PointerMode.Absolute &&
                  Mathf.Approximately(fresh.hands.relativeGain, 1.5f) && fresh.hands.calibration.Thresholds.Equals(HandThresholds.Default),
                "defaults: not calibrated (the old thresholds), absolute pointer, relative speed 1.5x");

            var s = new SpatialSettings();
            s.hands.calibration.pinchOn = 5f;
            s.hands.calibration.pinchOff = 0f;
            s.hands.calibration.curledRatio = float.NaN;
            s.hands.calibration.handLength = 2f;
            s.hands.relativeGain = 99f;
            s.hands.pointerMode = (PointerMode)7;
            SettingsStore.Sanitize(s);
            var c = s.hands.calibration;
            Check(c.pinchOn <= 0.6f && c.pinchOff > c.pinchOn && c.curledRatio < c.extendedRatio && c.handLength <= HandCalibration.MaxHandLength &&
                  Mathf.Approximately(s.hands.relativeGain, 3f) && s.hands.pointerMode == PointerMode.Absolute,
                "damaged values are put back in range on load");

            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HandSetupTest.json");
            try
            {
                System.IO.File.WriteAllText(file, "{\"version\":1,\"hands\":{\"sensitivity\":1.2}}");
                var old = SettingsStore.Load(file);
                Check(old.hands.calibration != null && !old.hands.calibration.calibrated && Mathf.Approximately(old.hands.sensitivity, 1.2f),
                    "a settings file from before Hand Setup loads uncalibrated and keeps its other values");
                old.hands.calibration.calibrated = true;
                old.hands.calibration.pinchOn = 0.3f;
                old.hands.pointerMode = PointerMode.Relative;
                SettingsStore.Save(old, file);
                var back = SettingsStore.Load(file);
                Check(back.hands.calibration.calibrated && Mathf.Approximately(back.hands.calibration.pinchOn, 0.3f) && back.hands.pointerMode == PointerMode.Relative,
                    "the calibration and pointer mode are saved and read back");
            }
            finally
            {
                System.IO.File.Delete(file);
            }
        }

        static Ray Aim(float yaw, float pitch = 0f) => new Ray(new Vector3(0.2f, -0.2f, 0.3f), Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);

        static float Yaw(Ray ray) => Mathf.Atan2(ray.direction.x, ray.direction.z) * Mathf.Rad2Deg;

        static void CheckRelativePointer()
        {
            const float dt = 1f / 60f;
            var p = new RelativePointer { Gain = 1.5f };
            var first = p.Update(Aim(0f), 0f, false, dt);
            Check(Mathf.Abs(Yaw(first)) < 1e-3f && p.Anchored, "the cursor starts where the hand points");
            for (int i = 1; i <= 10; i++) p.Update(Aim(i), 0f, false, dt);
            Check(Mathf.Abs(Yaw(p.Current) - 15f) < 0.05f, $"turning the hand 10 degrees turns the cursor 15 at gain 1.5 ({Yaw(p.Current):F2})");

            // Pinch onset: strength jumps; the hand keeps drifting 2 degrees while the fingers close.
            float before = Yaw(p.Current);
            p.Update(Aim(10.5f), 0.4f, false, dt);
            bool frozen = p.Frozen;
            p.Update(Aim(11.5f), 0.8f, false, dt);
            p.Update(Aim(12f), 1f, true, dt);
            Check(frozen && p.Frozen && Mathf.Abs(Yaw(p.Current) - before) < 1e-3f,
                $"from the start of a pinch the cursor holds still while the hand drifts ({before:F2} -> {Yaw(p.Current):F2})");
            p.Update(Aim(12f), 0f, false, dt);
            Check(p.State == RelativePointer.Phase.Free && Mathf.Abs(Yaw(p.Current) - before) < 1e-3f,
                "a click (pinch released) unfreezes it where it was");
            p.Update(Aim(14f), 0f, false, dt);
            Check(Mathf.Abs(Yaw(p.Current) - (before + 3f)) < 0.05f, "and it follows the hand's motion again from there");

            // A pinched hand turning past the drag angle turns into a drag.
            p.Update(Aim(14f), 1f, true, dt);
            p.Update(Aim(16f), 1f, true, dt);
            bool stillFrozen = p.Frozen;
            p.Update(Aim(18f), 1f, true, dt);
            float dragStart = Yaw(p.Current);
            p.Update(Aim(20f), 1f, true, dt);
            Check(stillFrozen && p.State == RelativePointer.Phase.Dragging && Mathf.Abs(Yaw(p.Current) - (dragStart + 3f)) < 0.05f,
                $"pinched and turning more than 3 degrees, it becomes a drag that follows the hand ({p.State})");
            p.Update(Aim(20f), 0f, false, dt);
            Check(p.State == RelativePointer.Phase.Free, "releasing ends the drag");

            // A slow rise of pinch strength (fingers relaxing) doesn't freeze; a rise that never pinches times out.
            for (int i = 0; i < 30; i++) p.Update(Aim(20f), i / 100f, false, dt);
            Check(!p.Frozen, "pinch strength creeping up slowly doesn't freeze the cursor");
            p.Update(Aim(20f), 0.7f, false, dt);
            bool started = p.Frozen;
            for (int i = 0; i < 45; i++) p.Update(Aim(20f), 0.7f, false, dt);
            Check(started && !p.Frozen, "a freeze whose pinch never closes ends after 0.6 s");

            // The cursor stays near where the hand really points, and re-anchors when the hand comes back.
            for (int i = 0; i <= 60; i++) p.Update(Aim(20f + i), 0f, false, dt);
            float off = Vector3.Angle(p.Current.direction, Aim(80f).direction);
            p.Reset();
            var again = p.Update(Aim(-5f), 0f, false, dt);
            Check(off <= p.MaxOffAngle + 0.01f && Mathf.Abs(Yaw(again) + 5f) < 1e-3f,
                $"it stays within {p.MaxOffAngle} degrees of the hand ({off:F1}) and re-anchors after the hand is lost");
        }

        static IEnumerator CheckSettingsApplied()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var hands = Object.FindFirstObjectByType<HandWindowInput>();
            var gestures = Object.FindFirstObjectByType<GestureRecognizers>();
            if (launcher == null || hands == null || gestures == null)
            {
                Check(false, "scene has a LauncherWindow, HandWindowInput and GestureRecognizers");
                yield break;
            }
            var temporary = new GameObject("HandSetupTest Device Hands");
            temporary.transform.SetParent(launcher.transform, false);
            var device = temporary.AddComponent<HoloKitHandSource>();
            try
            {
                var calibration = new HandCalibration { calibrated = true, pinchOn = 0.3f, pinchOff = 0.45f, curledRatio = 1.2f, extendedRatio = 1.75f, handLength = 0.082f };
                SettingsStore.Update(s =>
                {
                    s.hands.calibration = calibration;
                    s.hands.pointerMode = PointerMode.Relative;
                    s.hands.relativeGain = 2f;
                });
                yield return 0.1f;
                Check(Mathf.Approximately(device.Thresholds.PinchOn, 0.3f) && Mathf.Approximately(device.Thresholds.CurledRatio, 1.2f) &&
                      Mathf.Approximately(gestures.HandLength, 0.082f) && hands.Mode == PointerMode.Relative && Mathf.Approximately(hands.RelativeGain, 2f),
                    "a saved calibration reaches the device hand source and the gesture depth; the pointer mode and speed reach the hand input");
                SettingsStore.Update(s => s.hands.calibration = new HandCalibration());
                SettingsStore.Update(s => s.hands.pointerMode = PointerMode.Absolute);
                yield return 0.1f;
                Check(device.Thresholds.Equals(HandThresholds.Default) && hands.Mode == PointerMode.Absolute, "Reset puts the default thresholds back");
            }
            finally
            {
                Object.Destroy(temporary);
            }
        }

        static IEnumerator CheckWindow()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var hands = Object.FindFirstObjectByType<HandWindowInput>();
            var gestures = Object.FindFirstObjectByType<GestureRecognizers>();
            SettingsStore.Update(s => s.hands.calibration = new HandCalibration { handLength = 0.1f });
            var window = launcher.OpenHandSetup();
            var scripted = new ScriptedHands();
            try
            {
                yield return 0.3f;
                var content = window != null ? window.GetComponentInChildren<HandSetupContent>() : null;
                Check(content != null && content.CurrentStep == HandSetupContent.Step.Intro && content.PrimaryLabel == "Start" &&
                      content.SecondaryLabel == "Reset" && content.HandLengthSlider.gameObject.activeSelf,
                    "Hand Setup opens on its intro with Start, Reset and the hand length slider");
                if (content == null) yield break;
                content.Source = scripted;

                content.SetHandLength(0.0874f);
                Check(Mathf.Approximately(SettingsStore.Current.hands.calibration.handLength, 0.085f) && !SettingsStore.Current.hands.calibration.handLengthMeasured,
                    "the hand length can be typed in (0.5 cm steps) for phones without LiDAR");

                Press(content.PrimaryButton);
                Check(content.CurrentStep == HandSetupContent.Step.GetReady && content.CurrentPose == HandCalibrator.Pose.Open &&
                      content.PrimaryLabel == "Cancel" && hands.IsSuppressed(Handedness.Left) && hands.IsSuppressed(Handedness.Right) && gestures.Paused,
                    "Start asks for an open palm and pauses the hand pointers and gestures");
                yield return 0.5f;
                Check(content.CurrentStep == HandSetupContent.Step.GetReady && content.RingFill == 0f,
                    "with no hand in view the countdown waits");

                scripted.Right = Hand(0.8f, 1.75f, 0.086f, true);
                yield return WaitFor(() => content.CurrentPose == HandCalibrator.Pose.Pinch, 6f);
                Check(content.CurrentPose == HandCalibrator.Pose.Pinch && content.MeasuredHand == Handedness.Right,
                    $"the open palm is measured after the countdown, then it asks for a pinch (\"{content.Body}\")");
                scripted.Right = Hand(0.12f, 1.5f, 0.086f, true);
                yield return WaitFor(() => content.CurrentPose == HandCalibrator.Pose.Fist, 6f);
                scripted.Right = Hand(0.7f, 1.0f, 0.086f, true);
                yield return WaitFor(() => content.CurrentStep == HandSetupContent.Step.Result, 6f);
                var result = content.Result;
                Check(content.CurrentStep == HandSetupContent.Step.Result && result != null && result.handLengthMeasured &&
                      Mathf.Abs(result.pinchOn - 0.324f) < 0.01f && content.PrimaryLabel == "Save" && content.SecondaryLabel == "Retry" &&
                      !hands.IsSuppressed(Handedness.Right) && !gestures.Paused,
                    $"after the fist it shows the result with Save and Retry, and gives the hands back (\"{content.Body.Replace("\n", " | ")}\")");
                Check(!SettingsStore.Current.hands.calibration.calibrated, "nothing is saved before Save");
                Press(content.PrimaryButton);
                var savedCalibration = SettingsStore.Current.hands.calibration;
                Check(savedCalibration.calibrated && Mathf.Abs(savedCalibration.pinchOn - 0.324f) < 0.01f && Mathf.Abs(savedCalibration.handLength - 0.086f) < 0.001f &&
                      content.CurrentStep == HandSetupContent.Step.Intro && content.Body.Contains("Saved"),
                    $"Save stores it ({savedCalibration.Summary})");

                // A bad run: the "fist" is an open hand. And Cancel in the middle changes nothing.
                Press(content.PrimaryButton);
                scripted.Right = Hand(0.8f, 1.75f);
                yield return WaitFor(() => content.CurrentPose == HandCalibrator.Pose.Pinch, 6f);
                scripted.Right = Hand(0.12f, 1.5f);
                yield return WaitFor(() => content.CurrentPose == HandCalibrator.Pose.Fist, 6f);
                scripted.Right = Hand(0.8f, 1.7f);
                yield return WaitFor(() => content.CurrentStep == HandSetupContent.Step.Result, 6f);
                Check(content.Result == null && content.Problem.Length > 0 && content.PrimaryLabel == "Retry",
                    $"poses that don't differ are refused with a reason and Retry (\"{content.Problem}\")");
                Press(content.PrimaryButton);
                yield return 0.2f;
                Press(content.PrimaryButton);
                Check(content.CurrentStep == HandSetupContent.Step.Intro && SettingsStore.Current.hands.calibration.calibrated &&
                      Mathf.Abs(SettingsStore.Current.hands.calibration.pinchOn - 0.324f) < 0.01f && !gestures.Paused,
                    "Cancel during a run goes back to the intro and keeps the saved calibration");

                Press(content.SecondaryButton);
                Check(!SettingsStore.Current.hands.calibration.calibrated && SettingsStore.Current.hands.calibration.Thresholds.Equals(HandThresholds.Default),
                    "Reset puts the defaults back");

                // Closing the window mid-run gives the hands back.
                Press(content.PrimaryButton);
                Check(gestures.Paused, "measuring again pauses gestures");
                launcher.Settings.Manager.Close(window);
                yield return null;
                Check(!gestures.Paused && !hands.IsSuppressed(Handedness.Right), "closing Hand Setup mid-run gives the hands back");
            }
            finally
            {
                if (window != null) launcher.Settings.Manager.Close(window);
            }
        }

        static void Press(WindowButton button)
        {
            button.Press(button.transform.position);
            button.Release(true);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float seconds)
        {
            double deadline = EditorTestRunner.Now + seconds;
            while (!condition() && EditorTestRunner.Now < deadline) yield return null;
        }

        static IEnumerator CheckSettingsRows()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var window = launcher.OpenSettings();
            SpatialWindow setup = null;
            try
            {
                yield return 0.3f;
                var content = Object.FindFirstObjectByType<SettingsContent>();
                content.Select("Hands");
                yield return 0.2f;
                var relative = content.Control("Hands", "Pointer", "Relative");
                var calibrate = content.Control("Hands", "Hand setup", "Calibrate");
                Check(relative != null && content.Control("Hands", "Relative speed", "Slider") != null && calibrate != null &&
                      content.Control("Hands", "Hand setup", "Reset") != null && content.ValueText("Hands", "Calibration") != null &&
                      content.Control("Gestures", "Fist grab", "On") != null && content.Control("Gestures", "Cross to hide all", "On") != null,
                    "Settings > Hands has Pointer, Relative speed, Hand setup (Calibrate, Reset) and Calibration; the gesture toggles moved to Gestures");
                if (relative == null || calibrate == null) yield break;
                relative.Press(relative.transform.position);
                relative.Release(true);
                yield return 0.1f;
                Check(SettingsStore.Current.hands.pointerMode == PointerMode.Relative, "choosing Relative switches the pointer mode");
                calibrate.Press(calibrate.transform.position);
                calibrate.Release(true);
                yield return 0.2f;
                setup = launcher.HandSetupWindow;
                Check(setup != null && setup.GetComponentInChildren<HandSetupContent>() != null, "Calibrate opens Hand Setup");

                var value = FindDeep(content.transform, "Row Calibration").Find("Value");
                float bottom = content.transform.InverseTransformPoint(value.position).y - 0.018f;
                Check(bottom >= -SettingsContent.PreferredSize.y * 0.5f, $"the Hands rows fit in the Settings window ({bottom:F3} m from the middle)");
            }
            finally
            {
                SettingsStore.Update(s => s.hands.pointerMode = PointerMode.Absolute);
                if (setup != null) launcher.Settings.Manager.Close(setup);
                if (window != null) launcher.Settings.Manager.Close(window);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }

        static IEnumerator CheckRelativeInput()
        {
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var hands = Object.FindFirstObjectByType<HandWindowInput>();
            int previousInput = WindowTestUtil.SelectInput<HandWindowInput>(manager);
            var previousSource = hands.HandSource;
            var scripted = new ScriptedHands();
            var mode = hands.Mode;
            hands.SetHandSource(scripted);
            try
            {
                hands.Mode = PointerMode.Relative;
                hands.RelativeGain = 1.5f;
                var aim = Aim(0f);
                scripted.Right = new HandState { IsTracked = true, PointingRay = aim, IndexTipPosition = aim.origin, PinchStrength = 0f };
                yield return 0.3f;
                var settled = hands.GetPointerRay(Handedness.Right);
                // The fingers start closing fast while the hand drifts 2 degrees.
                for (int i = 1; i <= 4; i++)
                {
                    var drift = Aim(i * 0.5f);
                    scripted.Right = new HandState { IsTracked = true, PointingRay = drift, IndexTipPosition = drift.origin, PinchStrength = i * 0.25f,
                        Pinch = i == 4 ? PinchState.Pinched : PinchState.Open };
                    yield return null;
                }
                var during = hands.GetPointerRay(Handedness.Right);
                Check(hands.GetRelativePointer(Handedness.Right).Frozen && Vector3.Angle(settled.direction, during.direction) < 0.05f,
                    $"in relative mode the hand's cursor holds still from the start of a pinch ({Vector3.Angle(settled.direction, during.direction):F3} deg moved)");

                hands.Mode = PointerMode.Absolute;
                yield return 0.3f;
                var absolute = hands.GetPointerRay(Handedness.Right);
                Check(Vector3.Angle(absolute.direction, Aim(2f).direction) < 0.6f && !hands.GetRelativePointer(Handedness.Right).Anchored,
                    "in absolute mode it is the hand's own (filtered) ray");
            }
            finally
            {
                hands.Mode = mode;
                hands.SetHandSource(previousSource);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }
    }
}
