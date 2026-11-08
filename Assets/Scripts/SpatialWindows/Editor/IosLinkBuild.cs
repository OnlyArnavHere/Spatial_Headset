#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// After every iOS build, links into UnityFramework what the laptop link's native code needs: Security.framework for
    /// the Keychain identity (SpatialLink.mm), VideoToolbox, CoreMedia, CoreVideo and Metal for windows over the USB
    /// cable (SpatialVideoDecoder.mm), and Vision for reading the laptop's pairing QR code (SpatialQrScanner.mm).
    /// CommonCrypto and dns_sd are part of libSystem.
    /// </summary>
    static class IosLinkBuild
    {
        [PostProcessBuild(120)]
        static void Configure(BuildTarget target, string buildPath)
        {
            if (target != BuildTarget.iOS) return;
            string projectPath = PBXProject.GetPBXProjectPath(buildPath);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            string framework = project.GetUnityFrameworkTargetGuid();
            foreach (string name in new[] { "Security", "VideoToolbox", "CoreMedia", "CoreVideo", "Metal", "Vision" })
                project.AddFrameworkToProject(framework, name + ".framework", false);
            project.WriteToFile(projectPath);
        }
    }
}
#endif
