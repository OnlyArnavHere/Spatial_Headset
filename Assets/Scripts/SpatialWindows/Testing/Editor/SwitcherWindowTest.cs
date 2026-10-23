using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SpatialLauncher.LaptopShell;
using SpatialLauncher.Switcher;
using SpatialLauncher.SpatialWindows.Theme;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test of the app switcher with real windows and a made-up laptop (ShellFixture): the overview lists the
    /// headset's windows most recent first, then the laptop's windows that aren't streamed yet; a click (pinch) on a card
    /// switches; TRIGGER in Switcher mode opens it, steps it and switches after a pause; TRIGGER in Zoom mode still zooms
    /// and is ALLOW while the consent prompt asks; a laptop card opens that window as a stream; F6's quick switch; showing
    /// hidden windows; the pinch-and-hold gesture. Results use a [SwitcherWindowTest] prefix.
    /// </summary>
    static class SwitcherWindowTest
    {
        const float Settle = 0.3f;

        [MenuItem("Tools/Spatial Windows/Test Switcher Window")]
        static void Run() => EditorTestRunner.Run("SwitcherWindowTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Switcher Window", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var switcher = Object.FindFirstObjectByType<AppSwitcher>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (manager == null || switcher == null || mouse == null || cam == null)
            {
                Check(false, "scene has a WindowManager, an AppSwitcher (added by the launcher), MouseWindowInput and a camera");
                yield break;
            }
            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var opened = new List<SpatialWindow>();
            float now = Time.unscaledTime + 1000f;
            var realClock = switcher.Clock;
            switcher.Clock = () => now;
            bool triggerSetting = SettingsStore.Current.display.triggerSwitcher;
            try
            {
                // A laptop with its shell open, and three headset windows: A, B, then C in front.
                var agent = new ShellFixture.FakeAgent(ShellOs.Windows);
                var streams = new List<string>();
                agent.Client.StreamRequested += (id, title) => streams.Add(id);
                agent.Client.SetAvailable(true, ShellOs.Windows);
                var shellWindow = manager.Spawn("Laptop apps", LaptopShellContent.WindowSize);
                opened.Add(shellWindow);
                new GameObject().AddComponent<LaptopShellContent>().Init(shellWindow, manager.WindowMaterial, new ShellFixture.FakeSession(agent.Client));
                agent.Settle();
                var a = manager.Spawn("Window A");
                var b = manager.Spawn("Window B");
                var c = manager.Spawn("Window C");
                opened.AddRange(new[] { a, b, c });
                yield return Settle;

                // Overview by pinch / Home / F7: no auto switch.
                Check(switcher.OpenOverview() && switcher.IsOpen, "the overview opens");
                yield return Settle;
                var entries = switcher.Entries;
                Check(entries.Count >= 4 && entries[0].Window == c && entries[1].Window == b && entries[2].Window == a && entries[3].Window == shellWindow,
                      "headset windows come first, most recent first");
                Check(!entries.Any(e => e.Window == switcher.OverviewWindow), "the overview doesn't list itself");
                var laptop = entries.Where(e => e.IsLaptop).ToList();
                int laptopWindows = agent.Apps.Sum(x => x.Windows.Count);
                Check(laptop.Count == laptopWindows && entries.ToList().FindIndex(e => e.IsLaptop) > entries.ToList().FindLastIndex(e => !e.IsLaptop),
                      $"then the laptop's windows ({laptop.Count} of {laptopWindows})");
                Check(laptop.All(e => e.Detail.Contains("Studio laptop")), "laptop cards say which laptop");
                var overview = switcher.Overview;
                Check(overview.Cards.Count == Mathf.Min(entries.Count, SwitcherContent.PerPage) && overview.Selected == 1, "a card per window, the next one selected");
                Check(overview.HintText == AppSwitcher.PinchHint, "the hint says to pinch");
                foreach (var card in overview.Cards)
                {
                    var size = card.Button.transform.lossyScale;
                    Check(Mathf.Min(size.x, size.y) >= UITheme.PtToMetres(UITheme.MinHitTargetMm) - 1e-3f, $"card '{card.Entry.Title}' hit area at least 44 mm");
                }
                now += 10f;
                yield return Settle;
                Check(switcher.IsOpen, "opened without TRIGGER it waits for a pick");
                var cardA = overview.Cards.First(k => k.Entry.Window == a);
                LaptopStreamTest.Click(mouse, cam, cardA.Button.transform.position);
                yield return Settle;
                Check(!switcher.IsOpen && manager.FocusedWindow == a, "clicking (pinching) a card brings that window forward and closes the overview");

                // TRIGGER, Switcher mode: open, step, switch after a pause.
                SettingsStore.Update(s => s.display.triggerSwitcher = true);
                var zoom = Object.FindFirstObjectByType<PassthroughZoomControl>();
                float zoomBefore = SettingsStore.Current.display.passthroughZoom;
                if (zoom != null) zoom.OnTrigger();
                else switcher.TriggerPressed();
                yield return Settle;
                Check(switcher.IsOpen && switcher.Selection.AutoConfirm && switcher.Overview.HintText == AppSwitcher.TriggerHint,
                      "TRIGGER (Switcher) opens the overview and explains itself");
                Check(Mathf.Approximately(SettingsStore.Current.display.passthroughZoom, zoomBefore), "...and doesn't zoom");
                var order = switcher.Entries.ToList();
                now += 0.4f;
                if (zoom != null) zoom.OnTrigger(); else switcher.TriggerPressed();
                Check(switcher.Selection.Selected == 2 && switcher.Overview.Selected == 2, "the next TRIGGER moves the selection");
                Check(switcher.Overview.HeaderText == "Switch to: " + order[2].Title, "the header names the selected window");
                now += 1f;
                yield return null;
                Check(switcher.IsOpen, "before the pause ends nothing switches");
                now += SwitcherSelection.DefaultConfirmDelay;
                yield return Settle;
                Check(!switcher.IsOpen && manager.FocusedWindow == order[2].Window, $"after the pause the selection comes forward ({order[2].Title})");

                // TRIGGER, Zoom mode: zooms, no switcher. While consent asks: neither.
                SettingsStore.Update(s => s.display.triggerSwitcher = false);
                if (zoom != null)
                {
                    float before = SettingsStore.Current.display.passthroughZoom;
                    zoom.OnTrigger();
                    Check(!switcher.IsOpen && !Mathf.Approximately(SettingsStore.Current.display.passthroughZoom, before), "TRIGGER (Zoom) still zooms");
                    SettingsStore.Update(s => s.display.passthroughZoom = before);
                    var gate = ConsentGate.Current;
                    if (gate != null)
                    {
                        SettingsStore.Update(s => s.display.triggerSwitcher = true);
                        var session = new GallerylessSession();
                        gate.Request(session, null, null);
                        yield return Settle;
                        zoom.OnTrigger();
                        Check(!switcher.IsOpen, "while the consent prompt asks, TRIGGER is ALLOW, not the switcher");
                        gate.Withdraw(session);
                        yield return Settle;
                    }
                }
                else Debug.Log("[SwitcherWindowTest] No PassthroughZoomControl here: TRIGGER routing checked through TriggerPressed only.");

                // A laptop card opens that window as a stream (a minimized one is restored first).
                switcher.OpenOverview();
                yield return Settle;
                var laptopEntry = switcher.Entries.First(e => e.IsLaptop && !e.Minimized);
                switcher.Pick(switcher.Entries.ToList().IndexOf(laptopEntry));
                Check(streams.Contains(laptopEntry.LaptopWindowId) && !switcher.IsOpen, "a laptop card opens that laptop window as a stream");
                var minimized = SwitcherModel.Build(manager.Windows, null, AppSwitcher.Shells(), _ => null).First(e => e.IsLaptop && e.Minimized);
                switcher.SwitchTo(minimized);
                agent.Settle();
                Check(agent.Of("window.restore").Count > 0 && streams.Contains(minimized.LaptopWindowId), "a minimized laptop window is restored, then streamed");

                // A laptop window already shown in the headset isn't listed twice.
                string shownId = laptop[0].LaptopWindowId;
                var built = SwitcherModel.Build(manager.Windows, null, AppSwitcher.Shells(), w => w == b ? shownId : null);
                Check(built.Count(e => e.LaptopWindowId == shownId || (e.Window == b && e.IconId == shownId)) == 1 && built.First(e => e.Window == b).Detail == "Laptop window",
                      "a streamed laptop window is listed once, as the headset window showing it");

                // F6 quick switch.
                manager.Focus(c);
                now += 10f;
                var first = switcher.CycleNext();
                now += 0.3f;
                var second = switcher.CycleNext();
                Check(first != null && second != null && first != c && second != first && manager.FocusedWindow == second, $"F6 walks back through the windows ({first?.Title}, {second?.Title})");

                // Switching while everything is hidden shows the windows again.
                manager.HideAll();
                switcher.SwitchTo(new SwitcherEntry { Window = a, Title = a.Title });
                Check(!manager.WindowsHidden && manager.FocusedWindow == a, "switching while hidden shows the windows again");

                // The overview's own close button ends it.
                switcher.OpenOverview(byTrigger: true);
                yield return Settle;
                manager.Close(switcher.OverviewWindow);
                yield return Settle;
                Check(!switcher.IsOpen && !switcher.Selection.Open, "closing the overview cancels it");

                yield return PinchHold();
            }
            finally
            {
                switcher.Clock = realClock;
                if (switcher.IsOpen) switcher.CloseOverview();
                SettingsStore.Update(s => s.display.triggerSwitcher = triggerSetting);
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        // The recognizer on its own, with made-up hand frames: a pinch on nothing held long enough fires once.
        static IEnumerator PinchHold()
        {
            var host = Object.FindFirstObjectByType<GestureRecognizers>();
            if (host == null || host.PinchHold == null)
            {
                Check(false, "the scene has GestureRecognizers with the pinch-and-hold gesture");
                yield break;
            }
            var recognizer = host.PinchHold;
            var fire = recognizer.Fire;
            int fired = 0;
            recognizer.Fire = () => fired++;
            recognizer.Cancel();
            try
            {
                var left = new HandFeatures(Handedness.Left);
                var right = new HandFeatures(Handedness.Right);
                float t = Time.time + 1000f;
                GestureFrame Frame(float pinchFor, bool otherPinched)
                {
                    right.Tracked = true;
                    right.Pinched = pinchFor > 0f;
                    right.PinchFor = pinchFor;
                    left.Tracked = otherPinched;
                    left.Pinched = otherPinched;
                    t += 0.1f;
                    return new GestureFrame { Left = left, Right = right, Time = t, DeltaTime = 0.1f, Head = new Pose(Vector3.zero, Quaternion.identity) };
                }
                float hold = host.Config.pinchHoldTime;
                recognizer.Tick(Frame(hold * 0.5f, false));
                Check(fired == 0, "a short pinch is just a pinch");
                recognizer.Tick(Frame(hold + 0.05f, false));
                recognizer.Tick(Frame(hold + 0.15f, false));
                Check(fired == 1, "held long enough on nothing, it opens the switcher once");
                recognizer.Tick(Frame(0f, false));
                t += host.Config.pinchHoldCooldown;
                recognizer.Tick(Frame(hold + 0.05f, true));
                Check(fired == 1, "not while the other hand pinches too (that's a stretch)");
                recognizer.Tick(Frame(0f, false));
                recognizer.Tick(Frame(hold + 0.05f, false));
                Check(fired == 2, "released and held again, it fires again");
            }
            finally
            {
                recognizer.Fire = fire;
                recognizer.Cancel();
            }
            yield break;
        }

        sealed class GallerylessSession : IRemoteSession
        {
            public string SourceName => "Test laptop";
            public string Address => "127.0.0.1";
            public string ModeLabel => "full control";
            public SpatialWindow Window => null;
            public void StopForPanic() { }
        }
    }
}
