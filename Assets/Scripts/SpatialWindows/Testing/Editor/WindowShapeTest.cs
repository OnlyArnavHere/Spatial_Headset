using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for window shape: aspect preset maths (each ratio, the size limits), presets picked in a window's
    /// options strip, and curved windows: rays aimed at what is drawn hit the right part at the right layout point
    /// (analytic cylinder intersection), clicks on a curved laptop screen reach its sink at the right remote-screen
    /// position, rays past the bent edge miss, bent quads and labels lie on the cylinder, the options slider curves the
    /// window, and curve 0 restores the flat meshes. Results use a [ShapeTest] prefix.
    /// </summary>
    static class WindowShapeTest
    {
        const float Settle = 0.6f;

        [MenuItem("Tools/Spatial Windows/Test Window Shape")]
        static void Run() => EditorTestRunner.Run("ShapeTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Window Shape", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        /// <summary>
        /// Opens the Settings window and a curved window with its options strip open, and saves the Game view to
        /// Temp/Shape-*.png, to look at them. Leaves both open.
        /// </summary>
        [MenuItem("Tools/Spatial Windows/Capture Settings and Curved Window")]
        static void Capture() => EditorTestRunner.Run("ShapeCapture", CaptureSteps());

        [MenuItem("Tools/Spatial Windows/Capture Settings and Curved Window", true)]
        static bool CanCapture() => CanRun();

        static IEnumerator CaptureSteps()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            if (launcher == null || manager == null) yield break;
            var settings = launcher.OpenSettings().GetComponentInChildren<SettingsContent>();
            foreach (var section in new[] { "Laptop", "Hands", "About" })
            {
                settings.Select(section);
                yield return Settle;
                ScreenCapture.CaptureScreenshot($"Temp/Shape-Settings-{section}.png");
                yield return 0.3f;
            }
            var window = manager.Spawn("Curved window", new Vector2(0.64f, 0.4f));
            new GameObject().AddComponent<Testing.PlaceholderContent>().Init(window, manager.WindowMaterial, new Color(0.15f, 0.6f, 0.55f), "Curved 100%");
            window.SetCurve(1f);
            window.Options.SetOpen(true);
            yield return Settle;
            ScreenCapture.CaptureScreenshot("Temp/Shape-Curved.png");
            yield return 0.3f;
            Check(true, "saved Temp/Shape-Settings-*.png and Temp/Shape-Curved.png");
        }

        sealed class RecordingSink : IRemoteInputSink
        {
            public readonly List<Vector2> Downs = new List<Vector2>();
            public void PointerMove(Vector2 position) { }
            public void PointerDown(Vector2 position) => Downs.Add(position);
            public void PointerUp(Vector2 position) { }
            public void Scroll(Vector2 position, float delta) { }
        }

        static IEnumerator Steps()
        {
            AspectMath();

            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (manager == null || mouse == null || cam == null)
            {
                Check(false, "scene has a WindowManager, MouseWindowInput and main camera");
                yield break;
            }
            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var opened = new List<SpatialWindow>();
            try
            {
                yield return Presets(manager, mouse, cam, opened);
                yield return Curve(manager, mouse, cam, opened);
            }
            finally
            {
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static void AspectMath()
        {
            var expected = new Dictionary<AspectPreset, float>
            {
                { AspectPreset.Wide16x9, 16f / 9f }, { AspectPreset.Wide16x10, 1.6f }, { AspectPreset.Ultrawide21x9, 21f / 9f },
                { AspectPreset.Classic4x3, 4f / 3f }, { AspectPreset.SuperUltrawide32x9, 32f / 9f }, { AspectPreset.Square1x1, 1f },
                { AspectPreset.Photo3x2, 1.5f },
            };
            Check(AspectPresets.All.Length == 7 && AspectPresets.All.All(p => Mathf.Approximately(AspectPresets.Ratio(p), expected[p]))
                  && AspectPresets.Ratio(AspectPreset.Free) == 0f,
                $"7 presets with the right ratios: {string.Join(", ", AspectPresets.All.Select(AspectPresets.Label))}");

            Vector2 min = new Vector2(0.2f, 0.12f), max = new Vector2(2f, 1.5f);
            var wrong = new List<string>();
            foreach (var preset in AspectPresets.All)
            {
                var size = AspectPresets.Fit(new Vector2(0.6f, 0.4f), preset, min, max);
                if (Mathf.Abs(AspectPresets.ContentAspect(size) - AspectPresets.Ratio(preset)) > 1e-4f || Mathf.Abs(size.x - 0.6f) > 1e-5f)
                    wrong.Add($"{AspectPresets.Label(preset)} -> {size}");
            }
            Check(wrong.Count == 0, $"each preset gives the content area its aspect and keeps the width ({(wrong.Count == 0 ? "ok" : string.Join("; ", wrong))})");

            var thin = AspectPresets.Fit(new Vector2(0.3f, 0.4f), AspectPreset.SuperUltrawide32x9, min, max);
            Check(thin.y >= min.y - 1e-5f && Mathf.Abs(AspectPresets.ContentAspect(thin) - 32f / 9f) < 1e-4f,
                $"32:9 too short for the minimum height grows wider instead ({thin.x:F3} x {thin.y:F3})");
            var big = AspectPresets.Fit(new Vector2(2f, 1f), AspectPreset.Square1x1, min, max);
            Check(big.y <= max.y + 1e-5f && big.x <= max.x + 1e-5f && Mathf.Abs(AspectPresets.ContentAspect(big) - 1f) < 1e-4f,
                $"1:1 at the maximum width shrinks to fit the maximum height ({big.x:F3} x {big.y:F3})");
            var free = AspectPresets.Fit(new Vector2(5f, 0.05f), AspectPreset.Free, min, max);
            Check(free == new Vector2(2f, 0.12f), $"Free only clamps to the limits ({free})");
            var resized = AspectPresets.Constrain(new Vector2(0.9f, 0.2f), AspectPreset.Wide16x9, min, max);
            Check(Mathf.Abs(resized.x - 0.9f) < 1e-5f && Mathf.Abs(AspectPresets.ContentAspect(resized) - 16f / 9f) < 1e-4f,
                $"resizing a 16:9 window follows the dragged width and keeps 16:9 ({resized.x:F3} x {resized.y:F3})");
        }

        static IEnumerator Presets(WindowManager manager, MouseWindowInput mouse, Camera cam, List<SpatialWindow> opened)
        {
            var window = manager.Spawn("Shape", new Vector2(0.6f, 0.4f));
            opened.Add(window);
            yield return Settle;
            manager.SetAspect(window, AspectPreset.Ultrawide21x9);
            Check(window.Aspect == AspectPreset.Ultrawide21x9 && Mathf.Abs(AspectPresets.ContentAspect(window.Size) - 21f / 9f) < 1e-4f
                  && Mathf.Abs(window.Size.x - 0.6f) < 1e-5f, $"SetAspect 21:9 reshapes the window ({window.Size.x:F3} x {window.Size.y:F3})");

            // The options strip: "..." opens it, a preset button applies.
            Click(mouse, cam, window.Options.ToggleButton.transform.position);
            yield return Settle;
            Check(window.Options.IsOpen && window.LayoutBounds.yMin < -window.Size.y * 0.5f - 0.05f,
                "the title bar's ... button opens the options strip under the window");
            var fourThree = window.Options.AspectButtons[1 + System.Array.IndexOf(AspectPresets.All, AspectPreset.Classic4x3)];
            Click(mouse, cam, fourThree.transform.position);
            yield return Settle;
            Check(window.Aspect == AspectPreset.Classic4x3 && Mathf.Abs(AspectPresets.ContentAspect(window.Size) - 4f / 3f) < 1e-4f,
                $"clicking 4:3 in the strip applies it ({window.Size.x:F3} x {window.Size.y:F3})");
            Click(mouse, cam, window.Options.AspectButtons[0].transform.position);
            yield return 0.2f;
            Check(window.Aspect == AspectPreset.Free, "Free lifts the constraint");

            // The strip's slider curves the window.
            Click(mouse, cam, window.Options.CurveSlider.transform.TransformPoint(new Vector3(0f, 0f, 0f)));
            yield return 0.2f;
            Check(Mathf.Abs(window.Curve - 0.5f) < 0.03f && SpatialWindow.CurvedWindows.Contains(window),
                $"clicking the middle of the Curve slider curves the window by half ({window.Curve:F2})");
            window.SetCurve(0f);
            window.Options.SetOpen(false);
            manager.Close(window);
        }

        static IEnumerator Curve(WindowManager manager, MouseWindowInput mouse, Camera cam, List<SpatialWindow> opened)
        {
            var window = manager.Spawn("Curved", new Vector2(0.64f, 0.4f));
            opened.Add(window);
            var sink = new RecordingSink();
            var content = new GameObject().AddComponent<RemoteStreamContent>();
            content.Init(window, manager.WindowMaterial, sink, new Vector2Int(1600, 1000));
            yield return Settle;
            window.SetCurve(1f);
            yield return 0.2f;
            float radius = window.CurveRadius;
            var visual = window.Visual;
            var surface = content.Surface.transform;
            var surfacePart = content.Surface.GetComponent<WindowPart>();

            // Aim at where layout points are drawn (bent), as the user would.
            var misses = new List<string>();
            float worstFlat = 0f;
            foreach (var local in new[] { new Vector2(0.4f, 0.3f), new Vector2(-0.45f, -0.4f), new Vector2(0f, 0f), new Vector2(0.48f, -0.2f) })
            {
                var flatWorld = surface.TransformPoint(new Vector3(local.x, local.y, 0f));
                var drawn = visual.TransformPoint(WindowCurve.Bend(visual.InverseTransformPoint(flatWorld), radius));
                var ray = new Ray(cam.transform.position, (drawn - cam.transform.position).normalized);
                bool hit = WindowRaycast.TryHit(ray, 20f, ~0, out var info, out var part, out var surfacePoint);
                if (!hit || part != surfacePart || Vector3.Distance(info.point, flatWorld) > 0.002f || Vector3.Distance(surfacePoint, drawn) > 0.002f
                    || Mathf.Abs(info.distance - Vector3.Distance(cam.transform.position, drawn)) > 0.002f)
                    misses.Add($"{local}: {(hit ? $"{part.name}, {Vector3.Distance(info.point, flatWorld) * 1000f:F1} mm off" : "missed")}");
                // What the flat collider alone would have said: where the same ray crosses the flat window.
                if (new Plane(visual.forward, visual.position).Raycast(ray, out float flatDistance))
                    worstFlat = Mathf.Max(worstFlat, Vector3.Distance(ray.GetPoint(flatDistance), flatWorld));
            }
            Check(misses.Count == 0, $"rays aimed at the curved screen hit it at the layout point under the drawn one (within 2 mm; " +
                                     $"{(misses.Count == 0 ? "4 points" : string.Join("; ", misses))})");
            Check(worstFlat > 0.01f, $"(the flat collider alone would be off by up to {worstFlat * 1000f:F0} mm on these rays)");

            // A click on the curved screen reaches the remote screen at the right place.
            manager.Focus(window);
            yield return Settle;
            var target = surface.TransformPoint(new Vector3(0.4f, 0.3f, 0f));
            var drawnTarget = visual.TransformPoint(WindowCurve.Bend(visual.InverseTransformPoint(target), radius));
            Vector2 screen = cam.WorldToScreenPoint(drawnTarget);
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
            var expected = new Vector2(0.9f, 0.2f); // texture (0.9, 0.8) in screen convention
            Check(sink.Downs.Count == 1 && Vector2.Distance(sink.Downs[0], expected) < 0.01f,
                $"a click on the curved laptop screen reaches the sink at {(sink.Downs.Count > 0 ? sink.Downs[0].ToString("F3") : "nothing")} (expected {expected})");

            // Just past the bent right edge: no hit on this window.
            var bounds = window.LayoutBounds;
            var beyond = visual.TransformPoint(WindowCurve.Bend(new Vector3(bounds.xMax + 0.03f, 0f, 0f), radius));
            bool beyondHit = WindowRaycast.TryHit(new Ray(cam.transform.position, (beyond - cam.transform.position).normalized), 20f, ~0,
                out _, out var beyondPart) && beyondPart.Window == window;
            Check(!beyondHit, "a ray just past the bent edge misses the window");

            // Dragging a control follows the curve.
            var dragRay = new Ray(cam.transform.position, (drawnTarget - cam.transform.position).normalized);
            Check(WindowRaycast.TryProjectToLayout(window, dragRay, surface, out var projected) && Vector3.Distance(projected, target) < 0.002f,
                $"a drag ray is projected through the control's cylinder onto the flat layout ({Vector3.Distance(projected, target) * 1000f:F2} mm)");

            // What is drawn: the body quad is a bent strip on the cylinder, and the title text is bent too.
            var body = visual.Find("Body").GetComponent<MeshFilter>();
            var mesh = body.sharedMesh;
            float worst = 0f, sag = 0f;
            foreach (var vertex in mesh.vertices)
            {
                var p = visual.InverseTransformPoint(body.transform.TransformPoint(vertex));
                worst = Mathf.Max(worst, Mathf.Abs(new Vector2(p.x, p.z + radius).magnitude - radius));
                sag = Mathf.Min(sag, p.z);
            }
            // The body's edge is 0.32 m of arc from the centre.
            float expectedSag = radius * Mathf.Cos(0.32f / radius) - radius;
            Check(mesh.name == "Curved quad" && mesh.vertexCount > 20 && worst < 1e-4f && Mathf.Abs(sag - expectedSag) < 0.003f,
                $"the body is drawn as {mesh.vertexCount / 2 - 1} columns on the cylinder (off by {worst * 1000f:F3} mm; edges {-sag * 100f:F1} cm forward)");
            var title = visual.Find("TitleText").GetComponent<TextMeshPro>();
            var titleZ = title.mesh.vertices.Select(v => visual.InverseTransformPoint(title.transform.TransformPoint(v)).z).ToList();
            Check(titleZ.Count > 0 && titleZ.Max() - titleZ.Min() > 0.001f, $"the title text is bent ({(titleZ.Max() - titleZ.Min()) * 1000f:F1} mm deep)");

            // Flat again: the original quads come back and the colliders answer directly.
            window.SetCurve(0f);
            yield return 0.2f;
            Check(body.sharedMesh.name == "Quad" && !SpatialWindow.CurvedWindows.Contains(window), "curve 0 brings back the flat quads");
            var flatRay = new Ray(cam.transform.position, (target - cam.transform.position).normalized);
            Check(WindowRaycast.TryHit(flatRay, 20f, ~0, out var flatHit, out var flatPart, out var flatSurface) && flatPart == surfacePart
                  && Vector3.Distance(flatHit.point, flatSurface) < 1e-5f, "a flat window is hit through its colliders as before");
        }

        static void Click(MouseWindowInput mouse, Camera cam, Vector3 worldPoint)
        {
            Vector2 screen = cam.WorldToScreenPoint(worldPoint);
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
        }
    }
}
