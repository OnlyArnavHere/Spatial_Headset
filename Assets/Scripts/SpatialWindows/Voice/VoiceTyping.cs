using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Voice typing: push to talk (the Talk key on the virtual keyboard: hold it, speak, let go), and the recognised text
    /// is typed into a local text field that has focus (<see cref="WindowTextField"/>), or else sent as text to the
    /// front-most laptop window (<see cref="RemoteKeyboard"/>). The language and the on-device preference come from
    /// Settings > Voice. On iOS it uses Apple's Speech framework (<c>IosSpeechRecognizer</c>); in the editor a stub
    /// (<see cref="EditorSpeechStub"/>) hears a fixed phrase, and <see cref="EditorKey"/> types it at once.
    /// </summary>
    public class VoiceTyping : MonoBehaviour
    {
        // Only the editor stub reads it; serialized fields must exist in every build, so the iOS player's "unused" is expected.
#pragma warning disable CS0414
        [Tooltip("What the editor stub hears.")]
        [SerializeField] string editorPhrase = "Hello from voice typing";
#pragma warning restore CS0414
        [Tooltip("In the editor, this key types the phrase as if it had been spoken.")]
        [SerializeField] Key editorKey = Key.F4;
        [Tooltip("Gives up waiting for the final text this long after Talk is released.")]
        [SerializeField] float finishTimeout = 5f;

        public static VoiceTyping Current { get; private set; }
        /// <summary>The editor key, which physical-key forwarding leaves alone (Key.None when there's no voice typing).</summary>
        public static Key ReservedKey => Current != null && Application.isEditor ? Current.editorKey : Key.None;

        public ISpeechRecognizer Recognizer { get; private set; }
        public Key EditorKey => editorKey;
        public bool IsListening => Recognizer.IsListening;
        public string Partial => Recognizer.Partial;
        /// <summary>One line for the keyboard's status: listening, an error, where the last text went.</summary>
        public string Status { get; private set; } = "";

        /// <summary>Recognised text was typed: (text, where: a window title or "text field").</summary>
        public event Action<string, string> Delivered;

        float finishingSince = -1f;

        void Awake()
        {
#if UNITY_IOS && !UNITY_EDITOR
            Recognizer = new IosSpeechRecognizer();
#else
            Recognizer = new EditorSpeechStub { Phrase = editorPhrase };
#endif
            Status = Recognizer.Status;
        }

        void OnEnable() => Current = this;

        void OnDisable()
        {
            if (Current == this) Current = null;
            Recognizer?.Cancel();
        }

        /// <summary>Replaces the recognizer (tests).</summary>
        public void SetRecognizer(ISpeechRecognizer recognizer)
        {
            Recognizer?.Cancel();
            Recognizer = recognizer;
        }

        /// <summary>Talk pressed: starts listening in the Settings language.</summary>
        public void StartListening()
        {
            var voice = SettingsStore.Current.voice;
            if (Recognizer.Start(voice.language, voice.preferOnDevice)) Status = Recognizer.Status;
            else Status = Recognizer.TakeError() ?? Recognizer.Status;
        }

        /// <summary>Talk released: stops listening; the text is typed when the recognizer has it.</summary>
        public void StopListening()
        {
            if (!Recognizer.IsListening) return;
            Recognizer.Stop();
            finishingSince = Time.unscaledTime;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (Application.isEditor && keyboard != null && keyboard[editorKey].wasPressedThisFrame && Recognizer is EditorSpeechStub stub)
                stub.HearPhrase();

            string text;
            while ((text = Recognizer.TakeFinal()) != null) Deliver(text);
            string error = Recognizer.TakeError();
            if (error != null) Status = error;

            if (finishingSince >= 0f)
            {
                if (!Recognizer.IsFinishing) finishingSince = -1f;
                else if (Time.unscaledTime - finishingSince > finishTimeout)
                {
                    Recognizer.Cancel();
                    finishingSince = -1f;
                    Status = "No result: try again closer to the phone";
                }
            }
            if (Recognizer.IsListening) Status = Recognizer.Status;
        }

        /// <summary>Types text where it belongs: the focused text field, else the front-most laptop window.</summary>
        public void Deliver(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var field = WindowTextField.Focused;
            if (field != null)
            {
                field.SetText(field.Text + text);
                Status = $"Typed {text.Length} characters into the text field";
                Delivered?.Invoke(text, "text field");
                return;
            }
            var stream = RemoteKeyboard.Current != null ? RemoteKeyboard.Current.Target : null;
            if (stream != null && stream.KeyboardSink != null)
            {
                stream.KeyboardSink.Text(text);
                Status = $"Typed {text.Length} characters into {stream.Window.Title}";
                Delivered?.Invoke(text, stream.Window.Title);
                return;
            }
            Status = "Nothing to type into: open a laptop window or select a text field";
        }
    }
}
