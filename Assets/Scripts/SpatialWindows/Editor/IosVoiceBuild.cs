#if UNITY_IOS
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// After every iOS build, makes the Xcode project ready for voice typing: links Speech.framework (and AVFoundation)
    /// into UnityFramework and compiles Assets/Plugins/iOS/SpatialSpeech.mm with ARC, which it needs.
    /// </summary>
    static class IosVoiceBuild
    {
        const string PluginPath = "Libraries/Plugins/iOS/SpatialSpeech.mm";

        [PostProcessBuild(110)]
        static void Configure(BuildTarget target, string buildPath)
        {
            if (target != BuildTarget.iOS) return;
            string projectPath = PBXProject.GetPBXProjectPath(buildPath);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            string framework = project.GetUnityFrameworkTargetGuid();
            project.AddFrameworkToProject(framework, "Speech.framework", false);
            project.AddFrameworkToProject(framework, "AVFoundation.framework", false);

            string file = project.FindFileGuidByProjectPath(PluginPath);
            if (file != null) project.SetCompileFlagsForFile(framework, file, new List<string> { "-fobjc-arc" });
            else UnityEngine.Debug.LogWarning($"[VoiceBuild] {PluginPath} isn't in the Xcode project: voice typing won't build.");

            project.WriteToFile(projectPath);
        }
    }
}
#endif
