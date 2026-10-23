using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SpatialLauncher.LaptopShell;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test of the Laptop Shell window against a made-up laptop (ShellFixture): the taskbar style for Windows
    /// and the dock style for macOS, real icons and letter tiles, running indicators, hit areas and text sizes; tapping a
    /// bar icon (mouse click) streams, restores, launches or lists windows; holding one lists its windows; window
    /// minimize and close; Pinned / All apps and paging; search by typing, by the virtual keyboard and by voice, with
    /// Enter opening the best match; and the locked panel, whose Allow control asks once while nothing else reaches the
    /// laptop. Results use a [LaptopShellWindowTest] prefix.
    /// </summary>
    static class LaptopShellWindowTest
    {
        const float Settle = 0.3f;

        [MenuItem("Tools/Spatial Windows/Test Laptop Shell Window")]
        static void Run() => EditorTestRunner.Run("LaptopShellWindowTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Laptop Shell Window", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        sealed class Rig
        {
            public SpatialWindow Window;
            public LaptopShellContent Content;
            public ShellFixture.FakeAgent Agent;
            public ShellFixture.FakeSession Session;
            public readonly List<string> Streams = new List<string>();

            public void Settle()
            {
                Agent.Settle();
                Content.Rebuild();
            }
        }

        static Rig Open(WindowManager manager, ShellOs os, ShellLock lockState = ShellLock.None)
        {
            var rig = new Rig { Agent = new ShellFixture.FakeAgent(os) };
            rig.Session = new ShellFixture.FakeSession(rig.Agent.Client) { Lock = lockState };
            rig.Agent.Client.StreamRequested += (id, title) => rig.Streams.Add(id);
            rig.Agent.Client.SetAvailable(lockState == ShellLock.None, os);
            rig.Window = manager.Spawn("Laptop apps", LaptopShellContent.WindowSize);
            rig.Window.SetCurve(0f);
            rig.Content = new GameObject().AddComponent<LaptopShellContent>();
            rig.Content.Init(rig.Window, manager.WindowMaterial, rig.Session);
            for (int i = 0; i < 3; i++) rig.Settle();
            manager.Focus(rig.Window);
            return rig;
        }

        static IEnumerator Steps()
        {
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (manager == null || mouse == null || cam == null)
            {
                Check(false, "scene has a WindowManager, MouseWindowInput and a camera");
                yield break;
            }
            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var opened = new List<SpatialWindow>();
            try
            {
                var win = Open(manager, ShellOs.Windows);
                opened.Add(win.Window);
                yield return Settle;
                yield return WindowsStyle(win, mouse, cam);
                yield return Search(win, mouse, cam);
                yield return Grid(win);
                yield return Locked(manager, mouse, cam, opened);
                yield return MacStyle(manager, opened);

                manager.Close(win.Window);
                yield return Settle;
                Check(win.Session.Disposed && win.Agent.Client.Running.Count == 0, "closing the window ends its session use and forgets the lists");
            }
            finally
            {
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                WindowTextField.BlurAll();
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static void Click(MouseWindowInput mouse, Camera cam, Component control)
        {
            var point = control.transform.position;
            var ray = cam.ScreenPointToRay(cam.WorldToScreenPoint(point));
            string hit = Physics.Raycast(ray, out var h, 20f) ? h.collider.name : "nothing";
            if (hit != control.name)
                Debug.Log($"[LaptopShellWindowTest] Click on {control.name} at viewport {cam.WorldToViewportPoint(point)}: the ray hits {hit} first");
            LaptopStreamTest.Click(mouse, cam, point);
        }

        static void Tap(WindowControl control)
        {
            control.Press(control.transform.position);
            control.Release(true);
        }

        static IEnumerator WindowsStyle(Rig rig, MouseWindowInput mouse, Camera cam)
        {
            var c = rig.Content;
            var slots = c.BarSlots;
            Check(c.Style.Os == ShellOs.Windows && c.Shown == LaptopShellContent.Panel.Grid && c.HeaderText == "Pinned", "Windows: the Start-style panel opens on Pinned");
            Check(slots.Count > 1 && slots[0].IsAppsButton, $"the taskbar starts with the apps button ({slots.Count} slots)");
            var dock = rig.Agent.Client.DockItems();
            Check(slots.Skip(1).Select(s => s.App.Id).SequenceEqual(dock.Select(a => a.Id)), "then pins in order and the other running apps");
            Check(Mathf.Abs(c.Bar.localScale.x - (c.Window.Size.x - WindowContent.Padding * 2f)) < 1e-4f, "the taskbar spans the window");
            Check(!c.Divider.gameObject.activeSelf, "no divider on the taskbar");
            var xs = slots.Select(s => s.Button.transform.localPosition.x).ToList();
            bool ordered = true, apart = true;
            for (int i = 1; i < xs.Count; i++)
            {
                ordered &= xs[i] > xs[i - 1];
                float gap = (xs[i] - xs[i - 1]) - slots[i].Button.transform.localScale.x;
                apart &= gap >= UITheme.PtToMetres(UITheme.MinHitGapMm) - 1e-4f;
            }
            Check(ordered && apart, "slots run left to right with at least 8 mm between hit areas");
            Check(Mathf.Abs((xs[0] + xs[xs.Count - 1]) * 0.5f) < 1e-4f, "the icons are centred");

            var browser = slots.First(s => !s.IsAppsButton && s.App.Name == "Browser");
            var store = slots.First(s => !s.IsAppsButton && s.App.Name == "Store");
            var terminal = slots.First(s => !s.IsAppsButton && s.App.Name == "Terminal");
            Check(browser.Indicator.gameObject.activeSelf && browser.Focused && browser.Indicator.localScale.x > slots.First(s => s.App?.Name == "Code Editor").Indicator.localScale.x,
                  "running apps have a dash; the one in front a longer one");
            Check(!store.Indicator.gameObject.activeSelf, "a pin that isn't running has no dash");
            Check(browser.Icon.ShowsTexture && !terminal.Icon.ShowsTexture && terminal.Icon.Letter.gameObject.activeSelf && terminal.Icon.Letter.text == "T",
                  "real icons where the laptop sent one, a letter tile where it didn't");

            foreach (var control in c.Controls.Where(k => k.gameObject.activeInHierarchy && k != c.SearchField))
            {
                var size = control.transform.lossyScale;
                Check(Mathf.Min(size.x, size.y) >= UITheme.PtToMetres(UITheme.MinHitTargetMm) - 1e-3f, $"{control.name}: hit area at least 44 mm ({size.x:0.000} x {size.y:0.000})");
            }
            float minFont = UITheme.PtToMetres(UITheme.MinTextPt) * ThemeApplier.TmpWorldScale;
            var small = c.GetComponentsInChildren<TextMeshPro>(false).Where(t => t.fontSize < minFont - 1e-4f).Select(t => t.name).ToList();
            Check(small.Count == 0, $"no text under 10 pt ({string.Join(", ", small)})");

            // Taps: several windows list them; one minimized window is restored then streamed; a pin that isn't running launches.
            Click(mouse, cam, browser.Button);
            yield return Settle;
            Check(c.Shown == LaptopShellContent.Panel.Windows && c.WindowRows.Count == 2 && c.HeaderText == "Browser",
                  "clicking an app with two windows lists them");
            rig.Settle();
            Tap(c.WindowRows[1].Toggle);
            Check(rig.Agent.Of("window.minimize").Count == 1, "a window row's minimize asks the laptop");
            Tap(c.WindowRows[0].Close);
            Check(rig.Agent.Of("window.close").Count == 1, "...and its close");
            rig.Settle();
            Check(c.Shown == LaptopShellContent.Panel.Windows && c.WindowRows.Count == 1 && c.WindowRows[0].ToggleGlyph.text == UIIcons.OpenInFull,
                  "the list follows: one window left, minimized, so its button restores");
            Tap(c.WindowRows[0].Open);
            rig.Settle();
            Check(rig.Agent.Of("window.restore").Count == 1 && rig.Streams.Count == 1 && c.Shown == LaptopShellContent.Panel.Grid,
                  "opening a minimized window restores it, streams it and goes back");

            rig.Streams.Clear();
            var notes = c.BarSlots.First(s => !s.IsAppsButton && s.App.Name == "Notes");
            Click(mouse, cam, notes.Button);
            yield return Settle;
            rig.Settle();
            Check(rig.Agent.Of("window.restore").Count == 2 && rig.Streams.Count == 1, "clicking an app with one minimized window restores and streams it");

            store = c.BarSlots.First(s => !s.IsAppsButton && s.App.Name == "Store");
            Click(mouse, cam, store.Button);
            yield return Settle;
            rig.Settle();
            Check(rig.Agent.Of("apps.launch").Count == 1 && rig.Streams.Count == 2, "clicking a pin that isn't running launches it and streams its new window");

            // Holding a single-window app lists its windows instead of opening it.
            rig.Settle();
            var code = c.BarSlots.First(s => !s.IsAppsButton && s.App.Name == "Code Editor");
            int streams = rig.Streams.Count;
            var screen = cam.WorldToScreenPoint(code.Button.transform.position);
            mouse.PressAt(screen);
            yield return ShellIconButton.HoldSeconds + 0.25f;
            mouse.ReleaseAt(screen);
            yield return Settle;
            Check(c.Shown == LaptopShellContent.Panel.Windows && c.WindowRows.Count == 1 && rig.Streams.Count == streams,
                  "holding an icon lists its windows and opens nothing");
            Tap(c.ToggleButton);
            Check(c.Shown == LaptopShellContent.Panel.Grid, "Back returns to the apps");
        }

        static IEnumerator Search(Rig rig, MouseWindowInput mouse, Camera cam)
        {
            var c = rig.Content;
            rig.Streams.Clear();
            c.SearchField.Focus();
            c.SearchField.SetText("no");
            rig.Settle();
            Check(c.Shown == LaptopShellContent.Panel.Results && c.HeaderText == "Results" && c.ResultRows.Count > 0, $"typing shows results ({c.ResultRows.Count})");
            Check(c.ResultRows[0].Detail.text.StartsWith(c.Style.TopResultTitle), $"the first is marked \"{c.Style.TopResultTitle}\"");
            Check(rig.Agent.Of("search.query").Last()["q"] as string == "no", "the query went to the laptop");

            var keyboard = Object.FindFirstObjectByType<RemoteKeyboard>();
            if (keyboard != null)
            {
                keyboard.Send(new[] { KeyStroke.Text("tes") });
                rig.Settle();
                Check(c.SearchField.Text == "notes" && c.ResultRows.Count > 0 && c.ResultRows[0].Result.Name == "Notes",
                      "the virtual keyboard types into the search");
            }
            else Check(false, "the scene has a RemoteKeyboard");

            var voice = VoiceTyping.Current;
            if (voice != null)
            {
                c.SearchField.SetText("");
                c.SearchField.Focus();
                voice.Deliver("files");
                rig.Settle();
                Check(c.SearchField.Text == "files" && c.ResultRows.Count > 0 && c.ResultRows[0].Result.Name == "Files", "voice typing types into the search");
            }
            else Debug.Log("[LaptopShellWindowTest] No VoiceTyping in this scene; voice skipped.");

            c.SearchField.SetText("project");
            rig.Settle();
            Check(c.ResultRows.Count == 1 && c.ResultRows[0].Result.Kind == ShellResultKind.Window, "a window's title is found too");
            Click(mouse, cam, c.ResultRows[0].Button);
            yield return Settle;
            Check(rig.Streams.Count == 1, "clicking a window result streams that window");

            c.SearchField.Focus();
            c.SearchField.SetText("calc");
            rig.Settle();
            int launches = rig.Agent.Of("apps.launch").Count;
            keyboard?.Send(new[] { KeyStroke.Down("Enter"), KeyStroke.Up("Enter") });
            rig.Settle();
            Check(rig.Agent.Of("apps.launch").Count == launches + 1 && WindowTextField.Focused == null, "Enter opens the best match (launching it)");

            c.SearchField.SetText("zzzz");
            rig.Settle();
            Check(c.ResultRows.Count == 0 && c.MessageText.Contains("zzzz"), "nothing found says so");
            c.ShowStart();
            rig.Settle();
            Check(c.Shown == LaptopShellContent.Panel.Grid && c.SearchField.Text.Length == 0 && WindowTextField.Focused == c.SearchField,
                  "the apps button clears the search and gets ready to type");
            WindowTextField.BlurAll();
        }

        static IEnumerator Grid(Rig rig)
        {
            var c = rig.Content;
            Check(c.GridTiles.Count == rig.Agent.Client.Pinned.Count, "Pinned shows the pins");
            Tap(c.ToggleButton);
            rig.Settle();
            Check(c.ShowingAllApps && c.HeaderText == "All apps" && c.PageCount > 1 && c.GridTiles.Count == c.Style.GridColumns * c.Style.GridRows,
                  $"All apps fills the grid and pages ({c.PageCount} pages)");
            Check(!c.PreviousButton.Interactable && c.NextButton.Interactable, "on the first page only Next works");
            string first = c.GridTiles[0].App.Name;
            Tap(c.NextButton);
            rig.Settle();
            Check(c.Page == 1 && c.GridTiles[0].App.Name != first, "Next shows the next page");
            var idle = c.GridTiles.First(t => rig.Agent.App(t.App.Name).Windows.Count == 0);
            int launches = rig.Agent.Of("apps.launch").Count;
            Tap(idle.Button);
            rig.Settle();
            Check(rig.Agent.Of("apps.launch").Count == launches + 1, $"tapping {idle.App.Name} in the grid launches it");
            Tap(c.ToggleButton);
            rig.Settle();
            Check(!c.ShowingAllApps && c.HeaderText == "Pinned" && c.Page == 0, "Pinned again");
            yield break;
        }

        static IEnumerator Locked(WindowManager manager, MouseWindowInput mouse, Camera cam, List<SpatialWindow> opened)
        {
            var rig = Open(manager, ShellOs.Windows, ShellLock.NeedsControl);
            opened.Add(rig.Window);
            yield return Settle;
            var c = rig.Content;
            Check(c.Shown == LaptopShellContent.Panel.Locked && c.AllowButton.gameObject.activeSelf && c.AllowButton.Interactable,
                  "without control the panel offers Allow control");
            Check(c.MessageText.Contains("Studio laptop") && c.MessageText.Contains("Allow control"), $"and says why (\"{c.MessageText}\")");
            Check(!c.SearchField.Interactable && c.BarSlots.All(s => !s.Button.Interactable), "search and the bar are off");
            int requests = rig.Agent.Requests.Count;
            Tap(c.BarSlots[0].Button);
            c.SearchField.SetText("x");
            rig.Settle();
            Check(rig.Agent.Requests.Count == requests, "nothing reaches the laptop while locked");
            c.SearchField.SetText("");
            Click(mouse, cam, c.AllowButton);
            yield return Settle;
            Check(rig.Session.ControlRequests == 1 && c.Lock == ShellLock.Asking && !c.AllowButton.gameObject.activeSelf && c.MessageText.Contains("Waiting"),
                  "Allow control asks once and then waits");
            rig.Session.Lock = ShellLock.None;
            rig.Agent.Client.SetAvailable(true, ShellOs.Windows);
            yield return Settle;
            rig.Settle();
            Check(c.Shown == LaptopShellContent.Panel.Grid && c.GridTiles.Count > 0 && c.SearchField.Interactable, "once control is granted the apps appear");
            foreach (var reason in new[] { ShellLock.Stopped, ShellLock.CastOnly, ShellLock.NotOffered, ShellLock.Ended })
            {
                rig.Session.Lock = reason;
                yield return null;
                yield return null;
                Check(c.Shown == LaptopShellContent.Panel.Locked && !c.AllowButton.gameObject.activeSelf && c.MessageText == ShellLockText.Describe(reason, "Studio laptop"),
                      $"{reason}: locked, explained, nothing to ask");
            }
            manager.Close(rig.Window);
        }

        static IEnumerator MacStyle(WindowManager manager, List<SpatialWindow> opened)
        {
            var rig = Open(manager, ShellOs.MacOS);
            opened.Add(rig.Window);
            yield return Settle;
            var c = rig.Content;
            Check(c.Style.Os == ShellOs.MacOS && c.HeaderText == "Applications" && c.GridTiles.Count == 21, "macOS: Applications, seven by three");
            Check(c.BarSlots.All(s => !s.IsAppsButton), "the dock has no apps button");
            float content = c.Window.Size.x - WindowContent.Padding * 2f;
            Check(c.Bar.localScale.x < content - 0.05f, $"the dock fits its icons ({c.Bar.localScale.x:0.00} of {content:0.00} m)");
            int firstUnpinned = c.BarSlots.ToList().FindIndex(s => !s.App.Pinned);
            Check(c.Divider.gameObject.activeSelf && firstUnpinned > 0 &&
                  c.Divider.localPosition.x > c.BarSlots[firstUnpinned - 1].Button.transform.localPosition.x &&
                  c.Divider.localPosition.x < c.BarSlots[firstUnpinned].Button.transform.localPosition.x,
                  "a divider sits between the pinned apps and the others");
            var running = c.BarSlots.Where(s => s.App.Running).ToList();
            Check(running.Count > 0 && running.All(s => Mathf.Abs(s.Indicator.localScale.x - s.Indicator.localScale.y) < 1e-5f), "running apps get round dots");
            Check(c.BarSlots.First().App.Name == "Files", "the file manager comes first, as pinned");
            Check(!c.ToggleButton.gameObject.activeSelf, "no Pinned / All apps toggle");
            manager.Close(rig.Window);
        }
    }
}
