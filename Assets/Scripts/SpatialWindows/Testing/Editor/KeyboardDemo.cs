using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// End-to-end typing demo with real injection (not a dry run). On this machine:
    ///
    ///   open a file named "Spatial Launcher Typing Demo.txt" in Notepad (its tab must be the active one)
    ///   cd Tools/LaptopAgent
    ///   python agent.py --input --keyboard
    ///
    /// then, in Play mode, Tools > Spatial Windows > Demo Typing Into Notepad. Like a user, it lists the laptop's windows
    /// in the Laptop picker, opens the demo Notepad window's stream, opens the virtual keyboard with the window's Keys
    /// button and clicks keys (Shift for capitals) to type <see cref="Message"/> into Notepad, then saves the Game view to
    /// Temp/KeyboardDemo-GameView.png. Only a window whose title contains <see cref="WindowTitle"/> is typed into, so other
    /// Notepad documents are never touched. Results use a [KeyboardDemo] prefix.
    /// </summary>
    static class KeyboardDemo
    {
        const string WindowTitle = "Spatial Launcher Typing Demo";
        const string Message = "Hello from the headset! Typed on the virtual keyboard, key by key.";
        const string Capture = "Temp/KeyboardDemo-GameView.png";
        const float Settle = 0.7f;

        [MenuItem("Tools/Spatial Windows/Demo Typing Into Notepad (agent running with --keyboard)")]
        static void Run() => EditorTestRunner.Run("KeyboardDemo", Steps());

        [MenuItem("Tools/Spatial Windows/Demo Typing Into Notepad (agent running with --keyboard)", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            string token = LaptopStreamTest.ReadAgentToken();
            Check(token != null, "the agent is running");
            if (launcher == null || manager == null || mouse == null || cam == null || token == null) yield break;
            WindowTestUtil.SelectInput<MouseWindowInput>(manager);

            var pickerWindow = launcher.OpenLaptopWindow();
            yield return Settle;
            var picker = pickerWindow != null ? pickerWindow.GetComponentInChildren<LaptopPickerContent>() : null;
            if (picker == null) yield break;
            picker.HostField.SetText("127.0.0.1");
            picker.PortField.SetText("8889");
            picker.TokenField.SetText(token);
            LaptopStreamTest.Click(mouse, cam, picker.ListButton.transform.position);
            double deadline = EditorTestRunner.Now + 8.0;
            while (picker.Targets.Count == 0 && EditorTestRunner.Now < deadline) yield return 0.1f;
            LaptopTarget notepad = null;
            foreach (var target in picker.Targets)
                if (target.kind == "window" && target.title.Contains(WindowTitle)) notepad = target;
            Check(notepad != null, $"the picker lists \"{WindowTitle}.txt - Notepad\" (open that file in Notepad first)");
            if (notepad == null) yield break;

            SpatialWindow window = null;
            yield return LaptopStreamTest.PickEntry(manager, mouse, cam, pickerWindow, picker, notepad, w => window = w);
            var stream = window != null ? window.GetComponentInChildren<RemoteStreamContent>() : null;
            if (stream == null) yield break;
            manager.Close(pickerWindow);
            deadline = EditorTestRunner.Now + 15.0;
            while (!(stream.InputLink.IsConnected && stream.Video != null && stream.Video.IsConnected) && EditorTestRunner.Now < deadline)
                yield return 0.1f;
            Check(stream.Video != null && stream.Video.IsConnected, $"Notepad streams ({stream.Video?.StatusText})");
            Check(stream.InputLink.KeyboardAllowed && !stream.InputLink.AgentDryRun,
                $"the agent types for real (\"{stream.InputLink.StatusText}\"; start it with --input --keyboard, without --dry-run-input)");
            if (!stream.InputLink.KeyboardAllowed) yield break;

            manager.Focus(window);
            yield return Settle;
            LaptopStreamTest.Click(mouse, cam, stream.KeysButton.transform.position);
            yield return Settle;
            var keyboard = launcher.KeyboardInput.KeyboardContent;
            Check(keyboard != null && launcher.KeyboardInput.Target == stream, $"the Keys button opens the keyboard (\"{keyboard?.StatusText}\")");
            if (keyboard == null) yield break;

            // Start on a new line at the end of the document, then type the message key by key.
            var clicks = new List<string> { "Ctrl", "End", "Enter" };
            foreach (char c in Message)
            {
                if (!TryFindKey(c, out var id, out bool shift))
                {
                    Check(false, $"the keyboard has a key for '{c}'");
                    yield break;
                }
                if (shift) clicks.Add("Shift");
                clicks.Add(id);
            }
            foreach (var id in clicks)
            {
                LaptopStreamTest.Click(mouse, cam, keyboard.Button(id).transform.position);
                yield return 0.08f;
            }
            Check(true, $"clicked {clicks.Count} keys on the virtual keyboard to type \"{Message}\"");

            yield return 2.0f; // let the stream show the typed text
            ScreenCapture.CaptureScreenshot(Capture);
            yield return 0.5f;
            Debug.Log($"[KeyboardDemo] Game view saved to {Capture}. The stream window and keyboard stay open.");
        }

        static bool TryFindKey(char c, out string id, out bool shift)
        {
            string text = c.ToString();
            foreach (var key in VirtualKeyboardLayout.Keys)
            {
                if (key.Kind != VirtualKeyKind.Character) continue;
                if (key.Label == text) { id = key.Id; shift = false; return true; }
                if (key.ShiftLabel == text) { id = key.Id; shift = true; return true; }
            }
            id = null;
            shift = false;
            return false;
        }
    }
}
