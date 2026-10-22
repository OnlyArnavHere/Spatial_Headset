using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A switch for HoloKit's <c>ARKitCameraCompatibility</c>, which the SDK adds to every AR camera on iOS 27 (it replaces
    /// the camera background's material with a clone, overwrites its display matrix, the camera's pose and projection). If
    /// the camera feed is black or a flat colour on a device, Settings > Performance > "HoloKit iOS 27 camera fix" off
    /// removes the component at runtime (its OnDestroy gives the background its own material back), which shows whether it
    /// is the cause. On again adds it back. The type is found by name, so there is no compile dependency on the SDK's
    /// iOS-only code; where it doesn't exist (the editor, other iOS versions) this does nothing. Also reports the
    /// component's state for <see cref="DeviceStatusHud"/>.
    /// </summary>
    public class HoloKitCameraFix : MonoBehaviour
    {
        public const string TypeName = "ARKitCameraCompatibility";
        const float CheckEvery = 0.5f;

        static Type type;
        static bool typeSearched;

        /// <summary>One line for the Settings page and the HUD: present, removed or not installed.</summary>
        public static string State { get; private set; } = "-";

        ARCameraManager cameraManager;
        float nextCheck;

        /// <summary>The SDK's compatibility type, or null where it isn't compiled in (found once, by name).</summary>
        public static Type FindType()
        {
            if (typeSearched) return type;
            typeSearched = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var candidate in types)
                {
                    if (candidate == null) continue;
                    if (candidate.Name != TypeName || !typeof(MonoBehaviour).IsAssignableFrom(candidate)) continue;
                    type = candidate;
                    return type;
                }
            }
            return null;
        }

        /// <summary>The compatibility component on <paramref name="camera"/>'s object, or null.</summary>
        public static Component Get(Component camera)
        {
            var t = FindType();
            return t != null && camera != null ? camera.GetComponent(t) : null;
        }

        /// <summary>
        /// The state line, and what to do about it: remove the component when the setting is off, add it back when the setting
        /// turns on and the SDK would have installed it (iOS 27). Returns what to do ("remove", "add" or null).
        /// </summary>
        public static string Decide(bool settingOn, bool present, bool typeExists, bool ios27)
        {
            if (!typeExists) return null;
            if (!settingOn && present) return "remove";
            if (settingOn && !present && ios27) return "add";
            return null;
        }

        static bool IsIos27()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return UnityEngine.iOS.Device.systemVersion.StartsWith("27.");
#else
            return false;
#endif
        }

        void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + CheckEvery;
            if (cameraManager == null) cameraManager = FindFirstObjectByType<ARCameraManager>();
            if (cameraManager == null) { State = "no AR camera"; return; }

            bool settingOn = SettingsStore.Current.performance.holoKitCameraFix;
            var component = Get(cameraManager);
            var action = Decide(settingOn, component != null, FindType() != null, IsIos27());
            if (action == "remove") { Destroy(component); component = null; }
            else if (action == "add") component = cameraManager.gameObject.AddComponent(FindType());
            State = component != null ? "present" : FindType() == null ? "not installed (not iOS)" : settingOn ? "not installed (not iOS 27)" : "removed (setting)";
        }
    }
}
