using System.Collections;
using System.Reflection;
using SpatialLauncher.SpatialWindows.Theme;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of the glass window chrome: the body and shadow use the SpatialGlass material with the theme's
    /// radius and tint, the consent window is solid, the quads stay plain quads that bend with the curve, and the window
    /// parts keep their types. Results use a [GlassChromeTest] prefix.
    /// </summary>
    static class GlassChromeTest
    {
        const string GlassPath = "Assets/Materials/SpatialGlass.mat";
        const string WindowPath = "Assets/Materials/SpatialWindow.mat";

        [MenuItem("Tools/Spatial Windows/Test Glass Chrome")]
        static void Run() => EditorTestRunner.Run("GlassChromeTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Glass Chrome", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning && !Application.isPlaying;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static void SetField(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static float Float(Renderer renderer, string name)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetFloat(name);
        }

        static Color ColorOf(Renderer renderer, string name)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetColor(name);
        }

        static bool Near(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 1e-3f && Mathf.Abs(a.g - b.g) < 1e-3f && Mathf.Abs(a.b - b.b) < 1e-3f && Mathf.Abs(a.a - b.a) < 1e-3f;

        // Queue of a TextMeshPro label's material (what window text is drawn with).
        static int TextQueue()
        {
            var go = new GameObject("QueueProbe");
            try
            {
                var text = go.AddComponent<TMPro.TextMeshPro>();
                return text.fontSharedMaterial != null ? text.fontSharedMaterial.renderQueue : 3000;
            }
            finally { Object.DestroyImmediate(go); }
        }

        static IEnumerator Steps()
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            var window = AssetDatabase.LoadAssetAtPath<Material>(WindowPath);
            Check(glass != null && glass.shader != null && glass.shader.name == "SpatialLauncher/SpatialGlass", "SpatialGlass.mat uses the SpatialGlass shader");
            if (glass == null || window == null) yield break;

            var root = new GameObject("GlassChromeTest");
            try
            {
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, false);
                var manager = new GameObject("Manager").AddComponent<WindowManager>();
                manager.transform.SetParent(root.transform, false);
                SetField(manager, "windowMaterial", window);
                SetField(manager, "glassMaterial", glass);
                SetField(manager, "head", head);

                var a = manager.Spawn("Glass", new Vector2(0.6f, 0.4f));
                var b = manager.Spawn("Solid", new Vector2(0.5f, 0.3f));
                b.SetSolid(true);
                Check(a != null && b != null, "two windows spawn");

                var body = WindowTestUtil.Part(a, "Body");
                var bodyRenderer = body.GetComponent<MeshRenderer>();
                var shadow = WindowTestUtil.Part(a, "Shadow");
                Check(bodyRenderer.sharedMaterial == glass, "the body carries the glass material");
                var slab = WindowTestUtil.Part(a, "Glass");
                var slabRenderer = slab.GetComponent<MeshRenderer>();
                Check(slabRenderer.sharedMaterial == glass && !bodyRenderer.enabled && slabRenderer.enabled, "the glass slab is drawn with the glass material, not the body");
                Check(slab.GetComponent<Collider>() == null && body.GetComponent<Collider>() != null, "the slab has no collider and the body keeps its own");
                Check(glass.renderQueue < 3000 && glass.renderQueue < TextQueue(), $"glass queue {glass.renderQueue} is before TextMeshPro text");
                Check(shadow != null && shadow.GetComponent<MeshRenderer>().sharedMaterial.shader == glass.shader, "the shadow quad uses the glass shader");
                Check(Mathf.Approximately(Float(bodyRenderer, "_Radius"), UITheme.PtToMetres(24f)), "_Radius is 24 pt in metres");
                Check(Near(ColorOf(bodyRenderer, "_Tint"), UITheme.GlassTint) && Mathf.Approximately(ColorOf(bodyRenderer, "_Tint").a, 0.72f), "_Tint is glass.tint (alpha 0.72)");
                Check(Near(ColorOf(bodyRenderer, "_Fallback"), UITheme.GlassFallback), "_Fallback is glass.fallback");
                var size = new MaterialPropertyBlock();
                float extra = SpatialWindow.TitleBarGap + SpatialWindow.TitleBarHeight;
                slabRenderer.GetPropertyBlock(size);
                Check(Mathf.Approximately(size.GetVector("_Size").x, 0.6f) && Mathf.Approximately(size.GetVector("_Size").y, 0.4f + extra), "_Size is the body plus the title row");
                var titleBar = WindowTestUtil.Part(a, "TitleBar");
                float slabTop = slab.localPosition.y + slab.localScale.y * 0.5f, slabBottom = slab.localPosition.y - slab.localScale.y * 0.5f;
                Check(titleBar.localPosition.y + titleBar.localScale.y * 0.5f <= slabTop + 1e-4f && titleBar.localPosition.y - titleBar.localScale.y * 0.5f >= slabBottom
                      && Mathf.Abs(titleBar.localScale.x) <= slab.localScale.x + 1e-4f, "the slab contains the title bar");
                Check(Mathf.Abs(slabBottom + 0.2f) < 1e-4f, "the slab's bottom is the body's bottom");

                // The shadow quad is bigger than the body by the blur and offset, and sits behind it.
                Check(shadow.localScale.x > slab.localScale.x && shadow.localPosition.z > 0f, "the shadow quad is larger and behind the body");
                var shadowRenderer = shadow.GetComponent<MeshRenderer>();
                Check(Float(shadowRenderer, "_ShadowOnly") == 1f && Float(slabRenderer, "_ShadowOnly") == 0f, "only the shadow quad draws the shadow");
                Check(shadowRenderer.sharedMaterial.renderQueue < glass.renderQueue, "the shadow is drawn before the glass");
                Check(shadow.GetComponent<Collider>() == null, "the shadow has no collider");

                // Consent: solid.
                var solidBody = WindowTestUtil.Part(b, "Body").GetComponent<MeshRenderer>();
                Check(Float(solidBody, "_Solid") == 1f && b.IsSolid, "SetSolid(true) makes the window solid");
                Check(Float(bodyRenderer, "_Solid") == 0f && !a.IsSolid, "an ordinary window is not solid");
                Check(Mathf.Approximately(ColorOf(solidBody, "_RimTop").a, 0.5f) && Mathf.Approximately(Float(solidBody, "_RimWidth"), UITheme.PtToMetres(2f)),
                    "solid has a 2 pt rim at alpha 0.5");

                // Focus raises the rim alpha.
                a.SetFocused(false);
                float rest = ColorOf(bodyRenderer, "_RimTop").a;
                a.SetFocused(true);
                Check(ColorOf(bodyRenderer, "_RimTop").a > rest, "focus raises the rim alpha");

                // Quads stay plain quads and bend with the curve.
                var bodyFilter = body.GetComponent<MeshFilter>();
                var slabFilter = slab.GetComponent<MeshFilter>();
                var shadowFilter = shadow.GetComponent<MeshFilter>();
                Check(bodyFilter.sharedMesh.vertexCount == 4 && bodyFilter.sharedMesh.name == "Quad", "the body is a plain 4-vertex quad");
                Check(shadowFilter.sharedMesh.vertexCount == 4 && shadowFilter.sharedMesh.name == "Quad", "the shadow is a plain 4-vertex quad");
                a.SetCurve(1f);
                var curver = a.GetComponent<WindowCurver>();
                typeof(WindowCurver).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(curver, null);
                Check(curver.BentMeshOf(bodyFilter) != null, "a curved window bends the body");
                Check(curver.BentMeshOf(slabFilter) != null, "a curved window bends the glass slab");
                Check(curver.BentMeshOf(shadowFilter) != null, "a curved window bends the shadow");

                // Parts keep their types; the title band is a hit area only.
                Check(body.GetComponent<WindowPart>().Type == WindowPartType.Content, "Body is Content");
                Check(titleBar.GetComponent<WindowPart>().Type == WindowPartType.TitleBar && titleBar.GetComponent<Collider>() != null
                      && !titleBar.GetComponent<MeshRenderer>().enabled, "TitleBar keeps its collider and is not drawn");
                Check(WindowTestUtil.Part(a, "CloseButton").GetComponent<WindowPart>().Type == WindowPartType.CloseButton, "CloseButton is CloseButton");
                Check(WindowTestUtil.Part(a, "ResizeHandle").GetComponent<WindowPart>().Type == WindowPartType.ResizeHandle, "ResizeHandle is ResizeHandle");
                Check(a.Options != null, "the options strip exists");

                // Resizing updates the shader size.
                a.SetSize(new Vector2(0.8f, 0.5f), keepTopLeft: false);
                slabRenderer.GetPropertyBlock(size);
                Check(Mathf.Approximately(size.GetVector("_Size").x, 0.8f), "resize updates _Size");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            yield break;
        }
    }
}
