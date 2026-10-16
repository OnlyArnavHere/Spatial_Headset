using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// Compiles the scripts as the iOS player sees them (UNITY_IOS without UNITY_EDITOR), which the editor never does:
    /// device-only code such as <c>IosSpeechRecognizer</c> and <see cref="DeviceStatus"/>'s native calls would
    /// otherwise only show its errors on a Mac. Tools > Spatial Windows > Compile iOS Player Scripts; results use a
    /// [PlayerScripts] prefix. Native code (.mm) still needs Xcode.
    /// </summary>
    static class PlayerScriptsCheck
    {
        [MenuItem("Tools/Spatial Windows/Compile iOS Player Scripts")]
        static void Run()
        {
            string output = Path.Combine("Temp", "PlayerScriptsCheck");
            var settings = new ScriptCompilationSettings
            {
                target = BuildTarget.iOS,
                group = BuildTargetGroup.iOS,
                options = ScriptCompilationOptions.None,
            };
            var result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
            bool ours = result.assemblies.Any(a => a == "Assembly-CSharp.dll");
            if (ours) Debug.Log($"[PlayerScripts] PASS: iOS player scripts compile ({result.assemblies.Count} assemblies, Assembly-CSharp included)");
            else Debug.LogError($"[PlayerScripts] FAIL: Assembly-CSharp didn't compile for the iOS player; see the errors above " +
                                $"({result.assemblies.Count} assemblies built)");
        }
    }
}
