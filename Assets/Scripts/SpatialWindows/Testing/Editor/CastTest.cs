using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for Cast windows and Wired (USB) mode. Cast: the launcher's Cast icon, a view-only window (no
    /// pointer surface, no keyboard, no Keys button, eight media buttons), the media message whitelist, and the consent
    /// gate asking "view-only" for Cast and "full control" for a laptop window, with neither approval covering the other.
    /// With Tools/LaptopAgent/fake_cast_agent.py running (cast-only, recording instead of injecting) it also connects,
    /// presses every media button and checks the agent took exactly those and dropped nothing (the headset sends nothing
    /// else). Wired: the hotspot addresses, Find laptop against this machine, and the Settings > Laptop rows.
    /// Results use a [CastTest] prefix.
    /// </summary>
    static class CastTest
    {
        const string FakeToken = "cast-test-token";

        [MenuItem("Tools/Spatial Windows/Test Cast and Wired")]
        static void Run() => EditorTestRunner.Run("CastTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Cast and Wired", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static string StatePath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "LaptopAgent", ".runtime", "fake_cast_state.json"));

        static IEnumerator Steps()
        {
            var errors = new List<string>();
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && !message.Contains("[CastTest]")) errors.Add(message);
            };
            Application.logMessageReceived += onLog;
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var gate = ConsentGate.Current;
            string savedHost = SettingsStore.Current.laptop.host;
            bool savedWired = SettingsStore.Current.laptop.wired;
            var opened = new List<SpatialWindow>();
            try
            {
                if (launcher == null || gate == null)
                {
                    Check(false, "scene has a LauncherWindow with a consent gate");
                    yield break;
                }
                CheckProtocol();
                yield return CheckCastWindow(launcher, gate, opened);
                yield return CheckConsentModes(launcher, gate, opened);
                yield return CheckLiveAgent(launcher, gate, opened);
                CheckHotspotAddresses();
                yield return CheckWiredSettings(launcher);
            }
            finally
            {
                Application.logMessageReceived -= onLog;
                while (gate != null && gate.HasPending) gate.Deny();
                foreach (var window in opened)
                    if (window != null) launcher.Settings.Manager.Close(window);
                SettingsStore.Update(s =>
                {
                    s.laptop.host = savedHost;
                    s.laptop.wired = savedWired;
                });
            }
            Check(errors.Count == 0, $"no errors logged during the test ({errors.Count}{(errors.Count > 0 ? ": " + errors[0] : "")})");
        }

        static void CheckProtocol()
        {
            var actions = WebSocketRemoteInputSink.MediaActions;
            Check(actions.Length == 8 && actions.Contains("play_pause") && actions.Contains("seek_back") && actions.Contains("seek_forward") &&
                  actions.Contains("next") && actions.Contains("previous") && actions.Contains("volume_up") && actions.Contains("volume_down") &&
                  actions.Contains("mute"), "a cast has exactly play/pause, seek back/forward, next/previous, volume up/down and mute");
            Check(WebSocketRemoteInputSink.EncodeMedia("play_pause") == "{\"type\":\"media\",\"action\":\"play_pause\"}" &&
                  WebSocketRemoteInputSink.EncodeMedia("power") == null && WebSocketRemoteInputSink.EncodeMedia("Alt+F4") == null &&
                  WebSocketRemoteInputSink.EncodeMedia(null) == null,
                "only those actions can be encoded; anything else isn't sent at all");
        }

        static LaptopTarget Target(string title) => new LaptopTarget { id = "window:4242", title = title, path = "movie", width = 1280, height = 720 };

        static IEnumerator CheckCastWindow(LauncherWindow launcher, ConsentGate gate, List<SpatialWindow> opened)
        {
            Check(launcher.LauncherContent != null && launcher.LauncherContent.EntryLabel(9) == "Cast", "the launcher has a Cast icon");
            var picker = launcher.OpenCastPicker();
            opened.Add(picker);
            yield return 0.2f;
            Check(picker != null && picker.Title == "Cast" && picker.GetComponentInChildren<LaptopPickerContent>() != null,
                "the Cast icon opens the laptop picker for a cast");

            var window = launcher.OpenCastStream(Target("Movie"));
            opened.Add(window);
            yield return 0.2f;
            var cast = window.GetComponentInChildren<RemoteStreamContent>();
            Check(window.Title == "Cast: Movie" && cast.IsCast && cast.ModeLabel == "view-only", $"a cast window is view-only ({window.Title})");
            Check(cast.Surface.GetComponent<Collider>() == null && cast.Source.Sink == null && cast.Source.Input == null,
                "its screen takes no pointer input: no collider, no input sink");
            Check(cast.KeyboardSink == null && !cast.KeysButton.gameObject.activeSelf, "no keyboard reaches it and it has no Keys button");
            Check(cast.MediaButtons.Count == 8 && cast.MediaButtons.Values.All(b => b.gameObject.activeSelf),
                "it has the eight media buttons");
            var play = cast.MediaButtons["play_pause"];
            play.Press(play.transform.position);
            play.Release(true);
            Check(cast.LastMediaAction == "play_pause" && !cast.LastMediaSent && cast.InputLink.MediaSentCount == 0,
                "a media button while not connected sends nothing");

            var laptop = launcher.OpenLaptopStream(Target("Movie"));
            opened.Add(laptop);
            yield return 0.2f;
            var full = laptop.GetComponentInChildren<RemoteStreamContent>();
            Check(!full.IsCast && full.ModeLabel == "full control" && full.MediaButtons.Count == 0 && full.KeysButton.gameObject.activeSelf &&
                  full.Surface.GetComponent<Collider>() != null, "a laptop window stays full control (pointer, Keys, no media bar)");
            Check(window.Size.y > laptop.Size.y, "the cast window is taller by its media bar");
        }

        static IEnumerator CheckConsentModes(LauncherWindow launcher, ConsentGate gate, List<SpatialWindow> opened)
        {
            var castWindow = launcher.OpenCastStream(Target("Movie"));
            var laptopWindow = launcher.OpenLaptopStream(Target("Movie"));
            opened.Add(castWindow);
            opened.Add(laptopWindow);
            yield return 0.2f;
            var cast = castWindow.GetComponentInChildren<RemoteStreamContent>();
            var laptop = laptopWindow.GetComponentInChildren<RemoteStreamContent>();
            foreach (var s in new[] { cast, laptop })
            {
                s.HostField.SetText("127.0.0.1");
                s.PortField.SetText("9");
                s.TokenField.SetText("consent-test");
            }

            cast.Connect();
            yield return 0.1f;
            string castQuestion = gate.Question;
            Check(cast.AwaitingConsent && castQuestion == "Allow Movie (127.0.0.1) to show its screen on this headset? (view-only)",
                $"Cast asks first, saying view-only (\"{castQuestion}\")");
            gate.Approve();
            yield return 0.1f;
            Check(cast.IsActive && gate.LiveText.Contains("view-only"), $"approved, the live indicator says view-only ({gate.LiveText})");

            laptop.Connect();
            yield return 0.1f;
            Check(laptop.AwaitingConsent && gate.Question.EndsWith("(full control)"),
                $"a live view-only cast doesn't cover full control from the same laptop: it asks again (\"{gate.Question}\")");
            gate.Deny();
            yield return 0.1f;
            Check(!laptop.IsActive && laptop.ConsentOutcome == "denied" && cast.IsActive, "denying full control leaves the cast alone");

            gate.PanicStop();
            yield return 0.2f;
            Check(castWindow == null && !gate.IsActive, "panic stop ends the cast like any remote screen");
        }

        static bool AgentRunning()
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(IPAddress.Loopback, 8765);
                    return connect.Wait(300) && client.Connected;
                }
            }
            catch (System.Exception) { return false; }
        }

        static IEnumerator CheckLiveAgent(LauncherWindow launcher, ConsentGate gate, List<SpatialWindow> opened)
        {
            if (!AgentRunning() || !File.Exists(StatePath))
            {
                Debug.Log("[CastTest] Skipped the live part: start Tools/LaptopAgent/fake_cast_agent.py first");
                yield break;
            }
            var window = launcher.OpenCastStream(Target("Movie"), new LaptopConnection("127.0.0.1", 9, FakeToken));
            opened.Add(window);
            yield return 0.2f;
            gate.Approve();
            var cast = window.GetComponentInChildren<RemoteStreamContent>();
            double deadline = EditorTestRunner.Now + 6.0;
            while (!(cast.InputLink.IsConnected && cast.InputLink.StreamPath == "movie") && EditorTestRunner.Now < deadline) yield return 0.1f;
            Check(cast.InputLink.IsConnected && cast.InputLink.CastOnly && cast.InputLink.MediaAllowed && cast.InputLink.StreamPath == "movie",
                $"connected to the cast-only agent, which says media only ({cast.InputLink.StatusText})");

            foreach (var action in WebSocketRemoteInputSink.MediaActions)
            {
                var button = cast.MediaButtons[action];
                button.Press(button.transform.position);
                button.Release(true);
                yield return 0.05f;
            }
            // A click on the cast's screen: it has no collider, so the window body takes it and nothing is sent.
            var input = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (input != null && cam != null)
            {
                launcher.Settings.Manager.Focus(window);
                yield return 0.5f;
                Vector2 screen = cam.WorldToScreenPoint(cast.Surface.transform.position);
                input.PressAt(screen);
                input.ReleaseAt(screen);
            }
            yield return 1f;
            Check(cast.InputLink.MediaSentCount == 8 && cast.LastMediaSent, $"every media button sent its action ({cast.InputLink.MediaSentCount})");

            var state = JsonUtility.FromJson<FakeState>(File.ReadAllText(StatePath));
            Check(state.key_call_count == 16 && state.dropped_total == 0 && (state.other_calls == null || state.other_calls.Length == 0),
                $"the agent pressed and released exactly the 8 whitelisted keys and dropped nothing: the headset sent nothing else " +
                $"({state.key_call_count} key calls: {string.Join(" ", state.key_calls ?? new string[0])}; {state.dropped_total} dropped)");
        }

        [System.Serializable]
        sealed class FakeState
        {
            public int dropped_total;
            public int key_call_count;
            public string[] key_calls;
            public string[] other_calls;
        }

        static void CheckHotspotAddresses()
        {
            var hosts = UsbHotspot.CandidateHosts().ToList();
            Check(hosts.Count == 13 && hosts[0] == "172.20.10.2" && hosts[12] == "172.20.10.14",
                "Find laptop tries 172.20.10.2 to .14 (the phone is .1)");
            Check(UsbHotspot.IsLaptopAddress("172.20.10.5") && !UsbHotspot.IsLaptopAddress("172.20.10.1") &&
                  !UsbHotspot.IsLaptopAddress("172.20.10.15") && !UsbHotspot.IsLaptopAddress("192.168.1.5") && !UsbHotspot.IsLaptopAddress(""),
                "only 172.20.10.2..14 count as a laptop on the hotspot");
            Check(UsbHotspot.Help(null).Contains("Personal Hotspot") && UsbHotspot.Help("172.20.10.1").Contains("Hotspot on"),
                "the help line says whether this phone's hotspot is on");
        }

        static IEnumerator CheckWiredSettings(LauncherWindow launcher)
        {
            // Something listening on the control port on this machine stands in for the laptop.
            TcpListener listener = null;
            if (!AgentRunning())
            {
                listener = new TcpListener(IPAddress.Loopback, 8765);
                listener.Start();
            }
            var window = launcher.OpenSettings();
            try
            {
                yield return 0.3f;
                var content = Object.FindFirstObjectByType<SettingsContent>();
                content.Select("Laptop");
                yield return 0.2f;
                var wired = content.Control("Laptop", "Link", "Wired (USB)");
                var find = content.Control("Laptop", "Wired (USB)", "Find laptop");
                Check(wired != null && find != null && content.ValueText("Laptop", "Wired help") != null,
                    "Settings > Laptop has Link (Wi-Fi / Wired (USB)), Find laptop and a help line");
                if (wired == null || find == null) yield break;
                wired.Press(wired.transform.position);
                wired.Release(true);
                yield return 0.1f;
                Check(SettingsStore.Current.laptop.wired && content.ValueText("Laptop", "Wired help").Contains("agent.py --wired"),
                    $"choosing Wired shows how to connect (\"{content.ValueText("Laptop", "Wired help")}\")");

                content.WiredHosts = new[] { "127.0.0.2", "127.0.0.1" };
                find.Press(find.transform.position);
                find.Release(true);
                double deadline = EditorTestRunner.Now + 4.0;
                while (content.FindingLaptop && EditorTestRunner.Now < deadline) yield return 0.1f;
                Check(SettingsStore.Current.laptop.host == "127.0.0.1" && content.WiredStatus.StartsWith("Found the agent at 127.0.0.1"),
                    $"Find laptop puts the address that answers into Address (\"{content.WiredStatus}\")");

                content.WiredHosts = new[] { "127.0.0.2" };
                find.Press(find.transform.position);
                find.Release(true);
                deadline = EditorTestRunner.Now + 4.0;
                while (content.FindingLaptop && EditorTestRunner.Now < deadline) yield return 0.1f;
                Check(content.WiredStatus.StartsWith("No agent answered"), $"and says so when none does (\"{content.WiredStatus}\")");

                var value = FindDeep(content.transform, "Row Wired help").Find("Value");
                float bottom = content.transform.InverseTransformPoint(value.position).y - 0.018f;
                Check(bottom >= -SettingsContent.PreferredSize.y * 0.5f, $"the Laptop rows fit in the Settings window ({bottom:F3} m from the middle)");
            }
            finally
            {
                listener?.Stop();
                if (window != null) launcher.Settings.Manager.Close(window);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }
    }
}
