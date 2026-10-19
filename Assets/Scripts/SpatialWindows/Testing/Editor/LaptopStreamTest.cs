using System.Collections;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test of the Laptop picker's window UI, without a laptop: the Laptop icon opens the picker in list mode
    /// (paired laptops, a Pair button, no address, port or token fields). The link itself (pairing, session,
    /// video, input) is covered by the LaptopLink tests and the laptop agent's own. Results use a [LaptopTest] prefix.
    /// </summary>
    static class LaptopStreamTest
    {
        const float Settle = 0.7f;

        [MenuItem("Tools/Spatial Windows/Test Laptop Picker")]
        static void Run() => EditorTestRunner.Run("LaptopTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Laptop Picker", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (launcher == null || manager == null || mouse == null || cam == null)
            {
                Check(false, "scene has a LauncherWindow, WindowManager, MouseWindowInput and a camera");
                yield break;
            }

            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var pickerWindow = launcher.OpenLaptopWindow();
            try
            {
                yield return Settle;
                var picker = pickerWindow != null ? pickerWindow.GetComponentInChildren<LaptopPickerContent>() : null;
                Check(picker != null, "the Laptop icon opens the picker (LauncherWindow > Laptop Streaming is WebRTC)");
                if (picker == null) yield break;

                Check(picker.Mode == LaptopPickerContent.PickerMode.List && picker.PairButton != null && picker.MoreButton != null,
                    "the picker starts in list mode with a Pair button");
                Check(pickerWindow.transform.Find("HostField") == null && picker.transform.Find("HostField") == null &&
                      picker.transform.Find("TokenField") == null && picker.transform.Find("ListButton") == null,
                    "and has no address, port or token fields and no List button");
                Check(picker.EntryButtons.Count == picker.PageTargets.Count && picker.PageTargets.Count <= LaptopPickerContent.RowsPerPage,
                    $"the entries on the page match the rows ({picker.EntryButtons.Count})");
                Check(picker.StatusLine.Length > 0, $"the status line says what to do (\"{picker.StatusLine}\")");
            }
            finally
            {
                if (pickerWindow != null) manager.Close(pickerWindow);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        internal static void Click(MouseWindowInput mouse, Camera cam, Vector3 worldPoint)
        {
            Vector3 screen = cam.WorldToScreenPoint(worldPoint);
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
        }
    }
}
