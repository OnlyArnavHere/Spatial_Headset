#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// After every iOS build, links Security.framework into UnityFramework for the laptop link's Keychain identity
    /// (SpatialLink.mm). CommonCrypto and dns_sd are part of libSystem.
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
            project.AddFrameworkToProject(framework, "Security.framework", false);
            project.WriteToFile(projectPath);
        }
    }
}
#endif
