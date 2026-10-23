using System.Collections;
using System.Collections.Generic;
using SpatialLauncher.Switcher;
using SpatialLauncher.SpatialWindows.Theme;
using UnityEditor;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test for the app switcher's logic: the overview's selection (opening selects the window after the front
    /// one, presses wrap, a pause switches only when TRIGGER opened it), the quick cycle (F6) walking one snapshot of the
    /// order, and the glyphs for headset windows. Results use a [SwitcherTest] prefix.
    /// </summary>
    static class SwitcherTest
    {
        [MenuItem("Tools/Spatial Windows/Test Switcher")]
        static void Run() => EditorTestRunner.Run("SwitcherTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Switcher", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var s = new SwitcherSelection();
            s.Begin(4, 0f, autoConfirm: true);
            Check(s.Open && s.Selected == 1, "opening selects the window after the front one");
            s.Next(0.5f);
            s.Next(1.0f);
            s.Next(1.5f);
            Check(s.Selected == 0, "presses move on and wrap around");
            Check(!s.Due(1.5f + s.ConfirmDelay - 0.01f) && s.Due(1.5f + s.ConfirmDelay), $"it switches {SwitcherSelection.DefaultConfirmDelay} s after the last press, not before");
            s.Resize(0);
            Check(!s.Due(100f), "nothing to switch to: no switch");
            s.Begin(1, 0f, true);
            Check(s.Selected == 0, "a single window is selected as is");
            s.Begin(3, 0f, autoConfirm: false);
            Check(!s.Due(100f), "opened by pinch, Home or F7 it waits for a pick");
            s.Select(2, 1f);
            Check(s.Selected == 2, "a pick selects");
            s.Resize(2);
            Check(s.Selected == 1, "a list that shrank keeps the selection inside it");
            s.End();
            Check(!s.Open && !s.Due(100f), "closed: nothing happens");

            var quick = new QuickCycle<string>();
            var order = new List<string> { "C", "B", "A" };
            Check(quick.Step(order, 0f) == "B", "F6 brings the previous window forward");
            var after = new List<string> { "B", "C", "A" }; // the focus changed, but the walk keeps its snapshot
            Check(quick.Step(after, 0.5f) == "A", "pressed again soon, it goes further back, not back and forth");
            Check(quick.Step(after, 1.0f) == "C", "...and wraps");
            Check(quick.Step(after, 5f) == "C", "after a pause it starts again from the new order");
            Check(quick.Step(new List<string> { "X" }, 20f) == "X" && quick.Step(new List<string>(), 40f) == null, "one window or none");
            quick.Step(order, 50f);
            Check(quick.Step(new List<string> { "D", "C", "B", "A" }, 50.3f) == "C", "a window opened mid-walk starts a new walk");
            Check(quick.Step(order, 10f) == "B", "a clock that went back starts a new walk");

            Check(SwitcherModel.GlyphFor("Home", false) == UIIcons.Home && SwitcherModel.GlyphFor("Settings", false) == UIIcons.Settings &&
                  SwitcherModel.GlyphFor("Anything", true) == UIIcons.Laptop && SwitcherModel.GlyphFor("Video", false) == UIIcons.Movie,
                  "glyphs for headset windows by title, the laptop glyph for laptop windows");
            Check(AppSwitcher.TriggerHint.Contains("TRIGGER") && AppSwitcher.PinchHint.Contains("Pinch"), "hints say how to use it");
            yield break;
        }
    }
}
