#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>Speech to text for <see cref="VoiceTyping"/>: start listening, stop, then take the final text.</summary>
    public interface ISpeechRecognizer
    {
        /// <summary>Whether listening can work at all here (permissions, platform); <see cref="Status"/> says why not.</summary>
        bool IsAvailable { get; }
        bool IsListening { get; }
        /// <summary>Stopped listening, waiting for the final text.</summary>
        bool IsFinishing { get; }
        /// <summary>What has been heard so far while listening.</summary>
        string Partial { get; }
        /// <summary>Recognising on the device (nothing leaves the phone).</summary>
        bool OnDevice { get; }
        string Status { get; }

        /// <summary>Starts listening in a language (BCP 47, e.g. "en-US"); false (see <see cref="TakeError"/>) if it can't.</summary>
        bool Start(string language, bool preferOnDevice);
        /// <summary>Stops listening; the final text arrives through <see cref="TakeFinal"/>.</summary>
        void Stop();
        void Cancel();
        /// <summary>The next final text, or null.</summary>
        string TakeFinal();
        string TakeError();
    }

    /// <summary>
    /// The editor and other platforms: no microphone, but the whole path can be exercised. Listening "hears"
    /// <see cref="Phrase"/>: it shows as partial text while listening and becomes the final text on Stop.
    /// </summary>
    public class EditorSpeechStub : ISpeechRecognizer
    {
        public string Phrase { get; set; } = "Hello from voice typing";
        /// <summary>The language and on-device choice of the last Start (for tests).</summary>
        public string LastLanguage { get; private set; }
        public bool LastPreferOnDevice { get; private set; }

        public bool IsAvailable => true;
        public bool IsListening { get; private set; }
        public bool IsFinishing => false;
        public string Partial => IsListening ? Phrase : "";
        public bool OnDevice => LastPreferOnDevice;
        public string Status => IsListening ? "Listening (editor stub)" : "Editor stub: types a fixed phrase";

        string final;

        public bool Start(string language, bool preferOnDevice)
        {
            LastLanguage = language;
            LastPreferOnDevice = preferOnDevice;
            IsListening = true;
            return true;
        }

        public void Stop()
        {
            if (!IsListening) return;
            IsListening = false;
            final = Phrase;
        }

        public void Cancel() => IsListening = false;

        public string TakeFinal()
        {
            string text = final;
            final = null;
            return text;
        }

        public string TakeError() => null;

        /// <summary>Hears the phrase at once, as if a whole push-to-talk had happened.</summary>
        public void HearPhrase() => final = Phrase;
    }

#if UNITY_IOS && !UNITY_EDITOR
    /// <summary>
    /// iOS: Apple's Speech framework through Assets/Plugins/iOS/SpatialSpeech.mm, on the device when the language supports
    /// it. Asks for the speech-recognition and microphone permissions on first use. Untested on a device so far.
    /// </summary>
    public class IosSpeechRecognizer : ISpeechRecognizer
    {
        [DllImport("__Internal")] static extern int SpatialSpeech_AuthorizationStatus();
        [DllImport("__Internal")] static extern int SpatialSpeech_MicrophonePermission();
        [DllImport("__Internal")] static extern void SpatialSpeech_RequestAuthorization();
        [DllImport("__Internal")] static extern bool SpatialSpeech_Start(string locale, bool preferOnDevice);
        [DllImport("__Internal")] static extern void SpatialSpeech_Stop();
        [DllImport("__Internal")] static extern void SpatialSpeech_Cancel();
        [DllImport("__Internal")] static extern int SpatialSpeech_State();
        [DllImport("__Internal")] static extern bool SpatialSpeech_IsOnDevice();
        [DllImport("__Internal")] static extern string SpatialSpeech_CopyPartial();
        [DllImport("__Internal")] static extern string SpatialSpeech_TakeFinal();
        [DllImport("__Internal")] static extern string SpatialSpeech_TakeError();

        bool asked;

        public bool IsAvailable => SpatialSpeech_AuthorizationStatus() != 1 && SpatialSpeech_AuthorizationStatus() != 2
                                   && SpatialSpeech_MicrophonePermission() != 1;
        public bool IsListening => SpatialSpeech_State() == 1;
        public bool IsFinishing => SpatialSpeech_State() == 2;
        public string Partial => SpatialSpeech_CopyPartial() ?? "";
        public bool OnDevice => SpatialSpeech_IsOnDevice();

        public string Status
        {
            get
            {
                int speech = SpatialSpeech_AuthorizationStatus();
                if (speech == 1 || speech == 2) return "Speech recognition is off for this app (iOS Settings > Privacy)";
                if (SpatialSpeech_MicrophonePermission() == 1) return "The microphone is off for this app (iOS Settings > Privacy)";
                if (speech == 0) return "Hold Talk once to allow speech recognition";
                return IsListening ? (OnDevice ? "Listening (on this phone)" : "Listening (Apple's servers)") : "Hold Talk to speak";
            }
        }

        public bool Start(string language, bool preferOnDevice)
        {
            if (SpatialSpeech_AuthorizationStatus() == 0 || SpatialSpeech_MicrophonePermission() == 0)
            {
                // The first time, ask; the prompts are answered before the next Talk.
                if (!asked) SpatialSpeech_RequestAuthorization();
                asked = true;
                return false;
            }
            return SpatialSpeech_Start(language, preferOnDevice);
        }

        public void Stop() => SpatialSpeech_Stop();
        public void Cancel() => SpatialSpeech_Cancel();
        public string TakeFinal() => SpatialSpeech_TakeFinal();
        public string TakeError() => SpatialSpeech_TakeError();
    }
#endif
}
