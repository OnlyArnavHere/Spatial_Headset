using System;
using System.Linq;
using HoloKit;
using HoloKit.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// Applies what the iOS device build needs (Tools > Spatial Windows > Apply iOS Device Settings): player settings
    /// for a headset app that streams from the laptop, HoloKit's required fixes, DeviceMain as the only build scene,
    /// DeviceMain's launcher in WebRTC mode, HoloKit hand tracking as DeviceMain's window input (HandTrackingManager,
    /// LiDAR depth, <see cref="HoloKitHandSource"/>), HoloKit's Mono/Stereo button, and the microphone and speech-recognition usage
    /// descriptions voice typing needs (<see cref="VoiceTyping"/>). Safe to run again: it logs each change with a [DeviceSettings] prefix
    /// and leaves settings that are already right alone. Info.plist keys Unity has no setting for are added at build
    /// time by <see cref="IosInfoPlist"/>; <see cref="DeviceBuildCheck"/> checks the result.
    /// </summary>
    static class DeviceSettings
    {
        /// <summary>Used only while the project still has the Mobile AR template's identifier.</summary>
        public const string BundleId = "com.onlyarnavhere.spatiallauncher";
        /// <summary>HoloKit hand tracking, the only input on the device, needs iOS 18 (and a LiDAR iPhone).</summary>
        public static readonly Version MinimumIos = new Version(18, 0);
        public const string CameraUsage =
            "Spatial Launcher shows the room around you through the camera and follows your hands to control its windows.";
        /// <summary>Why voice typing listens (iOS asks before the first Talk).</summary>
        public const string MicrophoneUsage =
            "Spatial Launcher listens while you hold Talk on its keyboard, to type what you say.";
        /// <summary>Info.plist's NSSpeechRecognitionUsageDescription; Unity has no Player Setting for it, so IosInfoPlist adds it.</summary>
        public const string SpeechRecognitionUsage =
            "Spatial Launcher turns what you say into text while you hold Talk. It recognises speech on the phone when your language allows.";
        const string ConnectionSettingsPath = "Assets/Settings/RemoteConnectionSettings.asset";
        const string WebRtcDefine = "SPATIAL_LAUNCHER_WEBRTC";

        static int changes;

        [MenuItem("Tools/Spatial Windows/Apply iOS Device Settings")]
        static void Apply()
        {
            changes = 0;
            ApplyPlayerSettings();
            ApplyHoloKitFixes();
            ApplyBuildScenes();
            ApplyDeviceScene();
            AssetDatabase.SaveAssets();
            Debug.Log($"[DeviceSettings] Done: {changes} change(s). Run Tools > Spatial Windows > Check iOS Device Build to verify.");
        }

        static void Change(string what, object current, object wanted, Action apply)
        {
            if (Equals(current, wanted)) return;
            apply();
            changes++;
            Debug.Log($"[DeviceSettings] {what}: {current} -> {wanted}");
        }

        static void ApplyPlayerSettings()
        {
            var ios = NamedBuildTarget.iOS;

            string bundleId = PlayerSettings.GetApplicationIdentifier(ios);
            if (bundleId.StartsWith("com.unity.template"))
                Change("Bundle identifier (the template's can't be signed with your Apple ID)", bundleId, BundleId,
                    () => PlayerSettings.SetApplicationIdentifier(ios, BundleId));

            Change("Camera Usage Description", PlayerSettings.iOS.cameraUsageDescription, CameraUsage,
                () => PlayerSettings.iOS.cameraUsageDescription = CameraUsage);
            Change("Microphone Usage Description (voice typing)", PlayerSettings.iOS.microphoneUsageDescription, MicrophoneUsage,
                () => PlayerSettings.iOS.microphoneUsageDescription = MicrophoneUsage);
            Debug.Log($"[DeviceSettings] Speech Recognition Usage Description is added to Info.plist at build time: \"{SpeechRecognitionUsage}\"");

            if (!Version.TryParse(PlayerSettings.iOS.targetOSVersionString, out var minimum) || minimum < MinimumIos)
                Change("Target minimum iOS version (HoloKit hand tracking needs iOS 18)", PlayerSettings.iOS.targetOSVersionString,
                    MinimumIos.ToString(), () => PlayerSettings.iOS.targetOSVersionString = MinimumIos.ToString());

            // The phone sits sideways in the viewer; HoloKit's stereo mode renders in Landscape Left.
            Change("Default orientation", PlayerSettings.defaultInterfaceOrientation, UIOrientation.LandscapeLeft,
                () => PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft);

            // Without it iOS may power Wi-Fi down after 30 minutes without network-heavy foreground use.
            Change("Requires Persistent WiFi", PlayerSettings.iOS.requiresPersistentWiFi, true,
                () => PlayerSettings.iOS.requiresPersistentWiFi = true);

            // WHEP signaling with the laptop agent is plain HTTP on the local network (http://<laptop>:8889/...).
            Change("Allow downloads over HTTP", PlayerSettings.insecureHttpOption, InsecureHttpOption.AlwaysAllowed,
                () => PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed);

            var defines = PlayerSettings.GetScriptingDefineSymbols(ios).Split(';').Where(d => d.Length > 0).ToList();
            if (!defines.Contains(WebRtcDefine))
            {
                string before = string.Join(";", defines);
                defines.Add(WebRtcDefine);
                Change("iOS Scripting Define Symbols", before, string.Join(";", defines),
                    () => PlayerSettings.SetScriptingDefineSymbols(ios, string.Join(";", defines)));
            }
        }

        static void ApplyHoloKitFixes()
        {
            foreach (var rule in HoloKitProjectValidation.RulesFor(BuildTargetGroup.iOS))
            {
                if (!rule.IsRuleEnabled() || rule.CheckPredicate() || rule.FixIt == null) continue;
                rule.FixIt();
                changes++;
                Debug.Log($"[DeviceSettings] HoloKit fix: {rule.FixItMessage}");
            }
        }

        static void ApplyBuildScenes()
        {
            var enabled = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (enabled.Length == 1 && enabled[0] == DeviceBuildCheck.ScenePath) return;
            Change("Build scenes", string.Join(", ", enabled), DeviceBuildCheck.ScenePath,
                () => EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(DeviceBuildCheck.ScenePath, true) });
        }

        static void ApplyDeviceScene()
        {
            var scene = SceneManager.GetSceneByPath(DeviceBuildCheck.ScenePath);
            bool openedHere = false;
            if (!scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(DeviceBuildCheck.ScenePath, OpenSceneMode.Additive);
                openedHere = true;
            }
            int before = changes;
            try
            {
                T Find<T>() where T : Component =>
                    scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).FirstOrDefault();

                var launcher = Find<LauncherWindow>();
                if (launcher != null)
                {
                    var so = new SerializedObject(launcher);
                    var mode = so.FindProperty("laptopStreaming");
                    Change("DeviceMain LauncherWindow.laptopStreaming", (LauncherWindow.LaptopStreaming)mode.enumValueIndex,
                        LauncherWindow.LaptopStreaming.WebRtc, () => mode.enumValueIndex = (int)LauncherWindow.LaptopStreaming.WebRtc);
                    var connection = so.FindProperty("connectionSettings");
                    var asset = AssetDatabase.LoadAssetAtPath<RemoteConnectionSettings>(ConnectionSettingsPath);
                    Change("DeviceMain LauncherWindow.connectionSettings", connection.objectReferenceValue != null ? connection.objectReferenceValue.name : "none",
                        asset != null ? asset.name : "none", () => connection.objectReferenceValue = asset);
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                else Debug.LogWarning("[DeviceSettings] DeviceMain has no LauncherWindow.");

                var holokit = Find<HoloKitCameraManager>();
                if (holokit != null)
                {
                    // Landscape in Mono too (phone in hand before it goes into the viewer), like the app's default.
                    var so = new SerializedObject(holokit);
                    var orientations = so.FindProperty("m_SupportedMonoScreenOrientations");
                    Change("DeviceMain HoloKitCameraManager mono orientations", (MonoScreenOrientation)orientations.intValue,
                        MonoScreenOrientation.LandscapeLeft, () => orientations.intValue = (int)MonoScreenOrientation.LandscapeLeft);
                    so.ApplyModifiedPropertiesWithoutUndo();
                }

                ApplyHandTracking(scene, Find<HandWindowInput>(), Find<ARCameraManager>());
                ApplyStereoSwitch(scene);

                if (changes > before) EditorSceneManager.SaveScene(scene);
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(scene, true);
            }
        }

        const string HoloKitCanvasPrefab = "Packages/io.holokit.unity-sdk/Assets/Prefabs/HoloKit Default UI Canvas.prefab";

        // HoloKit starts in mono, and only a UI button switches to stereo: add the SDK's default canvas (a Mono/Stereo
        // button and recording; in stereo its buttons hide and HoloKit's stereo bar offers MONO to go back), with an
        // EventSystem on the Input System for its buttons.
        static void ApplyStereoSwitch(Scene scene)
        {
            bool Has<T>() where T : Component => scene.GetRootGameObjects().Any(r => r.GetComponentInChildren<T>(true) != null);

            if (!Has<HoloKit.UI.HoloKitDefaultUICanvas>())
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HoloKitCanvasPrefab);
                if (prefab == null)
                {
                    Debug.LogWarning($"[DeviceSettings] {HoloKitCanvasPrefab} not found: DeviceMain has no way into stereo mode.");
                    return;
                }
                PrefabUtility.InstantiatePrefab(prefab, scene);
                changes++;
                Debug.Log("[DeviceSettings] DeviceMain: added HoloKit Default UI Canvas (Mono/Stereo button)");
            }

            if (!Has<UnityEngine.EventSystems.EventSystem>())
            {
                var eventSystem = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
                SceneManager.MoveGameObjectToScene(eventSystem, scene);
                changes++;
                Debug.Log("[DeviceSettings] DeviceMain: added an EventSystem (Input System UI module)");
            }
        }

        // HoloKit hand tracking as the windows' input: depth for the 3D joints, HoloKit's HandTrackingManager, and
        // HoloKitHandSource feeding HandWindowInput.
        static void ApplyHandTracking(Scene scene, HandWindowInput hands, ARCameraManager arCamera)
        {
            if (hands == null || arCamera == null)
            {
                Debug.LogWarning("[DeviceSettings] DeviceMain needs a HandWindowInput and an AR camera for hand tracking.");
                return;
            }

            if (arCamera.GetComponent<AROcclusionManager>() == null)
            {
                var occlusion = arCamera.gameObject.AddComponent<AROcclusionManager>();
                // LiDAR depth places HoloKit's hand joints in 3D. Depth only: windows aren't hidden behind real objects.
                occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
                occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
                occlusion.requestedHumanDepthMode = HumanSegmentationDepthMode.Disabled;
                occlusion.requestedHumanStencilMode = HumanSegmentationStencilMode.Disabled;
                changes++;
                Debug.Log($"[DeviceSettings] DeviceMain: added AROcclusionManager to {arCamera.name} (environment depth Fastest, no occlusion)");
            }

#if UNITY_IOS
            var tracking = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<HoloKit.iOS.HandTrackingManager>(true)).FirstOrDefault();
            if (tracking == null)
            {
                var go = new GameObject("Hand Tracking");
                SceneManager.MoveGameObjectToScene(go, scene);
                tracking = go.AddComponent<HoloKit.iOS.HandTrackingManager>();
                var trackingSettings = new SerializedObject(tracking);
                trackingSettings.FindProperty("m_HandJointsVisibility").boolValue = false; // debug spheres would show in the headset
                trackingSettings.ApplyModifiedPropertiesWithoutUndo();
                changes++;
                Debug.Log("[DeviceSettings] DeviceMain: added Hand Tracking (HoloKit HandTrackingManager, joints hidden)");
            }

            // Two hands: two-hand stretch and cross-to-hide need both.
            var trackingSettings2 = new SerializedObject(tracking);
            var maxHands = trackingSettings2.FindProperty("m_MaxHandCount");
            Change("DeviceMain HandTrackingManager max hand count (two-hand gestures)", maxHands.intValue, (int)HoloKit.iOS.MaxHandCount.Both,
                () => maxHands.intValue = (int)HoloKit.iOS.MaxHandCount.Both);
            trackingSettings2.ApplyModifiedPropertiesWithoutUndo();

            var source = hands.GetComponent<HoloKitHandSource>();
            if (source == null)
            {
                source = hands.gameObject.AddComponent<HoloKitHandSource>();
                changes++;
                Debug.Log($"[DeviceSettings] DeviceMain: added HoloKitHandSource to {hands.name}");
            }
            var sourceSettings = new SerializedObject(source);
            var trackingField = sourceSettings.FindProperty("handTracking");
            var headField = sourceSettings.FindProperty("head");
            Change("DeviceMain HoloKitHandSource.handTracking", trackingField.objectReferenceValue, tracking, () => trackingField.objectReferenceValue = tracking);
            Change("DeviceMain HoloKitHandSource.head", headField.objectReferenceValue, arCamera.transform, () => headField.objectReferenceValue = arCamera.transform);
            sourceSettings.ApplyModifiedPropertiesWithoutUndo();

            var handsSettings = new SerializedObject(hands);
            var sourceField = handsSettings.FindProperty("handSource");
            Change("DeviceMain HandWindowInput.handSource", sourceField.objectReferenceValue, source, () => sourceField.objectReferenceValue = source);
            handsSettings.ApplyModifiedPropertiesWithoutUndo();
#endif
        }
    }
}
