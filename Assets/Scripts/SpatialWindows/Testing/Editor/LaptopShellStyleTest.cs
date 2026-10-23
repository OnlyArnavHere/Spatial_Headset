using System.Collections;
using SpatialLauncher.LaptopShell;
using SpatialLauncher.SpatialWindows.Theme;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test for the Laptop Shell's pieces that need no window: the Windows and macOS styles (taskbar or dock,
    /// dash or dot, Start-style box or capsule search, grid size, hit areas), the locked texts, letter tiles, a bar icon
    /// telling a tap from a hold, and the virtual keyboard typing into a focused text field (the shell's search).
    /// Results use a [LaptopShellStyleTest] prefix.
    /// </summary>
    static class LaptopShellStyleTest
    {
        [MenuItem("Tools/Spatial Windows/Test Laptop Shell Style")]
        static void Run() => EditorTestRunner.Run("LaptopShellStyleTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Laptop Shell Style", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            Styles();
            Locks();
            Letters();
            TapAndHold();
            KeyboardIntoField();
            yield break;
        }

        static void Styles()
        {
            var win = ShellStyle.For(ShellOs.Windows);
            var mac = ShellStyle.For(ShellOs.MacOS);
            Check(win.BarFullWidth && win.AppsButtonInBar && win.Indicator == RunningIndicator.Dash && !win.DividerBeforeUnpinned,
                  "Windows: a full-width taskbar with an apps button and dashes, no divider");
            Check(!mac.BarFullWidth && !mac.AppsButtonInBar && mac.Indicator == RunningIndicator.Dot && mac.DividerBeforeUnpinned,
                  "macOS: a dock sized to its icons with dots and a divider before unpinned apps");
            Check(!win.SearchCapsule && win.SearchWidth == 1f && mac.SearchCapsule && mac.SearchWidth < 1f && mac.SearchText == TextRole.Title,
                  "Windows search is a full-width rounded box; macOS search is a centred capsule with large text");
            Check(win.PinnedFirst && win.GridTitle == "Pinned" && win.AllAppsTitle == "All apps" && !mac.PinnedFirst && mac.GridTitle == "Applications",
                  "Windows shows Pinned with All apps a tap away; macOS shows Applications");
            Check(win.GridColumns * win.GridRows <= LaptopShellContent.MaxGridTiles && mac.GridColumns * mac.GridRows <= LaptopShellContent.MaxGridTiles,
                  "both grids fit the tile pool");
            Check(ShellStyle.For(ShellOs.Linux).BarFullWidth && ShellStyle.For(ShellOs.Unknown).GridTitle == "Pinned", "other systems get the Windows layout");
            foreach (var style in new[] { win, mac })
            {
                string os = style.Os.ToString();
                Check(style.BarSlot - LaptopShellContent.Gap >= UITheme.PtToMetres(UITheme.MinHitTargetMm) - 1e-4f &&
                      style.BarHeight - LaptopShellContent.Gap >= UITheme.PtToMetres(UITheme.MinHitTargetMm) - 1e-4f,
                      $"{os}: a bar slot's hit area is at least 44 mm ({style.BarSlot - LaptopShellContent.Gap:0.000} m)");
                Check(LaptopShellContent.Gap >= UITheme.PtToMetres(UITheme.MinHitGapMm) - 1e-4f, $"{os}: hit areas are 8 mm apart");
                Check(style.SearchHeight >= LaptopShellContent.RoundButton, $"{os}: the mic and keyboard buttons fit in the search");
                Check(style.IndicatorSize(true).x >= style.IndicatorSize(false).x, $"{os}: the app in front never has a smaller indicator");
                Check(!style.SearchPlaceholder.Contains("Spotlight") && !style.GridTitle.Contains("Launchpad") && !style.SearchPlaceholder.Contains("Start"),
                      $"{os}: no OS product names in the chrome");
            }
            Check(win.IndicatorColor(true) == UITheme.Accent && win.IndicatorColor(false) != UITheme.Accent && mac.IndicatorColor(true) == mac.IndicatorColor(false),
                  "the taskbar marks the app in front in accent; the dock's dots are all alike");
        }

        static void Locks()
        {
            foreach (ShellLock reason in System.Enum.GetValues(typeof(ShellLock)))
            {
                string text = ShellLockText.Describe(reason, "Studio laptop");
                Check(reason == ShellLock.None ? text.Length == 0 : text.Length > 0 && text.Length < 200, $"{reason} has {(reason == ShellLock.None ? "no" : "a short")} text");
                Check(ShellLockText.CanAsk(reason) == (reason == ShellLock.NeedsControl), $"{reason}: Allow control only when control can be asked for");
            }
            Check(ShellLockText.Describe(ShellLock.NeedsControl, null).Contains("the laptop"), "no name: \"the laptop\"");
        }

        static void Letters()
        {
            foreach (var name in new[] { "Files", "Browser", "Mail", "Notes", "Terminal", "3D Studio", "", null, "zzz", "Ω Omega" })
            {
                var color = LaptopShellContent.LetterColor(name);
                Check(UITheme.ContrastRatio(Color.white, color) >= LauncherContent.MinTileContrast, $"white on {name ?? "null"}'s tile reaches 4.5:1");
            }
            Check(LaptopShellContent.LetterColor("Mail") == LaptopShellContent.LetterColor("mail"), "the colour doesn't depend on case");
            Check(LaptopShellContent.Initial("files") == "F" && LaptopShellContent.Initial("  3D Studio") == "3" && LaptopShellContent.Initial("--") == "?" &&
                  LaptopShellContent.Initial(null) == "?", "initials");
        }

        static void TapAndHold()
        {
            var go = new GameObject("HoldTest");
            try
            {
                var button = go.AddComponent<ShellIconButton>();
                float now = 0f;
                button.Clock = () => now;
                int taps = 0, holds = 0;
                button.Clicked += () => taps++;
                button.Held += () => holds++;

                button.Press(Vector3.zero);
                now = 0.2f;
                button.CheckHold();
                button.Release(true);
                Check(taps == 1 && holds == 0, "a short press is a tap");

                button.Press(Vector3.zero);
                now = 0.2f + ShellIconButton.HoldSeconds + 0.01f;
                button.CheckHold();
                Check(holds == 1 && button.IsPressing, "holding fires while still held");
                button.CheckHold();
                button.Release(true);
                Check(holds == 1 && taps == 1, "...once, and the release after a hold isn't also a tap");

                button.Press(Vector3.zero);
                button.Release(false);
                Check(taps == 1, "releasing away from the icon is no tap");
                button.Interactable = false;
                button.Press(Vector3.zero);
                now += 2f;
                button.CheckHold();
                button.Release(true);
                Check(taps == 1 && holds == 1, "a disabled icon neither taps nor holds");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static void KeyboardIntoField()
        {
            var go = new GameObject("KeyboardTest");
            try
            {
                var field = go.AddComponent<WindowTextField>();
                var keyboard = go.AddComponent<RemoteKeyboard>();
                int submitted = 0;
                field.Submitted += _ => submitted++;
                Check(!keyboard.Send(new[] { KeyStroke.Text("x") }) && field.Text.Length == 0, "no focused field and no laptop window: nothing is typed");
                field.Focus();
                keyboard.Send(new[] { KeyStroke.Text("not") });
                keyboard.Send(new[] { KeyStroke.Text("e") });
                Check(field.Text == "note", "with the field focused, the virtual keyboard types into it");
                keyboard.Send(new[] { KeyStroke.Down("Backspace"), KeyStroke.Up("Backspace") });
                Check(field.Text == "not", "Backspace deletes");
                keyboard.Send(new[] { KeyStroke.Down("Ctrl"), KeyStroke.Down("A"), KeyStroke.Up("A"), KeyStroke.Up("Ctrl"), KeyStroke.Down("Left"), KeyStroke.Up("Left") });
                Check(field.Text == "not", "shortcuts and arrows don't type anything");
                keyboard.Send(new[] { KeyStroke.Down("Enter"), KeyStroke.Up("Enter"), KeyStroke.Text("z") });
                Check(submitted == 1 && WindowTextField.Focused == null && field.Text == "not", "Enter submits and leaves the field; what follows isn't for it");
                field.Focus();
                keyboard.Send(new[] { KeyStroke.Down("Escape") });
                Check(WindowTextField.Focused == null && submitted == 1, "Esc leaves without submitting");
                field.Focus();
                field.Interactable = false;
                Check(WindowTextField.Focused == null, "a disabled field gives up focus");
            }
            finally
            {
                WindowTextField.BlurAll();
                Object.DestroyImmediate(go);
            }
        }
    }
}
