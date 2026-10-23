using System;
using System.Collections;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of window open and close motion (Doc/ui-design.md section 8): alpha and scale at the end of the open,
    /// step-size independence, close timing and instant collider shut-off, a close that starts mid-open, label alpha following
    /// the fade (and keeping a control's own dimming), Reduce motion, the fade-only consent window with live buttons, and no
    /// allocations per frame. Results use a [WindowMotionTest] prefix.
    /// </summary>
    static class WindowMotionTest
    {
        const string GlassPath = "Assets/Materials/SpatialGlass.mat";
        const string WindowPath = "Assets/Materials/SpatialWindow.mat";

        [MenuItem("Tools/Spatial Windows/Test Window Motion")]
        static void Run() => EditorTestRunner.Run("WindowMotionTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Window Motion", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning && !Application.isPlaying;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static bool Near(float a, float b, float tolerance = 1e-4f) => Mathf.Abs(a - b) <= tolerance;

        static void SetField(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Advance(WindowMotion motion, float seconds, float step)
        {
            int n = Mathf.RoundToInt(seconds / step);
            for (int i = 0; i < n; i++) motion.Step(step);
        }

        static float FadeOf(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetFloat("_WindowFade");
        }

        static TextMeshPro AddLabel(SpatialWindow window, string text)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(window.ContentRoot, false);
            var label = go.AddComponent<TextMeshPro>();
            label.text = text;
            return label;
        }

        static IEnumerator Steps()
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            var window = AssetDatabase.LoadAssetAtPath<Material>(WindowPath);
            Check(glass != null && window != null, "the glass and window materials exist");
            if (glass == null || window == null) yield break;

            var root = new GameObject("WindowMotionTest");
            WindowMotion.ReduceMotionOverride = false;
            try
            {
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, false);
                var manager = new GameObject("Manager").AddComponent<WindowManager>();
                manager.transform.SetParent(root.transform, false);
                SetField(manager, "windowMaterial", window);
                SetField(manager, "glassMaterial", glass);
                SetField(manager, "head", head);

                // 1. Open: alpha and scale 1 at 250 ms, from 0 and 0.92, and the step size does not matter.
                var a = manager.Spawn("A", new Vector2(0.4f, 0.3f));
                a.SetPresentation(0f, 1f, instant: true);
                var label = AddLabel(a, "Hello");
                var motion = a.Motion;
                a.BeginOpen();
                Check(Near(motion.Alpha, 0f) && Near(motion.Scale, UITheme.OpenScaleFrom) && Near(a.Visual.localScale.x, UITheme.OpenScaleFrom),
                    $"opening starts at alpha 0, scale 0.92 ({motion.Alpha}, {a.Visual.localScale.x})");
                Check(Near(label.alpha, 0f) && Near(FadeOf(a.GetComponentInChildren<MeshRenderer>()), 0f), "the label and the renderers start invisible");
                Advance(motion, 0.1f, 0.01f);
                Check(motion.Alpha > 0.5f && motion.Alpha < 1f && motion.Scale > UITheme.OpenScaleFrom && motion.Scale < 1f,
                    $"ease-out: well under way at 100 ms ({motion.Alpha:F3}, {motion.Scale:F3})");
                Advance(motion, 0.15f, 0.01f);
                Check(Near(motion.Alpha, 1f) && Near(motion.Scale, 1f) && !motion.IsAnimating && Near(a.Visual.localScale.x, 1f),
                    $"at 250 ms alpha = 1 and scale = 1 ({motion.Alpha}, {motion.Scale})");

                a.BeginOpen();
                Advance(motion, 0.125f, 0.125f);
                float coarse = motion.Alpha, coarseScale = motion.Scale;
                a.BeginOpen();
                Advance(motion, 0.125f, 0.005f);
                Check(Near(coarse, motion.Alpha) && Near(coarseScale, motion.Scale), $"one 0.125 s step equals 25 steps of 5 ms ({coarse:F5} vs {motion.Alpha:F5})");
                a.BeginOpen();
                motion.Step(0.25f);
                float one = motion.Alpha;
                a.BeginOpen();
                Advance(motion, 0.25f, 0.01f);
                Check(Near(one, motion.Alpha) && Near(motion.Alpha, 1f), "one 0.25 s step equals 25 steps of 0.01 s");

                // 4. Label alpha follows the fade and keeps its own value.
                label.alpha = 0.5f;
                a.BeginOpen();
                Advance(motion, 0.1f, 0.01f);
                Check(Near(label.alpha, 0.5f * motion.Alpha), $"label alpha = own alpha (0.5) x fade ({label.alpha:F3})");
                Check(Near(FadeOf(a.GetComponentInChildren<MeshRenderer>()), motion.Alpha), "the renderers carry the same fade as _WindowFade");
                Advance(motion, 0.15f, 0.01f);
                Check(Near(label.alpha, 0.5f), $"after the open the label is back at its own alpha ({label.alpha})");

                // The focus presentation scale composes with the motion scale.
                a.SetPresentation(0f, 0.85f, instant: true);
                a.BeginOpen();
                Check(Near(a.Visual.localScale.x, 0.85f * UITheme.OpenScaleFrom), $"motion scale multiplies the focus scale ({a.Visual.localScale.x:F3})");
                motion.Snap();
                Check(Near(a.Visual.localScale.x, 0.85f), "after the open the focus scale is untouched");
                a.SetPresentation(0f, 1f, instant: true);

                // 3. Close mid-open starts from the current values and still ends.
                a.BeginOpen();
                Advance(motion, 0.05f, 0.01f);
                float openAlpha = motion.Alpha, openScale = motion.Scale;
                int closed = 0;
                a.BeginClose(() => closed++);
                Check(Near(motion.Alpha, openAlpha) && Near(motion.Scale, openScale), "a close mid-open starts from the current alpha and scale");
                Advance(motion, 0.1f, 0.01f);
                Check(closed == 0 && motion.IsClosing, "...and is still going at 100 ms");
                Advance(motion, 0.1f, 0.01f);
                Check(closed == 1 && Near(motion.Alpha, 0f), "...and ends once, at alpha 0");

                // 2. Close: colliders off at once, done after 180 ms, scale heads to 0.95.
                var b = manager.Spawn("B", new Vector2(0.4f, 0.3f));
                b.SetPresentation(0f, 1f, instant: true);
                b.BeginOpen();
                b.Motion.Snap();
                int bClosed = 0;
                int colliders = b.GetComponentsInChildren<Collider>().Length;
                b.BeginClose(() => bClosed++);
                bool allOff = true;
                foreach (var c in b.GetComponentsInChildren<Collider>(true)) allOff &= !c.enabled;
                Check(colliders > 0 && allOff, $"all {colliders} colliders are off the moment the close starts");
                Advance(b.Motion, 0.17f, 0.01f);
                Check(bClosed == 0, "not done at 170 ms");
                Advance(b.Motion, 0.01f, 0.01f);
                Check(bClosed == 1 && Near(b.Motion.Scale, UITheme.CloseScaleTo), $"done at 180 ms, scale {b.Motion.Scale:F3}");
                manager.Close(b);
                Check(b == null && !System.Linq.Enumerable.Contains(manager.Windows, b), "closing through the manager in edit mode removes the window at once");

                // 5. Reduce motion: scale stays 1.
                WindowMotion.ReduceMotionOverride = true;
                var c2 = manager.Spawn("C", new Vector2(0.4f, 0.3f));
                c2.SetPresentation(0f, 1f, instant: true);
                c2.BeginOpen();
                bool scaleOne = Near(c2.Motion.Scale, 1f);
                Advance(c2.Motion, 0.1f, 0.01f);
                scaleOne &= Near(c2.Motion.Scale, 1f) && Near(c2.Visual.localScale.x, 1f);
                Check(scaleOne && c2.Motion.Alpha > 0f && c2.Motion.Alpha < 1f, "Reduce motion: fade only, scale stays 1");
                c2.Motion.Snap();
                c2.BeginClose(() => { });
                Advance(c2.Motion, 0.1f, 0.01f);
                Check(Near(c2.Motion.Scale, 1f), "Reduce motion: the close keeps scale 1 too");
                WindowMotion.ReduceMotionOverride = false;

                // 6. Consent: fade only in 120 ms, buttons take a press at t = 0.
                var d = manager.Spawn("Allow screen?", new Vector2(0.4f, 0.3f));
                d.SetPresentation(0f, 1f, instant: true);
                d.SetSolid(true);
                var buttonObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                buttonObject.transform.SetParent(d.ContentRoot, false);
                var button = buttonObject.AddComponent<WindowButton>();
                button.Register(d);
                button.AlwaysEnabled = true;
                int clicks = 0;
                button.Clicked += () => clicks++;
                d.BeginOpen(fadeOnly: true);
                Check(Near(d.Motion.Scale, 1f) && Near(d.Motion.Alpha, 0f), "consent starts at alpha 0 with scale 1");
                button.Press(button.transform.position);
                button.Release(true);
                Check(clicks == 1 && button.GetComponent<Collider>().enabled && button.Interactable, "Approve/Deny accept a press at t = 0");
                Advance(d.Motion, 0.06f, 0.01f);
                Check(Near(d.Motion.Scale, 1f) && d.Motion.Alpha > 0f && d.Motion.Alpha < 1f, "consent never scales");
                Advance(d.Motion, 0.06f, 0.01f);
                Check(Near(d.Motion.Alpha, 1f) && Near(d.Motion.Scale, 1f) && !d.Motion.IsAnimating, "consent is fully shown at 120 ms");
                int dClosed = 0;
                d.BeginClose(() => dClosed++);
                Advance(d.Motion, 0.12f, 0.01f);
                Check(dClosed == 1 && Near(d.Motion.Scale, 1f), "consent closes in 120 ms, fade only");

                // 8. Snap finishes a close and runs it once; so does deactivating a closing window (Hide all).
                var g = manager.Spawn("G", new Vector2(0.4f, 0.3f));
                g.BeginOpen();
                int gClosed = 0;
                g.BeginClose(() => gClosed++);
                g.Motion.Snap();
                g.Motion.Snap();
                Check(gClosed == 1 && !g.Motion.IsAnimating, "Snap on a closing window finishes it and runs done exactly once");
                var h = manager.Spawn("H", new Vector2(0.4f, 0.3f));
                h.BeginOpen();
                int hClosed = 0;
                h.BeginClose(() => hClosed++);
                var hMotion = h.Motion;
                // OnDisable does not run on SetActive in edit mode, so call it as Unity would.
                typeof(WindowMotion).GetMethod("OnDisable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(hMotion, null);
                Check(hClosed == 1, "deactivating a closing window finishes the close, so it still gets destroyed");
                hMotion.Snap();
                Check(hClosed == 1, "...and done does not run again");

                // 7. No allocations per frame.
                var e = manager.Spawn("E", new Vector2(0.4f, 0.3f));
                e.SetPresentation(0f, 1f, instant: true);
                AddLabel(e, "Allocation check");
                AddLabel(e, "Second label");
                e.BeginOpen();
                var em = e.Motion;
                for (int i = 0; i < 3; i++) em.Step(0.001f);   // warm up the caches
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20; i++) em.Step(0.005f);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(allocated == 0, $"stepping the motion allocates nothing ({allocated} bytes over 20 steps)");
            }
            finally
            {
                WindowMotion.ReduceMotionOverride = null;
                UnityEngine.Object.DestroyImmediate(root);
            }
            yield break;
        }
    }
}
