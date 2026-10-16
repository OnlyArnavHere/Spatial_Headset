using System.Collections;
using System.Collections.Generic;
using HoloKit;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for the device fixes: HoloKitHandSource's depth choice (no LiDAR: hand size, not HoloKit's fixed
    /// plane) and its pipeline status; the status HUD's lines and its layout (once, or once per eye in Stereo); and
    /// StereoPassthrough's rules and geometry (the whole camera image from AR Foundation's display matrix, the plane
    /// matching the camera's frustum). Run in the editor test scene; in DeviceMain it also switches HoloKit to Stereo and
    /// checks the HUD is drawn in each eye and passthrough reacts. Also: the one-euro filter on 2D joints, two-hand
    /// identity matching, the tracking rate setting (Settings > Hands, with its heat note) and the camera format lines.
    /// Results use a [DeviceFixesTest] prefix.
    /// </summary>
    static class DeviceFixesTest
    {
        [MenuItem("Tools/Spatial Windows/Test Device Fixes")]
        static void Run() => EditorTestRunner.Run("DeviceFixesTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Device Fixes", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var errors = new List<string>();
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && !message.Contains("[DeviceFixesTest]")) errors.Add(message);
            };
            Application.logMessageReceived += onLog;
            try
            {
                CheckDepthChoice();
                CheckStatusLines();
                CheckLayout();
                CheckPassthroughRules();
                CheckPassthroughGeometry();
                CheckOneEuroFilter();
                CheckIdentityTracking();
                CheckCameraFormatLines();
                yield return CheckHandSourceStatus();
                yield return CheckTrackingRateSetting();
                yield return CheckScene();
            }
            finally
            {
                Application.logMessageReceived -= onLog;
            }
            Check(errors.Count == 0, $"no errors logged during the test ({errors.Count}{(errors.Count > 0 ? ": " + errors[0] : "")})");
        }

        static void CheckDepthChoice()
        {
            var d = HoloKitHandSource.DepthSource.Auto;
            Check(HoloKitHandSource.ResolveDepth(d, false) == HoloKitHandSource.DepthSource.HandScale,
                "Auto without LiDAR (iPhone 16) uses the hand's size, not HoloKit's fixed-plane 3D joints");
            Check(HoloKitHandSource.ResolveDepth(d, true) == HoloKitHandSource.DepthSource.LiDAR, "Auto with LiDAR uses LiDAR");
            Check(HoloKitHandSource.ResolveDepth(HoloKitHandSource.DepthSource.LiDAR, false) == HoloKitHandSource.DepthSource.LiDAR
                  && HoloKitHandSource.ResolveDepth(HoloKitHandSource.DepthSource.HandScale, true) == HoloKitHandSource.DepthSource.HandScale,
                "a forced depth source is kept");
        }

        static void CheckStatusLines()
        {
            var notStarted = new StatusReading
            {
                Mode = "Stereo", Session = "SessionTracking", HasDevicePipeline = true, Input = "HandWindowInput",
                HandInputActive = true, Passthrough = "on", Frame = 42, Fps = 60f,
                Hands = new HandTrackingStatus { TrackingFound = true, TrackingEnabled = true, DetectorCreated = false, ArFrames = 300, DepthPath = "-" },
            };
            string text = DeviceStatusHud.Describe(notStarted);
            Check(text.Contains("Hand tracking started: no - no Vision detector") && text.Contains("frame 42") &&
                  text.Contains("AR frames: 300") && text.Contains("Mode: Stereo"),
                $"the HUD says hand tracking didn't start and why, with frame counts (\"{OneLine(text)}\")");

            var disabled = new HandTrackingStatus { TrackingFound = true, TrackingEnabled = false, DetectorCreated = true };
            Check(!disabled.Started && disabled.NotStartedReason.Contains("disabled") &&
                  new HandTrackingStatus().NotStartedReason.Contains("no HandTrackingManager"),
                "each reason hand tracking can't start is named (missing, switched off, no detector)");

            var running = notStarted;
            running.Hands.DetectorCreated = true;
            running.Hands.VisionRequests = 120;
            running.Hands.HandsSeen = 1;
            running.Hands.DepthPath = "hand size";
            running.RightTracked = true;
            text = DeviceStatusHud.Describe(running);
            Check(text.Contains("Hand tracking started: yes") && text.Contains("Vision requests: 120") && text.Contains("hands seen: 1") &&
                  text.Contains("right yes") && text.Contains("left no") && text.Contains("Depth: hand size"),
                $"with hands, the HUD shows requests, hands seen and tracked, and the depth source (\"{OneLine(text)}\")");

            var editor = new StatusReading { Mode = "no HoloKit", HandSource = "SimulatedHandSource", Input = "MouseWindowInput", Passthrough = "off" };
            text = DeviceStatusHud.Describe(editor);
            Check(text.Contains("Hand source: SimulatedHandSource") && text.Contains("(hand input off)") && !text.Contains("Vision"),
                "without the device pipeline, the HUD names the hand source instead");
        }

        static void CheckLayout()
        {
            var panels = new List<Rect>();
            DeviceStatusHud.Layout(false, null, 1000f, new Vector2(400f, 200f), 20f, panels);
            Check(panels.Count == 1 && panels[0] == new Rect(20f, 20f, 400f, 200f), "in Mono the HUD is drawn once, top left");

            var eyes = new List<Rect> { new Rect(100f, 100f, 900f, 700f), new Rect(1100f, 100f, 900f, 700f) };
            DeviceStatusHud.Layout(true, eyes, 1000f, new Vector2(1200f, 200f), 10f, panels);
            bool inside = panels.Count == 2;
            for (int i = 0; inside && i < 2; i++)
            {
                var eyeTopLeft = new Rect(eyes[i].x, 1000f - eyes[i].yMax, eyes[i].width, eyes[i].height);
                inside = eyeTopLeft.Contains(panels[i].min) && eyeTopLeft.Contains(panels[i].max) &&
                         Mathf.Approximately(panels[i].y, 200f + 10f) && Mathf.Approximately(panels[i].x, eyes[i].x + 10f);
            }
            Check(inside && Mathf.Approximately(panels[0].width, 880f),
                "in Stereo the HUD is drawn once in each eye's viewport, at the same place, clipped to it");
        }

        static void CheckPassthroughRules()
        {
            Check(StereoPassthrough.ShouldShow(true, true, 0f) && StereoPassthrough.ShouldShow(true, true, 0.6f),
                "passthrough shows in Stereo at the passthrough end of the dial and under a part-immersed environment");
            Check(!StereoPassthrough.ShouldShow(true, true, 1f), "at full immersion the environment covers it, so it is off");
            Check(!StereoPassthrough.ShouldShow(true, false, 0f) && !StereoPassthrough.ShouldShow(false, true, 0f),
                "it is off in Mono (ARCameraBackground draws there) and when switched off");
        }

        static Vector2 Image(Matrix4x4 m, Vector2 uv) =>
            new Vector2(uv.x * m.m00 + uv.y * m.m10 + m.m20 + m.m30, uv.x * m.m01 + uv.y * m.m11 + m.m21 + m.m31);

        static bool Near(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < 1e-8f;
        static bool Near(Rect a, Rect b) => Near(a.min, b.min) && Near(a.max, b.max);

        static void CheckPassthroughGeometry()
        {
            Check(Near(StereoPassthrough.FullImageViewport(Matrix4x4.identity), new Rect(0f, 0f, 1f, 1f)),
                "with the identity display matrix the image is the screen");

            // Landscape screen wider than the image: the screen shows the image's middle 60% vertically.
            var crop = Matrix4x4.identity;
            crop.m11 = 0.6f;
            crop.m21 = 0.2f;
            var full = StereoPassthrough.FullImageViewport(crop);
            Check(Near(full, Rect.MinMaxRect(0f, -1f / 3f, 1f, 4f / 3f)), $"a cropped image reaches past the screen ({full})");

            // Rotated and cropped, as ARKit's matrices are: image.x = 0.9 - 0.8 v, image.y = u.
            var rotated = Matrix4x4.zero;
            rotated.m10 = -0.8f; rotated.m20 = 0.9f; rotated.m01 = 1f; rotated.m22 = 1f; rotated.m33 = 1f;
            full = StereoPassthrough.FullImageViewport(rotated);
            Check(Near(full, Rect.MinMaxRect(0f, -0.125f, 1f, 1.125f)), $"a rotated, cropped image is found too ({full})");

            var over = StereoPassthrough.DisplayOver(rotated, full);
            bool corners = true;
            foreach (var uv in new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one })
            {
                var image = Image(over, uv);
                corners &= (Mathf.Abs(image.x) < 1e-4f || Mathf.Abs(image.x - 1f) < 1e-4f) && (Mathf.Abs(image.y) < 1e-4f || Mathf.Abs(image.y - 1f) < 1e-4f);
            }
            var mid = new Vector2(0.3f, 0.7f);
            var direct = Image(rotated, new Vector2(full.x + mid.x * full.width, full.y + mid.y * full.height));
            Check(corners && Near(Image(over, mid), direct),
                "the copy's texcoords span the whole image and match the background's mapping inside it");

            var go = new GameObject("DeviceFixesTest Camera");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                go.transform.SetPositionAndRotation(new Vector3(0.3f, 1.4f, -0.2f), Quaternion.Euler(10f, 35f, 5f));
                cam.projectionMatrix = Matrix4x4.Frustum(-0.07f, 0.05f, -0.03f, 0.04f, 0.1f, 50f); // off-centre, like ARKit's
                var cornersLocal = new Vector3[4];
                var viewport = Rect.MinMaxRect(-0.1f, -0.3f, 1f, 1.3f);
                StereoPassthrough.PlaneCorners(cam.projectionMatrix, viewport, 2f, cornersLocal);
                float worst = 0f, worstReprojected = 0f;
                var inverse = cam.projectionMatrix.inverse;
                for (int i = 0; i < 4; i++)
                {
                    var uv = new Vector2((i & 1) == 0 ? viewport.xMin : viewport.xMax, (i >> 1) == 0 ? viewport.yMin : viewport.yMax);
                    var world = go.transform.TransformPoint(cornersLocal[i]);
                    // Unproject the viewport point through the projection, then go to view depth 2 along that ray.
                    var far = inverse * new Vector4(2f * uv.x - 1f, 2f * uv.y - 1f, 1f, 1f);
                    var ray = (Vector3)far / far.w;
                    var expected = cam.cameraToWorldMatrix.MultiplyPoint(ray * (2f / -ray.z));
                    worst = Mathf.Max(worst, Vector3.Distance(world, expected));
                    // And it projects back to that viewport point.
                    var view = cam.worldToCameraMatrix.MultiplyPoint(world);
                    var clip = cam.projectionMatrix * new Vector4(view.x, view.y, view.z, 1f);
                    var back = new Vector2((clip.x / clip.w + 1f) * 0.5f, (clip.y / clip.w + 1f) * 0.5f);
                    worstReprojected = Mathf.Max(worstReprojected, (back - uv).magnitude);
                }
                Check(worst < 1e-4f && worstReprojected < 1e-4f,
                    $"the plane's corners lie on the camera's frustum at its distance (off by {worst * 1000f:F3} mm, {worstReprojected:E1} in viewport)");
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        static void CheckOneEuroFilter()
        {
            const float rate = 30f, minCutoff = 1.5f, beta = 8f, dCutoff = 1f;
            var filter = new OneEuroFilter2D();
            var first = filter.Filter(new Vector2(0.4f, 0.6f), 1.0, minCutoff, beta, dCutoff);
            var repeat = filter.Filter(new Vector2(0.9f, 0.1f), 1.0, minCutoff, beta, dCutoff);
            Check(first == new Vector2(0.4f, 0.6f) && repeat == first,
                "the first joint sample passes through, and a repeated Vision result (same time) doesn't move it");

            // A still hand with Vision's jitter (about 1% of the image): the output is much steadier.
            filter.Reset();
            var random = new System.Random(7);
            float inSpread = 0f, outSpread = 0f;
            var centre = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < 90; i++)
            {
                var noisy = centre + new Vector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.02f;
                var output = filter.Filter(noisy, i / rate, minCutoff, beta, dCutoff);
                if (i < 30) continue;
                inSpread += (noisy - centre).sqrMagnitude;
                outSpread += (output - centre).sqrMagnitude;
            }
            float ratio = Mathf.Sqrt(outSpread / inSpread);
            Check(ratio < 0.4f, $"a still hand's joint jitter is cut to {ratio:P0} of Vision's");

            // A fast sweep (half the image per second): speed raises the cutoff, so it lags less than a fixed filter.
            float Lag(float b)
            {
                var f = new OneEuroFilter2D();
                Vector2 output = default, target = default;
                for (int i = 0; i <= 15; i++)
                {
                    target = new Vector2(0.2f + 0.5f * i / rate, 0.5f);
                    output = f.Filter(target, i / rate, minCutoff, b, dCutoff);
                }
                return target.x - output.x;
            }
            float lag = Lag(beta), fixedLag = Lag(0f);
            Check(lag < fixedLag * 0.6f && lag < 0.03f,
                $"a fast hand lags {lag:F3} of the image ({fixedLag:F3} without the speed term)");
        }

        static void CheckIdentityTracking()
        {
            var tracker = new HandIdentityTracker { MaxJump = 0.2f, Memory = 0.4f, SwitchAfter = 6 };
            var result = new List<Handedness>();
            double t = 0;
            Handedness One(Vector2 at, int chirality)
            {
                t += 1.0 / 30.0;
                tracker.Update(new[] { new HandIdentityTracker.Observation(at, chirality) }, t, result);
                return result[0];
            }

            bool steady = One(new Vector2(0.7f, 0.5f), 1) == Handedness.Right && tracker.Started(Handedness.Right);
            for (int i = 0; i < 4; i++) steady &= One(new Vector2(0.7f, 0.5f), -1) == Handedness.Right && !tracker.Started(Handedness.Right);
            Check(steady, "a hand keeps its identity when Vision calls it the other side for a few results");
            int switchedAt = -1;
            bool stays = true;
            for (int i = 0; i < 4; i++)
            {
                bool left = One(new Vector2(0.7f, 0.5f), -1) == Handedness.Left;
                if (left && switchedAt < 0 && tracker.Started(Handedness.Left)) switchedAt = i;
                else if (switchedAt >= 0) stays &= left && !tracker.Started(Handedness.Left);
            }
            Check(switchedAt == 1 && stays,
                $"but follows Vision on its 6th disagreement in a row, then stays (switched at {switchedAt + 5})");

            tracker.Reset();
            Check(One(new Vector2(0.25f, 0.5f), 0) == Handedness.Left && One(new Vector2(0.27f, 0.5f), 0) == Handedness.Left,
                "a new hand Vision can't place takes the side of the image it is on");
            t += 1.0; // lost for longer than the memory
            Check(One(new Vector2(0.8f, 0.5f), 1) == Handedness.Right && tracker.Started(Handedness.Right) && !tracker.IsActive(Handedness.Left),
                "after the memory runs out, a hand elsewhere starts a fresh track");

            // Two hands crossing, listed in Vision's order (which swaps every result), with no side from Vision.
            tracker.Reset();
            bool crossed = true;
            for (int i = 0; i <= 20; i++)
            {
                t += 1.0 / 30.0;
                var a = new Vector2(0.3f + 0.02f * i, 0.4f); // starts on the left, moves right
                var b = new Vector2(0.7f - 0.02f * i, 0.6f); // starts on the right, moves left
                bool swap = i % 2 == 1;
                var observations = swap
                    ? new[] { new HandIdentityTracker.Observation(b, 0), new HandIdentityTracker.Observation(a, 0) }
                    : new[] { new HandIdentityTracker.Observation(a, 0), new HandIdentityTracker.Observation(b, 0) };
                tracker.Update(observations, t, result);
                var aHand = swap ? result[1] : result[0];
                var bHand = swap ? result[0] : result[1];
                crossed &= aHand == Handedness.Left && bHand == Handedness.Right && (i == 0 || !tracker.Started(Handedness.Left));
            }
            Check(crossed, "two hands keep their identities while crossing, whatever order Vision lists them in");

            tracker.Reset();
            t += 1.0 / 30.0;
            tracker.Update(new[] { new HandIdentityTracker.Observation(new Vector2(0.4f, 0.5f), 1),
                new HandIdentityTracker.Observation(new Vector2(0.6f, 0.5f), -1) }, t, result);
            Check(result[0] == Handedness.Right && result[1] == Handedness.Left,
                "new hands take Vision's side when it gives one, even on the 'wrong' side of the image");
        }

        static void CheckCameraFormatLines()
        {
            Check(DeviceStatusHud.FormatName(new UnityEngine.XR.ARSubsystems.XRCameraConfiguration(System.IntPtr.Zero, new Vector2Int(1920, 1440), 60))
                  == "1920x1440 @ 60 fps", "a camera format reads as width x height @ fps");
            var reading = new StatusReading
            {
                Mode = "Mono", HasDevicePipeline = true, Passthrough = "off", Input = "HandWindowInput", HandInputActive = true,
                VisionHz = 29.6f, CameraFormat = "1920x1440 @ 60 fps",
                CameraFormats = new List<string> { "1920x1440 @ 60 fps", "1920x1080 @ 60 fps", "1280x720 @ 60 fps", "3840x2160 @ 30 fps" },
                Hands = new HandTrackingStatus { TrackingFound = true, TrackingEnabled = true, DetectorCreated = true, VisionFrameInterval = 2, MaxHands = 2, Smoothing2D = true },
            };
            string text = DeviceStatusHud.Describe(reading);
            Check(text.Contains("Vision: every 2nd frame, 29.6 Hz   max hands: 2") && text.Contains("2D smoothing: on"),
                $"the HUD shows Vision's frame interval, measured rate and hand count (\"{OneLine(text)}\")");
            Check(text.Contains("Camera: 1920x1440 @ 60 fps") && text.Contains("Formats (4):") && text.Contains("3840x2160 @ 30 fps"),
                "and ARKit's current camera format with the list the phone offers");
        }

        static IEnumerator CheckTrackingRateSetting()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            if (launcher == null)
            {
                Check(false, "scene has a LauncherWindow");
                yield break;
            }
            int before = SettingsStore.Current.hands.visionFrameInterval;
            Check(before >= 1 && before <= 3 && new HandSettings().visionFrameInterval == 2,
                $"the tracking rate setting is in range (every {before} frames) and defaults to every 2nd frame");

            // A device hand source under the launcher, as in DeviceMain, follows the setting.
            var existing = launcher.GetComponentInChildren<HoloKitHandSource>(true);
            var temporary = existing == null ? new GameObject("DeviceFixesTest Device Hands") : null;
            if (temporary != null) temporary.transform.SetParent(launcher.transform, false);
            var device = existing != null ? existing : temporary.AddComponent<HoloKitHandSource>();
            var window = launcher.OpenSettings();
            try
            {
                yield return 0.3f;
                var content = Object.FindFirstObjectByType<SettingsContent>();
                content.Select("Hands");
                yield return 0.3f;
                var twenty = content.Control("Hands", "Tracking rate", "20 Hz");
                var sixty = content.Control("Hands", "Tracking rate", "60 Hz");
                Check(twenty != null && sixty != null && content.ValueText("Hands", "Heat") != null,
                    "Settings > Hands has a Tracking rate choice and a Heat note");
                if (twenty == null || sixty == null) yield break;

                sixty.Press(sixty.transform.position);
                sixty.Release(true);
                yield return 0.4f;
                string heat = content.ValueText("Hands", "Heat");
                Check(SettingsStore.Current.hands.visionFrameInterval == 1 && device.VisionFrameInterval == 1 && heat.Contains("hottest"),
                    $"60 Hz runs Vision on every frame and warns about heat (\"{heat}\")");
                twenty.Press(twenty.transform.position);
                twenty.Release(true);
                yield return 0.4f;
                heat = content.ValueText("Hands", "Heat");
                Check(SettingsStore.Current.hands.visionFrameInterval == 3 && device.VisionFrameInterval == 3 && device.Status.VisionFrameInterval == 3 &&
                      heat.Contains("Coolest"), $"20 Hz runs it on every 3rd frame (\"{heat}\")");

                // The two new rows fit inside the window with the rest of Hands.
                var value = FindDeep(content.transform, "Row Heat").Find("Value");
                float bottom = content.transform.InverseTransformPoint(value.position).y - 0.018f; // half a row
                Check(bottom >= -SettingsContent.PreferredSize.y * 0.5f, $"the Heat row fits in the Settings window ({bottom:F3} m from the middle)");
            }
            finally
            {
                SettingsStore.Update(x => x.hands.visionFrameInterval = before);
                if (window != null) launcher.Settings.Manager.Close(window);
                if (temporary != null) Object.Destroy(temporary);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }

        static IEnumerator CheckHandSourceStatus()
        {
            var go = new GameObject("DeviceFixesTest Hands");
            try
            {
                var source = go.AddComponent<HoloKitHandSource>();
                yield return 0.1f;
                var status = source.Status;
                Check(!status.Started && status.NotStartedReason == "no HandTrackingManager assigned" &&
                      !source.GetHand(Handedness.Left).IsTracked && !source.GetHand(Handedness.Right).IsTracked && status.DepthPath == "-",
                    $"a HoloKitHandSource without HoloKit's manager reports why it hasn't started (\"{status.NotStartedReason}\")");
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        static IEnumerator CheckScene()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var hud = launcher != null ? launcher.GetComponent<DeviceStatusHud>() : null;
            var passthrough = launcher != null ? launcher.GetComponent<StereoPassthrough>() : null;
            Check(hud != null && passthrough != null, "the launcher adds the status HUD and stereo passthrough");
            if (hud == null || passthrough == null) yield break;

            bool wasVisible = hud.Visible;
            hud.Visible = true;
            var holoKit = Object.FindFirstObjectByType<HoloKitCameraManager>();
            try
            {
                yield return 0.5f;
                Check(hud.Text.Contains("Mode: ") && hud.Text.Contains("Passthrough: ") && hud.Panels.Count == 1,
                    $"the HUD shows its lines on screen once (\"{OneLine(hud.Text)}\")");

                if (holoKit == null)
                {
                    Check(!passthrough.Showing && passthrough.Status == "off (no HoloKit camera)",
                        $"without HoloKit, passthrough does nothing ({passthrough.Status})");
                    yield break;
                }

                var deviceHands = Object.FindFirstObjectByType<HoloKitHandSource>();
                Check(deviceHands != null && deviceHands.Status.MaxHands == 2 &&
                      deviceHands.Status.VisionFrameInterval == SettingsStore.Current.hands.visionFrameInterval,
                    $"DeviceMain tracks two hands at the Settings rate (max hands {deviceHands?.Status.MaxHands}, " +
                    $"every {deviceHands?.Status.VisionFrameInterval} frames)");

                // DeviceMain: switch HoloKit to Stereo and back.
                holoKit.ScreenRenderMode = ScreenRenderMode.Stereo;
                yield return 0.5f;
                Check(hud.Panels.Count == 2 && hud.Text.Contains("Mode: Stereo"),
                    $"in Stereo the HUD is drawn in both eyes ({hud.Panels.Count} panels)");
                Check(passthrough.Status != "off (mono)" && passthrough.Status != "off (no HoloKit camera)",
                    $"in Stereo, passthrough runs or waits for the feed ({passthrough.Status})");
                if (passthrough.Showing)
                {
                    // XR Simulation's feed in the editor: the plane fills the AR camera's view at its distance.
                    var cam = passthrough.Plane.parent.GetComponent<Camera>();
                    var bounds = passthrough.Plane.GetComponent<MeshRenderer>().bounds;
                    var centre = cam.transform.InverseTransformPoint(bounds.center);
                    Check(passthrough.Feed != null && passthrough.Feed.IsCreated() && Mathf.Abs(centre.z - passthrough.Distance) < 0.01f,
                        $"the feed is copied and the plane sits {centre.z:F2} m in front of the AR camera ({passthrough.Status})");
                    var immersion = launcher.Immersion;
                    float level = immersion.Level;
                    immersion.SetLevel(1f);
                    yield return 0.2f;
                    Check(!passthrough.Showing && passthrough.Status == "off (full immersion)", "full immersion switches it off");
                    immersion.SetLevel(level);
                    yield return 0.2f;
                    Check(passthrough.Showing, "and turning the dial back brings it back");
                }
                holoKit.ScreenRenderMode = ScreenRenderMode.Mono;
                yield return 0.3f;
                Check(!passthrough.Showing && passthrough.Status == "off (mono)" && hud.Panels.Count == 1,
                    $"back in Mono, passthrough is off and the HUD is drawn once ({passthrough.Status})");
            }
            finally
            {
                hud.Visible = wasVisible;
                if (holoKit != null) holoKit.ScreenRenderMode = ScreenRenderMode.Mono;
            }
        }

        static string OneLine(string text) => text.Replace("\r", "").Replace("\n", " | ");
    }
}
