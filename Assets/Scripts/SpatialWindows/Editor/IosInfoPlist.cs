#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// Adds the Info.plist keys Unity has no Player Setting for, after every iOS build: why the app uses the local
    /// network (iOS asks once before the app can reach the laptop agent on the Wi-Fi), why it recognises speech (voice
    /// typing; the microphone's reason is a Player Setting), and App Transport Security's local-networking exception for
    /// the agent's plain-HTTP WHEP endpoint.
    /// </summary>
    static class IosInfoPlist
    {
        public const string LocalNetworkUsage =
            "Spatial Launcher connects to the laptop agent on your Wi-Fi to show your laptop's windows.";

        [PostProcessBuild(100)]
        static void AddKeys(BuildTarget target, string buildPath)
        {
            if (target != BuildTarget.iOS) return;
            string plistPath = Path.Combine(buildPath, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            var root = plist.root;
            root.SetString("NSLocalNetworkUsageDescription", LocalNetworkUsage);
            root.SetString("NSSpeechRecognitionUsageDescription", DeviceSettings.SpeechRecognitionUsage);
            var ats = root.values.TryGetValue("NSAppTransportSecurity", out var existing) && existing is PlistElementDict dict
                ? dict
                : root.CreateDict("NSAppTransportSecurity");
            ats.SetBoolean("NSAllowsLocalNetworking", true);
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
