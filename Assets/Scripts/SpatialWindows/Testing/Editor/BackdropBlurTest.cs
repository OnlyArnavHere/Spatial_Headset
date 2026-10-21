using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test for the shared glass blur: which source is used (and off with the toggle, in full immersion or without a
    /// feed), the 240 x 180 target and the globals, that a hard pattern comes out blurred, the world-to-feed-UV maths (the same
    /// for both eyes, since it only uses the AR camera), the Glass blur setting surviving a reload, zero GC bytes per frame,
    /// the targets released on disposal, the consent window's solid glass not changing with the backdrop (and its contrast),
    /// the Resources blur material and both shaders compiling. Results use a [BackdropBlurTest] prefix.
    /// </summary>
    static class BackdropBlurTest
    {
        static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "BackdropBlurTest");

        [MenuItem("Tools/Spatial Windows/Test Backdrop Blur")]
        static void Run() => EditorTestRunner.Run("BackdropBlurTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Backdrop Blur", true)]
        static bool CanRun() => !Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            string userPath = SettingsStore.Path;
            try
            {
                CheckSources();
                CheckMatrix();
                CheckSetting();
                CheckAssets();
                CheckPass();
            }
            finally
            {
                SettingsStore.Path = userPath;
                BackdropBlurPass.SwitchOff();
            }
            yield break;
        }

        static void CheckSources()
        {
            // (setting, immersion, feed showing, stereo, background running, simulated)
            Check(BackdropBlur.ChooseSource(true, 0f, true, true, false, false) == BlurSource.Feed, "Stereo passthrough showing: blur the feed copy");
            Check(BackdropBlur.ChooseSource(true, 0f, false, false, true, false) == BlurSource.Background, "Mono with the camera running: blur the AR background");
            Check(BackdropBlur.ChooseSource(true, 0f, false, false, false, true) == BlurSource.Simulated, "the editor: blur the made-up room");
            Check(BackdropBlur.ChooseSource(true, 0f, false, true, true, false) == BlurSource.Off, "Stereo without the feed plane: off (the background isn't drawn)");
            Check(BackdropBlur.ChooseSource(true, 0f, false, false, false, false) == BlurSource.Off, "no feed: off");
            Check(BackdropBlur.ChooseSource(false, 0f, true, true, true, true) == BlurSource.Off, "Glass blur off: off whatever the feed");
            Check(BackdropBlur.ChooseSource(true, 1f, true, true, true, true) == BlurSource.Off, "full immersion (passthrough off): off");
        }

        static void CheckMatrix()
        {
            var projection = Matrix4x4.Perspective(60f, 4f / 3f, 0.1f, 20f);
            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));   // a camera at the origin looking along +Z
            var whole = BackdropBlurPass.FeedMatrix(projection * view, new Rect(0f, 0f, 1f, 1f));
            bool front = BackdropBlurPass.FeedUv(whole, new Vector3(0f, 0f, 2f), out var centre);
            Check(front && Vector2.Distance(centre, new Vector2(0.5f, 0.5f)) < 1e-4f, $"straight ahead is the middle of the feed ({centre})");
            float top = 2f * Mathf.Tan(30f * Mathf.Deg2Rad);
            BackdropBlurPass.FeedUv(whole, new Vector3(0f, top, 2f), out var up);
            Check(Mathf.Abs(up.y - 1f) < 1e-3f && Mathf.Abs(up.x - 0.5f) < 1e-4f, $"the top of the view is v = 1 ({up})");
            BackdropBlurPass.FeedUv(whole, new Vector3(top * 4f / 3f, 0f, 2f), out var right);
            Check(Mathf.Abs(right.x - 1f) < 1e-3f, $"the right edge is u = 1 ({right})");

            // A feed that covers only the middle half of the screen: the screen's centre stays at the feed's centre, its edge is outside.
            var half = BackdropBlurPass.FeedMatrix(projection * view, new Rect(0.25f, 0.25f, 0.5f, 0.5f));
            BackdropBlurPass.FeedUv(half, new Vector3(0f, 0f, 2f), out var middle);
            BackdropBlurPass.FeedUv(half, new Vector3(0f, top, 2f), out var edge);
            Check(Vector2.Distance(middle, new Vector2(0.5f, 0.5f)) < 1e-4f && Mathf.Abs(edge.y - 1.5f) < 1e-3f,
                $"a feed covering the middle half maps the screen's top edge to v = 1.5 ({edge})");

            // Both eyes: the same world point gives the same UV, since only the AR camera's matrices are used (not the eye's).
            var world = new Vector3(0.3f, -0.2f, 1.5f);
            var unit = new Rect(0f, 0f, 1f, 1f);
            var leftScreen = BackdropBlurPass.FeedMatrix(projection * Matrix4x4.Translate(new Vector3(0.032f, 0f, 0f)) * view, unit);
            var rightScreen = BackdropBlurPass.FeedMatrix(projection * Matrix4x4.Translate(new Vector3(-0.032f, 0f, 0f)) * view, unit);
            BackdropBlurPass.FeedUv(leftScreen, world, out var leftEyeUv);
            BackdropBlurPass.FeedUv(rightScreen, world, out var rightEyeUv);
            BackdropBlurPass.FeedUv(whole, world, out var feedUv);
            Check(Vector2.Distance(leftEyeUv, rightEyeUv) > 0.01f, $"the two eyes (+-32 mm) see the point at different screen UVs ({leftEyeUv} / {rightEyeUv})");
            Check(BackdropBlur.FeedUvFor(whole, world) == feedUv && Vector2.Distance(feedUv, (leftEyeUv + rightEyeUv) * 0.5f) < 1e-3f,
                $"the glass uses the AR camera's matrix, so both eyes read the same feed UV ({feedUv})");
            Check(!BackdropBlurPass.FeedUv(whole, new Vector3(0f, 0f, -1f), out _), "a point behind the camera has no UV");
        }

        static void CheckSetting()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            Directory.CreateDirectory(Folder);
            SettingsStore.Path = Path.Combine(Folder, "settings.json");
            Check(SettingsStore.Current.performance.glassBlur, "Glass blur is on by default");
            SettingsStore.Update(s => s.performance.glassBlur = false);
            SettingsStore.Save();
            SettingsStore.Path = SettingsStore.Path;   // forgets the loaded settings
            Check(!SettingsStore.Current.performance.glassBlur, "Glass blur off survives a reload");
            SettingsStore.Update(s => s.performance.glassBlur = true);
            SettingsStore.Save();
            SettingsStore.Path = SettingsStore.Path;
            Check(SettingsStore.Current.performance.glassBlur, "Glass blur on survives a reload");

            string old = Path.Combine(Folder, "old.json");
            File.WriteAllText(old, "{\"version\":1}");
            Check(SettingsStore.Load(old).performance.glassBlur, "an older settings file without the setting keeps blur on");
            Check(Array.IndexOf(SettingsContent.SectionNames, "Performance") >= 0, "Settings has a Performance section");
        }

        static void CheckAssets()
        {
            var material = Resources.Load<Material>(BackdropBlurPass.MaterialPath);
            Check(material != null && material.shader != null && material.shader.name == "SpatialLauncher/KawaseBlur",
                "Resources/Materials/BackdropBlur uses the KawaseBlur shader (a Resources material keeps the shader in the build)");
            Check(material != null && !ShaderUtil.ShaderHasError(material.shader), "the KawaseBlur shader compiles");
            var glass = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/SpatialGlass.mat");
            Check(glass != null && !ShaderUtil.ShaderHasError(glass.shader), "the SpatialGlass shader compiles");
        }

        static void CheckPass()
        {
            var pattern = new Texture2D(64, 48, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 64; x++)
                    pattern.SetPixel(x, y, ((x / 4 + y / 4) & 1) == 0 ? Color.black : Color.white);
            pattern.Apply(false);
            Texture2D readback = null;
            RenderTexture solidTarget = null;
            Material solid = null;
            BackdropBlurPass pass = null;
            try
            {
                pass = new BackdropBlurPass();
                Check(pass.Ready, "the pass found its material and made its targets");
                var vp = Matrix4x4.Perspective(60f, 4f / 3f, 0.1f, 20f) * Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
                bool ok = pass.Render(pattern, null, new Rect(0f, 0f, 1f, 1f), vp);
                Check(ok && pass.Result.width == 240 && pass.Result.height == 180, $"blurs into a 240 x 180 target ({pass.Result.width} x {pass.Result.height})");
                Check(Shader.GetGlobalFloat("_SpatialBlurOn") == 1f && Shader.GetGlobalTexture("_SpatialBackdrop") == pass.Result,
                    "_SpatialBlurOn is 1 and _SpatialBackdrop is the result while on");
                Check(Shader.GetGlobalMatrix("_SpatialBackdropVP") != Matrix4x4.zero, "_SpatialBackdropVP is set");

                // A black and white check pattern comes out nearly flat grey.
                readback = new Texture2D(240, 180, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                var previous = RenderTexture.active;
                RenderTexture.active = pass.Result;
                readback.ReadPixels(new Rect(0, 0, 240, 180), 0, 0);
                readback.Apply(false);
                RenderTexture.active = previous;
                float min = 1f, max = 0f;
                for (int y = 70; y < 110; y++)
                    for (int x = 100; x < 140; x++)
                    {
                        float g = readback.GetPixel(x, y).grayscale;
                        min = Mathf.Min(min, g);
                        max = Mathf.Max(max, g);
                    }
                Check(max - min < 0.25f, $"a black and white check pattern is blurred to a swing of {max - min:0.00} (was 1.00)");

                // No GC per frame.
                for (int i = 0; i < 4; i++) pass.Render(pattern, null, new Rect(0f, 0f, 1f, 1f), vp);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 30; i++) pass.Render(pattern, null, new Rect(0f, 0f, 1f, 1f), vp);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(bytes == 0, $"30 blur passes allocate {bytes} bytes");

                // Consent: solid glass ignores the backdrop, and white text on it keeps 7:1.
                var glass = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/SpatialGlass.mat");
                solid = new Material(glass) { hideFlags = HideFlags.HideAndDontSave };
                solid.SetFloat("_Solid", 1f);
                solidTarget = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
                var white = SolidPixel(solid, solidTarget, pass, MakeFlat(Color.white), vp);
                var black = SolidPixel(solid, solidTarget, pass, MakeFlat(Color.black), vp);
                Check(white.a > 0.9f && white == black, $"solid glass is the same over a white and a black backdrop ({white} / {black})");
                float luminance = 0.2126f * Mathf.GammaToLinearSpace(white.r) + 0.7152f * Mathf.GammaToLinearSpace(white.g) + 0.0722f * Mathf.GammaToLinearSpace(white.b);
                float contrast = 1.05f / (luminance + 0.05f);
                Check(contrast >= 7f, $"white text on solid glass has {contrast:0.0}:1 contrast");
                Check(Mathf.Approximately(solid.GetFloat("_Solid"), 1f), "the consent material keeps _Solid = 1");

                // Ordinary glass over a flat white backdrop: the worst case, at the top where the sheen is strongest.
                var ordinary = new Material(glass) { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    var whitePixels = GlassPixels(ordinary, solidTarget, pass, MakeFlat(Color.white), vp);
                    float worst = 0f;
                    foreach (var pixel in whitePixels) worst = Mathf.Max(worst, Luminance(pixel));
                    var secondary = Theme.UITheme.TextSecondary;
                    float textLuminance = Luminance(secondary) * secondary.a;
                    float primary = 1.05f / (worst + 0.05f);
                    float second = (textLuminance + (1f - secondary.a) * worst + 0.05f) / (worst + 0.05f);
                    Check(primary >= 7f, $"blurred glass over white at the top: TextPrimary {primary:0.0}:1 (glass luminance {worst:0.000})");
                    Check(second >= 4.5f, $"blurred glass over white at the top: TextSecondary {second:0.0}:1");
                }
                finally { UnityEngine.Object.DestroyImmediate(ordinary); }

                pass.Dispose();
                Check(Shader.GetGlobalFloat("_SpatialBlurOn") == 0f, "disposing switches _SpatialBlurOn to 0 (glass shows glass.fallback)");
                Check(pass.Result == null && !pass.Ready, "the targets are released on disposal");
                pass = null;
            }
            finally
            {
                pass?.Dispose();
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                if (solid != null) UnityEngine.Object.DestroyImmediate(solid);
                if (solidTarget != null) UnityEngine.Object.DestroyImmediate(solidTarget);
                UnityEngine.Object.DestroyImmediate(pattern);
            }
        }

        static float Luminance(Color c) =>
            0.2126f * Mathf.GammaToLinearSpace(c.r) + 0.7152f * Mathf.GammaToLinearSpace(c.g) + 0.0722f * Mathf.GammaToLinearSpace(c.b);

        // The glass drawn across a target after blurring a backdrop; pixels near the top and bottom of its middle column
        // (the Blit may be flipped, so both are returned and the brighter counts).
        static Color[] GlassPixels(Material glass, RenderTexture target, BackdropBlurPass pass, Texture2D backdrop, Matrix4x4 vp)
        {
            pass.Render(backdrop, null, new Rect(0f, 0f, 1f, 1f), vp);
            UnityEngine.Object.DestroyImmediate(backdrop);
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(false, true, Color.clear);
            Graphics.Blit(null, target, glass);
            var read = new Texture2D(64, 64, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
            var pixels = new[] { read.GetPixel(32, 58), read.GetPixel(32, 5) };
            RenderTexture.active = previous;
            UnityEngine.Object.DestroyImmediate(read);
            return pixels;
        }

        static Texture2D MakeFlat(Color color)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++) t.SetPixel(x, y, color);
            t.Apply(false);
            return t;
        }

        // Draws the glass material across a target after blurring a flat backdrop, and reads the middle pixel.
        static Color SolidPixel(Material solid, RenderTexture target, BackdropBlurPass pass, Texture2D backdrop, Matrix4x4 vp)
        {
            pass.Render(backdrop, null, new Rect(0f, 0f, 1f, 1f), vp);
            UnityEngine.Object.DestroyImmediate(backdrop);
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(false, true, Color.clear);
            Graphics.Blit(null, target, solid);
            var read = new Texture2D(64, 64, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
            var pixel = read.GetPixel(32, 32);
            RenderTexture.active = previous;
            UnityEngine.Object.DestroyImmediate(read);
            return pixel;
        }
    }
}
