using System.Collections;
using System.Reflection;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of the rounded in-window shapes: buttons, slider (capsule track and 28 pt knob) and text field are flat
    /// SpatialGlass quads with a 12 pt radius, their hit boxes are at least 44 mm and 8 mm apart, images are clipped to the
    /// window radius less the padding, shapes draw between the glass slab and the text, and the consent buttons stay
    /// solid Success and Destructive at 56 pt. Results use a [RoundedControlsTest] prefix.
    /// </summary>
    static class RoundedControlsTest
    {
        const string GlassPath = "Assets/Materials/SpatialGlass.mat";
        const string WindowPath = "Assets/Materials/SpatialWindow.mat";
        const string ShaderName = "SpatialLauncher/SpatialGlass";

        [MenuItem("Tools/Spatial Windows/Test Rounded Controls")]
        static void Run() => EditorTestRunner.Run("RoundedControlsTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Rounded Controls", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning && !Application.isPlaying;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static void SetField(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool Near(float a, float b, float tolerance = 1e-5f) => Mathf.Abs(a - b) <= tolerance;

        static bool Near(Color a, Color b) => Near(a.r, b.r, 1e-3f) && Near(a.g, b.g, 1e-3f) && Near(a.b, b.b, 1e-3f) && Near(a.a, b.a, 1e-3f);

        static float Radius(Component c)
        {
            var block = new MaterialPropertyBlock();
            c.GetComponent<MeshRenderer>().GetPropertyBlock(block);
            return block.GetFloat("_Radius");
        }

        static Vector2 DrawnSize(Component c)
        {
            var block = new MaterialPropertyBlock();
            c.GetComponent<MeshRenderer>().GetPropertyBlock(block);
            return block.GetVector("_Size");
        }

        static Material MaterialOf(Component c) => c.GetComponent<MeshRenderer>().sharedMaterial;

        static bool IsFlatGlass(Component c)
        {
            var m = MaterialOf(c);
            return m != null && m.shader != null && m.shader.name == ShaderName && m.GetFloat("_Flat") > 0.5f;
        }

        // The hit rectangle of a control in its parent's space: the collider box times the quad's scale.
        static Vector2 HitSize(Component c)
        {
            var box = c.GetComponent<BoxCollider>();
            return new Vector2(box.size.x * c.transform.localScale.x, box.size.y * c.transform.localScale.y);
        }

        static float GapBetween(Component a, Component b)
        {
            var sa = HitSize(a) * 0.5f;
            var sb = HitSize(b) * 0.5f;
            float dx = Mathf.Max(0f, Mathf.Abs(a.transform.localPosition.x - b.transform.localPosition.x) - sa.x - sb.x);
            float dy = Mathf.Max(0f, Mathf.Abs(a.transform.localPosition.y - b.transform.localPosition.y) - sa.y - sb.y);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        static int TextQueue()
        {
            var go = new GameObject("QueueProbe");
            try
            {
                var text = go.AddComponent<TextMeshPro>();
                return text.fontSharedMaterial != null ? text.fontSharedMaterial.renderQueue : 3000;
            }
            finally { Object.DestroyImmediate(go); }
        }

        // A content with one of each control, laid out well apart.
        sealed class SampleContent : WindowContent
        {
            public WindowButton Button;
            public WindowHoldButton Hold;
            public WindowSlider Slider;
            public WindowTextField Field;
            public TextMeshPro FieldLabel;
            public Transform Panel;

            public void Build(SpatialWindow window, Material baseMaterial)
            {
                Attach(window, baseMaterial);
                Button = CreateControl<WindowButton>("Button", transform, CreateMaterial(Color.red));
                Hold = CreateControl<WindowHoldButton>("Hold", transform, CreateMaterial(Color.green));
                Slider = CreateControl<WindowSlider>("Slider", transform, CreateMaterial(Color.blue));
                AddKnob(Slider);
                Field = CreateControl<WindowTextField>("Field", transform, CreateMaterial(Color.grey));
                FieldLabel = CreateLabel("FieldLabel", transform, "", 0.2f, TextAlignmentOptions.MidlineLeft);
                Field.Init(FieldLabel, MaterialOf(Field), "placeholder");
                Panel = CreateQuad("Panel", transform, CreateMaterial(UITheme.GlassRaised));
                Round(Panel, UITheme.ControlRadius);
                Relayout();
            }

            protected override void Layout(Vector2 size)
            {
                Place(Button.transform, -0.2f, 0.1f, 0.1f, 0.05f);
                Place(Hold.transform, -0.05f, 0.1f, 0.1f, 0.05f);
                Place(Field.transform, 0.12f, 0.1f, 0.18f, 0.05f);
                Place(Slider.transform, 0f, -0.05f, 0.4f, 0.016f);
                Place(Panel, 0f, -0.15f, 0.4f, 0.04f);
            }

            static void Place(Transform t, float x, float y, float w, float h)
            {
                t.localPosition = new Vector3(x, y, -0.002f);
                t.localScale = new Vector3(w, h, 1f);
            }
        }

        static IEnumerator Steps()
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            var window = AssetDatabase.LoadAssetAtPath<Material>(WindowPath);
            Check(glass != null && window != null, "the glass and window materials exist");
            if (glass == null || window == null) yield break;

            var root = new GameObject("RoundedControlsTest");
            try
            {
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, false);
                var manager = new GameObject("Manager").AddComponent<WindowManager>();
                manager.transform.SetParent(root.transform, false);
                SetField(manager, "windowMaterial", window);
                SetField(manager, "glassMaterial", glass);
                SetField(manager, "head", head);

                var a = manager.Spawn("Controls", new Vector2(0.6f, 0.6f));
                a.MoveTo(new Vector3(0f, 0f, 1.5f));
                Check(a.GlassMaterial == glass, "a window exposes its glass material to its content");

                var sample = new GameObject().AddComponent<SampleContent>();
                sample.Build(a, window);

                // 1. Buttons, slider and text field are flat SpatialGlass quads with the right radii.
                float control = UITheme.PtToMetres(UITheme.ControlRadius);
                Check(IsFlatGlass(sample.Button) && IsFlatGlass(sample.Hold) && IsFlatGlass(sample.Slider) && IsFlatGlass(sample.Field),
                    "buttons, slider and text field use the SpatialGlass shader in flat mode");
                Check(Near(Radius(sample.Button), control) && Near(Radius(sample.Hold), control) && Near(Radius(sample.Field), control),
                    "buttons and the text field have a 12 pt radius");
                float trackHeight = sample.Slider.transform.localScale.y;
                Check(Near(Radius(sample.Slider), trackHeight * 0.5f), "the slider track is a capsule (radius = height / 2)");
                Check(Near(Radius(sample.Panel), control), "an inner panel can take a 12 pt radius");
                Check(Near(DrawnSize(sample.Button).x, 0.1f) && Near(DrawnSize(sample.Button).y, 0.05f), "the shader is told the quad's size");

                var knob = sample.Slider.Knob;
                sample.Slider.SetValue(0.25f);
                Check(knob != null && IsFlatGlass(knob) && Near(Radius(knob), UITheme.PtToMetres(14f)), "the slider knob is a round SpatialGlass shape, 14 pt radius");
                var knobSize = new Vector2(knob.localScale.x * sample.Slider.transform.localScale.x, knob.localScale.y * sample.Slider.transform.localScale.y);
                Check(Near(knobSize.x, UITheme.PtToMetres(28f)) && Near(knobSize.y, UITheme.PtToMetres(28f)), "the knob is 28 pt across whatever the track's size");
                Check(Near(knob.localPosition.x, -0.25f), "the knob sits at the slider's value");

                bool quads = true;
                foreach (var c in new Component[] { sample.Button, sample.Hold, sample.Slider, sample.Field, knob, sample.Panel })
                    quads &= c.GetComponent<MeshFilter>().sharedMesh.vertexCount == 4;
                Check(quads, "control and knob meshes are 4-vertex quads");
                a.SetCurve(1f);
                typeof(WindowCurver).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(a.GetComponent<WindowCurver>(), null);
                Check(a.GetComponent<WindowCurver>().BentMeshOf(sample.Button.GetComponent<MeshFilter>()) != null
                      && a.GetComponent<WindowCurver>().BentMeshOf(sample.Slider.GetComponent<MeshFilter>()) != null,
                    "a curved window bends the rounded controls");
                a.SetCurve(0f);

                // 2. Hit boxes are at least 44 mm and 8 mm apart.
                float hit = UITheme.PtToMetres(UITheme.MinHitTargetMm);
                bool hitsOk = true;
                foreach (var c in new Component[] { sample.Button, sample.Hold, sample.Slider, sample.Field })
                {
                    var size = HitSize(c);
                    hitsOk &= size.x >= hit - 1e-6f && size.y >= hit - 1e-6f;
                }
                Check(hitsOk, "button, slider and text field hit boxes are at least 44 mm");
                var all = new Component[] { sample.Button, sample.Hold, sample.Slider, sample.Field };
                float minGap = float.MaxValue;
                for (int i = 0; i < all.Length; i++)
                for (int j = i + 1; j < all.Length; j++)
                    minGap = Mathf.Min(minGap, GapBetween(all[i], all[j]));
                Check(minGap >= UITheme.PtToMetres(UITheme.MinHitGapMm) - 1e-6f, $"hit boxes are at least 8 mm apart (smallest gap {minGap * 1000f:F1} mm)");

                // 3. Shapes draw after the slab and before the text.
                int slabQueue = glass.renderQueue;
                int textQueue = TextQueue();
                bool queuesOk = true;
                foreach (var c in new Component[] { sample.Button, sample.Slider, sample.Field, knob, sample.Panel })
                    queuesOk &= MaterialOf(c).renderQueue > slabQueue && MaterialOf(c).renderQueue < textQueue;
                Check(queuesOk && MaterialOf(sample.Button).renderQueue == WindowContent.ShapeQueue + WindowContent.ControlLayer,
                    $"shape queue {MaterialOf(sample.Button).renderQueue} is above the slab ({slabQueue}) and below text ({textQueue})");

                // Panels draw under controls, which draw under knobs and fills; all before text.
                Check(MaterialOf(sample.Panel).renderQueue == WindowContent.ShapeQueue && MaterialOf(sample.Panel).renderQueue < MaterialOf(sample.Button).renderQueue
                      && MaterialOf(sample.Button).renderQueue < MaterialOf(knob).renderQueue && MaterialOf(knob).renderQueue < textQueue,
                    "layers: panel < control < knob < text");

                // The picker and pairing screens: text on the glass and white on the accent button both reach 4.5:1.
                var wall = UITheme.CompositeOver(UITheme.GlassFallback, Color.white);
                wall = UITheme.CompositeOver(LaptopPickerContent.PanelColor, wall);
                Check(LaptopPickerContent.PanelColor.a == 0f && PairingCodeContent.PanelColor.a == 0f
                      && UITheme.ContrastRatio(UITheme.TextPrimary, wall) >= 4.5f, "picker and pairing text on glass over a white wall is at least 4.5:1");
                Check(UITheme.ContrastRatio(Color.white, UITheme.AccentFill) >= 4.5f && LaptopPickerContent.ListColor == UITheme.AccentFill
                      && PairingCodeContent.ButtonColor == UITheme.AccentFill, "the pair and new-code buttons are AccentFill with white text at least 4.5:1");

                // 4. The text field takes its colours from the theme tokens.
                Check(Near(MaterialOf(sample.Field).color, WindowTextField.NormalColor), "an unfocused field is the raised glass fill");
                sample.Field.Focus();
                Check(Near(MaterialOf(sample.Field).color, WindowTextField.FocusedColor) && !Near(WindowTextField.FocusedColor, WindowTextField.NormalColor),
                    "a focused field changes to the accent-tinted fill");
                sample.Field.Blur();
                Check(Near(MaterialOf(sample.Field).color, WindowTextField.NormalColor), "blurring puts the normal fill back");

                // 5. Images are clipped to the window radius less the padding, using the texture.
                var texture = new Texture2D(4, 4);
                var image = new GameObject().AddComponent<ImageContent>();
                image.Init(a, window, texture);
                var surface = image.GetComponentInChildren<ContentSurface>();
                Check(surface != null && IsFlatGlass(surface), "the image surface is a flat SpatialGlass shape");
                Check(MaterialOf(surface).GetFloat("_UseTex") == 1f && MaterialOf(surface).mainTexture == texture, "the image material samples the texture (_UseTex = 1)");
                float inset = UITheme.PtToMetres(WindowContent.InsetRadiusPt);
                Check(Near(WindowContent.InsetRadiusPt, 12f) && Near(Radius(surface), inset), $"the image radius is 12 pt (24 pt less the 20 pt inset, at least 12) ({Radius(surface) * 1000f:F1} mm)");
                Check(MaterialOf(surface).renderQueue > slabQueue && MaterialOf(surface).renderQueue < textQueue, "the image draws between the slab and the text");
                Object.DestroyImmediate(texture);

                // 6. Consent: solid Success and Destructive, 56 pt, white SemiBold, 12 pt radius.
                var consentWindow = manager.Spawn("Consent", new Vector2(0.56f, 0.24f));
                consentWindow.SetSolid(true);
                var gate = new GameObject("Gate").AddComponent<ConsentGate>();
                gate.transform.SetParent(root.transform, false);
                var consent = new GameObject().AddComponent<ConsentContent>();
                consent.Init(consentWindow, window, gate);
                var approve = consent.ApproveButton;
                var deny = consent.DenyButton;
                Check(IsFlatGlass(approve) && IsFlatGlass(deny), "Approve and Deny are flat SpatialGlass shapes");
                Check(Near(MaterialOf(approve).color, UITheme.SuccessFill) && Near(MaterialOf(deny).color, UITheme.DestructiveFill)
                      && MaterialOf(approve).color.a == 1f && MaterialOf(deny).color.a == 1f, "Approve is solid SuccessFill and Deny solid DestructiveFill");
                Check(UITheme.ContrastRatio(Color.white, UITheme.SuccessFill) >= 4.5f && UITheme.ContrastRatio(Color.white, UITheme.DestructiveFill) >= 4.5f,
                    "white text on SuccessFill and DestructiveFill is at least 4.5:1");
                Check(Near(approve.transform.localScale.y, UITheme.PtToMetres(56f)) && Near(deny.transform.localScale.y, UITheme.PtToMetres(56f)), "both buttons are 56 pt high");
                Check(Near(Radius(approve), control) && Near(Radius(deny), control), "both buttons have the 12 pt radius");
                Check(HitSize(approve).y >= hit && HitSize(deny).y >= hit && GapBetween(approve, deny) >= UITheme.PtToMetres(UITheme.MinHitGapMm) - 1e-6f,
                    "Approve and Deny are big enough and 8 mm apart");
                var approveLabel = approve.transform.parent.Find("ApproveLabel").GetComponent<TextMeshPro>();
                var denyLabel = deny.transform.parent.Find("DenyLabel").GetComponent<TextMeshPro>();
                var semiBold = ThemeApplier.FontFor(TextRole.Headline);
                Check(approveLabel.color == Color.white && denyLabel.color == Color.white && (semiBold == null || (approveLabel.font == semiBold && denyLabel.font == semiBold)),
                    "the button texts are white SemiBold");
            }
            finally
            {
                WindowTextField.BlurAll();
                Object.DestroyImmediate(root);
            }
            yield break;
        }
    }
}
