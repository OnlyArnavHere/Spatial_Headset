using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for keyboard input to laptop windows, without the laptop agent: the key/text protocol messages
    /// (encode, decode, splitting), the virtual keyboard's layout and what each key sends with modifiers, the physical
    /// key map, and focus routing: two stream windows with recording sinks, the virtual keyboard opened from the launcher
    /// and its keys clicked through MouseWindowInput, checking which window receives what. Results use a [KeyboardTest]
    /// prefix. The agent side has its own tests (Tools/LaptopAgent/test_keyboard.py and selftest_input.py).
    /// </summary>
    static class KeyboardInputTest
    {
        const float Settle = 0.6f;

        [MenuItem("Tools/Spatial Windows/Test Keyboard Input")]
        static void Run() => EditorTestRunner.Run("KeyboardTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Keyboard Input", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        /// <summary>Records what a stream window would send to the laptop.</summary>
        sealed class RecordingSink : IRemoteInputSink, IRemoteKeyboardSink
        {
            public readonly List<KeyStroke> Strokes = new List<KeyStroke>();
            public void PointerMove(Vector2 position) { }
            public void PointerDown(Vector2 position) { }
            public void PointerUp(Vector2 position) { }
            public void Scroll(Vector2 position, float delta) { }
            public void Key(string code, bool down) => Strokes.Add(down ? KeyStroke.Down(code) : KeyStroke.Up(code));
            public void Text(string text) => Strokes.Add(KeyStroke.Text(text));
            public string Describe() => Strokes.Count == 0 ? "nothing" : string.Join(", ", Strokes);
        }

        static IEnumerator Steps()
        {
            ProtocolChecks();
            LayoutChecks();
            PhysicalKeyChecks();

            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (launcher == null || manager == null || mouse == null || cam == null || launcher.KeyboardInput == null)
            {
                Check(false, "scene has a LauncherWindow (with its RemoteKeyboard), WindowManager, MouseWindowInput and main camera");
                yield break;
            }

            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var opened = new List<SpatialWindow>();
            try
            {
                yield return Routing(launcher, manager, mouse, cam, opened);
            }
            finally
            {
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static void ProtocolChecks()
        {
            bool key = RemoteKeyProtocol.TryDecode(RemoteKeyProtocol.EncodeKey("A", true), out var down)
                       && down.type == "key" && down.code == "A" && down.down;
            bool up = RemoteKeyProtocol.TryDecode(RemoteKeyProtocol.EncodeKey("Left", false), out var release)
                      && release.code == "Left" && !release.down;
            Check(key && up, $"protocol: key messages decode to what was encoded ({RemoteKeyProtocol.EncodeKey("A", true)})");

            const string tricky = "He said \"hi\" \\ back\n\ttab é ✓ 😀";
            string encoded = RemoteKeyProtocol.EncodeText(tricky);
            Check(RemoteKeyProtocol.TryDecode(encoded, out var text) && text.type == "text" && text.text == tricky,
                $"protocol: text with quotes, backslashes, control characters and emoji survives encoding ({encoded})");

            Check(!RemoteKeyProtocol.TryDecode("{\"type\":\"key\",\"code\":\"LWin\",\"down\":true}", out _) &&
                  !RemoteKeyProtocol.TryDecode("{\"type\":\"move\",\"x\":0.5}", out _) && !RemoteKeyProtocol.TryDecode("not json", out _),
                "protocol: unknown keys (the Windows key), other messages and junk don't decode as keyboard messages");

            var pieces = RemoteKeyProtocol.SplitText(new string('x', 150)).ToList();
            string emoji = new string('a', RemoteKeyProtocol.MaxTextLength - 1) + "😀b";
            var split = RemoteKeyProtocol.SplitText(emoji).ToList();
            Check(pieces.Select(p => p.Length).SequenceEqual(new[] { 64, 64, 22 }) && string.Concat(split) == emoji &&
                  split.All(p => p.Length <= RemoteKeyProtocol.MaxTextLength && !char.IsHighSurrogate(p[p.Length - 1])),
                $"protocol: long text is split into pieces of at most {RemoteKeyProtocol.MaxTextLength}, never inside an emoji " +
                $"({string.Join("+", pieces.Select(p => p.Length))}; {string.Join("+", split.Select(p => p.Length))})");
        }

        static void LayoutChecks()
        {
            var rows = VirtualKeyboardLayout.Rows;
            Check(rows.Count == 5 && rows.All(r => Mathf.Abs(r.Sum(k => k.Width) - VirtualKeyboardLayout.RowUnits) < 1e-4f),
                $"layout: 5 rows, each {VirtualKeyboardLayout.RowUnits} units wide ({string.Join(", ", rows.Select(r => r.Sum(k => k.Width)))})");
            var keys = VirtualKeyboardLayout.Keys.ToList();
            Check(keys.Select(k => k.Id).Distinct().Count() == keys.Count, $"layout: {keys.Count} keys with unique ids");
            var unknown = keys.Where(k => k.Kind != VirtualKeyKind.Voice && !RemoteKeyProtocol.IsKnown(k.Code)).Select(k => k.Id).ToList();
            Check(unknown.Count == 0, $"layout: every key is one the agent accepts ({(unknown.Count == 0 ? "all" : string.Join(", ", unknown))}), " +
                                      $"plus the Talk key ({keys.Count(k => k.Kind == VirtualKeyKind.Voice)})");
            string letters = string.Concat(keys.Where(k => k.Kind == VirtualKeyKind.Character && k.Id.Length == 1 && char.IsLetter(k.Id[0])).Select(k => k.Label));
            Check(letters == "qwertyuiopasdfghjklzxcvbnm", $"layout: QWERTY letter order ({letters})");

            Expect("a", "A", KeyModifiers.None, KeyStroke.Text("a"));
            Expect("Shift+a", "A", KeyModifiers.Shift, KeyStroke.Text("A"));
            Expect("Shift+1", "1", KeyModifiers.Shift, KeyStroke.Text("!"));
            Expect("Shift+\\", "Backslash", KeyModifiers.Shift, KeyStroke.Text("|"));
            Expect("Space", "Space", KeyModifiers.None, KeyStroke.Text(" "));
            Expect("Ctrl+C", "C", KeyModifiers.Ctrl, KeyStroke.Down("Ctrl"), KeyStroke.Down("C"), KeyStroke.Up("C"), KeyStroke.Up("Ctrl"));
            Expect("Ctrl+Shift+Z", "Z", KeyModifiers.Ctrl | KeyModifiers.Shift, KeyStroke.Down("Ctrl"), KeyStroke.Down("Shift"),
                KeyStroke.Down("Z"), KeyStroke.Up("Z"), KeyStroke.Up("Shift"), KeyStroke.Up("Ctrl"));
            Expect("Enter", "Enter", KeyModifiers.None, KeyStroke.Down("Enter"), KeyStroke.Up("Enter"));
            Expect("Backspace", "Backspace", KeyModifiers.None, KeyStroke.Down("Backspace"), KeyStroke.Up("Backspace"));
            Expect("Shift+Left", "Left", KeyModifiers.Shift, KeyStroke.Down("Shift"), KeyStroke.Down("Left"), KeyStroke.Up("Left"), KeyStroke.Up("Shift"));
            Expect("Alt+F", "F", KeyModifiers.Alt, KeyStroke.Down("Alt"), KeyStroke.Down("F"), KeyStroke.Up("F"), KeyStroke.Up("Alt"));
            Expect("Shift on its own", "Shift", KeyModifiers.None);
        }

        static void Expect(string what, string id, KeyModifiers modifiers, params KeyStroke[] expected)
        {
            var key = VirtualKeyboardLayout.Find(id);
            var strokes = VirtualKeyboardLayout.Resolve(key, modifiers);
            Check(key != null && strokes.SequenceEqual(expected),
                $"layout: {what} sends {(strokes.Count == 0 ? "nothing" : string.Join(", ", strokes))}");
        }

        static void PhysicalKeyChecks()
        {
            bool mapped = PhysicalKeyMap.TryGetCode(Key.A, out var a) && a == "A"
                          && PhysicalKeyMap.TryGetCode(Key.Digit0, out var zero) && zero == "0"
                          && PhysicalKeyMap.TryGetCode(Key.Digit1, out var one) && one == "1"
                          && PhysicalKeyMap.TryGetCode(Key.Enter, out var enter) && enter == "Enter"
                          && PhysicalKeyMap.TryGetCode(Key.RightCtrl, out var ctrl) && ctrl == "Ctrl"
                          && PhysicalKeyMap.TryGetCode(Key.UpArrow, out var upArrow) && upArrow == "Up";
            Check(mapped, "physical keys: letters, digits, Enter, Ctrl and arrows map to the protocol's names");
            Check(!PhysicalKeyMap.TryGetCode(Key.LeftWindows, out _) && !PhysicalKeyMap.TryGetCode(Key.RightCommand, out _)
                  && !PhysicalKeyMap.TryGetCode(Key.PrintScreen, out _), "physical keys: Windows/Command and Print Screen aren't forwarded");
            var unknown = PhysicalKeyMap.Codes.Values.Where(c => !RemoteKeyProtocol.IsKnown(c)).Distinct().ToList();
            Check(unknown.Count == 0 && PhysicalKeyMap.TypesCharacter(Key.Q) && PhysicalKeyMap.TypesCharacter(Key.Space)
                  && !PhysicalKeyMap.TypesCharacter(Key.Enter) && !PhysicalKeyMap.TypesCharacter(Key.LeftShift),
                $"physical keys: all map to keys the agent accepts; Q and Space type characters, Enter and Shift don't");
        }

        static IEnumerator Routing(LauncherWindow launcher, WindowManager manager, MouseWindowInput mouse, Camera cam, List<SpatialWindow> opened)
        {
            var remoteKeyboard = launcher.KeyboardInput;
            var sinkA = new RecordingSink();
            var sinkB = new RecordingSink();
            var a = OpenStream(manager, "Stream A", sinkA);
            opened.Add(a.Window);
            yield return Settle;

            // The keyboard opens under the stream window it will type into.
            int count = manager.Windows.Count;
            var keyboardWindow = launcher.OpenKeyboard();
            opened.Add(keyboardWindow);
            yield return Settle;
            var keyboard = remoteKeyboard.KeyboardContent;
            Check(keyboardWindow != null && keyboardWindow.Title == "Keyboard" && manager.Windows.Count == count + 1 && keyboard != null,
                "the launcher opens a Keyboard window");
            if (keyboard == null) yield break;
            Check(keyboardWindow.transform.position.y < a.Window.transform.position.y - a.Window.Size.y * 0.5f,
                $"it opens below the stream window ({keyboardWindow.transform.position.y:F2} m vs {a.Window.transform.position.y:F2} m)");
            Check(launcher.OpenKeyboard() == keyboardWindow && manager.Windows.Count == count + 1, "opening it again brings the same window back");
            Check(remoteKeyboard.Target == a && keyboard.StatusText == "Typing into: Stream A",
                $"with the keyboard in front, keys go to the stream window focused before it (\"{keyboard.StatusText}\")");

            yield return ClickKey(manager, mouse, cam, keyboard, "H");
            yield return ClickKey(manager, mouse, cam, keyboard, "I");
            Check(sinkA.Strokes.SequenceEqual(new[] { KeyStroke.Text("h"), KeyStroke.Text("i") }) && manager.FocusedWindow == keyboardWindow,
                $"clicking h, i types \"hi\" into Stream A ({sinkA.Describe()})");
            Check(remoteKeyboard.IsForwardingPhysicalKeys && KeyboardShortcuts.Blocked,
                "in the editor the physical keyboard is forwarded too, and single-key shortcuts stand down");

            // A second stream window: focusing it moves the keyboard's target.
            var b = OpenStream(manager, "Stream B", sinkB);
            opened.Add(b.Window);
            yield return Settle;
            Check(remoteKeyboard.Target == b, "focusing another stream window makes it the target");
            manager.Focus(keyboardWindow);
            yield return Settle;
            Check(remoteKeyboard.Target == b && keyboard.StatusText == "Typing into: Stream B",
                $"and it stays the target when the keyboard comes to the front (\"{keyboard.StatusText}\")");

            sinkA.Strokes.Clear();
            yield return ClickKey(manager, mouse, cam, keyboard, "Shift");
            Check((keyboard.Modifiers & KeyModifiers.Shift) != 0 && sinkB.Strokes.Count == 0, "Shift toggles on and sends nothing by itself");
            yield return ClickKey(manager, mouse, cam, keyboard, "H");
            Check(sinkB.Strokes.SequenceEqual(new[] { KeyStroke.Text("H") }) && keyboard.Modifiers == KeyModifiers.None,
                $"Shift then h types \"H\" into Stream B and Shift turns off ({sinkB.Describe()})");

            sinkB.Strokes.Clear();
            yield return ClickKey(manager, mouse, cam, keyboard, "Ctrl");
            yield return ClickKey(manager, mouse, cam, keyboard, "C");
            yield return ClickKey(manager, mouse, cam, keyboard, "Enter");
            yield return ClickKey(manager, mouse, cam, keyboard, "Backspace");
            var expected = new[]
            {
                KeyStroke.Down("Ctrl"), KeyStroke.Down("C"), KeyStroke.Up("C"), KeyStroke.Up("Ctrl"),
                KeyStroke.Down("Enter"), KeyStroke.Up("Enter"), KeyStroke.Down("Backspace"), KeyStroke.Up("Backspace"),
            };
            Check(sinkB.Strokes.SequenceEqual(expected), $"Ctrl, c, Enter, Backspace send Ctrl+C, Enter, Backspace to Stream B ({sinkB.Describe()})");
            Check(sinkA.Strokes.Count == 0, $"Stream A received nothing meanwhile ({sinkA.Describe()})");

            // Another (non-stream) window in front: keys still go to the front-most stream window, physical keys don't.
            var blank = manager.Spawn("Blank");
            opened.Add(blank);
            yield return Settle;
            Check(remoteKeyboard.Target == b && !remoteKeyboard.IsForwardingPhysicalKeys && !KeyboardShortcuts.Blocked,
                "with a non-stream window in front, the physical keyboard isn't forwarded and shortcuts work again");

            // Closing stream windows moves the target back, then to none.
            manager.Close(b.Window);
            yield return Settle;
            Check(remoteKeyboard.Target == a, "closing Stream B makes Stream A the target again");
            manager.Close(a.Window);
            yield return Settle;
            sinkA.Strokes.Clear();
            manager.Focus(keyboardWindow);
            yield return Settle;
            yield return ClickKey(manager, mouse, cam, keyboard, "Q");
            Check(remoteKeyboard.Target == null && sinkA.Strokes.Count == 0 && keyboard.StatusText.StartsWith("Open a laptop window"),
                $"with no stream window, keys go nowhere (\"{keyboard.StatusText}\")");
        }

        static RemoteStreamContent OpenStream(WindowManager manager, string title, RecordingSink sink)
        {
            var window = manager.Spawn(title, new Vector2(0.5f, 0.32f));
            var content = new GameObject().AddComponent<RemoteStreamContent>();
            content.Init(window, manager.WindowMaterial, sink, new Vector2Int(1920, 1080));
            return content;
        }

        // Clicks a key like a user would (press and release through the mouse input), checking the ray hits that key.
        static IEnumerator ClickKey(WindowManager manager, MouseWindowInput mouse, Camera cam, VirtualKeyboardContent keyboard, string id)
        {
            var button = keyboard.Button(id);
            if (button == null)
            {
                Check(false, $"the keyboard has a {id} key");
                yield break;
            }
            Vector2 screen = cam.WorldToScreenPoint(button.transform.position);
            bool onKey = WindowRaycast.TryHit(cam.ScreenPointToRay(screen), 20f, ~0, out _, out var part) && part.gameObject == button.gameObject;
            if (!onKey) Check(false, $"the {id} key is visible to click (hit {(part != null ? part.name : "nothing")})");
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
            yield return 0.15f;
        }
    }
}
