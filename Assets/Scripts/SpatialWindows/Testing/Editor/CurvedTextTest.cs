using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode regression test for curved text (<see cref="WindowCurver"/>): windows with empty text, long wrapped
    /// text, text that needs the fallback font (Cyrillic and Greek), several labels and labels cut short with an
    /// ellipsis, plus the launcher and the windows it opens, are curved at 0.25, 0.5 and 1.0 and run for a number of
    /// frames. Each time: no Console errors, and every drawn character vertex lies on the cylinder its label is drawn on
    /// (so bending is neither missing nor compounded). Also: dragging the Curve slider re-bends labels without
    /// regenerating them, idle labels aren't regenerated, regenerated and changed text is bent (including labels for
    /// which TextMeshPro over-reports its vertex count, the cause of an IndexOutOfRangeException in the old bending),
    /// and curve 0 brings the flat text back. Results use a [CurvedTextTest] prefix.
    /// </summary>
    static class CurvedTextTest
    {
        const int Frames = 90;
        const float Tolerance = 2e-4f; // metres off the cylinder

        static readonly float[] Curves = { 0.25f, 0.5f, 1f };
        const string LongText =
            "Curved windows wrap their content around a gentle cylinder. This label is long enough to wrap onto many lines, " +
            "so its mesh has hundreds of vertices: the quick brown fox jumps over the lazy dog, again and again, while the " +
            "window bends. 0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~ The end of the long label, one more time: the quick " +
            "brown fox jumps over the lazy dog.";
        const string FallbackText = "Привет, мир! Γειά σου κόσμε. Mixed with Latin.";

        [MenuItem("Tools/Spatial Windows/Test Curved Text")]
        static void Run() => EditorTestRunner.Run("CurvedTextTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Curved Text", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        // Console errors and exceptions raised while the test runs (its own FAIL lines are plain logs).
        static readonly List<string> errors = new List<string>();
        // How often each label was regenerated.
        static readonly Dictionary<Object, int> regenerated = new Dictionary<Object, int>();

        static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors.Add(message + "\n" + stack.Split('\n').FirstOrDefault());
        }

        static void OnTextChanged(Object text)
        {
            regenerated.TryGetValue(text, out int count);
            regenerated[text] = count + 1;
        }

        static IEnumerator Steps()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            if (launcher == null || manager == null || launcher.Launcher == null)
            {
                Check(false, "scene has a LauncherWindow with its launcher open and a WindowManager");
                yield break;
            }

            errors.Clear();
            regenerated.Clear();
            Application.logMessageReceived += OnLog;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
            var opened = new List<SpatialWindow>();
            float launcherCurve = launcher.Launcher.Curve;
            bool settingsWasOpen = launcher.SettingsWindow != null;
            try
            {
                yield return Body(launcher, manager, opened);
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                if (!settingsWasOpen && launcher.SettingsWindow != null) manager.Close(launcher.SettingsWindow);
                if (launcher.Launcher != null) launcher.Launcher.SetCurve(launcherCurve);
            }
        }

        static IEnumerator Body(LauncherWindow launcher, WindowManager manager, List<SpatialWindow> opened)
        {
            var empty = Placeholder(manager, opened, "Empty text", "");
            var longWindow = Placeholder(manager, opened, "Long text", LongText);
            var fallback = Placeholder(manager, opened, "Fallback font", FallbackText);
            var several = Placeholder(manager, opened, "Several labels", "Main label");
            var extras = new List<TextMeshPro>
            {
                ExtraLabel(several, "First extra", new Vector3(-0.15f, 0.1f, -0.004f)),
                ExtraLabel(several, "Второй: second", new Vector3(0.12f, 0.08f, -0.004f)),
                ExtraLabel(several, "", new Vector3(0f, -0.1f, -0.004f)),
                ExtraLabel(several, "Λ fourth, with fallback", new Vector3(0.05f, -0.05f, -0.006f)),
            };
            // Labels cut short with an ellipsis, as all window labels are (WindowContent and the title bar use
            // TextOverflowModes.Ellipsis): single-line, wrapped, and mixed with fallback characters.
            var truncated = Placeholder(manager, opened, "Truncated labels with a long title that does not fit", "");
            var cut = new List<TextMeshPro>
            {
                ExtraLabel(truncated, "Settings and preferences", new Vector3(-0.15f, 0.1f, -0.004f), new Vector2(0.1f, 0.05f), false),
                ExtraLabel(truncated, LongText, new Vector3(0.08f, 0.05f, -0.004f), new Vector2(0.2f, 0.1f), true),
                ExtraLabel(truncated, "Привет мир, длинный текст", new Vector3(-0.1f, -0.08f, -0.004f), new Vector2(0.12f, 0.05f), false),
                ExtraLabel(truncated, "Hide all windows now", new Vector3(0.12f, -0.1f, -0.006f), new Vector2(0.07f, 0.05f), false),
            };
            // Labels whose text later grows to exactly fill the vertex buffer TMP sized for the first text, and is cut
            // short: TMP adds the ellipsis to its reference count after sizing the buffers, so it then reports four
            // vertices more than the buffer holds (meshInfo[0].vertexCount > vertices.Length).
            var refill = new List<(TextMeshPro label, string later)>();
            foreach (int n in new[] { 3, 7, 15 })
                refill.Add((ExtraLabel(truncated, new string('a', n), new Vector3(0.2f, 0.12f - refill.Count * 0.05f, -0.004f),
                    new Vector2(0.05f, 0.05f), false), new string('W', n + 1)));
            // A sweep of widths, so some labels need more than one try to fit the ellipsis.
            var sweep = Placeholder(manager, opened, "Ellipsis sweep", "");
            for (int i = 0; i < 24; i++)
            {
                float width = 0.012f + i * 0.004f;
                cut.Add(ExtraLabel(sweep, i % 2 == 0 ? "Settings and preferences" : "Hide all, WWW iii",
                    new Vector3(-0.2f + (i % 6) * 0.08f, 0.12f - (i / 6) * 0.06f, -0.004f), new Vector2(width, 0.05f), false));
            }
            // And the windows the launcher opens (not the laptop ones, which connect to the agent).
            var settings = launcher.OpenSettings();
            var fromLauncher = new List<SpatialWindow>
            {
                launcher.OpenImageWindow(), launcher.OpenVideoWindow(), launcher.OpenImmersionDial(), launcher.OpenKeyboard(),
            };
            opened.AddRange(fromLauncher.Where(w => w != launcher.SettingsWindow));
            var windows = new List<SpatialWindow>(opened) { launcher.Launcher, settings };
            yield return WaitFrames(10);

            var fallbackLabel = fallback.ContentRoot.GetComponentInChildren<TextMeshPro>();
            Check(fallbackLabel.textInfo.materialCount >= 2,
                $"the Cyrillic/Greek label is drawn with the fallback font too ({fallbackLabel.textInfo.materialCount} materials)");
            Check(errors.Count == 0, $"no Console errors while opening the windows flat{FirstError()}");
            int ellipses = cut.Count(t => Enumerable.Range(0, t.textInfo.characterCount).Any(i => t.textInfo.characterInfo[i].character == '…'));
            Check(ellipses >= cut.Count / 2, $"the narrow labels are cut short with an ellipsis ({ellipses} of {cut.Count})");
            var placeholders = new[] { empty, longWindow, fallback, several, truncated, sweep };

            foreach (float curve in Curves)
            {
                errors.Clear();
                foreach (var window in windows) window.SetCurve(curve);
                yield return WaitFrames(Frames);
                Check(errors.Count == 0, $"curve {curve}: no Console errors over {Frames} frames{FirstError()}");
                OnSurface(windows, $"curve {curve}");
            }

            // Dragging the options strip's Curve slider, as a person curves a window: the curve changes every frame and
            // so does the strip's "Curve N%" label.
            errors.Clear();
            foreach (var window in windows) window.Options.SetOpen(true);
            yield return WaitFrames(5);
            // The test windows' labels, without the strip's own (its "Curve N%" label changes with the slider).
            var staticLabels = placeholders.SelectMany(w => w.Visual.GetComponentsInChildren<TMP_Text>())
                .Where(t => t.transform.parent == null || t.transform.parent.name != "Options").ToList();
            regenerated.Clear();
            var sliders = windows.Select(w => w.Options.CurveSlider).ToList();
            const int dragFrames = 150;
            for (int frame = 0; frame <= dragFrames; frame++)
            {
                float u = 0.05f + 0.95f * Mathf.PingPong(frame / 60f, 1f); // ends halfway
                foreach (var slider in sliders)
                {
                    var point = slider.transform.TransformPoint(new Vector3(u - 0.5f, 0f, 0f));
                    if (frame == 0) slider.Press(point);
                    else slider.Drag(point);
                }
                yield return WaitFrames(1);
            }
            foreach (var slider in sliders) slider.Release(true);
            yield return WaitFrames(5);
            Check(errors.Count == 0, $"dragging the Curve slider for {dragFrames} frames raises no Console errors{FirstError()}");
            Check(windows.All(w => w.Curve > 0f), $"the slider curved the windows ({string.Join(", ", windows.Select(w => w.Curve.ToString("F2")))})");
            int dragged = staticLabels.Sum(t => regenerated.TryGetValue(t, out int n) ? n : 0);
            Check(dragged == 0, $"while the curve changes, labels are re-bent without regenerating their text " +
                                $"({dragged} regenerations of {staticLabels.Count} labels in {dragFrames} frames)");
            OnSurface(windows, "after dragging the Curve slider");

            // Labels regenerated without a text change (a colour change) are bent once, from their new flat vertices;
            // then a curve change re-bends them from the same flat copy.
            errors.Clear();
            foreach (var label in windows.SelectMany(w => w.Visual.GetComponentsInChildren<TextMeshPro>()).Where(t => t.isActiveAndEnabled))
            {
                label.color *= 0.99f;
                try { label.ForceMeshUpdate(); }
                catch (System.Exception e) { errors.Add($"{e.GetType().Name} regenerating {label.name}: {e.StackTrace.Split('\n').FirstOrDefault()}"); }
            }
            yield return WaitFrames(5);
            OnSurface(windows, "after regenerating every label");
            foreach (var window in windows) window.SetCurve(0.75f);
            yield return WaitFrames(10);
            Check(errors.Count == 0, $"regenerating curved labels and changing the curve raise no Console errors{FirstError()}");
            OnSurface(windows, "curve 0.75 after regenerating every label");

            // Idle: nothing changes, so the static labels of the test windows are not regenerated (the launched windows
            // have live labels, such as the video's time).
            regenerated.Clear();
            yield return WaitFrames(Frames);
            int idle = staticLabels.Sum(t => regenerated.TryGetValue(t, out int n) ? n : 0);
            Check(idle == 0, $"idle curved labels are not regenerated ({idle} regenerations of {staticLabels.Count} labels in {Frames} frames)");

            // Text changed while curved is bent too: longer, shorter, empty, and new fallback characters.
            errors.Clear();
            empty.ContentRoot.GetComponentInChildren<TextMeshPro>().text = "Not empty any more";
            longWindow.ContentRoot.GetComponentInChildren<TextMeshPro>().text = "Short now";
            fallbackLabel.text = "Новые буквы: ЖЩЮЯ, ψωξ " + FallbackText;
            extras[0].text = "";
            extras[2].text = LongText.Substring(0, 120);
            foreach (var (label, later) in refill) label.text = later;
            yield return WaitFrames(10);
            int overReported = refill.Count(r => r.label.textInfo.meshInfo[0].vertexCount > r.label.textInfo.meshInfo[0].vertices.Length);
            Check(overReported > 0, $"labels cut short after filling their buffer exercise TMP's over-reported vertex count ({overReported} of {refill.Count}: " +
                string.Join(", ", refill.Select(r => $"{r.label.textInfo.meshInfo[0].vertexCount}/{r.label.textInfo.meshInfo[0].vertices.Length}")) + ")");
            Check(errors.Count == 0, $"changing text while curved raises no Console errors{FirstError()}");
            OnSurface(windows, "after text changes");
            foreach (var window in windows) window.SetCurve(0.6f);
            yield return WaitFrames(10);
            Check(errors.Count == 0, $"changing the curve after the text changes raises no Console errors{FirstError()}");
            OnSurface(windows, "curve 0.6 after text changes");

            // A label moved by layout (a resize) is re-bent from its new place.
            manager.Resize(longWindow, new Vector2(0.7f, 0.45f));
            fallbackLabel.transform.localPosition += new Vector3(0.03f, 0f, 0f);
            yield return WaitFrames(10);
            OnSurface(windows, "after a resize and a moved label");

            // Flat again: the text is drawn flat on its own plane.
            errors.Clear();
            foreach (var window in windows) window.SetCurve(0f);
            yield return WaitFrames(10);
            Check(errors.Count == 0, $"curve 0: no Console errors{FirstError()}");
            OnSurface(windows, "curve 0");
        }

        static SpatialWindow Placeholder(WindowManager manager, List<SpatialWindow> opened, string title, string text)
        {
            var window = manager.Spawn(title, new Vector2(0.5f, 0.35f));
            opened.Add(window);
            new GameObject().AddComponent<PlaceholderContent>().Init(window, manager.WindowMaterial, new Color(0.15f, 0.3f, 0.45f), text);
            return window;
        }

        static TextMeshPro ExtraLabel(SpatialWindow window, string text, Vector3 position, Vector2? size = null, bool wrap = false)
        {
            var label = new GameObject("Extra").AddComponent<TextMeshPro>();
            label.transform.SetParent(window.ContentRoot, false);
            label.transform.localPosition = position;
            label.rectTransform.sizeDelta = size ?? new Vector2(0.2f, 0.06f);
            label.fontSize = 0.3f;
            label.alignment = TextAlignmentOptions.Center;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            label.text = text;
            return label;
        }

        static IEnumerator WaitFrames(int frames)
        {
            int until = Time.frameCount + frames;
            while (Time.frameCount < until) yield return null;
        }

        static string FirstError() => errors.Count == 0 ? "" : $" ({errors.Count}; first: {errors[0]})";

        /// <summary>
        /// Every visible character's four drawn vertices (in the mesh that is actually uploaded, main or fallback
        /// sub-mesh) are where TextMeshPro's flat layout of that character lands when bent onto the window's cylinder
        /// (flat: the layout itself). Bending twice, not at all, the wrong vertices, or keeping a stale mesh puts them
        /// elsewhere.
        /// </summary>
        static void OnSurface(List<SpatialWindow> windows, string when)
        {
            float worst = 0f;
            int checkedVertices = 0, labels = 0;
            string worstWhere = "";
            foreach (var window in windows)
            {
                var visual = window.Visual;
                float radius = window.CurveRadius;
                foreach (var text in visual.GetComponentsInChildren<TMP_Text>())
                {
                    if (!text.isActiveAndEnabled) continue;
                    labels++;
                    var info = text.textInfo;
                    var toVisual = visual.worldToLocalMatrix * text.transform.localToWorldMatrix;
                    var meshVertices = new Dictionary<int, Vector3[]>();
                    for (int c = 0; c < info.characterCount; c++)
                    {
                        var character = info.characterInfo[c];
                        if (!character.isVisible) continue;
                        int m = character.materialReferenceIndex;
                        if (!meshVertices.TryGetValue(m, out var vertices))
                        {
                            var mesh = info.meshInfo[m].mesh;
                            meshVertices[m] = vertices = mesh != null ? mesh.vertices : new Vector3[0];
                        }
                        // TextMeshPro writes each quad as bottom-left, top-left, top-right, bottom-right.
                        var flat = new[] { character.vertex_BL.position, character.vertex_TL.position, character.vertex_TR.position, character.vertex_BR.position };
                        for (int k = 0; k < 4; k++)
                        {
                            int index = character.vertexIndex + k;
                            float off = index >= vertices.Length
                                ? float.PositiveInfinity
                                : Vector3.Distance(toVisual.MultiplyPoint3x4(vertices[index]), WindowCurve.Bend(toVisual.MultiplyPoint3x4(flat[k]), radius));
                            checkedVertices++;
                            if (off <= worst) continue;
                            worst = off;
                            worstWhere = $"{window.Title}/{text.name} '{character.character}'";
                        }
                    }
                }
            }
            Check(checkedVertices > 0 && worst < Tolerance,
                $"{when}: {checkedVertices} character vertices of {labels} labels are drawn bent onto their curved surface " +
                $"(worst {worst * 1000f:F3} mm{(worst > 0f ? " at " + worstWhere : "")})");
        }
    }
}
