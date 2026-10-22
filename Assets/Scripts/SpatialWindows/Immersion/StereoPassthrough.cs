using HoloKit;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The camera feed in both eyes in HoloKit's Stereo mode, for a closed (video see-through) viewer, and a zoomed
    /// camera feed in Mono. HoloKit is built for optical see-through: in Stereo it turns the AR camera's
    /// <see cref="ARCameraBackground"/> off and its eye cameras clear to black. This keeps the background component
    /// running (it is what updates the feed's textures and display matrix every frame; the AR camera itself stays off, so
    /// it draws nothing), copies the whole camera image into a texture with AR Foundation's background material, and
    /// shows it on a plane <see cref="Distance"/> in front of the AR camera, sized to the camera's field of view, so it
    /// lines up with the room as the phone's camera sees it. Both eyes see the plane, at that one depth (the camera is
    /// mono).
    ///
    /// <see cref="Zoom"/> scales the plane about the camera's axis (windows and hand tracking are untouched). At 1 the
    /// image covers exactly the camera's field of view, so things at the plane's distance appear at their real size and
    /// place. Below 1 it shrinks and leaves black around it, so more of the room fits in HoloKit's narrower eye view.
    /// Above 1 it magnifies. In Mono, ARCameraBackground draws the feed at 1; at any other zoom the plane is drawn over
    /// it, with a black backdrop behind a shrunk image.
    ///
    /// The plane draws before everything else and writes no depth: windows draw over it, and immersive environments
    /// (<see cref="ImmersionController"/>) blend over it, so the immersion dial's passthrough end drives it. At full
    /// immersion the environment covers it and it is switched off. In scenes without HoloKit (the editor) it does nothing.
    /// </summary>
    public class StereoPassthrough : MonoBehaviour
    {
        static readonly int DisplayTransformId = Shader.PropertyToID("_UnityDisplayTransform");
        const int BackgroundQueue = 1000; // before environments (1400+) and windows (2000)
        /// <summary>The backdrop behind a shrunk image is this many times the unzoomed image, so it fills any view.</summary>
        const float BackdropScale = 4f;
        public const float MinZoom = 0.5f, MaxZoom = 1.5f;

        [Tooltip("Show the camera feed in Stereo. Off: the eyes stay black outside windows (HoloKit's optical see-through).")]
        [SerializeField] bool showInStereo = true;
        [Tooltip("Distance of the camera image plane from the phone's camera, in metres.")]
        [SerializeField] float distance = 2f;
        [Tooltip("Size of the camera image: 1 matches the camera's field of view (real-world scale), less shrinks it " +
                 "(black around it), more magnifies it. Settings > Display > Passthrough zoom.")]
        [Range(MinZoom, MaxZoom)]
        [SerializeField] float zoom = 1f;
        [Tooltip("Size of the copied camera image relative to the screen, in Stereo (Mono copies it at full size).")]
        [Range(0.25f, 1f)]
        [SerializeField] float resolutionScale = 0.5f;
        [SerializeField] ImmersionController immersion;
        [Tooltip("Base material for the plane (URP Unlit). Defaults to the WindowManager's window material.")]
        [SerializeField] Material planeMaterial;

        public bool ShowInStereo
        {
            get => showInStereo;
            set => showInStereo = value;
        }

        public float Distance => distance;

        /// <summary>Image size relative to the camera's field of view, <see cref="MinZoom"/>..<see cref="MaxZoom"/>.</summary>
        public float Zoom
        {
            get => zoom;
            set => zoom = ClampZoom(value);
        }

        /// <summary>The plane is drawn this frame.</summary>
        public bool Showing { get; private set; }
        /// <summary>One line for <see cref="DeviceStatusHud"/>: on, off and why, or waiting for the feed.</summary>
        public string Status { get; private set; } = "off (mono)";
        /// <summary>
        /// The angles (degrees across and up the screen) the shown image spans as seen from the phone's camera, zoom
        /// included; null while it isn't shown.
        /// </summary>
        public Vector2? ImageFov { get; private set; }
        /// <summary>
        /// The angles (degrees across and up the screen) the whole camera image spans at zoom 1, from the AR camera's
        /// projection and display matrix; null until the plane was first shown.
        /// </summary>
        public Vector2? CameraFov { get; private set; }
        /// <summary>The copied camera image (null until first shown).</summary>
        public RenderTexture Feed => feed;
        /// <summary>The plane, while shown.</summary>
        public Transform Plane => plane != null ? plane.transform : null;
        /// <summary>The black quad behind a shrunk image (active only while the zoom is below 1).</summary>
        public Transform Backdrop => backdrop != null ? backdrop.transform : null;

        HoloKitCameraManager holoKit;
        ARCameraManager cameraManager;
        ARCameraBackground background;
        Camera arCamera;
        bool searched, enabledBackground, feedArrived;
        RenderTexture feed;
        Material feedMaterial, displayMaterial, backdropMaterial;
        GameObject plane, backdrop;
        Mesh mesh, backdropMesh;
        CommandBuffer copyCommands;
        readonly Vector3[] corners = new Vector3[4];

        /// <summary>Shown when switched on, in Stereo, and not fully immersed (at zoom 1).</summary>
        public static bool ShouldShow(bool showInStereo, bool stereo, float immersionLevel) =>
            ShouldShow(showInStereo, stereo, immersionLevel, 1f);

        /// <summary>
        /// Shown when not fully immersed and either in Stereo (when switched on) or in Mono at a zoom other than 1 (at 1,
        /// ARCameraBackground's own drawing is already the camera's real size).
        /// </summary>
        public static bool ShouldShow(bool showInStereo, bool stereo, float immersionLevel, float zoom) =>
            ImmersionController.Opacity(immersionLevel) < 1f && (stereo ? showInStereo : !IsNormalZoom(zoom));

        public static float ClampZoom(float zoom) => float.IsNaN(zoom) ? 1f : Mathf.Clamp(zoom, MinZoom, MaxZoom);

        public static bool IsNormalZoom(float zoom) => Mathf.Abs(zoom - 1f) < 0.001f;

        /// <summary>
        /// The camera viewport rect (0..1 is the screen, larger reaches past it) that the whole camera image covers, from
        /// AR Foundation's display matrix (texcoord = (u, v, 1, 1) * matrix). The screen is wider than the 4:3 image,
        /// so the background shows a band of it; this is the full image.
        /// </summary>
        public static Rect FullImageViewport(Matrix4x4 display)
        {
            var a = new Vector2(display.m00, display.m01);
            var b = new Vector2(display.m10, display.m11);
            var c = new Vector2(display.m20 + display.m30, display.m21 + display.m31);
            float det = a.x * b.y - a.y * b.x;
            if (Mathf.Abs(det) < 1e-6f) return new Rect(0f, 0f, 1f, 1f);
            Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                var image = new Vector2(i & 1, i >> 1) - c;
                var uv = new Vector2((image.x * b.y - image.y * b.x) / det, (a.x * image.y - a.y * image.x) / det);
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        /// <summary>A display matrix for texcoords 0..1 across <paramref name="viewport"/> instead of across the screen.</summary>
        public static Matrix4x4 DisplayOver(Matrix4x4 display, Rect viewport)
        {
            var a = new Vector2(display.m00, display.m01);
            var b = new Vector2(display.m10, display.m11);
            var c = new Vector2(display.m20 + display.m30, display.m21 + display.m31);
            var origin = viewport.xMin * a + viewport.yMin * b + c;
            var m = Matrix4x4.identity;
            m.m00 = viewport.width * a.x; m.m01 = viewport.width * a.y;
            m.m10 = viewport.height * b.x; m.m11 = viewport.height * b.y;
            m.m20 = origin.x; m.m21 = origin.y; m.m22 = 1f; m.m23 = 0f;
            m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
            return m;
        }

        /// <summary>
        /// Corners (bottom-left, bottom-right, top-left, top-right) of <paramref name="viewport"/> at
        /// <paramref name="depth"/> in front of a camera with this projection, in the camera's local space.
        /// </summary>
        public static void PlaneCorners(Matrix4x4 projection, Rect viewport, float depth, Vector3[] corners)
        {
            Vector3 At(float u, float v) => new Vector3(
                (2f * u - 1f + projection.m02) * depth / projection.m00,
                (2f * v - 1f + projection.m12) * depth / projection.m11,
                depth);
            corners[0] = At(viewport.xMin, viewport.yMin);
            corners[1] = At(viewport.xMax, viewport.yMin);
            corners[2] = At(viewport.xMin, viewport.yMax);
            corners[3] = At(viewport.xMax, viewport.yMax);
        }

        /// <summary>Scales plane corners about the camera's axis (x = y = 0), keeping their depth.</summary>
        public static void ZoomCorners(Vector3[] corners, float zoom)
        {
            for (int i = 0; i < corners.Length; i++)
                corners[i] = new Vector3(corners[i].x * zoom, corners[i].y * zoom, corners[i].z);
        }

        /// <summary>
        /// The angles (degrees across and up) that a plane with these corners (as <see cref="PlaneCorners"/> gives them)
        /// spans as seen from the camera.
        /// </summary>
        public static Vector2 AngularSize(Vector3[] corners)
        {
            float z = corners[0].z;
            return new Vector2(
                (Mathf.Atan2(corners[1].x, z) - Mathf.Atan2(corners[0].x, z)) * Mathf.Rad2Deg,
                (Mathf.Atan2(corners[2].y, z) - Mathf.Atan2(corners[0].y, z)) * Mathf.Rad2Deg);
        }

        /// <summary>A projection's field of view in degrees (across, up), off-centre ones included.</summary>
        public static Vector2 ProjectionFov(Matrix4x4 projection) => new Vector2(
            (Mathf.Atan((1f + projection.m02) / projection.m00) - Mathf.Atan((-1f + projection.m02) / projection.m00)) * Mathf.Rad2Deg,
            (Mathf.Atan((1f + projection.m12) / projection.m11) - Mathf.Atan((-1f + projection.m12) / projection.m11)) * Mathf.Rad2Deg);

        /// <summary>
        /// A camera image's field of view in degrees (along its width, along its height) from its intrinsics in pixels of
        /// that image: focal length, principal point and resolution. Zero when the focal length is unknown.
        /// </summary>
        public static Vector2 IntrinsicsFov(Vector2 focalLength, Vector2 principalPoint, Vector2Int resolution)
        {
            if (focalLength.x <= 0f || focalLength.y <= 0f) return Vector2.zero;
            return new Vector2(
                (Mathf.Atan(principalPoint.x / focalLength.x) + Mathf.Atan((resolution.x - principalPoint.x) / focalLength.x)) * Mathf.Rad2Deg,
                (Mathf.Atan(principalPoint.y / focalLength.y) + Mathf.Atan((resolution.y - principalPoint.y) / focalLength.y)) * Mathf.Rad2Deg);
        }

        /// <summary>
        /// The zoom at which an image spanning <paramref name="imageFov"/> just fits a view of <paramref name="viewFov"/>
        /// (degrees, both centred on the same axis).
        /// </summary>
        public static float FitZoom(Vector2 imageFov, Vector2 viewFov)
        {
            float Ratio(float view, float image) => Mathf.Tan(view * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(image * 0.5f * Mathf.Deg2Rad);
            return Mathf.Min(Ratio(viewFov.x, imageFov.x), Ratio(viewFov.y, imageFov.y));
        }

        void Awake()
        {
            if (immersion == null) immersion = GetComponent<ImmersionController>();
            zoom = ClampZoom(zoom);
        }

        bool Find()
        {
            if (holoKit != null && background != null) return true;
            if (searched) return false;
            searched = true;
            holoKit = FindFirstObjectByType<HoloKitCameraManager>();
            if (holoKit == null) return false;
            cameraManager = FindFirstObjectByType<ARCameraManager>();
            if (cameraManager == null) return false;
            background = cameraManager.GetComponent<ARCameraBackground>();
            arCamera = cameraManager.GetComponent<Camera>();
            // The background material has the feed once a frame has arrived while the background was enabled.
            cameraManager.frameReceived += OnFrameReceived;
            return background != null && arCamera != null;
        }

        void OnFrameReceived(ARCameraFrameEventArgs args)
        {
            if (background != null && background.enabled) feedArrived = true;
        }

        void LateUpdate()
        {
            if (!Find())
            {
                Status = "off (no HoloKit camera)";
                Hide();
                return;
            }
            bool stereo = holoKit.ScreenRenderMode == ScreenRenderMode.Stereo;
            float level = immersion != null ? immersion.Level : 0f;
            if (!ShouldShow(showInStereo, stereo, level, zoom))
            {
                Status = ImmersionController.Opacity(level) >= 1f ? "off (full immersion)"
                    : !stereo ? "off (mono)" : "off (switched off)";
                Hide();
                return;
            }

            // HoloKit (and its recorder) switch it off on entering Stereo; it keeps the feed's material current.
            if (!background.enabled)
            {
                background.enabled = true;
                enabledBackground = true;
            }
            var source = background.material;
            if (source == null || !feedArrived)
            {
                Status = "waiting for camera feed";
                SetPlaneActive(false);
                return;
            }

            // ARKit's background shader has the display matrix; XR Simulation's (the editor) maps the screen directly.
            bool hasDisplay = source.HasMatrix(DisplayTransformId);
            var display = hasDisplay ? source.GetMatrix(DisplayTransformId) : Matrix4x4.identity;
            var viewport = FullImageViewport(display);
            CopyFeed(source, hasDisplay, display, viewport, stereo ? resolutionScale : 1f);
            UpdatePlane(viewport);
            Status = $"on ({(stereo ? "" : "mono, ")}zoom {zoom:0.00}x, {feed.width}x{feed.height} at {distance:0.0} m" +
                     $"{(hasDisplay ? "" : ", no display matrix")})";
        }

        void CopyFeed(Material source, bool hasDisplay, Matrix4x4 display, Rect viewport, float scale)
        {
            int width = Mathf.Clamp(Mathf.RoundToInt(Screen.width * viewport.width * scale), 16, 2048);
            int height = Mathf.Clamp(Mathf.RoundToInt(Screen.height * viewport.height * scale), 16, 2048);
            if (feed == null || feed.width != width || feed.height != height)
            {
                if (feed != null) Destroy(feed);
                // A depth buffer: ARKit's background shader writes SV_Depth, and with no depth attachment it draws nothing.
                feed = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                    { name = "Stereo Passthrough Feed" };
                feed.Create();
                if (displayMaterial != null) displayMaterial.mainTexture = feed;
            }
            if (feedMaterial == null || feedMaterial.shader != source.shader)
            {
                if (feedMaterial != null) Destroy(feedMaterial);
                feedMaterial = new Material(source) { name = "Stereo Passthrough Copy" };
            }
            // The feed's textures, keywords and display matrix, then texcoords across the whole image.
            feedMaterial.CopyPropertiesFromMaterial(source);
            if (hasDisplay) feedMaterial.SetMatrix(DisplayTransformId, DisplayOver(display, viewport));
            copyCommands ??= new CommandBuffer { name = "Stereo Passthrough Copy" };
            DrawInto(copyCommands, feed, feedMaterial);
        }

        /// <summary>
        /// Draws a full-screen pass of <paramref name="material"/> (AR Foundation's background) into
        /// <paramref name="target"/>, which needs a depth buffer. AR Foundation's documented camera-image copy: a
        /// CommandBuffer that sets the target, clears it and blits to the current target. The caller owns the buffer, so
        /// nothing is allocated per call; the previous render target is restored.
        /// </summary>
        public static void DrawInto(CommandBuffer commands, RenderTexture target, Material material)
        {
            var color = Graphics.activeColorBuffer;
            var depth = Graphics.activeDepthBuffer;
            commands.Clear();
            commands.SetRenderTarget(target.colorBuffer, target.depthBuffer);
            commands.ClearRenderTarget(true, true, Color.black);
            commands.Blit((Texture)null, BuiltinRenderTextureType.CurrentActive, material);
            Graphics.ExecuteCommandBuffer(commands);
            Graphics.SetRenderTarget(color, depth);
        }

        void UpdatePlane(Rect viewport)
        {
            if (plane == null && !CreatePlane()) return;
            if (plane.transform.parent != arCamera.transform)
            {
                plane.transform.SetParent(arCamera.transform, false);
                backdrop.transform.SetParent(arCamera.transform, false);
            }
            PlaneCorners(arCamera.projectionMatrix, viewport, distance, corners);
            CameraFov = AngularSize(corners);
            // The backdrop: the unzoomed image several times over, so it fills the view around a shrunk one.
            ZoomCorners(corners, BackdropScale);
            backdropMesh.vertices = corners;
            backdropMesh.RecalculateBounds();
            ZoomCorners(corners, zoom / BackdropScale);
            mesh.vertices = corners;
            mesh.RecalculateBounds();
            SetPlaneActive(true);
            ImageFov = AngularSize(corners);
        }

        bool CreatePlane()
        {
            var baseMaterial = planeMaterial;
            if (baseMaterial == null && TryGetComponent<WindowManager>(out var manager)) baseMaterial = manager.WindowMaterial;
            if (baseMaterial == null) return false;

            displayMaterial = Background(baseMaterial, "Stereo Passthrough", Color.white, BackgroundQueue);
            displayMaterial.mainTexture = feed;
            backdropMaterial = Background(baseMaterial, "Stereo Passthrough Backdrop", Color.black, BackgroundQueue - 1);
            plane = CreateQuad("Stereo Passthrough", displayMaterial, out mesh);
            backdrop = CreateQuad("Stereo Passthrough Backdrop", backdropMaterial, out backdropMesh);
            return true;
        }

        static Material Background(Material baseMaterial, string name, Color color, int queue)
        {
            var material = new Material(baseMaterial) { name = name, color = color, mainTexture = null };
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.renderQueue = queue;
            return material;
        }

        GameObject CreateQuad(string name, Material material, out Mesh quad)
        {
            quad = new Mesh { name = name };
            quad.vertices = new Vector3[4];
            quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };

            var go = new GameObject(name);
            go.transform.SetParent(arCamera.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return go;
        }

        void SetPlaneActive(bool active)
        {
            Showing = active;
            if (!active) ImageFov = null;
            if (plane != null && plane.activeSelf != active) plane.SetActive(active);
            // Black around a shrunk image: the eyes clear to black in Stereo anyway; in Mono it covers the camera background.
            bool around = active && zoom < 1f;
            if (backdrop != null && backdrop.activeSelf != around) backdrop.SetActive(around);
        }

        void Hide()
        {
            SetPlaneActive(false);
            // Give HoloKit back the state it chose for Stereo.
            if (enabledBackground && background != null && holoKit != null && holoKit.ScreenRenderMode == ScreenRenderMode.Stereo)
                background.enabled = false;
            enabledBackground = false;
        }

        void OnDisable() => Hide();

        void OnDestroy()
        {
            if (cameraManager != null) cameraManager.frameReceived -= OnFrameReceived;
            if (plane != null) Destroy(plane);
            if (mesh != null) Destroy(mesh);
            if (backdrop != null) Destroy(backdrop);
            if (backdropMesh != null) Destroy(backdropMesh);
            if (feed != null) Destroy(feed);
            if (feedMaterial != null) Destroy(feedMaterial);
            copyCommands?.Release();
            if (displayMaterial != null) Destroy(displayMaterial);
            if (backdropMaterial != null) Destroy(backdropMaterial);
        }
    }
}
