using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using GalleryState = SpatialLauncher.SpatialWindows.Editor.GalleryState;
using UIGalleryShots = SpatialLauncher.SpatialWindows.Editor.UIGalleryShots;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test for the UI gallery's pure logic (<see cref="UIGalleryShots"/>): every window and state is listed
    /// where it applies, file names are <c>&lt;window&gt;_&lt;state&gt;.png</c> and unique (also next to the overview), the
    /// Disabled shots join only when asked for, and a label becomes a safe folder name. Results use a [UIGalleryTest] prefix.
    /// </summary>
    static class UIGalleryTest
    {
        [MenuItem("Tools/Spatial Windows/Test UI Gallery")]
        static void Run() => EditorTestRunner.Run("UIGalleryTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test UI Gallery", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var shots = UIGalleryShots.All();

            // Every window appears, in the order of the list, and the nine required types are all there.
            foreach (var id in new[] { "home", "image", "video", "remote_stream", "laptop_picker", "pairing_code", "settings", "consent", "keyboard" })
                Check(System.Array.IndexOf(UIGalleryShots.Windows, id) >= 0, $"{id} is in the window list");
            Check(UIGalleryShots.Windows.Length == 9, "nine window types");

            // Every window has an idle and a focused shot; hover and pressed only where it has controls.
            foreach (var id in UIGalleryShots.Windows)
            {
                var states = UIGalleryShots.StatesFor(id);
                Check(states.Contains(GalleryState.Idle) && states.Contains(GalleryState.Focused), $"{id}: idle and focused shots");
                bool hasHover = states.Contains(GalleryState.Hover), hasPressed = states.Contains(GalleryState.Pressed);
                Check(hasHover == UIGalleryShots.HasControls(id) && hasPressed == hasHover, $"{id}: hover and pressed only with controls");
            }
            Check(!UIGalleryShots.HasControls("image") && UIGalleryShots.HasControls("home") && UIGalleryShots.HasControls("keyboard"),
                "controls: the image has none, home and keyboard do");

            // Names are the documented pattern and unique, overview included.
            Check(UIGalleryShots.FileName("home", GalleryState.Hover) == "home_hover.png", "home hover is home_hover.png");
            Check(UIGalleryShots.FileName("remote_stream", GalleryState.Idle) == "remote_stream_idle.png", "remote stream idle is remote_stream_idle.png");
            Check(UIGalleryShots.OverviewFileName == "overview.png", "the overview is overview.png");
            var names = new HashSet<string> { UIGalleryShots.OverviewFileName };
            bool unique = true, pattern = true;
            foreach (var shot in shots)
            {
                unique &= names.Add(shot.FileName);
                pattern &= shot.FileName == shot.Window + "_" + shot.State.ToString().ToLowerInvariant() + ".png";
            }
            Check(unique, $"{shots.Count} shot names are unique and none is the overview");
            Check(pattern, "every name is <window>_<state>.png");

            // Disabled is left out until controls have a look for it, and ready when they do.
            Check(!UIGalleryShots.DisabledSupported, "disabled is off until the controls have a disabled look");
            bool noDisabled = true;
            foreach (var shot in shots) noDisabled &= shot.State != GalleryState.Disabled;
            Check(noDisabled, "no disabled shots by default");
            var withDisabled = UIGalleryShots.All(true);
            int disabled = 0;
            foreach (var shot in withDisabled) if (shot.State == GalleryState.Disabled) disabled++;
            Check(disabled > 0 && withDisabled.Count == shots.Count + disabled, $"asked for, {disabled} disabled shots join the list");
            Check(UIGalleryShots.FileName("home", GalleryState.Disabled) == "home_disabled.png", "home disabled is home_disabled.png");

            // Folder labels.
            Check(UIGalleryShots.SanitizeLabel("before") == "before" && UIGalleryShots.SanitizeLabel("latest") == "latest", "plain labels stay");
            Check(UIGalleryShots.SanitizeLabel("My Run 2") == "My_Run_2", "spaces become underscores");
            Check(UIGalleryShots.SanitizeLabel("../..\\x") == "______x", "path separators and dots can't escape the folder");
            Check(UIGalleryShots.SanitizeLabel("a-b_c") == "a-b_c", "dash and underscore stay");
            Check(UIGalleryShots.SanitizeLabel("") == "latest" && UIGalleryShots.SanitizeLabel("   ") == "latest" && UIGalleryShots.SanitizeLabel(null) == "latest",
                "a blank label is latest");
            yield break;
        }
    }
}
