using System;
using System.Diagnostics;
using HoloKit;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using Object = UnityEngine.Object;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>Where the backdrop blur gets its picture this frame (<see cref="BackdropBlur.ChooseSource"/>).</summary>
    public enum BlurSource
    {
        /// <summary>No blur: glass uses its fallback colour.</summary>
        Off,
        /// <summary>The copy of the camera image <see cref="StereoPassthrough"/> shows (Stereo, or Mono away from zoom 1).</summary>
        Feed,
        /// <summary>Mono: the AR camera background's own material, drawn straight into the blur target.</summary>
        Background,
        /// <summary>The editor: <see cref="BackdropBlur.SimulatedFeed"/> (a made-up room or photo), as the camera's picture.</summary>
        Simulated,
    }

    /// <summary>
    /// The shared blur target: downsamples a picture into 240 x 180, then dual-Kawase passes (down, down, up, up) on a
    /// CommandBuffer, and publishes the result as the global <c>_SpatialBackdrop</c> with <c>_SpatialBackdropVP</c> (world to
    /// feed UV) and <c>_SpatialBlurOn</c>. The targets, material and CommandBuffer are made once and freed by
    /// <see cref="Dispose"/>, which also switches the blur off. Nothing here allocates per call.
    /// </summary>
    public sealed class BackdropBlurPass : IDisposable
    {
        public const int Width = 240, Height = 180;
        public const string MaterialPath = "Materials/BackdropBlur";
        public const string SamplerName = "SpatialBackdropBlur";

        static readonly int BackdropId = Shader.PropertyToID("_SpatialBackdrop");
        static readonly int MatrixId = Shader.PropertyToID("_SpatialBackdropVP");
        static readonly int OnId = Shader.PropertyToID("_SpatialBlurOn");
        static readonly int TexelId = Shader.PropertyToID("_BlurTexel");
        static readonly int CopyStepId = Shader.PropertyToID("_CopyStep");
        static readonly ProfilerMarker Marker = new ProfilerMarker(SamplerName);

        readonly Material material;
        readonly RenderTexture full, half, quarter, result;
        RenderTexture mid;   // 480 x 360, only for a material source (the AR background), so its full-size image isn't point-sampled
        Material pictureCopy; // private copy of the AR background material: the live one is never drawn with
        CommandBuffer drawCommands;
        readonly CommandBuffer commands;
        readonly CustomSampler sampler;
        readonly Recorder recorder;
        readonly Stopwatch stopwatch = new Stopwatch();
        bool disposed;

        /// <summary>The blurred picture (also the global <c>_SpatialBackdrop</c> while on).</summary>
        public RenderTexture Result => result;
        /// <summary>False when the blur material isn't in Resources, so nothing can be drawn.</summary>
        public bool Ready => material != null && !disposed;
        /// <summary>CPU time of the last <see cref="Render"/>, milliseconds.</summary>
        public float CpuMs { get; private set; }
        /// <summary>GPU time of the blur passes from the profiler's GPU sampler, milliseconds; 0 where the device doesn't report it.</summary>
        public float GpuMs => recorder != null && recorder.isValid ? recorder.gpuElapsedNanoseconds * 1e-6f : 0f;

        public BackdropBlurPass()
        {
            var shared = Resources.Load<Material>(MaterialPath);
            if (shared == null) return;
            material = new Material(shared) { name = "Backdrop Blur (instance)", hideFlags = HideFlags.HideAndDontSave };
            material.SetVector(CopyStepId, new Vector4(0.5f / Width, 0.5f / Height, 0f, 0f));
            full = Target("Backdrop Blur 240x180", Width, Height);
            half = Target("Backdrop Blur 120x90", Width / 2, Height / 2);
            quarter = Target("Backdrop Blur 60x45", Width / 4, Height / 4);
            result = Target("Spatial Backdrop", Width, Height);
            commands = new CommandBuffer { name = SamplerName };
            sampler = CustomSampler.Create(SamplerName, true);
            recorder = sampler.GetRecorder();
            recorder.enabled = true;
        }

        static RenderTexture Target(string name, int width, int height, int depth = 0)
        {
            var rt = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Blurs a texture or, when it is null, a material that draws the picture across the target (the AR background's), and
        /// publishes it. <paramref name="viewport"/> is the part of the AR camera's screen (0..1) that the picture covers;
        /// <paramref name="viewProjection"/> is that camera's projection times its worldToCamera matrix.
        /// </summary>
        public bool Render(Texture texture, Material picture, Rect viewport, Matrix4x4 viewProjection)
        {
            if (!Ready || (texture == null && picture == null)) return false;
            stopwatch.Restart();
            using (Marker.Auto())
            {
                if (texture != null) Graphics.Blit(texture, full, material, 0);
                else
                {
                    // The background shader writes depth, so the target has a depth buffer; a copy of the material, as the
                    // live one belongs to ARCameraBackground (textures, keywords and display matrix are copied each frame).
                    mid ??= Target("Backdrop Blur 480x360", Width * 2, Height * 2, 24);
                    if (pictureCopy == null || pictureCopy.shader != picture.shader)
                    {
                        if (pictureCopy != null) Object.DestroyImmediate(pictureCopy);
                        pictureCopy = new Material(picture) { name = "Backdrop Blur Picture (instance)", hideFlags = HideFlags.HideAndDontSave };
                    }
                    pictureCopy.CopyPropertiesFromMaterial(picture);
                    drawCommands ??= new CommandBuffer { name = SamplerName + " Draw" };
                    StereoPassthrough.DrawInto(drawCommands, mid, pictureCopy);
                    Graphics.Blit(mid, full, material, 0);
                }

                commands.Clear();
                commands.BeginSample(sampler);
                Pass(full, half, 1, Width, Height);
                Pass(half, quarter, 1, Width / 2, Height / 2);
                Pass(quarter, half, 2, Width / 4, Height / 4);
                Pass(half, result, 2, Width / 2, Height / 2);
                commands.EndSample(sampler);
                Graphics.ExecuteCommandBuffer(commands);

                Shader.SetGlobalTexture(BackdropId, result);
                Shader.SetGlobalMatrix(MatrixId, FeedMatrix(viewProjection, viewport));
                Shader.SetGlobalFloat(OnId, 1f);
            }
            CpuMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            return true;
        }

        void Pass(RenderTexture from, RenderTexture to, int pass, int fromWidth, int fromHeight)
        {
            commands.SetGlobalVector(TexelId, new Vector4(1f / fromWidth, 1f / fromHeight, 0f, 0f));
            commands.Blit(from, to, material, pass);
        }

        /// <summary>Glass falls back to its flat colour.</summary>
        public static void SwitchOff() => Shader.SetGlobalFloat(OnId, 0f);

        /// <summary>
        /// World position to feed UV: (x, y) / w of <c>matrix * (world, 1)</c>. <paramref name="viewProjection"/> is the camera's
        /// projection times worldToCamera (OpenGL conventions, as Unity's scripts see them); <paramref name="viewport"/> is the
        /// part of the camera's screen (0..1, bottom-left origin) the picture spans, so UV 0..1 covers exactly that.
        /// </summary>
        public static Matrix4x4 FeedMatrix(Matrix4x4 viewProjection, Rect viewport)
        {
            var t = Matrix4x4.zero;
            t.m00 = 0.5f / viewport.width;
            t.m03 = (0.5f - viewport.xMin) / viewport.width;
            t.m11 = 0.5f / viewport.height;
            t.m13 = (0.5f - viewport.yMin) / viewport.height;
            t.m33 = 1f;
            return t * viewProjection;
        }

        /// <summary>The feed UV a world position maps to (what the glass shader computes); false behind the camera.</summary>
        public static bool FeedUv(Matrix4x4 feedMatrix, Vector3 world, out Vector2 uv)
        {
            var h = feedMatrix * new Vector4(world.x, world.y, world.z, 1f);
            uv = h.w > 1e-6f ? new Vector2(h.x / h.w, h.y / h.w) : Vector2.zero;
            return h.w > 1e-6f;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SwitchOff();
            Shader.SetGlobalTexture(BackdropId, Texture2D.blackTexture);
            commands?.Release();
            drawCommands?.Release();
            if (pictureCopy != null) Object.DestroyImmediate(pictureCopy);
            if (recorder != null) recorder.enabled = false;
            DestroyTarget(full);
            DestroyTarget(half);
            DestroyTarget(quarter);
            DestroyTarget(result);
            DestroyTarget(mid);
            if (material != null) Object.DestroyImmediate(material);
        }

        static void DestroyTarget(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }

    /// <summary>
    /// Once a frame, after <see cref="StereoPassthrough"/>, blurs the mono passthrough feed into one global texture that
    /// both eyes' glass samples (Doc/ui-design.md sections 3 and 11). The feed is mono, so one blur serves both eyes; glass
    /// looks it up by the feed UV of its own world position (the AR camera's view-projection, then the feed's mapping), not
    /// by screen position, so the left and right eye read the same spot. With no feed, in full immersion, or with Settings >
    /// Performance > Glass blur off, <c>_SpatialBlurOn</c> is 0 and glass uses glass.fallback. LauncherWindow adds it next to
    /// <see cref="StereoPassthrough"/>, so the scene needs no wiring.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class BackdropBlur : MonoBehaviour
    {
        static readonly int DisplayTransformId = Shader.PropertyToID("_UnityDisplayTransform");

        /// <summary>
        /// The editor has no camera: a picture standing in for it (a made-up room), set by CameraBackgroundFallback. Covers
        /// <see cref="SimulatedViewport"/> of the AR camera's screen.
        /// </summary>
        public static Texture SimulatedFeed;
        public static Rect SimulatedViewport = new Rect(0f, 0f, 1f, 1f);

        public static BackdropBlur Current { get; private set; }

        /// <summary>Blurring this frame (<c>_SpatialBlurOn</c> is 1).</summary>
        public bool On { get; private set; }
        public BlurSource Source { get; private set; }
        /// <summary>One line for <see cref="DeviceStatusHud"/>: "on" or "off" and why.</summary>
        public string Status { get; private set; } = "off";
        public BackdropBlurPass Pass => pass;
        /// <summary>The last blur's cost in milliseconds: GPU when the device reports it, else CPU.</summary>
        public float Ms => pass == null ? 0f : (pass.GpuMs > 0f ? pass.GpuMs : pass.CpuMs);
        public bool MsIsGpu => pass != null && pass.GpuMs > 0f;

        StereoPassthrough passthrough;
        ImmersionController immersion;
        HoloKitCameraManager holoKit;
        ARCameraManager cameraManager;
        ARCameraBackground background;
        Camera arCamera;
        BackdropBlurPass pass;
        bool searched, hasFrameProjection;
        Matrix4x4 frameProjection;

        /// <summary>The feed UV of a world point through the AR camera's matrix (what glass samples).</summary>
        public static Vector2 FeedUvFor(Matrix4x4 feedMatrix, Vector3 world)
        {
            BackdropBlurPass.FeedUv(feedMatrix, world, out var uv);
            return uv;
        }

        /// <summary>The source for a frame. Pure, so it can be tested without a camera.</summary>
        public static BlurSource ChooseSource(bool settingOn, float immersionLevel, bool feedShowing, bool stereo,
            bool backgroundRunning, bool hasSimulated)
        {
            if (!settingOn || ImmersionController.Opacity(immersionLevel) >= 1f) return BlurSource.Off;
            if (feedShowing) return BlurSource.Feed;
            if (!stereo && backgroundRunning) return BlurSource.Background;
            return hasSimulated ? BlurSource.Simulated : BlurSource.Off;
        }

        void Awake()
        {
            Current = this;
            passthrough = GetComponent<StereoPassthrough>();
            immersion = GetComponent<ImmersionController>();
        }

        void Find()
        {
            if (searched) return;
            searched = true;
            holoKit = FindFirstObjectByType<HoloKitCameraManager>();
            cameraManager = FindFirstObjectByType<ARCameraManager>();
            background = FindFirstObjectByType<ARCameraBackground>();
            arCamera = background != null ? background.GetComponent<Camera>() : cameraManager != null ? cameraManager.GetComponent<Camera>() : null;
            // The projection ARKit reports for the frame; the camera's own can lag it or be changed by HoloKit.
            if (cameraManager != null) cameraManager.frameReceived += OnFrameReceived;
        }

        void OnFrameReceived(ARCameraFrameEventArgs args)
        {
            hasFrameProjection = args.projectionMatrix.HasValue;
            if (hasFrameProjection) frameProjection = args.projectionMatrix.Value;
        }

        void LateUpdate()
        {
            Find();
            bool settingOn = SettingsStore.Current.performance.glassBlur;
            bool stereo = holoKit != null && holoKit.ScreenRenderMode == ScreenRenderMode.Stereo;
            var material = background != null && background.enabled ? background.material : null;
            bool running = material != null && cameraManager != null && cameraManager.subsystem != null && cameraManager.subsystem.running;
            bool showing = passthrough != null && passthrough.Showing && passthrough.Feed != null;
            var source = ChooseSource(settingOn, immersion != null ? immersion.Level : 0f, showing, stereo, running, SimulatedFeed != null);
            var camera = arCamera != null ? arCamera : Camera.main;
            if (source == BlurSource.Off || camera == null)
            {
                Disable(settingOn ? "off (no feed)" : "off (setting)");
                return;
            }
            pass ??= new BackdropBlurPass();
            if (!pass.Ready)
            {
                Disable("off (blur material missing)");
                return;
            }

            var viewProjection = (hasFrameProjection && camera == arCamera ? frameProjection : camera.projectionMatrix) * camera.worldToCameraMatrix;
            bool ok;
            switch (source)
            {
                case BlurSource.Feed:
                    // The feed holds the whole camera image; the display matrix says which part of the screen that is.
                    var viewport = background != null && background.material != null && background.material.HasMatrix(DisplayTransformId)
                        ? StereoPassthrough.FullImageViewport(background.material.GetMatrix(DisplayTransformId))
                        : new Rect(0f, 0f, 1f, 1f);
                    ok = pass.Render(passthrough.Feed, null, viewport, viewProjection);
                    break;
                case BlurSource.Background:
                    ok = pass.Render(null, material, new Rect(0f, 0f, 1f, 1f), viewProjection);
                    break;
                default:
                    ok = pass.Render(SimulatedFeed, null, SimulatedViewport, viewProjection);
                    break;
            }
            On = ok;
            Source = ok ? source : BlurSource.Off;
            Status = ok ? "on" : "off (no feed)";
            if (!ok) BackdropBlurPass.SwitchOff();
        }

        void Disable(string status)
        {
            if (On) BackdropBlurPass.SwitchOff();
            On = false;
            Source = BlurSource.Off;
            Status = status;
        }

        void OnDisable()
        {
            On = false;
            Source = BlurSource.Off;
            Status = "off";
            pass?.Dispose();
            pass = null;
            BackdropBlurPass.SwitchOff();
            if (cameraManager != null) cameraManager.frameReceived -= OnFrameReceived;
            hasFrameProjection = false;
            searched = false;
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }
    }
}
