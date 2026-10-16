using System.Collections.Generic;
using System.IO;
using System.Linq;
using HoloKit;
using HoloKit.Editor;
using SpatialLauncher.SpatialWindows.Testing;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARKit;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// Pre-flight check for an iOS device build of <see cref="ScenePath"/>: player settings, HoloKit's own
    /// build rules, the XR loader, the build scene list, the scene's contents, the materials and shaders it
    /// uses, video import settings and texture import settings. Logs one line per check with a
    /// [DeviceCheck] prefix and a summary; changes nothing (<see cref="DeviceSettings"/> applies the settings).
    /// </summary>
    static class DeviceBuildCheck
    {
        public const string ScenePath = "Assets/Scenes/DeviceMain.unity";

        static int passed, warnings, failures;

        [MenuItem("Tools/Spatial Windows/Check iOS Device Build")]
        static void Run()
        {
            passed = warnings = failures = 0;
            CheckBuildTarget();
            CheckPlayerSettings();
            CheckHoloKitRules();
            CheckXRLoader();
            CheckRenderPipeline();
            CheckBuildScenes();
            CheckScene();
            CheckSceneAssets();
            CheckTextures();
            Debug.Log($"[DeviceCheck] Done: {passed} passed, {warnings} warnings, {failures} failures -> " +
                      (failures == 0 ? "OK to build" : "fix the failures before building"));
        }

        public const string XcodeProjectPath = "Builds/iOS";

        /// <summary>
        /// Exports the Xcode project for <see cref="ScenePath"/> to Builds/iOS (git-ignored). This runs the real
        /// build steps (HoloKit validation, shader stripping, IL2CPP), so failures show up before you're on a Mac.
        /// Starts after the menu call returns and logs the result with a [DeviceBuild] prefix.
        /// </summary>
        [MenuItem("Tools/Spatial Windows/Export iOS Xcode Project")]
        static void ExportXcodeProject()
        {
            EditorApplication.update += StartWhenIdle;
        }

        // A build refuses to start while scripts compile, so wait for the editor to be idle.
        static void StartWhenIdle()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= StartWhenIdle;
            EditorApplication.delayCall += () =>
            {
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = XcodeProjectPath,
                    target = BuildTarget.iOS,
                    targetGroup = BuildTargetGroup.iOS,
                    options = BuildOptions.None,
                };
                Debug.Log($"[DeviceBuild] Exporting Xcode project to {XcodeProjectPath}...");
                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;
                Debug.Log($"[DeviceBuild] Result: {summary.result}, {summary.totalErrors} errors, {summary.totalWarnings} warnings, " +
                          $"{summary.totalTime.TotalSeconds:F0}s, output {summary.outputPath}");
                foreach (var step in report.steps)
                foreach (var message in step.messages)
                {
                    if (message.type == LogType.Error || message.type == LogType.Exception)
                        Debug.Log($"[DeviceBuild] ERROR in '{step.name}': {message.content}");
                }
            };
        }

        /// <summary>Applies HoloKit's own Fix for every iOS project-validation rule that currently fails.</summary>
        [MenuItem("Tools/Spatial Windows/Apply HoloKit iOS Fixes")]
        static void ApplyHoloKitFixes()
        {
            foreach (var rule in HoloKitProjectValidation.RulesFor(BuildTargetGroup.iOS))
            {
                if (!rule.IsRuleEnabled() || rule.CheckPredicate() || rule.FixIt == null) continue;
                rule.FixIt();
                Debug.Log($"[DeviceCheck] Applied HoloKit fix: {rule.FixItMessage}");
            }
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Tools/Spatial Windows/Make DeviceMain The Only Build Scene")]
        static void UseDeviceSceneOnly()
        {
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[DeviceCheck] Build scene list: {ScenePath}");
        }

        /// <summary>
        /// Points WindowManager.head and HandWindowInput.head at the AR camera (the one with ARCameraManager).
        /// The default, Camera.main, is null in HoloKit Stereo because the mono camera is disabled there,
        /// while its transform keeps tracking in both modes.
        /// </summary>
        [MenuItem("Tools/Spatial Windows/Assign Head To AR Camera")]
        static void AssignHead()
        {
            var arCamera = Object.FindFirstObjectByType<ARCameraManager>(FindObjectsInactive.Include);
            if (arCamera == null)
            {
                Debug.LogWarning("[DeviceCheck] No ARCameraManager in the open scene.");
                return;
            }

            int assigned = 0;
            foreach (var behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!(behaviour is WindowManager) && !(behaviour is HandWindowInput)) continue;
                var so = new SerializedObject(behaviour);
                so.FindProperty("head").objectReferenceValue = arCamera.transform;
                so.ApplyModifiedProperties();
                assigned++;
            }
            EditorSceneManager.MarkSceneDirty(arCamera.gameObject.scene);
            Debug.Log($"[DeviceCheck] Assigned head = {arCamera.name} on {assigned} component(s).");
        }

        static void Pass(string message) { passed++; Debug.Log($"[DeviceCheck] PASS: {message}"); }
        static void Warn(string message) { warnings++; Debug.LogWarning($"[DeviceCheck] WARN: {message}"); }
        static void Fail(string message) { failures++; Debug.LogError($"[DeviceCheck] FAIL: {message}"); }
        static void Expect(bool ok, string message, bool failIfNot = true)
        {
            if (ok) Pass(message);
            else if (failIfNot) Fail(message);
            else Warn(message);
        }

        static void CheckBuildTarget()
        {
            bool iosModule = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS);
            Expect(iosModule, "iOS Build Support module is installed");
            var active = EditorUserBuildSettings.activeBuildTarget;
            Expect(active == BuildTarget.iOS, $"active build target is iOS (currently {active}); " +
                                              "HoloKit adds its video recorder and iOS code paths only when targeting iOS", false);
        }

        static void CheckPlayerSettings()
        {
            var ios = NamedBuildTarget.iOS;
            Expect(!string.IsNullOrWhiteSpace(PlayerSettings.iOS.cameraUsageDescription),
                $"Camera Usage Description is set (\"{PlayerSettings.iOS.cameraUsageDescription}\")");
            Expect(!string.IsNullOrWhiteSpace(PlayerSettings.iOS.microphoneUsageDescription),
                $"Microphone Usage Description is set for voice typing (\"{PlayerSettings.iOS.microphoneUsageDescription}\")");
            Expect(System.IO.File.Exists("Assets/Plugins/iOS/SpatialSpeech.mm"),
                "the voice typing plugin is in Assets/Plugins/iOS (Speech.framework and ARC are set after each build)");

            string bundleId = PlayerSettings.GetApplicationIdentifier(ios);
            Expect(!bundleId.StartsWith("com.unity.template"),
                $"bundle identifier is your own ({bundleId}); the template's can clash with other installs and won't match your signing", false);

            Expect(PlayerSettings.GetScriptingBackend(ios) == ScriptingImplementation.IL2CPP, "scripting backend is IL2CPP");
            // iOS architecture: 0 = ARMv7, 1 = ARM64, 2 = Universal.
            Expect(PlayerSettings.GetArchitecture(ios) == 1, "target architecture is ARM64", false);

            if (System.Version.TryParse(PlayerSettings.iOS.targetOSVersionString, out var minimum))
                Expect(minimum >= DeviceSettings.MinimumIos,
                    $"minimum iOS version {minimum} is {DeviceSettings.MinimumIos} or later (HoloKit hand tracking, the device's input)", false);

            Expect(PlayerSettings.defaultInterfaceOrientation == UIOrientation.LandscapeLeft,
                $"default orientation is Landscape Left, as the phone sits in the viewer ({PlayerSettings.defaultInterfaceOrientation})", false);
            Expect(PlayerSettings.insecureHttpOption == InsecureHttpOption.AlwaysAllowed,
                $"HTTP is allowed: WHEP to the laptop agent is plain HTTP on the local network ({PlayerSettings.insecureHttpOption})");
            Expect(PlayerSettings.iOS.requiresPersistentWiFi, "Requires Persistent WiFi is on (streams from the laptop)", false);

            bool define = PlayerSettings.GetScriptingDefineSymbols(ios).Split(';').Contains("SPATIAL_LAUNCHER_WEBRTC");
            bool webRtcPackage = System.Type.GetType("Unity.WebRTC.WebRTC, Unity.WebRTC") != null;
            Expect(define && webRtcPackage, $"WebRTC streaming is compiled in for iOS (define {(define ? "set" : "missing")}, " +
                                            $"com.unity.webrtc {(webRtcPackage ? "installed" : "missing")})");

            Expect(string.IsNullOrEmpty(PlayerSettings.iOS.appleDeveloperTeamID) == false,
                "Apple Developer Team ID is set for automatic signing (otherwise set it in Xcode)", false);
        }

        static void CheckHoloKitRules()
        {
            foreach (var rule in HoloKitProjectValidation.RulesFor(BuildTargetGroup.iOS))
            {
                if (!rule.IsRuleEnabled()) continue;
                if (rule.CheckPredicate()) Pass($"HoloKit rule: {Shorten(rule.Message)}");
                else if (rule.Error) Fail($"HoloKit rule (blocks the build): {rule.Message} Fix: {rule.FixItMessage}");
                else Warn($"HoloKit rule: {rule.Message} Fix: {rule.FixItMessage}");
            }
        }

        static string Shorten(string text) => text.Length > 70 ? text.Substring(0, 70) + "..." : text;

        static void CheckXRLoader()
        {
            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.iOS);
            var loaders = settings != null && settings.Manager != null ? settings.Manager.activeLoaders : null;
            Expect(loaders != null && loaders.Any(l => l is ARKitLoader), "XR Plug-in Management uses the ARKit loader on iOS");
            Expect(settings != null && settings.InitManagerOnStart, "XR is initialised on startup for iOS");
        }

        static void CheckRenderPipeline()
        {
            var pipeline = GraphicsSettings.defaultRenderPipeline;
            Expect(pipeline != null, $"a render pipeline asset is set ({(pipeline != null ? pipeline.name : "none")})");
            string pipelinePath = AssetDatabase.GetAssetPath(pipeline);
            bool hasArFeature = !string.IsNullOrEmpty(pipelinePath) && AssetDatabase.GetDependencies(pipelinePath, true)
                .Select(AssetDatabase.LoadMainAssetAtPath)
                .OfType<UnityEngine.Rendering.Universal.ScriptableRendererData>()
                .Any(data => data.rendererFeatures.Any(f => f is ARBackgroundRendererFeature && f.isActive));
            Expect(hasArFeature, "the URP renderer has an active AR Background Renderer Feature (needed for the camera feed)");
        }

        static void CheckBuildScenes()
        {
            var enabled = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            Expect(enabled.Length == 1 && enabled[0] == ScenePath,
                $"{ScenePath} is the only scene in the build list (list: {string.Join(", ", enabled)})");
        }

        static void CheckScene()
        {
            var scene = SceneManager.GetSceneByPath(ScenePath);
            bool openedHere = false;
            if (!scene.isLoaded)
            {
                if (!File.Exists(ScenePath))
                {
                    Fail($"{ScenePath} exists");
                    return;
                }
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                openedHere = true;
            }

            try
            {
                var roots = scene.GetRootGameObjects();
                T[] Find<T>() where T : Component => roots.SelectMany(r => r.GetComponentsInChildren<T>(true)).ToArray();

                Expect(Find<ARSession>().Length == 1, "scene has one ARSession");

                var cameraManagers = Find<ARCameraManager>();
                var backgrounds = Find<ARCameraBackground>();
                Expect(cameraManagers.Length == 1 && backgrounds.Length == 1 && backgrounds[0].enabled &&
                       backgrounds[0].GetComponent<ARCameraManager>() != null,
                    "the AR camera has an enabled ARCameraManager and ARCameraBackground (camera feed in Mono)");

                var holokit = Find<HoloKitCameraManager>();
                Expect(holokit.Length == 1, "scene has one HoloKitCameraManager");
                if (holokit.Length == 1)
                {
                    var so = new SerializedObject(holokit[0]);
                    foreach (var field in new[] { "m_MonoCamera", "m_CenterEyePose", "m_BlackCamera", "m_LeftEyeCamera", "m_RightEyeCamera", "m_iOSPhoneModelList" })
                        Expect(so.FindProperty(field)?.objectReferenceValue != null, $"HoloKitCameraManager.{field} is assigned");
                    Expect(holokit[0].GetComponentInChildren<LowLatencyTrackingManager>(true) != null, "the HoloKit rig has a LowLatencyTrackingManager");
                }

                var mainCameras = Find<Camera>().Where(c => c.CompareTag("MainCamera")).ToArray();
                Expect(mainCameras.Length == 1, $"exactly one camera is tagged MainCamera ({mainCameras.Length})");

                // Editor-only stand-ins must not ship.
                var editorOnly = new List<string>();
                void NoEditorOnly<T>() where T : Behaviour
                {
                    foreach (var c in Find<T>()) editorOnly.Add($"{typeof(T).Name} on {c.gameObject.name}");
                }
                NoEditorOnly<MouseWindowInput>();
                NoEditorOnly<SimulatedHandSource>();
                NoEditorOnly<WindowSpawnTester>();
                NoEditorOnly<CameraBackgroundFallback>();
                Expect(editorOnly.Count == 0, "no editor-only components in the scene" +
                                              (editorOnly.Count > 0 ? $" (found: {string.Join(", ", editorOnly)})" : ""));

                var managers = Find<WindowManager>();
                Expect(managers.Length == 1, "scene has one WindowManager");
                if (managers.Length == 1)
                {
                    var so = new SerializedObject(managers[0]);
                    var material = so.FindProperty("windowMaterial").objectReferenceValue as Material;
                    Expect(material != null, "WindowManager has its window material assigned (no Shader.Find fallback at runtime)");
                    var head = so.FindProperty("head").objectReferenceValue as Transform;
                    Expect(head != null && head.GetComponent<Camera>() != null && head.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>() != null,
                        $"WindowManager.head is the tracked AR camera ({(head != null ? head.name : "unset")}); Camera.main is null in HoloKit Stereo");
                }

                foreach (var hands in Find<HandWindowInput>())
                {
                    var so = new SerializedObject(hands);
                    Expect(so.FindProperty("head").objectReferenceValue != null, "HandWindowInput.head is assigned");
                    var source = so.FindProperty("handSource").objectReferenceValue as HoloKitHandSource;
                    Expect(source != null, "HandWindowInput takes its hands from HoloKitHandSource");
                    if (source != null)
                    {
                        var sourceSettings = new SerializedObject(source);
                        Expect(sourceSettings.FindProperty("handTracking").objectReferenceValue != null &&
                               sourceSettings.FindProperty("head").objectReferenceValue != null,
                            "HoloKitHandSource has its HandTrackingManager and head assigned");
                    }
                }
                var eventSystems = Find<UnityEngine.EventSystems.EventSystem>();
                Expect(Find<HoloKit.UI.HoloKitDefaultUICanvas>().Length == 1 && eventSystems.Length == 1 &&
                       eventSystems[0].GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() != null,
                    "a Mono/Stereo button gets into stereo (HoloKit Default UI Canvas, EventSystem on the Input System); HoloKit starts in mono");
                Expect(cameraManagers.Length == 1 && cameraManagers[0].GetComponent<AROcclusionManager>() != null,
                    "the AR camera has an AROcclusionManager (LiDAR depth for HoloKit's 3D hand joints)");
#if UNITY_IOS
                var handTracking = Find<HoloKit.iOS.HandTrackingManager>();
                Expect(handTracking.Length == 1, "scene has HoloKit's HandTrackingManager");
                if (handTracking.Length == 1)
                    Expect(new SerializedObject(handTracking[0]).FindProperty("m_MaxHandCount").intValue == (int)HoloKit.iOS.MaxHandCount.Both,
                        "HoloKit's HandTrackingManager tracks two hands (two-hand stretch, cross to hide)");
#endif

                var launchers = Find<LauncherWindow>();
                Expect(launchers.Length == 1, "scene has one LauncherWindow");
                if (launchers.Length == 1)
                {
                    var so = new SerializedObject(launchers[0]);
                    Expect(so.FindProperty("sampleImage").objectReferenceValue != null || so.FindProperty("sampleSprite").objectReferenceValue != null,
                        "LauncherWindow has a sample image");
                    Expect(so.FindProperty("sampleVideo").objectReferenceValue != null, "LauncherWindow has a sample video clip");
                    Expect(so.FindProperty("laptopStreaming").enumValueIndex == (int)LauncherWindow.LaptopStreaming.WebRtc &&
                           so.FindProperty("connectionSettings").objectReferenceValue != null,
                        "LauncherWindow streams laptop windows over WebRTC and has its connection settings");
                }

                bool hasInput = managers.Length == 1 && managers[0].GetComponents<MonoBehaviour>().Any(c => c is IWindowInput);
                Expect(hasInput && Find<MonoBehaviour>().Any(m => m is IHandSource),
                    "a device input source drives the windows (IWindowInput with a device IHandSource)", false);
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(scene, true);
            }
        }

        static void CheckSceneAssets()
        {
            var dependencies = AssetDatabase.GetDependencies(ScenePath, true);

            foreach (var path in dependencies.Where(p => p.EndsWith(".mat")))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                var shader = material != null ? material.shader : null;
                bool ok = shader != null && shader.name != "Hidden/InternalErrorShader" && !ShaderUtil.ShaderHasError(shader);
                Expect(ok, $"material {path} uses a working shader ({(shader != null ? shader.name : "none")}) and is referenced by the scene, so it ships");
            }

            // Text: TextMeshPro objects created at runtime use the default font asset from TMP Settings (a Resources asset, so it ships).
            var tmpFont = TMPro.TMP_Settings.defaultFontAsset;
            Expect(tmpFont != null && tmpFont.material != null && tmpFont.material.shader != null && !ShaderUtil.ShaderHasError(tmpFont.material.shader),
                $"TextMeshPro default font and shader are available ({(tmpFont != null ? tmpFont.name : "none")})");

            foreach (var path in dependencies.Where(p => AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(VideoClip)))
                CheckVideo(path);
        }

        static void CheckVideo(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as VideoClipImporter;
            var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(path);
            if (importer == null || clip == null)
            {
                Fail($"video {path} imports as a VideoClip");
                return;
            }

            var settings = importer.GetTargetSettings("iPhone") ?? importer.defaultTargetSettings;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (settings.enableTranscoding)
            {
                Expect(settings.codec == VideoCodec.H264 || settings.codec == VideoCodec.H265 || settings.codec == VideoCodec.Auto,
                    $"video {path} transcodes to {settings.codec} for iOS");
            }
            else
            {
                // Without transcoding the source file ships as-is; iOS plays H.264/HEVC in .mp4/.mov via AVFoundation.
                Expect(ext == ".mp4" || ext == ".mov" || ext == ".m4v",
                    $"video {path} ships untranscoded as {ext} ({clip.width}x{clip.height}, {clip.length:F1}s); its codec must be H.264 or HEVC");
            }
        }

        static void CheckTextures()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue; // font atlases etc.
                Expect(importer.npotScale == TextureImporterNPOTScale.None,
                    $"image {path} has Non-Power of 2 = None ({importer.npotScale})");
            }
        }
    }
}
