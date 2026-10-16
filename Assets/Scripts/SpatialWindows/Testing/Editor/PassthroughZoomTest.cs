using System.Collections;
using System.Collections.Generic;
using HoloKit;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for the passthrough zoom: the setting (default 1, clamped, saved, presets and their cycle), when
    /// the plane shows (Mono only away from 1), the zoomed geometry (scaled about the camera's axis, angles from a
    /// projection and from ARKit-style intrinsics, the zoom that fits the whole image into HoloKit's eye view), the status
    /// lines, the quick toggle's button, and Settings > Display. In DeviceMain it also zooms the live plane in Mono and
    /// Stereo and fires HoloKit's stereo bar TRIGGER. Results use a [PassthroughZoomTest] prefix.
    /// </summary>
    static class PassthroughZoomTest
    {
        [MenuItem("Tools/Spatial Windows/Test Passthrough Zoom")]
        static void Run() => EditorTestRunner.Run("PassthroughZoomTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Passthrough Zoom", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var errors = new List<string>();
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && !message.Contains("[PassthroughZoomTest]")) errors.Add(message);
            };
            Application.logMessageReceived += onLog;
            float before = SettingsStore.Current.display.passthroughZoom;
            try
            {
                CheckSetting();
                CheckPresets();
                CheckRules();
                CheckGeometry();
                CheckHoloKitEye();
                CheckStatusLines();
                CheckButton();
                yield return CheckSettingsWindow();
                yield return CheckScene();
            }
            finally
            {
                Application.logMessageReceived -= onLog;
                SettingsStore.Update(x => x.display.passthroughZoom = before);
            }
            Check(errors.Count == 0, $"no errors logged during the test ({errors.Count}{(errors.Count > 0 ? ": " + errors[0] : "")})");
        }

        static void CheckSetting()
        {
            Check(Mathf.Approximately(new SpatialSettings().display.passthroughZoom, 1f), "passthrough zoom defaults to 1 (real size)");
            var s = new SpatialSettings();
            s.display.passthroughZoom = 3f;
            SettingsStore.Sanitize(s);
            float high = s.display.passthroughZoom;
            s.display.passthroughZoom = 0.1f;
            SettingsStore.Sanitize(s);
            float low = s.display.passthroughZoom;
            s.display.passthroughZoom = float.NaN;
            SettingsStore.Sanitize(s);
            Check(Mathf.Approximately(high, 1.5f) && Mathf.Approximately(low, 0.5f) && Mathf.Approximately(s.display.passthroughZoom, 1f),
                $"it is clamped to 0.5..1.5 on load, and an unreadable value goes back to 1 ({high}, {low}, {s.display.passthroughZoom})");

            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PassthroughZoomTest.json");
            try
            {
                System.IO.File.WriteAllText(file, "{\"version\":1,\"display\":{\"brightness\":0.8,\"ipdMm\":63.0}}");
                var old = SettingsStore.Load(file);
                Check(Mathf.Approximately(old.display.passthroughZoom, 1f) && Mathf.Approximately(old.display.ipdMm, 63f),
                    "a settings file from before the zoom loads with zoom 1 and keeps its other values");
                old.display.passthroughZoom = 0.7f;
                SettingsStore.Save(old, file);
                Check(Mathf.Approximately(SettingsStore.Load(file).display.passthroughZoom, 0.7f), "the zoom is saved and read back");
            }
            finally
            {
                System.IO.File.Delete(file);
            }
        }

        static void CheckPresets()
        {
            Check(DisplaySettings.ZoomPresetNames.Length == 3 && DisplaySettings.ZoomPresetNames[0] == "Wide" &&
                  DisplaySettings.ZoomPresetNames[1] == "Normal" && DisplaySettings.ZoomPresetNames[2] == "Close" &&
                  DisplaySettings.ZoomPresets[0] < 1f && Mathf.Approximately(DisplaySettings.ZoomPresets[1], 1f) && DisplaySettings.ZoomPresets[2] > 1f,
                "presets are Wide (below 1), Normal (1) and Close (above 1)");
            float wide = DisplaySettings.ZoomPresets[0], close = DisplaySettings.ZoomPresets[2];
            Check(Mathf.Approximately(DisplaySettings.NextZoomPreset(wide), 1f) && Mathf.Approximately(DisplaySettings.NextZoomPreset(1f), close) &&
                  Mathf.Approximately(DisplaySettings.NextZoomPreset(close), wide),
                "the quick toggle steps Wide, Normal, Close and back to Wide");
            Check(Mathf.Approximately(DisplaySettings.NextZoomPreset(0.85f), 1f) && Mathf.Approximately(DisplaySettings.NextZoomPreset(1.5f), wide) &&
                  Mathf.Approximately(DisplaySettings.NextZoomPreset(0.5f), wide),
                "from a zoom between presets it goes to the next larger one");
            Check(DisplaySettings.ZoomPresetIndex(0.85f) == -1 && DisplaySettings.ZoomLabel(0.85f) == "0.85x" &&
                  DisplaySettings.ZoomLabel(1f) == "Normal (1.00x)" && DisplaySettings.ZoomLabel(wide) == $"Wide ({wide:0.00}x)",
                $"labels name the preset with its value (\"{DisplaySettings.ZoomLabel(wide)}\")");
        }

        static void CheckRules()
        {
            Check(!StereoPassthrough.ShouldShow(true, false, 0f, 1f) && StereoPassthrough.ShouldShow(true, false, 0f, 0.7f) &&
                  StereoPassthrough.ShouldShow(true, false, 0f, 1.3f),
                "in Mono the plane shows only away from 1x (at 1x ARCameraBackground draws the feed at its real size)");
            Check(StereoPassthrough.ShouldShow(true, true, 0f, 1f) && StereoPassthrough.ShouldShow(true, true, 0.5f, 0.7f) &&
                  !StereoPassthrough.ShouldShow(false, true, 0f, 0.7f),
                "in Stereo it shows at every zoom when switched on");
            Check(!StereoPassthrough.ShouldShow(true, true, 1f, 0.7f) && !StereoPassthrough.ShouldShow(true, false, 1f, 0.7f),
                "full immersion hides it at any zoom");
            Check(Mathf.Approximately(StereoPassthrough.ClampZoom(9f), StereoPassthrough.MaxZoom) &&
                  Mathf.Approximately(StereoPassthrough.ClampZoom(0f), StereoPassthrough.MinZoom), "the component clamps its zoom too");
        }

        static void CheckGeometry()
        {
            // An off-centre projection (as ARKit's is) and the whole image reaching past a wide screen.
            var projection = Matrix4x4.Frustum(-0.07f, 0.05f, -0.03f, 0.04f, 0.1f, 50f);
            var viewport = Rect.MinMaxRect(0f, -0.3f, 1f, 1.3f);
            var corners = new Vector3[4];
            StereoPassthrough.PlaneCorners(projection, new Rect(0f, 0f, 1f, 1f), 2f, corners);
            var screen = StereoPassthrough.AngularSize(corners);
            var expected = StereoPassthrough.ProjectionFov(projection);
            Check(Vector2.Distance(screen, expected) < 0.01f,
                $"the plane over the screen spans the projection's field of view ({screen.x:F2} x {screen.y:F2} deg)");

            StereoPassthrough.PlaneCorners(projection, viewport, 2f, corners);
            var full = StereoPassthrough.AngularSize(corners);
            var centre = (corners[0] + corners[3]) * 0.5f;
            var zoomed = (Vector3[])corners.Clone();
            StereoPassthrough.ZoomCorners(zoomed, 0.5f);
            var half = StereoPassthrough.AngularSize(zoomed);
            bool scaled = true;
            for (int i = 0; i < 4; i++)
                scaled &= Mathf.Abs(zoomed[i].x - corners[i].x * 0.5f) < 1e-5f && Mathf.Abs(zoomed[i].y - corners[i].y * 0.5f) < 1e-5f &&
                          Mathf.Approximately(zoomed[i].z, 2f);
            // A direction on the camera's axis stays put: the plane point straight ahead is the same at any zoom.
            Check(scaled && half.x < full.x * 0.55f && half.y < full.y * 0.55f && centre.z > 0f,
                $"zoom 0.5 halves the plane about the camera's axis at the same depth ({full.x:F1} -> {half.x:F1} deg across)");

            // ARKit-style intrinsics: 1920x1440, focal 1500 px, centred.
            var fov = StereoPassthrough.IntrinsicsFov(new Vector2(1500f, 1500f), new Vector2(960f, 720f), new Vector2Int(1920, 1440));
            float wantX = 2f * Mathf.Atan(960f / 1500f) * Mathf.Rad2Deg, wantY = 2f * Mathf.Atan(720f / 1500f) * Mathf.Rad2Deg;
            Check(Mathf.Abs(fov.x - wantX) < 0.01f && Mathf.Abs(fov.y - wantY) < 0.01f &&
                  StereoPassthrough.IntrinsicsFov(Vector2.zero, Vector2.zero, new Vector2Int(1920, 1440)) == Vector2.zero,
                $"the camera's field of view from its intrinsics ({fov.x:F1} x {fov.y:F1} deg; unknown focal length gives none)");
        }

        // HoloKit's eye projection, built as HoloKitCameraManager.SetupCameraData builds it.
        static Matrix4x4 HoloKitEyeProjection(HoloKitModelSpecs specs, float ipd)
        {
            float width = specs.ViewportInner + specs.ViewportOuter, height = specs.ViewportTop + specs.ViewportBottom, near = specs.LensToEye;
            float gap = specs.OpticalAxisDistance + 2f * specs.ViewportOuter - 2f * width;
            var m = Matrix4x4.zero;
            m[0, 0] = 2f * near / width;
            m[1, 1] = 2f * near / height;
            m[0, 2] = (ipd - width - gap) / width;
            m[2, 2] = -1f; m[2, 3] = -2f * near; m[3, 2] = -1f;
            return m;
        }

        static void CheckHoloKitEye()
        {
            var specs = DeviceProfile.GetHoloKitModelSpecs(HoloKitGeneration.HoloKitX);
            var eye = StereoPassthrough.ProjectionFov(HoloKitEyeProjection(specs, 0.064f));
            float wantX = 2f * Mathf.Atan(0.0292f / 0.06395f) * Mathf.Rad2Deg, wantY = 2f * Mathf.Atan(0.02386f / 0.06395f) * Mathf.Rad2Deg;
            Check(Mathf.Abs(eye.x - wantX) < 0.05f && Mathf.Abs(eye.y - wantY) < 0.05f,
                $"HoloKit X's eye view is {eye.x:F1} x {eye.y:F1} deg (58.4 x 47.7 mm at 63.95 mm from the eye)");

            // An iPhone-like camera image (4:3, about 65 x 51 deg) is wider than the eye: at 1x the eye sees its middle.
            var camera = StereoPassthrough.IntrinsicsFov(new Vector2(1500f, 1500f), new Vector2(960f, 720f), new Vector2Int(1920, 1440));
            float fit = StereoPassthrough.FitZoom(camera, eye);
            var corners = new Vector3[4];
            float tx = Mathf.Tan(camera.x * 0.5f * Mathf.Deg2Rad), ty = Mathf.Tan(camera.y * 0.5f * Mathf.Deg2Rad);
            corners[0] = new Vector3(-tx, -ty, 1f); corners[1] = new Vector3(tx, -ty, 1f);
            corners[2] = new Vector3(-tx, ty, 1f); corners[3] = new Vector3(tx, ty, 1f);
            StereoPassthrough.ZoomCorners(corners, fit);
            var shown = StereoPassthrough.AngularSize(corners);
            Check(fit < 1f && shown.x <= eye.x + 0.01f && shown.y <= eye.y + 0.01f &&
                  (Mathf.Abs(shown.x - eye.x) < 0.05f || Mathf.Abs(shown.y - eye.y) < 0.05f),
                $"the whole camera image just fits the eye at {fit:F2}x ({shown.x:F1} x {shown.y:F1} deg); Wide is {DisplaySettings.ZoomPresets[0]:0.00}x");
            Check(Mathf.Abs(DisplaySettings.ZoomPresets[0] - fit) < 0.1f, "Wide is close to that fit");
        }

        static void CheckStatusLines()
        {
            var reading = new StatusReading
            {
                Mode = "Stereo", Passthrough = "on", Input = "HandWindowInput", HandSource = "SimulatedHandSource",
                CameraFormat = "1920x1440 @ 60 fps", CameraFov = new Vector2(65.2f, 51.3f), Zoom = 0.7f,
                ImageFov = new Vector2(47.8f, 36.6f), FullImageFov = new Vector2(65.2f, 51.3f), ViewFov = new Vector2(49.1f, 40.9f),
            };
            string text = DeviceStatusHud.Describe(reading);
            Check(text.Contains("Camera: 1920x1440 @ 60 fps   FOV 65.2° x 51.3° (plane at 1x: 65.2° x 51.3°)"),
                $"the HUD shows the camera format with its field of view (\"{OneLine(text)}\")");
            Check(text.Contains("Zoom: Wide (0.70x)   image 47.8° x 36.6°   eye 49.1° x 40.9°   whole image fits at"),
                "and the zoom, the image's angles and an eye's view");
            reading.Mode = "Mono";
            reading.ImageFov = null;
            reading.FullImageFov = null;
            reading.CameraFov = null;
            reading.Zoom = 1f;
            text = DeviceStatusHud.Describe(reading);
            Check(text.Contains("Zoom: Normal (1.00x)   image -   screen 49.1° x 40.9°") && text.Contains("FOV -") && !text.Contains("fits at"),
                $"unknown angles read as - (\"{OneLine(text)}\")");
        }

        static void CheckButton()
        {
            var screen = new Vector2(2556f, 1179f);
            var rect = PassthroughZoomControl.ButtonRectFor(screen);
            Check(rect.xMax < screen.x && rect.yMax < screen.y && rect.xMin > screen.x * 0.5f && rect.yMin > screen.y * 0.5f && rect.height > 60f,
                $"the Mono button sits at the bottom right, big enough to tap ({rect})");
            var inside = new Vector2(rect.center.x, screen.y - rect.center.y); // Input System: bottom-left origin
            Check(PassthroughZoomControl.Hits(rect, inside, screen.y) && !PassthroughZoomControl.Hits(rect, new Vector2(rect.center.x, rect.center.y), screen.y),
                "a touch on it hits (screen positions flipped to IMGUI's top-left origin), the mirrored spot doesn't");
            Check(PassthroughZoomControl.Caption(1.3f) == "Zoom: Close (1.30x)", "its caption names the zoom");
        }

        static IEnumerator CheckSettingsWindow()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            if (launcher == null)
            {
                Check(false, "scene has a LauncherWindow");
                yield break;
            }
            var passthrough = launcher.GetComponent<StereoPassthrough>();
            var control = launcher.GetComponent<PassthroughZoomControl>();
            Check(passthrough != null && control != null, "the launcher adds stereo passthrough and the zoom toggle");
            var window = launcher.OpenSettings();
            try
            {
                yield return 0.3f;
                var content = Object.FindFirstObjectByType<SettingsContent>();
                content.Select("Display");
                yield return 0.3f;
                var wide = content.Control("Display", "Zoom preset", "Wide");
                var close = content.Control("Display", "Zoom preset", "Close");
                var slider = content.Control("Display", "Passthrough zoom", "Slider");
                Check(wide != null && close != null && slider != null && content.ValueText("Display", "Field of view") != null,
                    "Settings > Display has a Passthrough zoom slider, Wide/Normal/Close presets and a field of view line");
                if (wide == null || close == null || slider == null) yield break;

                wide.Press(wide.transform.position);
                wide.Release(true);
                yield return 0.2f;
                Check(Mathf.Approximately(SettingsStore.Current.display.passthroughZoom, 0.7f) && passthrough != null &&
                      Mathf.Approximately(passthrough.Zoom, 0.7f) && content.ValueText("Display", "Passthrough zoom") == "0.70x",
                    $"Wide sets 0.70x and the passthrough follows ({passthrough?.Zoom})");
                close.Press(close.transform.position);
                close.Release(true);
                yield return 0.2f;
                Check(Mathf.Approximately(passthrough.Zoom, 1.3f), "Close sets 1.30x");

                // The slider's far left end.
                var track = slider.transform;
                slider.Press(track.TransformPoint(new Vector3(-0.5f, 0f, 0f)));
                slider.Release(true);
                yield return 0.2f;
                Check(Mathf.Abs(SettingsStore.Current.display.passthroughZoom - StereoPassthrough.MinZoom) < 0.051f,
                    $"the slider's left end is 0.5x ({SettingsStore.Current.display.passthroughZoom:0.00}x)");

                SettingsStore.Update(x => x.display.passthroughZoom = 1f);
                control.Step();
                yield return 0.1f;
                float first = passthrough.Zoom;
                control.Step();
                yield return 0.1f;
                Check(Mathf.Approximately(first, 1.3f) && Mathf.Approximately(passthrough.Zoom, 0.7f) &&
                      Mathf.Approximately(SettingsStore.Current.display.passthroughZoom, 0.7f),
                    "each quick-toggle press steps the saved setting to the next preset (Normal -> Close -> Wide)");

                var value = FindDeep(content.transform, "Row Field of view").Find("Value");
                float bottom = content.transform.InverseTransformPoint(value.position).y - 0.018f;
                Check(bottom >= -SettingsContent.PreferredSize.y * 0.5f, $"the new rows fit in the Settings window ({bottom:F3} m from the middle)");
            }
            finally
            {
                SettingsStore.Update(x => x.display.passthroughZoom = 1f);
                if (window != null) launcher.Settings.Manager.Close(window);
            }
        }

        static IEnumerator CheckScene()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            if (launcher == null) yield break;
            var passthrough = launcher.GetComponent<StereoPassthrough>();
            var control = launcher.GetComponent<PassthroughZoomControl>();
            var holoKit = Object.FindFirstObjectByType<HoloKitCameraManager>();
            if (holoKit == null)
            {
                SettingsStore.Update(x => x.display.passthroughZoom = 0.7f);
                yield return 0.2f;
                Check(!passthrough.Showing && !control.ButtonVisible && passthrough.Status == "off (no HoloKit camera)",
                    $"without HoloKit (editor test scene) a zoom changes nothing on screen ({passthrough.Status})");
                yield break;
            }

            var immersion = launcher.Immersion;
            float level = immersion.Level;
            immersion.SetLevel(0f);
            try
            {
                Check(control.ButtonVisible && holoKit.StereoTriggerLabel == "ZOOM",
                    $"with HoloKit, Mono shows the zoom button and the stereo bar's TRIGGER reads ZOOM (\"{holoKit.StereoTriggerLabel}\")");

                SettingsStore.Update(x => x.display.passthroughZoom = 1f);
                yield return 0.3f;
                Check(!passthrough.Showing && passthrough.Status == "off (mono)", $"Mono at 1x leaves the camera background alone ({passthrough.Status})");

                SettingsStore.Update(x => x.display.passthroughZoom = 0.7f);
                yield return 0.5f;
                Check(passthrough.Status != "off (mono)", $"Mono away from 1x runs the plane ({passthrough.Status})");
                if (passthrough.Showing)
                    yield return CheckPlaneZoom(passthrough, "Mono");

                holoKit.ScreenRenderMode = ScreenRenderMode.Stereo;
                yield return 0.5f;
                Check(!control.ButtonVisible, "in Stereo the on-screen button is hidden (the wearer uses ZOOM on the stereo bar)");
                if (passthrough.Showing)
                    yield return CheckPlaneZoom(passthrough, "Stereo");
                if (holoKit.StereoBar != null)
                {
                    int presses = control.Presses;
                    holoKit.StereoBar.FireTrigger();
                    yield return 0.2f;
                    Check(control.Presses == presses + 1 && Mathf.Approximately(passthrough.Zoom, 1f) &&
                          holoKit.StereoTriggerPressedLabel == "NORMAL",
                        $"the stereo bar's ZOOM steps Wide -> Normal and flashes the new preset (\"{holoKit.StereoTriggerPressedLabel}\")");
                }
                else Check(false, "HoloKit made its stereo bar");
            }
            finally
            {
                holoKit.ScreenRenderMode = ScreenRenderMode.Mono;
                immersion.SetLevel(level);
                SettingsStore.Update(x => x.display.passthroughZoom = 1f);
            }
            yield return 0.3f;
            Check(!passthrough.Showing && !passthrough.Backdrop.gameObject.activeSelf, "back in Mono at 1x the plane and backdrop are off");
        }

        // The plane at the current zoom (0.7) against 1x: same centre direction and depth, 0.7 times the size, with a
        // black backdrop around it; at 1.3 it grows and the backdrop goes.
        static IEnumerator CheckPlaneZoom(StereoPassthrough passthrough, string mode)
        {
            var cam = passthrough.Plane.parent;
            Vector3 Size(out Vector3 centre)
            {
                var vertices = passthrough.Plane.GetComponent<MeshFilter>().sharedMesh.vertices;
                centre = (vertices[0] + vertices[3]) * 0.5f;
                return vertices[3] - vertices[0];
            }
            var zoomed = Size(out var zoomedCentre);
            bool backdrop = passthrough.Backdrop.gameObject.activeSelf;
            var image = passthrough.ImageFov;
            SettingsStore.Update(x => x.display.passthroughZoom = 1f);
            yield return 0.3f;
            if (mode == "Mono")
            {
                Check(!passthrough.Showing, "Mono back at 1x hides the plane");
                SettingsStore.Update(x => x.display.passthroughZoom = 1.3f);
                yield return 0.3f;
                var close = Size(out _);
                bool backdropAtClose = passthrough.Backdrop.gameObject.activeSelf;
                SettingsStore.Update(x => x.display.passthroughZoom = 0.7f);
                yield return 0.3f;
                Check(Mathf.Abs(close.x / zoomed.x - 1.3f / 0.7f) < 0.01f && !backdropAtClose && passthrough.Backdrop.gameObject.activeSelf,
                    $"Mono: 1.3x is {close.x / zoomed.x:F3} times 0.7x across ({1.3f / 0.7f:F3} expected)");
                Check(backdrop && image.HasValue && passthrough.CameraFov.HasValue && image.Value.x < passthrough.CameraFov.Value.x,
                    $"Mono: a shrunk image has the black backdrop behind it and spans less than the camera ({image?.x:F1} of {passthrough.CameraFov?.x:F1} deg)");
                yield break;
            }
            var normal = Size(out var normalCentre);
            Check(Mathf.Abs(zoomed.x / normal.x - 0.7f) < 0.001f && Mathf.Abs(zoomed.y / normal.y - 0.7f) < 0.001f &&
                  Mathf.Abs(zoomedCentre.z - normalCentre.z) < 1e-4f && backdrop && !passthrough.Backdrop.gameObject.activeSelf,
                $"{mode}: at 0.7x the plane is 0.7 times the 1x plane at the same depth, with the backdrop only while shrunk");
            var cameraFov = passthrough.CameraFov ?? Vector2.zero;
            var fullFov = passthrough.ImageFov ?? Vector2.zero;
            Check(cameraFov.x > 0f && Vector2.Distance(cameraFov, fullFov) < 0.01f,
                $"{mode}: at 1x the image spans exactly the camera's angles ({fullFov.x:F1} x {fullFov.y:F1} deg)");
            SettingsStore.Update(x => x.display.passthroughZoom = 0.7f);
            yield return 0.3f;
            Check(cam != null, $"{mode}: the plane stays on the AR camera");
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }

        static string OneLine(string text) => text.Replace("\r", "").Replace("\n", " | ");
    }
}
