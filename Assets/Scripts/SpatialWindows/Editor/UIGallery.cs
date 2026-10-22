using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// An editor feedback loop for the UI: renders every window type in each of its states, in Edit mode, against a
    /// generated room standing in for the passthrough, and saves the PNGs to <c>UIGallery/&lt;label&gt;/</c> at the
    /// project root (git-ignored). "Before" keeps a baseline to compare a later "latest" against.
    /// Everything is built in a preview scene (the open scene is untouched and never marked dirty), as hidden objects that
    /// are destroyed again afterwards. Awake, OnEnable and LateUpdate don't run in Edit mode, so windows are set up by hand:
    /// presentation is forced instant and each content's Update runs once for its texts.
    /// </summary>
    static class UIGallery
    {
        const int Width = 1280, Height = 800;
        const string WindowMaterialPath = "Assets/Materials/SpatialWindow.mat";
        const string GlassMaterialPath = "Assets/Materials/SpatialGlass.mat";
        const string MenuRoot = "Tools/Spatial Windows/";
        const float FocusedDepth = 0.15f;
        const float UnfocusedScale = 0.85f;

        [MenuItem(MenuRoot + "Capture UI Gallery")]
        static void CaptureLatest() => Capture("latest");

        [MenuItem(MenuRoot + "Capture UI Gallery", true)]
        static bool CanCaptureLatest() => !Application.isPlaying;

        [MenuItem(MenuRoot + "Capture UI Gallery (Before)")]
        static void CaptureBefore() => Capture("before");

        [MenuItem(MenuRoot + "Capture UI Gallery (Before)", true)]
        static bool CanCaptureBefore() => !Application.isPlaying;

        static void Capture(string label)
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[UIGallery] Leave Play mode first; the gallery is captured in Edit mode.");
                return;
            }
            var windowMaterial = AssetDatabase.LoadAssetAtPath<Material>(WindowMaterialPath);
            if (windowMaterial == null)
            {
                Debug.LogError($"[UIGallery] {WindowMaterialPath} not found.");
                return;
            }

            string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UIGallery", UIGalleryShots.SanitizeLabel(label));
            int saved = 0;
            Rig rig = null;
            var dirtyBefore = DirtyScenes();
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var old in Directory.GetFiles(folder, "*.png")) File.Delete(old);

                rig = new Rig(windowMaterial);
                var ids = UIGalleryShots.Windows;
                for (int i = 0; i < ids.Length; i++)
                {
                    EditorUtility.DisplayProgressBar("UI Gallery", ids[i], (float)i / (ids.Length + 1));
                    saved += CaptureWindow(rig, ids[i], folder);
                }
                EditorUtility.DisplayProgressBar("UI Gallery", UIGalleryShots.Overview, (float)ids.Length / (ids.Length + 1));
                saved += CaptureOverview(rig, folder);
                Debug.Log($"[UIGallery] Saved {saved} shots to {folder}");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError($"[UIGallery] Failed after {saved} shots; see the exception above.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                rig?.Dispose();
                // Objects are made hidden (not saved) and moved to a preview scene, but they briefly exist in the active scene.
                var dirtyAfter = DirtyScenes();
                if (dirtyAfter != dirtyBefore)
                    Debug.LogError($"[UIGallery] An open scene became dirty during the capture (before: '{dirtyBefore}', after: '{dirtyAfter}'). Revert it without saving.");
            }
        }

        // The paths of the open scenes that have unsaved changes.
        static string DirtyScenes()
        {
            var dirty = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty) dirty.Add(scene.path);
            }
            return string.Join(",", dirty);
        }

        // One window in each of its states.
        static int CaptureWindow(Rig rig, string id, string folder)
        {
            var manager = rig.NewManager();
            var window = Build(id, rig, manager);
            if (window == null) return 0;
            window.SetCurve(0f);
            rig.Prepare(window);

            var control = FirstControl(window);
            var bounds = window.LayoutBounds;
            rig.Frame(new Vector2(window.transform.position.x + bounds.center.x, window.transform.position.y + bounds.center.y),
                bounds.size, UIGallery.Distance);

            int count = 0;
            foreach (var state in UIGalleryShots.StatesFor(id))
            {
                if ((state == GalleryState.Hover || state == GalleryState.Pressed) && control == null) continue;
                ApplyState(window, control, state);
                window.SnapMotion();
                rig.Prepare(window);
                rig.Save(Path.Combine(folder, UIGalleryShots.FileName(id, state)));
                ResetState(control);
                count++;
            }
            rig.ClearWindows();
            return count;
        }

        // Several windows side by side at one distance, parallel to the camera, the last opened one in front.
        static int CaptureOverview(Rig rig, string folder)
        {
            var ids = new[] { "keyboard", "laptop_picker", "image", "settings", "home" };
            var manager = rig.NewManager();
            var windows = new List<SpatialWindow>();
            foreach (var id in ids)
            {
                var window = Build(id, rig, manager);
                if (window != null) windows.Add(window);
            }
            if (windows.Count == 0) return 0;

            const float gap = 0.06f;
            float total = -gap;
            foreach (var window in windows) total += window.Size.x + gap;
            float x = -total * 0.5f;
            foreach (var window in windows)
            {
                window.Billboard = false; // keeps them parallel to the camera instead of turning in
                window.MoveTo(new Vector3(x + window.Size.x * 0.5f, 0f, Distance));
                x += window.Size.x + gap;
            }

            manager.Focus(windows[windows.Count - 1]);
            foreach (var window in windows)
            {
                bool front = window == windows[windows.Count - 1];
                window.SetPresentation(front ? FocusedDepth : 0f, front ? 1f : UnfocusedScale, instant: true);
                window.SnapMotion();
                rig.Prepare(window);
            }
            rig.FrameWindows(windows);
            rig.Save(Path.Combine(folder, UIGalleryShots.OverviewFileName));
            rig.ClearWindows();
            return 1;
        }

        const float Distance = 1.5f;
        static readonly object GalleryPointer = new object();

        static void ApplyState(SpatialWindow window, WindowControl control, GalleryState state)
        {
            switch (state)
            {
                case GalleryState.Idle:
                    window.SetFocused(false);
                    window.SetPresentation(0f, 1f, instant: true);
                    window.SetReveal(0f); // chrome controls hidden
                    break;
                case GalleryState.Disabled:
                    window.SetFocused(false);
                    window.SetPresentation(0f, 1f, instant: true);
                    window.SetReveal(0f);
                    if (control != null) control.Interactable = false;
                    break;
                case GalleryState.Hover:
                    Focus(window);
                    window.SetReveal(1f); // chrome controls shown
                    control.HoverFrom(GalleryPointer, control.transform.position);
                    break;
                case GalleryState.Pressed:
                    Focus(window);
                    window.SetReveal(1f);
                    control.Press(control.transform.position);
                    break;
                case GalleryState.Focused:
                    Focus(window);
                    window.SetReveal(0f);
                    if (control != null) control.SetFocused(true);
                    break;
            }
            control?.SnapVisual();
        }

        static void Focus(SpatialWindow window)
        {
            window.SetFocused(true);
            window.SetPresentation(FocusedDepth, 1f, instant: true);
        }

        // Releases outside (so nothing is clicked) and ends the hover, so the next state starts clean.
        static void ResetState(WindowControl control)
        {
            if (control == null) return;
            control.Release(false);
            control.HoverExitFrom(GalleryPointer);
            control.SetFocused(false);
            control.Interactable = true;
            control.SnapVisual();
        }

        // The first button-like control, which hover and press are shown on.
        static WindowControl FirstControl(SpatialWindow window)
        {
            foreach (var control in window.GetComponentsInChildren<WindowControl>(false))
                if (!(control is ContentSurface)) return control;
            return null;
        }

        static SpatialWindow Open(WindowManager manager, string title, Vector2 size)
        {
            var window = manager.Spawn(title, size);
            window?.SetPresentation(0f, 1f, instant: true);
            window?.SnapMotion();
            return window;
        }

        static T AddContent<T>(Rig rig) where T : Component => rig.NewObject(typeof(T).Name, track: false).AddComponent<T>();

        static SpatialWindow Build(string id, Rig rig, WindowManager manager)
        {
            var mat = rig.WindowMaterial;
            const float pad = WindowContent.Padding * 2f;
            SpatialWindow window;
            switch (id)
            {
                case "home":
                    window = Open(manager, "Home", new Vector2(0.5f, 0.3f));
                    AddContent<LauncherContent>(rig).Init(window, mat, new[]
                    {
                        Icon("Image", "IMG", 0.20f, 0.62f, 0.45f), Icon("Video", "VID", 0.78f, 0.32f, 0.42f),
                        Icon("Blank", "+", 0.42f, 0.44f, 0.52f), Icon("Laptop", "PC", 0.30f, 0.42f, 0.70f),
                        Icon("Void", "VOID", 0.10f, 0.12f, 0.22f), Icon("Cinema", "CIN", 0.45f, 0.10f, 0.14f),
                        Icon("Sky", "SKY", 0.35f, 0.58f, 0.85f), Icon("Settings", "SET", 0.45f, 0.47f, 0.52f),
                        Icon("Hide all", "HIDE", 0.35f, 0.30f, 0.45f), Icon("Cast", "CAST", 0.62f, 0.38f, 0.16f),
                    });
                    return window;
                case "image":
                    window = Open(manager, "Image", new Vector2(0.6f + pad, 0.375f + pad));
                    var photo = MakePhoto(640, 400);
                    rig.Track(photo);
                    AddContent<ImageContent>(rig).Init(window, mat, photo);
                    return window;
                case "video":
                    // A real VideoContent needs a VideoPlayer and a prepared clip; this stands in for its first frame.
                    window = Open(manager, "Video", new Vector2(0.6f + pad, 0.3375f + VideoContent.ControlBarHeight + pad));
                    AddContent<Testing.PlaceholderContent>(rig).Init(window, mat, new Color(0.08f, 0.08f, 0.1f), "Video");
                    return window;
                case "remote_stream":
                    window = Open(manager, "Laptop", new Vector2(0.6f + pad, 0.3375f + pad));
                    AddContent<RemoteStreamContent>(rig).Init(window, mat, null, new Vector2Int(1920, 1080));
                    return window;
                case "laptop_picker":
                    window = Open(manager, "Connect laptop", new Vector2(0.6f + pad, LaptopPickerContent.ContentHeight + pad));
                    AddContent<LaptopPickerContent>(rig).Init(window, mat, () => { });
                    return window;
                case "pairing_code":
                    window = Open(manager, "Pair laptop", PairingCodeContent.PreferredSize + Vector2.one * pad);
                    AddContent<PairingCodeContent>(rig).Init(window, mat, null);
                    return window;
                case "settings":
                    window = Open(manager, "Settings", SettingsContent.PreferredSize + Vector2.one * pad);
                    var service = rig.NewObject("SettingsService").AddComponent<SettingsService>();
                    AddContent<SettingsContent>(rig).Init(window, mat, service);
                    return window;
                case "consent":
                    // The gate opens the modal itself (Spawn + ConsentContent) once a request is pending.
                    var gate = rig.NewObject("ConsentGate").AddComponent<ConsentGate>();
                    Rig.SetField(gate, "manager", manager);
                    gate.Request(new GallerySession(), null, null);
                    return gate.Modal;
                case "keyboard":
                    window = Open(manager, "Keyboard", VirtualKeyboardContent.WindowSize);
                    AddContent<VirtualKeyboardContent>(rig).Init(window, mat, strokes => false, () => null);
                    return window;
                default:
                    Debug.LogWarning($"[UIGallery] No builder for '{id}'.");
                    return null;
            }
        }

        static LauncherContent.Entry Icon(string label, string unused, float r, float g, float b) =>
            new LauncherContent.Entry { Label = label, Color = new Color(r, g, b) };

        sealed class GallerySession : IRemoteSession
        {
            public string SourceName => "Notepad";
            public string Address => "192.168.1.20";
            public string ModeLabel => "full control";
            public SpatialWindow Window => null;
            public void StopForPanic() { }
        }

        // A stand-in picture: sky over sea with a sun.
        static Texture2D MakePhoto(int w, int h)
        {
            var texture = new Texture2D(w, h, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float u = (float)x / w, v = (float)y / h;
                    Color c = v > 0.4f
                        ? Color.Lerp(new Color(0.95f, 0.66f, 0.42f), new Color(0.25f, 0.45f, 0.80f), (v - 0.4f) / 0.6f)
                        : Color.Lerp(new Color(0.08f, 0.22f, 0.36f), new Color(0.30f, 0.50f, 0.62f), v / 0.4f);
                    float sun = Mathf.Clamp01(1f - Mathf.Sqrt((u - 0.68f) * (u - 0.68f) * 2.5f + (v - 0.55f) * (v - 0.55f)) / 0.12f);
                    c = Color.Lerp(c, new Color(1f, 0.93f, 0.7f), sun);
                    pixels[y * w + x] = c;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        /// <summary>The preview scene, camera, backdrop and render target, and the objects built for the current shot.</summary>
        sealed class Rig : IDisposable
        {
            const float BackdropDistance = 4f;
            const float Margin = 1.2f;

            public Material WindowMaterial { get; }

            readonly Scene scene;
            readonly Camera camera;
            readonly Transform head, backdrop;
            readonly RenderTexture target;
            readonly Texture2D readback;
            readonly Texture backdropTexture;
            readonly BackdropBlurPass blur = new BackdropBlurPass();
            readonly List<Object> owned = new List<Object>();
            readonly List<GameObject> windowRoots = new List<GameObject>();
            readonly List<GameObject> fixedRoots = new List<GameObject>();

            public Rig(Material windowMaterial)
            {
                WindowMaterial = windowMaterial;
                scene = EditorSceneManager.NewPreviewScene();

                head = NewObject("Head", track: false).transform;
                fixedRoots.Add(head.gameObject);

                var cameraObject = NewObject("Camera", track: false);
                fixedRoots.Add(cameraObject);
                camera = cameraObject.AddComponent<Camera>();
                camera.scene = scene;
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.2f, 0.2f, 0.22f);
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 20f;
                camera.aspect = (float)Width / Height;

                var backdropObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                backdropObject.name = "Backdrop";
                DestroyImmediateSafe(backdropObject.GetComponent<Collider>());
                SceneManager.MoveGameObjectToScene(backdropObject, scene);
                fixedRoots.Add(backdropObject);
                backdrop = backdropObject.transform;
                backdropTexture = MakeBackdropTexture(1024, 640);
                var backdropMaterial = new Material(windowMaterial) { color = Color.white, hideFlags = HideFlags.HideAndDontSave };
                backdropMaterial.mainTexture = backdropTexture;
                if (backdropMaterial.HasProperty("_BaseMap")) backdropMaterial.SetTexture("_BaseMap", backdropTexture);
                if (backdropMaterial.HasProperty("_BaseColor")) backdropMaterial.SetColor("_BaseColor", Color.white);
                backdropObject.GetComponent<MeshRenderer>().sharedMaterial = backdropMaterial;
                owned.Add(backdropTexture);
                owned.Add(backdropMaterial);

                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                readback = new Texture2D(Width, Height, TextureFormat.RGB24, false, false) { hideFlags = HideFlags.HideAndDontSave };
                foreach (var go in fixedRoots) Flag(go);
            }

            /// <summary>A hidden object in the preview scene. Tracked ones are destroyed by <see cref="ClearWindows"/>.</summary>
            public GameObject NewObject(string name, bool track = true)
            {
                var go = new GameObject(name);
                SceneManager.MoveGameObjectToScene(go, scene);
                go.hideFlags = HideFlags.HideAndDontSave;
                if (track) windowRoots.Add(go);
                return go;
            }

            public WindowManager NewManager()
            {
                var manager = NewObject("WindowManager").AddComponent<WindowManager>();
                SetField(manager, "windowMaterial", WindowMaterial);
                SetField(manager, "glassMaterial", AssetDatabase.LoadAssetAtPath<Material>(GlassMaterialPath));
                SetField(manager, "head", head);
                return manager;
            }

            public void Track(Object asset) => owned.Add(asset);

            public static void SetField(Object target, string field, Object value)
            {
                var serialized = new SerializedObject(target);
                var property = serialized.FindProperty(field);
                if (property == null) throw new InvalidOperationException($"{target.GetType().Name} has no serialized field '{field}'.");
                property.objectReferenceValue = value;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            /// <summary>Hides what was built, runs each content's Update once (its texts) and rebuilds the TMP meshes.</summary>
            public void Prepare(SpatialWindow window)
            {
                foreach (var content in window.GetComponentsInChildren<WindowContent>(true))
                {
                    var update = content.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    try { update?.Invoke(content, null); }
                    catch (Exception e) { Debug.LogWarning($"[UIGallery] {content.GetType().Name}.Update failed: {e.InnerException?.Message ?? e.Message}"); }
                }
                foreach (var text in window.GetComponentsInChildren<TMP_Text>(true)) text.ForceMeshUpdate();
                Flag(window.gameObject);
                // The script-made materials (non-assets) are hidden too, and destroyed with their renderers in Dispose.
                foreach (var renderer in window.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null && !AssetDatabase.Contains(material)) material.hideFlags |= HideFlags.HideAndDontSave;
            }

            static void Flag(GameObject root)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            }

            /// <summary>Points the camera at a rectangle (centre and size in metres, at the given distance) and fits the backdrop.</summary>
            public void Frame(Vector2 center, Vector2 size, float distance)
            {
                float aspect = (float)Width / Height;
                float visible = Mathf.Max(size.y * Margin, size.x * Margin / aspect);
                ApplyCamera(center, 2f * Mathf.Atan(visible * 0.5f / distance) * Mathf.Rad2Deg);
            }

            /// <summary>Frames what the windows really draw: their layout corners in world space (presentation, depth and all).</summary>
            public void FrameWindows(IReadOnlyList<SpatialWindow> windows)
            {
                var points = new List<Vector3>();
                foreach (var window in windows)
                {
                    var b = window.LayoutBounds;
                    foreach (var corner in new[] { new Vector3(b.xMin, b.yMin), new Vector3(b.xMin, b.yMax), new Vector3(b.xMax, b.yMin), new Vector3(b.xMax, b.yMax) })
                    {
                        var world = window.Visual.TransformPoint(corner);
                        if (world.z > 0.05f) points.Add(world);
                    }
                }
                if (points.Count == 0) return;
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (var p in points)
                {
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                }
                var center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
                float aspect = (float)Width / Height, tan = 0f;
                foreach (var p in points)
                {
                    tan = Mathf.Max(tan, Mathf.Abs(p.y - center.y) / p.z);
                    tan = Mathf.Max(tan, Mathf.Abs(p.x - center.x) / p.z / aspect);
                }
                ApplyCamera(center, 2f * Mathf.Atan(tan * Margin) * Mathf.Rad2Deg);
            }

            void ApplyCamera(Vector2 center, float degrees)
            {
                float aspect = (float)Width / Height;
                float fov = Mathf.Clamp(degrees, 10f, 100f);
                camera.transform.position = new Vector3(center.x, center.y, 0f);
                camera.transform.rotation = Quaternion.identity;
                camera.fieldOfView = fov;

                float height = 2f * BackdropDistance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.02f;
                backdrop.position = new Vector3(center.x, center.y, BackdropDistance);
                backdrop.rotation = Quaternion.identity;
                backdrop.localScale = new Vector3(height * aspect, height, 1f);
            }

            /// <summary>Renders the preview scene and writes it as a PNG.</summary>
            public void Save(string path)
            {
                var previous = RenderTexture.active;
                try
                {
                    // Glass blurs the room behind it: the backdrop quad covers 1/1.02 of the camera's view (see ApplyCamera), and
                    // the blur projects through this same camera, so the shots match what the windows would show on a device.
                    const float cover = 1f / 1.02f;
                    if (!blur.Render(backdropTexture, null, new Rect(0.5f - cover * 0.5f, 0.5f - cover * 0.5f, cover, cover),
                            camera.projectionMatrix * camera.worldToCameraMatrix))
                        Debug.LogWarning("[UIGallery] The backdrop blur couldn't run (Resources/Materials/BackdropBlur missing); glass shows its fallback.");
                    var request = new RenderPipeline.StandardRequest { destination = target };
                    if (RenderPipeline.SupportsRenderRequest(camera, request))
                    {
                        RenderPipeline.SubmitRenderRequest(camera, request);
                    }
                    else
                    {
                        camera.targetTexture = target;
                        camera.Render();
                        camera.targetTexture = null;
                    }
                    RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    readback.Apply(false);
                }
                finally
                {
                    RenderTexture.active = previous;
                }
                File.WriteAllBytes(path, readback.EncodeToPNG());
            }

            /// <summary>Destroys the windows (and their managers, gates and services) built for the last shot.</summary>
            public void ClearWindows()
            {
                foreach (var root in windowRoots) DestroyWithMaterials(root);
                windowRoots.Clear();
            }

            public void Dispose()
            {
                try
                {
                    ClearWindows();
                    blur.Dispose();
                    foreach (var root in fixedRoots) DestroyImmediateSafe(root);
                    fixedRoots.Clear();
                    foreach (var asset in owned) DestroyImmediateSafe(asset);
                    owned.Clear();
                    if (target != null)
                    {
                        target.Release();
                        DestroyImmediateSafe(target);
                    }
                    DestroyImmediateSafe(readback);
                }
                finally
                {
                    if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                }
            }

            // Windows and contents make their own materials, which Destroy would normally clean up in OnDestroy.
            static void DestroyWithMaterials(GameObject root)
            {
                if (root == null) return;
                var materials = new HashSet<Material>();
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null && !AssetDatabase.Contains(material)) materials.Add(material);
                DestroyImmediateSafe(root);
                foreach (var material in materials) DestroyImmediateSafe(material);
            }

            static void DestroyImmediateSafe(Object o)
            {
                if (o != null) Object.DestroyImmediate(o);
            }

            // A made-up room: wall and floor gradients, a couch, a lamp, a plant and a bright window.
            static Texture2D MakeBackdropTexture(int w, int h)
            {
                var texture = new Texture2D(w, h, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color32[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        pixels[y * w + x] = Room((x + 0.5f) / w, (y + 0.5f) / h, x, y);
                texture.SetPixels32(pixels);
                texture.Apply(false);
                return texture;
            }

            const float FloorLine = 0.30f;

            static Color Room(float u, float v, int px, int py)
            {
                Color c;
                if (v < FloorLine)
                {
                    float t = v / FloorLine;
                    c = Color.Lerp(new Color(0.26f, 0.19f, 0.14f), new Color(0.46f, 0.34f, 0.24f), t);
                    c *= 0.96f + 0.04f * Mathf.Sin(u * 90f + v * 6f);
                }
                else
                {
                    float t = (v - FloorLine) / (1f - FloorLine);
                    c = Color.Lerp(new Color(0.74f, 0.71f, 0.66f), new Color(0.52f, 0.55f, 0.60f), t);
                    // Light spilling in from the window on the right.
                    float spill = Mathf.Clamp01(1f - Mathf.Sqrt((u - 0.75f) * (u - 0.75f) + (v - 0.65f) * (v - 0.65f) * 1.5f) / 0.55f);
                    c = Color.Lerp(c, new Color(0.98f, 0.93f, 0.82f), spill * 0.45f);
                    if (v < FloorLine + 0.012f) c *= 0.82f; // skirting board
                }

                c = Blend(c, new Color(0.17f, 0.30f, 0.34f), Shape(u, v, 0.24f, 0.32f, 0.17f, 0.075f, 0.03f));  // couch seat
                c = Blend(c, new Color(0.20f, 0.35f, 0.39f), Shape(u, v, 0.24f, 0.43f, 0.17f, 0.05f, 0.03f));   // couch back
                c = Blend(c, new Color(0.12f, 0.12f, 0.14f), Shape(u, v, 0.50f, 0.38f, 0.008f, 0.20f, 0.004f)); // lamp pole
                c = Blend(c, new Color(0.95f, 0.85f, 0.62f), Shape(u, v, 0.50f, 0.60f, 0.035f, 0.045f, 0.03f)); // lamp shade
                c = Blend(c, new Color(0.18f, 0.40f, 0.22f), Shape(u, v, 0.43f, 0.42f, 0.03f, 0.07f, 0.03f));   // plant
                c = Blend(c, new Color(0.30f, 0.20f, 0.14f), Shape(u, v, 0.43f, 0.33f, 0.025f, 0.035f, 0.01f)); // pot

                // The window: bright glass with a cross of mullions.
                float glass = Shape(u, v, 0.76f, 0.62f, 0.13f, 0.20f, 0.01f);
                var sky = Color.Lerp(new Color(1.0f, 0.97f, 0.88f), new Color(0.80f, 0.90f, 1.0f), Mathf.Clamp01((v - 0.45f) / 0.35f));
                float frame = Mathf.Max(Mathf.Abs(u - 0.76f) < 0.004f ? 1f : 0f, Mathf.Abs(v - 0.62f) < 0.006f ? 1f : 0f);
                c = Blend(c, new Color(0.9f, 0.88f, 0.84f), Shape(u, v, 0.76f, 0.62f, 0.14f, 0.21f, 0.01f));    // frame
                c = Blend(c, Color.Lerp(sky, new Color(0.85f, 0.85f, 0.85f), frame), glass);

                float noise = (Hash(px, py) - 0.5f) * 0.02f;
                return new Color(c.r + noise, c.g + noise, c.b + noise, 1f);
            }

            // 1 inside a rounded rectangle, falling to 0 over a pixel-ish soft edge.
            static float Shape(float u, float v, float cx, float cy, float halfW, float halfH, float radius)
            {
                float dx = Mathf.Abs(u - cx) - (halfW - radius), dy = Mathf.Abs(v - cy) - (halfH - radius);
                float outside = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) + Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                float distance = outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;
                return Mathf.Clamp01(0.5f - distance / 0.006f);
            }

            static Color Blend(Color from, Color to, float amount) => Color.Lerp(from, to, amount);

            static float Hash(int x, int y)
            {
                uint n = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                n = (n << 13) ^ n;
                return ((n * (n * n * 15731u + 789221u) + 1376312589u) & 0x7fffffffu) / (float)0x7fffffff;
            }
        }
    }
}
