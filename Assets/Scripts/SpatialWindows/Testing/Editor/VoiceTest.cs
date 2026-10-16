using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for voice typing with the editor stub (the iOS recognizer needs a phone): the keyboard's Talk key held
    /// and released through the mouse input types the heard text into the front-most laptop window, in the Settings
    /// language; a focused local text field takes it instead; the editor key types it at once and isn't forwarded to the
    /// laptop; with nowhere to type, it says so. Results use a [VoiceTest] prefix.
    /// </summary>
    static class VoiceTest
    {
        const float Settle = 0.5f;

        [MenuItem("Tools/Spatial Windows/Test Voice Typing")]
        static void Run() => EditorTestRunner.Run("VoiceTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Voice Typing", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        sealed class RecordingSink : IRemoteInputSink, IRemoteKeyboardSink
        {
            public readonly List<string> Texts = new List<string>();
            public readonly List<string> Keys = new List<string>();
            public void PointerMove(Vector2 position) { }
            public void PointerDown(Vector2 position) { }
            public void PointerUp(Vector2 position) { }
            public void Scroll(Vector2 position, float delta) { }
            public void Key(string code, bool down) => Keys.Add(code);
            public void Text(string text) => Texts.Add(text);
        }

        static IEnumerator Steps()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var voice = Object.FindFirstObjectByType<VoiceTyping>();
            var cam = Camera.main;
            var stub = voice != null ? voice.Recognizer as EditorSpeechStub : null;
            if (launcher == null || manager == null || mouse == null || voice == null || cam == null || stub == null)
            {
                Check(false, "scene has a LauncherWindow, WindowManager, MouseWindowInput, main camera and VoiceTyping with the editor stub");
                yield break;
            }

            var talkKey = VirtualKeyboardLayout.Find("Talk");
            Check(talkKey != null && talkKey.Kind == VirtualKeyKind.Voice && VirtualKeyboardLayout.Resolve(talkKey, KeyModifiers.None).Count == 0,
                "the keyboard layout has a Talk key, which sends no keys itself");

            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var opened = new List<SpatialWindow>();
            var delivered = new List<(string text, string where)>();
            System.Action<string, string> record = (text, where) => delivered.Add((text, where));
            voice.Delivered += record;
            try
            {
                yield return Body(launcher, manager, mouse, voice, stub, cam, opened, delivered);
            }
            finally
            {
                voice.Delivered -= record;
                WindowTextField.BlurAll();
                foreach (var window in opened)
                    if (window != null) manager.Close(window);
                if (launcher.KeyboardInput.KeyboardWindow != null) manager.Close(launcher.KeyboardInput.KeyboardWindow);
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static IEnumerator Body(LauncherWindow launcher, WindowManager manager, MouseWindowInput mouse, VoiceTyping voice,
            EditorSpeechStub stub, Camera cam, List<SpatialWindow> opened, List<(string text, string where)> delivered)
        {
            var sink = new RecordingSink();
            var streamWindow = manager.Spawn("Voice target", new Vector2(0.5f, 0.32f));
            opened.Add(streamWindow);
            new GameObject().AddComponent<RemoteStreamContent>().Init(streamWindow, manager.WindowMaterial, sink, new Vector2Int(1920, 1080));
            yield return Settle;
            launcher.OpenKeyboard();
            yield return Settle;
            var keyboard = launcher.KeyboardInput.KeyboardContent;
            var talk = keyboard != null ? keyboard.Button("Talk") as WindowHoldButton : null;
            Check(talk != null, "the virtual keyboard has a push-to-talk Talk key");
            if (talk == null) yield break;

            // Hold Talk: listening, in the Settings language, with what is heard on the status line.
            Vector2 screen = cam.WorldToScreenPoint(talk.transform.position);
            mouse.PressAt(screen);
            yield return 0.3f;
            var settings = SettingsStore.Current.voice;
            Check(voice.IsListening && talk.IsHeld && keyboard.StatusText.Contains(stub.Phrase),
                $"holding Talk listens and shows what it hears (\"{keyboard.StatusText}\")");
            Check(stub.LastLanguage == settings.language && stub.LastPreferOnDevice == settings.preferOnDevice,
                $"it listens in the Settings language ({stub.LastLanguage}, on-device {stub.LastPreferOnDevice})");

            // Let go: the text is typed into the laptop window keys go to.
            mouse.ReleaseAt(screen);
            yield return 0.3f;
            Check(!voice.IsListening && sink.Texts.SequenceEqual(new[] { stub.Phrase }) && delivered.Any(d => d.where == "Voice target"),
                $"letting go types \"{stub.Phrase}\" into the front-most laptop window as text ({string.Join(" | ", sink.Texts)}; \"{voice.Status}\")");

            // The editor key types the phrase at once, and isn't forwarded to the laptop as a key.
            manager.Focus(streamWindow);
            yield return Settle;
            sink.Texts.Clear();
            sink.Keys.Clear();
            yield return Tap(voice.EditorKey);
            Check(sink.Texts.SequenceEqual(new[] { stub.Phrase }) && !sink.Keys.Contains(voice.EditorKey.ToString()),
                $"the editor key ({voice.EditorKey}) types the phrase and isn't forwarded as a key (keys sent: {sink.Keys.Count})");

            // A focused local text field takes the text instead.
            var field = Field(manager, opened);
            yield return Settle;
            field.Focus();
            sink.Texts.Clear();
            yield return Tap(voice.EditorKey);
            manager.Focus(launcher.KeyboardInput.KeyboardWindow); // the field stays focused for typing
            yield return Settle;
            mouse.PressAt(screen = cam.WorldToScreenPoint(talk.transform.position));
            yield return 0.2f;
            mouse.ReleaseAt(screen);
            yield return 0.3f;
            Check(field.Text == stub.Phrase + stub.Phrase && sink.Texts.Count == 0 && delivered.Last().where == "text field",
                $"with a text field focused, the key and Talk type into it (\"{field.Text}\"), not the laptop");

            // Nowhere to type.
            WindowTextField.BlurAll();
            manager.Close(streamWindow);
            yield return Settle;
            int before = delivered.Count;
            voice.Deliver("lost words");
            Check(delivered.Count == before && voice.Status.StartsWith("Nothing to type into"),
                $"with no laptop window and no text field, it says so (\"{voice.Status}\")");
        }

        // A bare window with one text field, as content would build it.
        static WindowTextField Field(WindowManager manager, List<SpatialWindow> opened)
        {
            var window = manager.Spawn("Voice field", new Vector2(0.4f, 0.15f));
            opened.Add(window);
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(quad.GetComponent<MeshCollider>());
            quad.transform.SetParent(window.ContentRoot, false);
            quad.transform.localScale = new Vector3(0.3f, 0.05f, 1f);
            quad.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, 0.001f);
            var material = new Material(manager.WindowMaterial);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            var label = new GameObject("Text").AddComponent<TextMeshPro>();
            label.transform.SetParent(window.ContentRoot, false);
            label.fontSize = 0.2f;
            var field = quad.AddComponent<WindowTextField>();
            field.Register(window);
            field.Init(label, material, "Say something");
            return field;
        }

        static IEnumerator Tap(Key key)
        {
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(key));
            yield return 0.1f;
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
            yield return 0.2f;
        }
    }
}
