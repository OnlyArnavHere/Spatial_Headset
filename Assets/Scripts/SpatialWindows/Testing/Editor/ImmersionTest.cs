using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for immersion: the controller's level (clamping, change events, the [ and ] keys), the blend at
    /// each level for each environment (applied in the same frame), readability (environments draw before windows and
    /// never write depth or catch clicks; windows keep their poses through environment and level changes), and the dial
    /// opened from the launcher's environment icons, driven with mouse clicks. Restores level 0 afterwards. Results use
    /// an [ImmersionTest] prefix.
    /// </summary>
    static class ImmersionTest
    {
        const float Settle = 0.6f;

        [MenuItem("Tools/Spatial Windows/Test Immersion")]
        static void Run() => EditorTestRunner.Run("ImmersionTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Immersion", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        /// <summary>Saves the Game view for each environment at 50% and 100% to Temp/Immersion-*.png, to look at them.</summary>
        [MenuItem("Tools/Spatial Windows/Capture Environments")]
        static void Capture() => EditorTestRunner.Run("ImmersionCapture", CaptureSteps());

        [MenuItem("Tools/Spatial Windows/Capture Environments", true)]
        static bool CanCapture() => CanRun();

        static IEnumerator CaptureSteps()
        {
            var immersion = UnityEngine.Object.FindFirstObjectByType<ImmersionController>();
            if (immersion == null) yield break;
            try
            {
                foreach (ImmersiveEnvironment environment in Enum.GetValues(typeof(ImmersiveEnvironment)))
                foreach (float level in new[] { 0.5f, 1f })
                {
                    immersion.SetEnvironment(environment);
                    immersion.SetLevel(level);
                    yield return 0.3f;
                    string path = $"Temp/Immersion-{environment}-{Mathf.RoundToInt(level * 100)}.png";
                    ScreenCapture.CaptureScreenshot(path);
                    yield return 0.3f;
                    Check(true, $"saved {path}");
                }
            }
            finally
            {
                immersion.SetLevel(0f);
                immersion.SetEnvironment(ImmersiveEnvironment.Void);
            }
        }

        static readonly float[] Levels = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        static IEnumerator Steps()
        {
            var launcher = UnityEngine.Object.FindFirstObjectByType<LauncherWindow>();
            var manager = UnityEngine.Object.FindFirstObjectByType<WindowManager>();
            var mouse = UnityEngine.Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            var immersion = launcher != null ? launcher.Immersion : null;
            if (launcher == null || manager == null || mouse == null || cam == null || immersion == null)
            {
                Check(false, "scene has a LauncherWindow (with its ImmersionController), WindowManager, MouseWindowInput and main camera");
                yield break;
            }

            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var opened = new List<SpatialWindow>();
            try
            {
                yield return Controller(immersion);
                yield return Blending(immersion, cam);
                yield return Readability(immersion, manager, mouse, cam, opened);
                yield return Dial(launcher, immersion, manager, mouse, cam, opened);
            }
            finally
            {
                immersion.SetLevel(0f);
                immersion.SetEnvironment(ImmersiveEnvironment.Void);
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                if (launcher.Dial != null) manager.Close(launcher.Dial);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static IEnumerator Controller(ImmersionController immersion)
        {
            immersion.SetEnvironment(ImmersiveEnvironment.Void);
            immersion.SetLevel(0f);
            int changes = 0;
            Action count = () => changes++;
            immersion.Changed += count;
            try
            {
                immersion.SetLevel(-0.5f);
                Check(immersion.Level == 0f && changes == 0, "a level below 0 clamps to 0 (no change, no event)");
                immersion.SetLevel(1.7f);
                Check(immersion.Level == 1f && changes == 1, "a level above 1 clamps to 1 and raises one change event");
                immersion.SetLevel(1f);
                Check(changes == 1, "setting the same level again raises no event");
                immersion.SetLevel(0.3f);
                immersion.Step(ImmersionController.KeyStep);
                Check(Mathf.Abs(immersion.Level - 0.4f) < 1e-5f && changes == 3, $"Step adds {ImmersionController.KeyStep} ({immersion.Level:F2})");
                immersion.SetEnvironment(ImmersiveEnvironment.Cinema);
                Check(immersion.Environment == ImmersiveEnvironment.Cinema && Mathf.Abs(immersion.Level - 0.4f) < 1e-5f && changes == 4,
                    "switching environment keeps the level and raises a change event");
                Check(ImmersionController.Opacity(0f) == 0f && ImmersionController.Opacity(0.5f) == 0.5f && ImmersionController.Opacity(1f) == 1f
                      && ImmersionController.Opacity(-1f) == 0f && ImmersionController.Opacity(2f) == 1f,
                    "the blend curve is linear: opacity equals the level, clamped");
            }
            finally
            {
                immersion.Changed -= count;
            }

            // The real key path: ] three times, then [ once, as Input System keyboard events.
            immersion.SetEnvironment(ImmersiveEnvironment.Void);
            immersion.SetLevel(0f);
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                Check(false, "a keyboard device exists for the [ and ] keys");
                yield break;
            }
            for (int i = 0; i < 3; i++) yield return Tap(keyboard, Key.RightBracket);
            float afterMore = immersion.Level;
            yield return Tap(keyboard, Key.LeftBracket);
            Check(Mathf.Abs(afterMore - 0.3f) < 1e-4f && Mathf.Abs(immersion.Level - 0.2f) < 1e-4f,
                $"] three times raises the level to 0.3 ({afterMore:F2}), [ lowers it to 0.2 ({immersion.Level:F2})");
        }

        static IEnumerator Tap(Keyboard keyboard, Key key)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
            yield return 0.1f;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return 0.1f;
        }

        static IEnumerator Blending(ImmersionController immersion, Camera cam)
        {
            foreach (ImmersiveEnvironment environment in Enum.GetValues(typeof(ImmersiveEnvironment)))
            {
                immersion.SetLevel(0f);
                immersion.SetEnvironment(environment);
                var failures = new List<string>();
                foreach (float level in Levels)
                {
                    immersion.SetLevel(level);
                    // Same frame: nothing waits for an update.
                    var scene = immersion.Active;
                    if (level == 0f)
                    {
                        if (scene != null && scene.Root.activeSelf) failures.Add("drawn at level 0");
                        continue;
                    }
                    if (scene == null) { failures.Add($"not built at {level}"); continue; }
                    if (!scene.Root.activeSelf) failures.Add($"hidden at {level}");
                    var wrong = scene.Materials.Where(m => Mathf.Abs(m.color.a - level) > 1e-4f).ToList();
                    if (wrong.Count > 0) failures.Add($"alpha {wrong[0].color.a:F2} at level {level}");
                    var others = immersion.Built.Where(p => p.Key != environment && p.Value.Root.activeSelf).Select(p => p.Key).ToList();
                    if (others.Count > 0) failures.Add($"{string.Join(", ", others)} also drawn at {level}");
                }
                Check(failures.Count == 0, $"{environment}: every layer's opacity follows the level (0, 0.25, 0.5, 0.75, 1) at once, " +
                                           $"hidden at 0, alone ({(failures.Count == 0 ? "ok" : string.Join("; ", failures))})");
                yield return null;
            }

            // Placement: the floor under the user, the sky centred on the head.
            immersion.SetLevel(1f);
            immersion.SetEnvironment(ImmersiveEnvironment.Sky);
            yield return 0.1f;
            var sky = immersion.Active;
            var head = cam.transform.position;
            Check(sky != null && Vector3.Distance(sky.SkyRoot.position, head) < 1e-3f && Mathf.Abs(sky.Root.transform.position.y - (head.y - 1.5f)) < 1e-3f,
                "the sky is centred on the head and the ground is 1.5 m below it");
            // A unit sphere primitive has radius 0.5; its world bounds would overstate a rotated sphere.
            float radius = sky.Renderers.First(r => r.name == "Dome").transform.lossyScale.x * 0.5f;
            Check(radius < cam.farClipPlane, $"the sky dome (radius {radius:F1} m) fits inside the far clip plane ({cam.farClipPlane} m)");
        }

        static IEnumerator Readability(ImmersionController immersion, WindowManager manager, MouseWindowInput mouse, Camera cam,
            List<SpatialWindow> opened)
        {
            var a = manager.Spawn("Readable A");
            var b = manager.Spawn("Readable B");
            opened.Add(a);
            opened.Add(b);
            yield return Settle;

            int windowQueue = a.GetComponentsInChildren<MeshRenderer>().Min(r => r.sharedMaterial.renderQueue);
            foreach (ImmersiveEnvironment environment in Enum.GetValues(typeof(ImmersiveEnvironment)))
            {
                immersion.SetEnvironment(environment);
                immersion.SetLevel(1f);
                var scene = immersion.Active;
                var materials = scene.Materials.ToList();
                int maxQueue = materials.Max(m => m.renderQueue);
                bool noDepth = materials.All(m => m.GetFloat("_ZWrite") == 0f);
                bool noColliders = scene.Root.GetComponentsInChildren<Collider>(true).Length == 0;
                Check(maxQueue < windowQueue && noDepth && noColliders,
                    $"{environment}: drawn before windows (queues up to {maxQueue} < {windowQueue}), no depth writes, no colliders");
            }

            // Windows keep their poses through environment and level round trips.
            var poses = new[] { a, b }.Select(w => (w.transform.position, w.transform.rotation, w.Size)).ToArray();
            foreach (ImmersiveEnvironment environment in Enum.GetValues(typeof(ImmersiveEnvironment)))
            {
                immersion.SetEnvironment(environment);
                foreach (float level in Levels) immersion.SetLevel(level);
            }
            immersion.SetLevel(0f);
            immersion.SetEnvironment(ImmersiveEnvironment.Void);
            yield return null;
            bool unchanged = new[] { a, b }.Select((w, i) => Vector3.Distance(w.transform.position, poses[i].position) < 1e-5f
                                                              && Quaternion.Angle(w.transform.rotation, poses[i].rotation) < 1e-3f
                                                              && w.Size == poses[i].Size).All(x => x);
            Check(unchanged, "every window's pose and size is unchanged after a round trip through all environments and levels");

            // At full immersion a click still reaches the window behind the front one.
            immersion.SetEnvironment(ImmersiveEnvironment.Cinema);
            immersion.SetLevel(1f);
            yield return Settle;
            bool found = WindowTestUtil.TryFindVisiblePoint(manager, a, cam.transform.position, out var point);
            if (found)
            {
                Vector2 screen = cam.WorldToScreenPoint(point);
                mouse.PressAt(screen);
                mouse.ReleaseAt(screen);
                yield return Settle;
            }
            Check(found && manager.FocusedWindow == a, $"in the cinema at 100%, clicking a window still focuses it ({manager.FocusedWindow?.Title})");
        }

        static IEnumerator Dial(LauncherWindow launcher, ImmersionController immersion, WindowManager manager, MouseWindowInput mouse,
            Camera cam, List<SpatialWindow> opened)
        {
            immersion.SetLevel(0f);
            immersion.SetEnvironment(ImmersiveEnvironment.Void);
            launcher.OpenLauncher();
            yield return Settle;
            var icons = launcher.LauncherContent.Buttons;
            Check(icons.Count >= 7, $"the launcher has the Void, Cinema and Sky icons ({icons.Count} icons)");
            if (icons.Count < 7) yield break;

            Click(mouse, cam, icons[5].transform.position);
            yield return Settle;
            var dialWindow = launcher.Dial;
            var dial = dialWindow != null ? dialWindow.GetComponentInChildren<ImmersionDialContent>() : null;
            Check(immersion.Environment == ImmersiveEnvironment.Cinema && immersion.Level == 1f,
                $"the Cinema icon selects the cinema at full immersion from passthrough ({immersion.Environment}, {immersion.Level:F2})");
            Check(dial != null && manager.FocusedWindow == dialWindow && dialWindow.Title == "Immersion",
                "and opens the immersion dial in front");
            if (dial == null) yield break;
            Check(dial.Readout == "Immersion 100%  ·  Cinema", $"the dial reads \"{dial.Readout}\"");

            // Click a quarter of the way along the slider.
            Click(mouse, cam, dial.Slider.transform.TransformPoint(new Vector3(-0.25f, 0f, 0f)));
            yield return 0.2f;
            Check(Mathf.Abs(immersion.Level - 0.25f) < 0.02f && dial.Readout.StartsWith("Immersion 25%"),
                $"clicking a quarter along the slider sets 25% ({immersion.Level:F3}; \"{dial.Readout}\")");
            Click(mouse, cam, dial.Slider.transform.TransformPoint(new Vector3(-0.5f + 0.001f, 0f, 0f)));
            yield return 0.2f;
            Check(immersion.Level == 0f && (immersion.Active == null || !immersion.Active.Root.activeSelf),
                $"a click at its left end snaps to full passthrough: the environment isn't drawn ({immersion.Level:F3})");
            Check(ImmersionDialContent.SnapToEnds(0.015f) == 0f && ImmersionDialContent.SnapToEnds(0.985f) == 1f
                  && ImmersionDialContent.SnapToEnds(0.5f) == 0.5f && ImmersionDialContent.SnapToEnds(0.03f) == 0.03f,
                "the slider snaps within 2% of either end and nowhere else");

            Click(mouse, cam, dial.Slider.transform.TransformPoint(new Vector3(0.1f, 0f, 0f)));
            yield return 0.2f;
            Click(mouse, cam, dial.EnvironmentButtons[(int)ImmersiveEnvironment.Sky].transform.position);
            yield return 0.2f;
            Check(immersion.Environment == ImmersiveEnvironment.Sky && Mathf.Abs(immersion.Level - 0.6f) < 0.02f && dial.Readout == "Immersion 60%  ·  Sky",
                $"the Sky button switches environment and keeps the level (\"{dial.Readout}\")");

            immersion.Step(-ImmersionController.KeyStep);
            Check(dial.Readout == "Immersion 50%  ·  Sky", $"the dial follows changes made elsewhere, e.g. the [ key (\"{dial.Readout}\")");

            int count = manager.Windows.Count;
            Check(launcher.OpenImmersionDial() == dialWindow && manager.Windows.Count == count, "opening the dial again brings the same window back");
        }

        static void Click(MouseWindowInput mouse, Camera cam, Vector3 worldPoint)
        {
            Vector2 screen = cam.WorldToScreenPoint(worldPoint);
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
        }
    }
}
