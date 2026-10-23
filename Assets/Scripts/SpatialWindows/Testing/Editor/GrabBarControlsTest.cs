using System;
using System.Collections;
using System.Reflection;
using SpatialLauncher.SpatialWindows.Theme;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of the grab bar and the round hover-revealed controls: the grab bar drags like the title bar, hit
    /// areas are at least 44 mm, controls are invisible but clickable until the pointer arrives, the consent window
    /// always shows them, the reveal does not depend on the frame rate, and the pill has its size, place and colours.
    /// Results use a [GrabBarControlsTest] prefix.
    /// </summary>
    static class GrabBarControlsTest
    {
        const string GlassPath = "Assets/Materials/SpatialGlass.mat";
        const string WindowPath = "Assets/Materials/SpatialWindow.mat";

        [MenuItem("Tools/Spatial Windows/Test Grab Bar and Controls")]
        static void Run() => EditorTestRunner.Run("GrabBarControlsTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Grab Bar and Controls", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning && !Application.isPlaying;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static void SetField(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool Near(float a, float b, float tolerance = 1e-4f) => Mathf.Abs(a - b) <= tolerance;

        static Color Drawn(ChromeControl control)
        {
            var block = new MaterialPropertyBlock();
            control.Disc.GetComponent<MeshRenderer>().GetPropertyBlock(block);
            return block.GetColor("_Fallback");
        }

        // Size of a hit area in metres (its quad is scaled to the size; the collider is a unit box).
        static Vector2 HitSize(Transform hit) => new Vector2(hit.localScale.x, hit.localScale.y);

        static string ModeOf(MouseWindowInput input) =>
            typeof(MouseWindowInput).GetField("mode", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(input).ToString();

        // Drawn over an opaque white backdrop (the worst case for a light disc).
        static Color CompositeOverWhite(Color c) => UITheme.CompositeOver(c, Color.white);

        // Distance between two hit rectangles in layout space (0 if they touch or overlap).
        static float GapBetween(Transform a, Transform b)
        {
            var sa = HitSize(a) * 0.5f;
            var sb = HitSize(b) * 0.5f;
            float dx = Mathf.Max(0f, Mathf.Abs(a.localPosition.x - b.localPosition.x) - sa.x - sb.x);
            float dy = Mathf.Max(0f, Mathf.Abs(a.localPosition.y - b.localPosition.y) - sa.y - sb.y);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        sealed class ScriptedHands : IHandSource
        {
            public HandState Left = HandState.Untracked, Right = HandState.Untracked;
            public HandState GetHand(Handedness hand) => hand == Handedness.Left ? Left : Right;
        }

        static HandState HandAt(Vector3 tip, Vector3 aimAt, bool pinched) => new HandState
        {
            IsTracked = true,
            IndexTipPosition = tip,
            WristPosition = tip - Vector3.forward * 0.09f,
            Pinch = pinched ? PinchState.Pinched : PinchState.Open,
            PinchStrength = pinched ? 1f : 0f,
            PointingRay = new Ray(tip, (aimAt - tip).normalized),
        };

        // Runs one input update with the right hand's filter snapped to the new aim (Edit mode has no useful frame time).
        static void TickHand(HandWindowInput input, ScriptedHands hands, HandState state)
        {
            var pointer = typeof(HandWindowInput).GetField("right", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(input);
            if (pointer != null && state.IsTracked)
                ((RayFilter)pointer.GetType().GetField("Filter").GetValue(pointer)).Reset();
            hands.Right = state;
            typeof(HandWindowInput).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
        }

        static IEnumerator Steps()
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            var window = AssetDatabase.LoadAssetAtPath<Material>(WindowPath);
            Check(glass != null && window != null, "the glass and window materials exist");
            if (glass == null || window == null) yield break;

            // Appended at the end, so the saved values of the existing parts don't change.
            var values = (WindowPartType[])Enum.GetValues(typeof(WindowPartType));
            Check(values[values.Length - 1] == WindowPartType.GrabBar && (int)WindowPartType.GrabBar == values.Length - 1,
                "GrabBar is the last WindowPartType");

            var root = new GameObject("GrabBarControlsTest");
            try
            {
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, false);
                var manager = new GameObject("Manager").AddComponent<WindowManager>();
                manager.transform.SetParent(root.transform, false);
                SetField(manager, "windowMaterial", window);
                SetField(manager, "glassMaterial", glass);
                SetField(manager, "head", head);

                var a = manager.Spawn("A", new Vector2(0.6f, 0.4f));
                var b = manager.Spawn("B", new Vector2(0.6f, 0.4f));
                var consent = manager.Spawn("Consent", new Vector2(0.5f, 0.3f));
                consent.SetSolid(true);
                a.MoveTo(new Vector3(0f, 0f, 1.5f));
                b.MoveTo(new Vector3(3f, 0f, 1.5f));
                consent.MoveTo(new Vector3(-3f, 0f, 1.5f));
                a.SetPresentation(0f, 1f, instant: true);
                Physics.SyncTransforms();

                // 1. The grab bar is a part of its own and moves the window like the title bar.
                var bar = WindowTestUtil.Part(a, "GrabBar");
                var title = WindowTestUtil.Part(a, "TitleBar");
                Check(bar != null && bar.GetComponent<WindowPart>().Type == WindowPartType.GrabBar && bar.GetComponent<WindowPart>().Window == a,
                    "the window has a GrabBar part");

                var cameraObject = new GameObject("Camera");
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.position = Vector3.zero;
                var camera = cameraObject.AddComponent<Camera>();
                var input = root.AddComponent<MouseWindowInput>();
                typeof(MouseWindowInput).GetField("rayCamera", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, camera);
                SpatialWindow selected = null;
                input.SelectRequested += w => selected = w;

                var barScreen = camera.WorldToScreenPoint(bar.position);
                var titleScreen = camera.WorldToScreenPoint(title.position);
                input.PressAt(barScreen);
                string barMode = ModeOf(input);
                bool barSelected = selected == a;
                input.ReleaseAt(barScreen);
                selected = null;
                input.PressAt(titleScreen);
                string titleMode = ModeOf(input);
                input.ReleaseAt(titleScreen);
                Check(barSelected && barMode == "Drag" && titleMode == "Drag", $"pressing the grab bar starts a drag like the title bar ({barMode}, {titleMode})");

                // 2. Every control has a hit area of at least 44 mm and is drawn 32 pt wide.
                float hit = UITheme.PtToMetres(UITheme.MinHitTargetMm);
                var close = WindowTestUtil.Part(a, "CloseButton");
                var resize = WindowTestUtil.Part(a, "ResizeHandle");
                var more = a.Options.ToggleButton.transform;
                bool hitsOk = true;
                foreach (var t in new[] { close, resize, more, bar })
                {
                    var size = HitSize(t);
                    var box = t.GetComponent<BoxCollider>();
                    hitsOk &= box != null && size.x >= hit - 1e-6f && size.y >= hit - 1e-6f;
                }
                Check(hitsOk, "close, options, resize and grab bar hit areas are at least 44 mm");
                Check(Near(HitSize(bar).x, UITheme.PtToMetres(140f)) && Near(HitSize(bar).y, UITheme.PtToMetres(44f)), "the grab bar hit area is 140 x 44 pt");
                bool discsOk = true;
                foreach (var control in new[] { a.CloseControl, a.ResizeControl, a.Options.Toggle })
                {
                    var drawn = new Vector2(control.Disc.localScale.x * control.Hit.localScale.x, control.Disc.localScale.y * control.Hit.localScale.y);
                    discsOk &= Near(drawn.x, UITheme.PtToMetres(32f), 1e-5f) && Near(drawn.y, UITheme.PtToMetres(32f), 1e-5f);
                }
                Check(discsOk, "the round controls are drawn 32 pt across");
                Check(a.Options.Toggle.Glyph != null && a.CloseControl.Glyph.text == UIIcons.Close && a.ResizeControl.Glyph.text == UIIcons.OpenInFull
                      && a.Options.Toggle.Glyph.text == UIIcons.MoreHoriz, "the icons are close, open_in_full and more_horiz");
                Check(close.localPosition.x > a.Options.ToggleButton.transform.localPosition.x && close.localPosition.x < bar.localPosition.x
                      && Near(close.localPosition.y, bar.localPosition.y) && close.localPosition.y < -a.Size.y * 0.5f,
                    "close and options sit left of the grab bar in a row below the slab");
                Check(resize.localPosition.x > a.Size.x * 0.5f && resize.localPosition.y < -a.Size.y * 0.5f, "the resize control sits outside the bottom-right corner");

                // Hit areas are at least 8 mm apart.
                var rects = new[] { close, resize, more, bar };
                float minGap = float.MaxValue;
                for (int i = 0; i < rects.Length; i++)
                for (int j = i + 1; j < rects.Length; j++)
                    minGap = Mathf.Min(minGap, GapBetween(rects[i], rects[j]));
                Check(minGap >= UITheme.PtToMetres(UITheme.MinHitGapMm) - 1e-6f, $"chrome hit areas are at least 8 mm apart (smallest gap {minGap * 1000f:F1} mm)");

                // 3. Not hovered: invisible, but the colliders stay on.
                Check(a.Reveal == 0f && a.ControlsAlpha == 0f && !a.AlwaysShowControls, "a window starts with its controls hidden");
                Check(Drawn(a.CloseControl).a == 0f && Drawn(a.ResizeControl).a == 0f && Drawn(a.Options.Toggle).a == 0f, "hidden controls are drawn with alpha 0");
                bool collidersOn = true;
                foreach (var t in new[] { close, resize, more, bar }) collidersOn &= t.GetComponent<Collider>().enabled && t.gameObject.activeInHierarchy;
                Check(collidersOn, "hidden controls keep their colliders enabled");

                // 4. The consent window shows them all the time, and its close is still a close.
                Check(consent.AlwaysShowControls && Near(consent.ControlsAlpha, 1f), "a solid (consent) window always shows its controls");
                float discAlpha = Drawn(consent.CloseControl).a;
                Check(Near(discAlpha, ChromeControl.DiscFill.a) && discAlpha >= 0.85f, $"its close control is drawn nearly opaque (alpha {discAlpha:F2})");
                var wall = CompositeOverWhite(Drawn(consent.CloseControl));
                float iconContrast = UITheme.ContrastRatio(Color.white, wall);
                Check(iconContrast >= 4.5f, $"the white icon on a disc over a white wall is {iconContrast:F2}:1 (>= 4.5:1)");
                Check(WindowTestUtil.Part(consent, "CloseButton").GetComponent<WindowPart>().Type == WindowPartType.CloseButton
                      && WindowTestUtil.Part(consent, "CloseButton").GetComponent<Collider>().enabled, "its close is still the close button");
                consent.SetSolid(false);
                Check(!consent.AlwaysShowControls && consent.ControlsAlpha == 0f, "leaving solid hides them again");

                // 5. The reveal is frame-rate independent.
                a.SetPointerOver(true);
                b.SetPointerOver(true);
                a.UpdateReveal(0.1f);
                for (int i = 0; i < 10; i++) b.UpdateReveal(0.01f);
                Check(Near(a.Reveal, b.Reveal) && a.Reveal > 0.3f && a.Reveal < 0.9f, $"1 x 0.1 s equals 10 x 0.01 s ({a.Reveal:F5} vs {b.Reveal:F5})");
                Check(Near(a.Reveal, 1f - Mathf.Exp(-0.1f / 0.15f)), "it approaches with a 150 ms time constant");
                a.SetReveal(1f);
                b.SetReveal(1f);
                a.SetPointerOver(false);
                b.SetPointerOver(false);
                a.UpdateReveal(0.1f);
                for (int i = 0; i < 10; i++) b.UpdateReveal(0.01f);
                Check(Near(a.Reveal, b.Reveal) && Near(a.Reveal, Mathf.Exp(-0.1f / 0.6f)), $"fading out takes 600 ms and is also frame-rate independent ({a.Reveal:F5})");
                Check(Drawn(a.CloseControl).a > 0f && Drawn(a.CloseControl).a < ChromeControl.DiscFill.a, "a half-revealed control is drawn half-way");

                // Input tells the window when the pointer is over it, and when it leaves.
                a.SetReveal(0f);
                b.SetReveal(0f);
                var update = typeof(MouseWindowInput).GetMethod("UpdatePointerOver", BindingFlags.Instance | BindingFlags.NonPublic);
                update.Invoke(input, new object[] { new Ray(Vector3.zero, a.transform.position - Vector3.zero), true });
                Check(a.IsPointerOver && !b.IsPointerOver, "a ray on a window marks it pointer-over");
                update.Invoke(input, new object[] { new Ray(Vector3.zero, b.transform.position - Vector3.zero), true });
                Check(!a.IsPointerOver && b.IsPointerOver, "moving the ray to another window clears the first");
                update.Invoke(input, new object[] { new Ray(Vector3.zero, Vector3.up), true });
                Check(!a.IsPointerOver && !b.IsPointerOver, "a ray that hits nothing clears both");
                a.SetReveal(0f);

                // 6. The pill: white 0.45, 0.8 on hover, 120 x 10 pt drawn, 12 pt below the slab.
                var pill = a.GrabBarControl;
                var idle = Drawn(pill);
                Check(Near(idle.r, 1f) && Near(idle.g, 1f) && Near(idle.b, 1f) && Near(idle.a, 0.45f), "the grab bar is white at alpha 0.45");
                a.SetReveal(1f);
                Check(Near(Drawn(pill).a, 0.8f), "the grab bar is white at alpha 0.8 while hovered");
                a.SetReveal(0f);
                var outlineOnWall = UITheme.CompositeOver(ChromeControl.PillOutline, Color.white);
                float pillContrast = UITheme.ContrastRatio(outlineOnWall, Color.white);
                Check(pill.OutlinePt > 0f && pillContrast >= 3f, $"the grab bar's dark outline over a white wall is {pillContrast:F2}:1 (>= 3:1)");
                var drawnSize = new Vector2(pill.Disc.localScale.x * pill.Hit.localScale.x, pill.Disc.localScale.y * pill.Hit.localScale.y);
                Check(Near(drawnSize.x, UITheme.PtToMetres(120f), 1e-5f) && Near(drawnSize.y, UITheme.PtToMetres(10f), 1e-5f), "the grab bar is drawn 120 x 10 pt");
                float top = pill.Hit.localPosition.y + pill.Disc.localPosition.y * pill.Hit.localScale.y + drawnSize.y * 0.5f;
                Check(Near(top, -a.Size.y * 0.5f - UITheme.PtToMetres(12f), 1e-5f), "the grab bar's top is 12 pt below the slab");
                var barBounds = new Vector3(pill.Hit.localPosition.x, pill.Hit.localPosition.y - hit * 0.5f);
                Check(a.LayoutBounds.Contains(barBounds) && a.LayoutBounds.Contains(new Vector2(resize.localPosition.x, resize.localPosition.y)),
                    "the layout bounds include the grab bar and the resize control (curved windows are hit-tested inside them)");
                a.Options.SetOpen(true);
                float openTop = pill.Hit.localPosition.y + pill.Hit.localScale.y * 0.5f;
                Check(Near(openTop, -a.Size.y * 0.5f - a.Options.ExtraHeight, 1e-5f), "with the options strip open the row moves below it");
                a.Options.SetOpen(false);

                // Hands: the window under the ray is pointer-over, also while dragging off it; each pointer counts for itself.
                a.SetReveal(0f);
                b.SetReveal(0f);
                a.MoveTo(new Vector3(0f, 0f, 1.5f));
                b.MoveTo(new Vector3(3f, 0f, 1.5f));
                Physics.SyncTransforms();
                var handsObject = new GameObject("Hands");
                handsObject.transform.SetParent(root.transform, false);
                var handInput = handsObject.AddComponent<HandWindowInput>();
                var scripted = new ScriptedHands();
                handInput.SetHandSource(scripted);
                var tip = new Vector3(0.15f, -0.2f, 0.35f);
                var aimA = WindowTestUtil.Part(a, "Body").position;
                var aimB = WindowTestUtil.Part(b, "Body").position;
                TickHand(handInput, scripted, HandAt(tip, aimA, false));
                Check(a.IsPointerOver && !b.IsPointerOver, "a hand aimed at A marks A pointer-over");
                TickHand(handInput, scripted, HandAt(tip, aimB, false));
                Check(!a.IsPointerOver && b.IsPointerOver, "aiming the hand at B clears A and marks B");
                TickHand(handInput, scripted, HandAt(tip, Vector3.up * 5f, false));
                Check(!a.IsPointerOver && !b.IsPointerOver, "aiming at nothing clears both");
                TickHand(handInput, scripted, HandAt(tip, aimA, false));
                TickHand(handInput, scripted, HandState.Untracked);
                Check(!a.IsPointerOver, "losing the hand clears the pointer-over");

                // A drag keeps the window revealed after the ray leaves it.
                var barPoint = bar.position;
                TickHand(handInput, scripted, HandAt(tip, barPoint, false));
                TickHand(handInput, scripted, HandAt(tip, barPoint, true));
                Check(handInput.GetActiveWindow(Handedness.Right) == a, "pinching the grab bar starts a drag with the hand");
                TickHand(handInput, scripted, HandAt(tip, Vector3.up * 5f, true));
                Check(a.IsPointerOver, "the window stays pointer-over while a drag has left it");
                TickHand(handInput, scripted, HandAt(tip, Vector3.up * 5f, false));
                Check(!a.IsPointerOver, "and lets go when the drag ends off the window");

                // Two pointers: one leaving does not clear the other.
                var mouseKey = new object();
                a.SetPointerOver(mouseKey, true);
                a.SetPointerOver(new object(), false);
                Check(a.IsPointerOver, "a pointer that leaves does not clear another that is still over");
                a.SetPointerOver(mouseKey, false);
                Check(!a.IsPointerOver, "the last pointer leaving clears it");
                a.SetCurve(0f);

                // 7. Round controls are plain 4-vertex quads that bend with the curve.
                var filter = a.CloseControl.Disc.GetComponent<MeshFilter>();
                Check(filter.sharedMesh.vertexCount == 4, "a round control is a 4-vertex quad");
                a.SetCurve(1f);
                typeof(WindowCurver).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(a.GetComponent<WindowCurver>(), null);
                Check(a.GetComponent<WindowCurver>().BentMeshOf(filter) != null && a.GetComponent<WindowCurver>().BentMeshOf(pill.Disc.GetComponent<MeshFilter>()) != null,
                    "a curved window bends the round controls and the grab bar");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            yield break;
        }
    }
}
