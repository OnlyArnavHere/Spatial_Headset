using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// End-to-end test of the Laptop picker and several stream windows against a running laptop agent on this machine:
    ///
    ///   cd Tools/LaptopAgent
    ///   python clock.py                                   (and open Notepad)
    ///   python agent.py --input --keyboard --dry-run-input
    ///
    /// then, in Play mode, Tools > Spatial Windows > Test Laptop Stream. It reads the current pairing token from the
    /// agent's runtime config (Tools/LaptopAgent/.runtime/mediamtx.yml), lists the laptop's windows through the picker,
    /// opens the test clock and Notepad as two stream windows at once and checks for each: video, aspect, its own input
    /// link and stream path, and that its clicks map to its own window's pixels (dry run: nothing is clicked). Then the
    /// virtual keyboard's keys reaching the focused window's stream, automatic reconnection, wrong-token handling and the
    /// "Whole desktop" entry. Results use a [LaptopTest] prefix.
    /// </summary>
    static class LaptopStreamTest
    {
        const string AgentConfig = "Tools/LaptopAgent/.runtime/mediamtx.yml";
        const string ClockTitle = "Spatial Launcher Test Clock";
        const string NotepadTitle = "Notepad";
        const float Settle = 0.7f;

        [MenuItem("Tools/Spatial Windows/Test Laptop Stream (agent running)")]
        static void Run() => EditorTestRunner.Run("LaptopTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Laptop Stream (agent running)", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            string token = ReadAgentToken();
            Check(token != null, $"the agent is running (token found in {AgentConfig})");
            if (launcher == null || manager == null || mouse == null || cam == null || token == null) yield break;

            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            string savedHost = RemoteConnectionPrefs.Host(null), savedToken = RemoteConnectionPrefs.Token();
            int savedPort = RemoteConnectionPrefs.VideoPort(null);
            var opened = new List<SpatialWindow>();
            try
            {
                yield return Body(launcher, manager, mouse, cam, token, opened);
            }
            finally
            {
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
                RemoteConnectionPrefs.Save(savedHost, savedPort, savedToken);
            }
        }

        static IEnumerator Body(LauncherWindow launcher, WindowManager manager, MouseWindowInput mouse, Camera cam, string token,
            List<SpatialWindow> opened)
        {
            // The Laptop icon's picker lists what the laptop can stream.
            var pickerWindow = launcher.OpenLaptopWindow();
            opened.Add(pickerWindow);
            yield return Settle;
            var picker = pickerWindow != null ? pickerWindow.GetComponentInChildren<LaptopPickerContent>() : null;
            Check(picker != null, "the Laptop icon opens the picker (LauncherWindow > Laptop Streaming is WebRTC)");
            if (picker == null) yield break;

            picker.HostField.SetText("127.0.0.1");
            picker.PortField.SetText("8889");
            picker.TokenField.SetText(token);
            Click(mouse, cam, picker.ListButton.transform.position);
            double deadline = EditorTestRunner.Now + 8.0;
            while (picker.Targets.Count == 0 && EditorTestRunner.Now < deadline) yield return 0.1f;
            var targets = picker.Targets;
            Check(targets.Count > 0 && targets[0].id == "desktop" && targets[0].title == "Whole desktop",
                $"the picker lists the whole desktop first ({targets.Count} entries; \"{picker.StatusLine}\")");
            var clock = Find(targets, ClockTitle);
            var notepad = Find(targets, NotepadTitle);
            Check(clock != null && notepad != null,
                $"it lists the test clock and Notepad (start clock.py and Notepad first): {Describe(clock)}, {Describe(notepad)}");
            Check(EntriesMatch(picker), $"the entries show the targets' titles ({picker.EntryButtons.Count} on page {picker.Page + 1})");
            if (clock == null || notepad == null) yield break;

            // Pick both: two stream windows at once, each with its own stream and input link.
            var streams = new List<RemoteStreamContent>();
            foreach (var target in new[] { clock, notepad })
            {
                SpatialWindow window = null;
                yield return PickEntry(manager, mouse, cam, pickerWindow, picker, target, w => window = w);
                if (window != null) opened.Add(window);
                var stream = window != null ? window.GetComponentInChildren<RemoteStreamContent>() : null;
                Check(stream != null && stream.Target != null && stream.Target.id == target.id && window.Title.StartsWith(target.title.Substring(0, Mathf.Min(20, target.title.Length))),
                    $"picking \"{target.title}\" opens its own stream window (/{target.path})");
                if (stream != null && streams.Count == 0)
                {
                    // The first screen from the laptop asks first; the next ones from it are covered while it is live.
                    Check(stream.AwaitingConsent && !stream.IsActive && !stream.InputLink.IsConnected && ConsentGate.Current.HasPending,
                        $"nothing connects before the headset approves (\"{ConsentGate.Current?.Question}\")");
                    yield return ApproveIfAsked(manager, mouse, cam);
                    Check(stream.IsActive && stream.ConsentOutcome == "approved" && ConsentGate.Current.LiveText.Contains("LIVE"),
                        $"Approve connects it and the live indicator shows (\"{ConsentGate.Current.LiveText}\")");
                }
                if (stream != null) streams.Add(stream);
            }
            if (streams.Count < 2) yield break;

            deadline = EditorTestRunner.Now + 20.0;
            while (!streams.TrueForAll(s => s.Video != null && s.Video.IsConnected && s.InputLink.IsConnected && s.InputLink.RoundTripMs.HasValue)
                   && EditorTestRunner.Now < deadline)
                yield return 0.1f;
            foreach (var stream in streams)
            {
                string name = stream.Target.title;
                Check(stream.Video != null && stream.Video.IsConnected, $"{name}: video arrives over WHEP ({stream.Video?.StatusText})");
                Check(stream.InputLink.IsConnected && stream.InputLink.AgentDryRun && stream.InputLink.StreamPath == stream.Target.path,
                    $"{name}: its input link opened /{stream.InputLink.StreamPath} ({stream.InputLink.StatusText}; {stream.InputLink.TargetDescription})");
                if (stream.Video == null || !stream.Video.IsConnected) continue;
                var size = stream.Video.PixelSize;
                float aspect = size.x / (float)size.y;
                float shown = stream.DisplaySize.x / stream.DisplaySize.y;
                Check(Mathf.Abs(shown - aspect) < aspect * 0.01f, $"{name}: shows the {size.x}x{size.y} stream at its aspect ({shown:F3})");
            }
            Check(streams.TrueForAll(s => s.Video != null && s.Video.IsConnected), "both windows stream at the same time");

            yield return 1.5f; // a few pings
            var first = streams[0];
            Check(first.InputLink.RoundTripMs.HasValue, $"round-trip time is measured ({first.InputLink.RoundTripMs:0.0} ms, best {first.InputLink.BestRoundTripMs:0.0} ms)");
            Check(first.StatusLine.Contains("Laptop clock"), $"the status line shows the laptop clock (\"{first.StatusLine}\")");
            var offsetMs = (first.InputLink.LaptopNow.Value - System.DateTime.UtcNow).TotalMilliseconds;
            Check(Mathf.Abs((float)offsetMs) < 50f, $"same machine: the estimated laptop clock matches this one ({offsetMs:+0.0;-0.0} ms)");

            // Each window's clicks go to its own laptop window (the agent is in dry-run mode, so they're only logged).
            foreach (var stream in streams)
            {
                var window = stream.Window;
                manager.Focus(window);
                yield return Settle;
                var surface = stream.Surface.transform;
                Vector2Int? a = null, b = null;
                string pathA = null, pathB = null;
                yield return ClickAndAck(mouse, cam, stream, surface.TransformPoint(new Vector3(0.25f, 0.25f, 0f)), (p, path) => { a = p; pathA = path; });
                yield return ClickAndAck(mouse, cam, stream, surface.TransformPoint(new Vector3(-0.25f, -0.25f, 0f)), (p, path) => { b = p; pathB = path; });
                var target = stream.Target;
                if (a.HasValue && b.HasValue)
                {
                    // Screen (0.75, 0.25) to (0.25, 0.75): half the width left, half the height down, in laptop pixels.
                    var delta = b.Value - a.Value;
                    var expected = new Vector2Int(-target.width / 2, target.height / 2);
                    Check(pathA == target.path && pathB == target.path && Mathf.Abs(delta.x - expected.x) <= 3 && Mathf.Abs(delta.y - expected.y) <= 3,
                        $"{target.title}: clicks go to /{pathA} and map to its pixels: moved {delta}, expected {expected}");
                }
                else Check(false, $"{target.title}: the agent acknowledged both clicks");
            }

            yield return KeyboardChecks(launcher, manager, mouse, cam, streams[1], opened);

            // Automatic reconnection after a dropped connection, while the other window keeps streaming.
            var other = streams[1];
            int before = first.Video.ConnectCount;
            first.Video.SimulateDrop();
            deadline = EditorTestRunner.Now + 3.0;
            while (first.Video.IsConnected && EditorTestRunner.Now < deadline) yield return 0.05f;
            bool dropped = !first.Video.IsConnected;
            deadline = EditorTestRunner.Now + 15.0;
            while (!first.Video.IsConnected && EditorTestRunner.Now < deadline) yield return 0.1f;
            Check(dropped && first.Video.IsConnected && first.Video.ConnectCount == before + 1 && other.Video.IsConnected,
                $"a dropped stream reconnects on its own while the other keeps streaming ({first.Video.StatusText})");

            // Disconnect, then a wrong token: a clear message and no endless retrying.
            manager.Focus(first.Window);
            yield return Settle;
            Click(mouse, cam, first.ConnectButton.transform.position);
            yield return 0.3f;
            Check(!first.IsActive && first.Video.Texture == null && first.StatusText == "Not connected" && other.Video.IsConnected,
                "Disconnect stops that stream only");

            first.TokenField.SetText("wrong-token");
            Click(mouse, cam, first.ConnectButton.transform.position);
            deadline = EditorTestRunner.Now + 8.0;
            while ((first.Video.CurrentState != RemoteVideoState.Failed ||
                    first.InputLink.CurrentState != WebSocketRemoteInputSink.State.Rejected) && EditorTestRunner.Now < deadline)
                yield return 0.1f;
            Check(first.Video.CurrentState == RemoteVideoState.Failed && first.Video.StatusText.StartsWith("Wrong token"),
                $"a wrong token stops the video with a clear message (\"{first.Video.StatusText}\")");
            Check(first.InputLink.CurrentState == WebSocketRemoteInputSink.State.Rejected,
                $"and the input link stops retrying (\"{first.InputLink.StatusText}\")");

            // The whole desktop streams too.
            SpatialWindow desktopWindow = null;
            yield return PickEntry(manager, mouse, cam, pickerWindow, picker, targets[0], w => desktopWindow = w);
            if (desktopWindow != null) opened.Add(desktopWindow);
            var desktop = desktopWindow != null ? desktopWindow.GetComponentInChildren<RemoteStreamContent>() : null;
            deadline = EditorTestRunner.Now + 15.0;
            while (desktop != null && desktop.Video != null && !desktop.Video.IsConnected && EditorTestRunner.Now < deadline) yield return 0.1f;
            Check(desktop != null && desktop.Video != null && desktop.Video.IsConnected && desktop.InputLink.StreamPath == "desktop",
                $"\"Whole desktop\" streams the screen on /desktop ({desktop?.Video?.StatusText})");
        }

        // The stream window's Keys button opens the virtual keyboard; its keys reach that window's stream on the agent
        // (dry run: acknowledged, not typed).
        static IEnumerator KeyboardChecks(LauncherWindow launcher, WindowManager manager, MouseWindowInput mouse, Camera cam,
            RemoteStreamContent stream, List<SpatialWindow> opened)
        {
            string name = stream.Target.title;
            manager.Focus(stream.Window);
            yield return Settle;
            Click(mouse, cam, stream.KeysButton.transform.position);
            yield return Settle;
            var keyboardWindow = launcher.KeyboardInput.KeyboardWindow;
            if (keyboardWindow != null) opened.Add(keyboardWindow);
            var keyboard = launcher.KeyboardInput.KeyboardContent;
            Check(keyboardWindow != null && manager.FocusedWindow == keyboardWindow && launcher.KeyboardInput.Target == stream,
                $"{name}: the Keys button opens the keyboard, typing into this window");
            Check(stream.InputLink.KeyboardAllowed, $"{name}: the agent allows keyboard input (start it with --input --keyboard --dry-run-input)");
            if (keyboard == null || !stream.InputLink.KeyboardAllowed) yield break;

            var link = stream.InputLink;
            var received = new List<string>();
            foreach (var id in new[] { "Q", "Shift", "W", "Enter" })
            {
                int acks = link.KeyAckCount;
                int expected = id == "Shift" ? 0 : id == "Enter" ? 2 : 1; // Enter is a press and a release
                Click(mouse, cam, keyboard.Button(id).transform.position);
                double deadline = EditorTestRunner.Now + 3.0;
                while (link.KeyAckCount < acks + expected && EditorTestRunner.Now < deadline) yield return 0.05f;
                if (expected > 0 && link.LastKeyAck != null)
                    received.Add(link.LastKeyAck.type == "text" ? link.LastKeyAck.text : $"{link.LastKeyAck.code}{(link.LastKeyAck.down ? "↓" : "↑")}");
            }
            Check(string.Join(" ", received) == "q W Enter↑" && link.LastKeyAckPath == stream.Target.path,
                $"{name}: q, Shift+w and Enter reach /{link.LastKeyAckPath} as typed (\"{string.Join(" ", received)}\")");

            manager.Close(keyboardWindow);
            yield return Settle;
        }

        static LaptopTarget Find(IReadOnlyList<LaptopTarget> targets, string titlePart)
        {
            foreach (var target in targets)
                if (target.kind == "window" && target.title.Contains(titlePart)) return target;
            return null;
        }

        static string Describe(LaptopTarget target) => target == null ? "missing" : $"\"{target.title}\" on /{target.path}";

        static bool EntriesMatch(LaptopPickerContent picker)
        {
            if (picker.EntryButtons.Count == 0 || picker.EntryButtons.Count != picker.PageTargets.Count) return false;
            for (int i = 0; i < picker.EntryButtons.Count; i++)
            {
                var label = picker.transform.Find($"EntryLabel {i}")?.GetComponent<TMPro.TextMeshPro>();
                if (label == null || !label.text.StartsWith(picker.PageTargets[i].title)) return false;
            }
            return true;
        }

        // Pages to the target in the picker and clicks its entry, like a user would; reports the window it opened.
        internal static IEnumerator PickEntry(WindowManager manager, MouseWindowInput mouse, Camera cam, SpatialWindow pickerWindow,
            LaptopPickerContent picker, LaptopTarget target, System.Action<SpatialWindow> result)
        {
            manager.Focus(pickerWindow);
            yield return Settle;
            for (int page = 0; page < 10 && IndexOnPage(picker, target) < 0; page++)
            {
                Click(mouse, cam, picker.MoreButton.transform.position);
                yield return 0.1f;
            }
            int index = IndexOnPage(picker, target);
            if (index < 0) yield break;
            int count = manager.Windows.Count;
            Click(mouse, cam, picker.EntryButtons[index].transform.position);
            yield return Settle;
            if (manager.Windows.Count == count + 1) result(manager.FocusedWindow);
        }

        static int IndexOnPage(LaptopPickerContent picker, LaptopTarget target)
        {
            for (int i = 0; i < picker.PageTargets.Count; i++)
                if (picker.PageTargets[i] == target) return i;
            return -1;
        }

        static IEnumerator ClickAndAck(MouseWindowInput mouse, Camera cam, RemoteStreamContent laptop, Vector3 point,
            System.Action<Vector2Int, string> result)
        {
            int acks = laptop.InputLink.AckCount;
            Click(mouse, cam, point);
            double deadline = EditorTestRunner.Now + 3.0;
            while (laptop.InputLink.AckCount < acks + 2 && EditorTestRunner.Now < deadline) yield return 0.05f; // down + up
            if (laptop.InputLink.AckCount >= acks + 2 && laptop.InputLink.LastAck.HasValue)
                result(laptop.InputLink.LastAck.Value, laptop.InputLink.LastAckPath);
        }

        /// <summary>Clicks Approve on the consent modal if it is asking.</summary>
        internal static IEnumerator ApproveIfAsked(WindowManager manager, MouseWindowInput mouse, Camera cam)
        {
            var gate = ConsentGate.Current;
            if (gate == null || !gate.HasPending || gate.Modal == null) yield break;
            manager.Focus(gate.Modal);
            yield return Settle;
            var content = gate.Modal.GetComponentInChildren<ConsentContent>();
            Click(mouse, cam, content.ApproveButton.transform.position);
            yield return Settle;
        }

        internal static void Click(MouseWindowInput mouse, Camera cam, Vector3 worldPoint)
        {
            Vector3 screen = cam.WorldToScreenPoint(worldPoint);
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
        }

        // The agent writes this run's token into its MediaMTX config (git-ignored, deleted with .runtime).
        internal static string ReadAgentToken()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), AgentConfig);
            if (!File.Exists(path)) return null;
            var match = Regex.Match(File.ReadAllText(path), @"pass:\s*'([^']+)'");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
