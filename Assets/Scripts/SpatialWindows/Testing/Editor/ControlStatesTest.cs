using System.Collections;
using SpatialLauncher.SpatialWindows.Theme;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of the shared control state (Doc/ui-design.md section 7): overlay alphas and press scale, two pointers
    /// hovering, the focus ring, disabled controls, frame-rate independence, and a distinct look per state on every control type.
    /// Results use a [ControlStatesTest] prefix.
    /// </summary>
    static class ControlStatesTest
    {
        [MenuItem("Tools/Spatial Windows/Test Control States")]
        static void Run() => EditorTestRunner.Run("ControlStatesTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Control States", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning && !Application.isPlaying;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static bool Near(float a, float b, float tolerance = 1e-4f) => Mathf.Abs(a - b) <= tolerance;

        // overlay alpha, ring alpha, fade, press scale as drawn on a renderer.
        static Vector4 Look(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return new Vector4(block.GetColor("_Overlay").a, block.GetColor("_FocusRing").a, block.GetFloat("_Fade"), block.GetFloat("_PressScale"));
        }

        static GameObject Make<T>(string name) where T : WindowControl
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.AddComponent<T>();
            if (typeof(T) == typeof(WindowSlider))
            {
                var knob = GameObject.CreatePrimitive(PrimitiveType.Quad);
                knob.name = "Knob";
                knob.transform.SetParent(go.transform, false);
                go.GetComponent<WindowSlider>().AttachKnob(knob.transform);
            }
            return go;
        }

        static IEnumerator Steps()
        {
            // 1. Overlay alphas and press scale, once settled.
            var s = new ControlVisualState();
            s.Snap();
            Check(Near(s.OverlayAlpha, 0f) && Near(s.Scale, 1f), "idle: overlay alpha 0, scale 1");
            s.SetHover("mouse", true);
            s.Snap();
            Check(Near(s.OverlayAlpha, 0.10f), $"hover: overlay alpha 0.10 ({s.OverlayAlpha})");
            s.SetPressed(true);
            s.Snap();
            Check(Near(s.OverlayAlpha, 0.18f) && Near(s.Scale, 0.96f), $"pressed: overlay alpha 0.18 and scale 0.96 ({s.OverlayAlpha}, {s.Scale})");

            // 2. Two pointers.
            var two = new ControlVisualState();
            two.SetHover("mouse", true);
            two.SetHover("hand", true);
            two.SetHover("mouse", false);
            Check(two.Hovered && two.HoverCount == 1, "one pointer leaving does not clear the other's hover");
            two.SetHover("hand", false);
            Check(!two.Hovered, "the last pointer leaving clears the hover");

            // 3. Focus ring only when focused.
            var f = new ControlVisualState();
            Check(!f.RingVisible, "no focus ring when idle");
            f.SetFocused(true);
            Check(f.RingVisible, "focus ring when focused");
            f.SetFocused(false);
            Check(!f.RingVisible, "no focus ring once focus is gone");

            // 4. Disabled.
            var d = new ControlVisualState();
            d.SetHover("mouse", true);
            d.SetDisabled(true);
            d.SetPressed(true);
            d.Snap();
            Check(Near(d.ContentAlpha, 0.38f), "disabled content alpha is 0.38");
            Check(!d.Hovered && !d.Pressed && Near(d.OverlayAlpha, 0f) && Near(d.Scale, 1f), "disabled shows no hover and ignores press");
            Check(Near(d.LabelAlpha(1f, false), 0.38f), "a disabled label is dimmed to 0.38");
            Check(Near(d.LabelAlpha(1f, true), 1f), "a label that must keep contrast stays opaque");
            float contrast = UITheme.ContrastRatio(UITheme.TextPrimary, UITheme.GlassFallback);
            Check(contrast >= 4.5f, $"the label that keeps contrast reads {contrast:F2}:1 on the glass fallback (>= 4.5:1)");

            // 5. Frame-rate independence.
            var a = new ControlVisualState();
            var b = new ControlVisualState();
            a.SetHover("mouse", true);
            b.SetHover("mouse", true);
            a.SetPressed(true);
            b.SetPressed(true);
            a.Step(0.1f);
            for (int i = 0; i < 10; i++) b.Step(0.01f);
            Check(Near(a.OverlayAlpha, b.OverlayAlpha) && Near(a.Scale, b.Scale),
                $"1 x 0.1 s and 10 x 0.01 s agree ({a.OverlayAlpha:F5} / {b.OverlayAlpha:F5}, {a.Scale:F5} / {b.Scale:F5})");
            Check(a.OverlayAlpha > 0f && a.OverlayAlpha < 0.18f, "a step moves towards the target without reaching it");

            // 6. Every control type draws a different look in each state; a disabled one ignores Press.
            var made = new[]
            {
                Make<WindowButton>("Button"), Make<WindowHoldButton>("Hold"), Make<WindowSlider>("Slider"), Make<WindowTextField>("Field"),
            };
            foreach (var go in made)
            {
                var control = go.GetComponent<WindowControl>();
                var renderer = control is WindowSlider ? go.transform.Find("Knob").GetComponent<Renderer>() : go.GetComponent<Renderer>();
                string type = control.GetType().Name;

                control.SnapVisual();
                var idle = Look(renderer);
                control.HoverFrom("mouse", Vector3.zero);
                control.SnapVisual();
                var hover = Look(renderer);
                control.Press(Vector3.zero);
                control.SnapVisual();
                var pressed = Look(renderer);
                control.Release(false);
                control.HoverExitFrom("mouse");
                control.SetFocused(true);
                control.SnapVisual();
                var focused = Look(renderer);
                control.SetFocused(false);
                control.Interactable = false;
                control.SnapVisual();
                var disabled = Look(renderer);
                control.Press(Vector3.zero);
                bool ignored = !control.State.Pressed;
                control.Interactable = true;

                Check(idle != hover && idle != pressed && idle != focused && idle != disabled && hover != pressed && hover != focused
                    && hover != disabled && pressed != focused && pressed != disabled && focused != disabled,
                    $"{type}: idle, hover, pressed, focused and disabled look different");
                Check(Near(idle.x, 0f) && Near(hover.x, 0.10f) && Near(pressed.x, 0.18f) && Near(pressed.w, 0.96f), $"{type}: overlay and scale as specified");
                Check(Near(idle.y, 0f) && focused.y > 0.5f, $"{type}: the focus ring shows only when focused");
                Check(Near(disabled.z, 0.38f) && ignored, $"{type}: disabled draws at 0.38 and ignores Press");
            }

            // A disabled button does not click.
            var button = made[0].GetComponent<WindowButton>();
            int clicks = 0;
            button.Clicked += () => clicks++;
            button.Interactable = false;
            button.Press(Vector3.zero);
            button.Release(true);
            Check(clicks == 0, "a disabled button does not click");
            button.Interactable = true;
            button.Press(Vector3.zero);
            button.Release(true);
            Check(clicks == 1, "an enabled button clicks");

            // 7. Controls that are never disabled (consent): no disabled look, no overlay, no shrink, whatever the state.
            var solidObject = Make<WindowButton>("Solid");
            var solid = solidObject.GetComponent<WindowButton>();
            solid.AlwaysEnabled = true;
            solid.Interactable = false;
            solid.HoverFrom("mouse", Vector3.zero);
            solid.Press(Vector3.zero);
            solid.SnapVisual();
            var solidLook = Look(solidObject.GetComponent<Renderer>());
            Check(solid.Interactable && Near(solidLook.z, 1f) && Near(solidLook.x, 0f) && Near(solidLook.w, 1f),
                "an AlwaysEnabled control cannot be disabled and keeps full alpha, no overlay and no shrink");
            Check(UITheme.ContrastRatio(Color.white, UITheme.SuccessFill) >= 4.5f && UITheme.ContrastRatio(Color.white, UITheme.DestructiveFill) >= 4.5f,
                "white text on SuccessFill and DestructiveFill reads at least 4.5:1");

            // 8. A disabled focused control gives up focus (a text field stops taking typing).
            var fieldObject = Make<WindowTextField>("FocusField");
            var field = fieldObject.GetComponent<WindowTextField>();
            field.Focus();
            Check(WindowTextField.IsTyping && field.State.Focused, "a focused text field is the typing target");
            field.Interactable = false;
            Check(!WindowTextField.IsTyping && !field.State.Focused && !field.State.RingVisible, "disabling it clears focus and stops typing");
            field.Interactable = true;
            Object.DestroyImmediate(fieldObject);

            // 9. AlwaysEnabled wins whichever order the two are set in.
            var orderObject = Make<WindowButton>("Order");
            var order = orderObject.GetComponent<WindowButton>();
            order.Interactable = false;
            order.AlwaysEnabled = true;
            Check(order.Interactable && !order.State.Disabled, "AlwaysEnabled set after Interactable = false makes it interactable again");
            Object.DestroyImmediate(orderObject);

            // 10. Chrome controls have a pressed state: overlay 0.18 and scale 0.96.
            var hitObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var chrome = new ChromeControl(hitObject.transform, hitObject.transform, null, Vector2.one * 44f, Vector2.one * 32f, 0f, Color.white, false, null);
            chrome.State.SetHover("mouse", true);
            chrome.State.Snap();
            float hoverAlpha = chrome.State.OverlayAlpha;
            chrome.State.SetPressed(true);
            chrome.State.Snap();
            Check(Near(hoverAlpha, 0.10f) && Near(chrome.State.OverlayAlpha, 0.18f) && Near(chrome.State.Scale, 0.96f),
                "a chrome control shows hover 0.10, then pressed 0.18 with scale 0.96");
            chrome.State.SetPressed(false);
            chrome.State.Snap();
            Check(Near(chrome.State.Scale, 1f), "and releases back to full size");
            Object.DestroyImmediate(hitObject);

            foreach (var go in made) Object.DestroyImmediate(go);
            Object.DestroyImmediate(solidObject);
            yield break;
        }
    }
}
