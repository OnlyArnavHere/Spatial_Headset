using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Keyboard input for laptop stream windows. <see cref="Open"/> shows the floating virtual keyboard
    /// (<see cref="VirtualKeyboardContent"/>), usable with the mouse or a pinch; its keys go to the front-most stream
    /// window (<see cref="KeyboardRouting"/>). In the editor the physical keyboard is also forwarded while a stream window
    /// (or the virtual keyboard) has focus; single-key shortcuts stand down meanwhile (<see cref="KeyboardShortcuts"/>).
    /// What a stream window does with the keys is up to its sink: the laptop agent types them only when started with
    /// --input --keyboard.
    /// </summary>
    public class RemoteKeyboard : MonoBehaviour
    {
        [SerializeField] WindowManager manager;
        [Tooltip("In the editor, forward the physical keyboard to the focused laptop window.")]
        [SerializeField] bool forwardPhysicalKeyboard = true;
        [SerializeField] Vector2 keyboardSize = new Vector2(0.66f, 0.29f);
        [Tooltip("Gap between the laptop window and the keyboard opened under it, in metres.")]
        [SerializeField] float gapBelowTarget = 0.1f;

        /// <summary>The enabled instance, for <see cref="KeyboardShortcuts"/>.</summary>
        public static RemoteKeyboard Current { get; private set; }

        public SpatialWindow KeyboardWindow { get; private set; }
        public VirtualKeyboardContent KeyboardContent { get; private set; }
        /// <summary>The stream window keys go to now, or null.</summary>
        public RemoteStreamContent Target => manager != null ? KeyboardRouting.FindTarget(manager.Windows) : null;

        public WindowManager Manager
        {
            get => manager;
            set => manager = value;
        }

        public bool ForwardPhysicalKeyboard
        {
            get => forwardPhysicalKeyboard;
            set => forwardPhysicalKeyboard = value;
        }

        /// <summary>
        /// True while physical keys go to a laptop window: in the editor, with a stream window or the virtual keyboard in
        /// front and no text field being typed into.
        /// </summary>
        public bool IsForwardingPhysicalKeys
        {
            get
            {
                if (!forwardPhysicalKeyboard || !Application.isEditor || manager == null || WindowTextField.IsTyping) return false;
                var front = manager.FocusedWindow;
                if (front == null) return false;
                if (front != KeyboardWindow && front.GetComponentInChildren<RemoteStreamContent>() == null) return false;
                return Target?.KeyboardSink != null;
            }
        }

        readonly StringBuilder typed = new StringBuilder();
        // Physical keys sent down, and where, so each release goes where its press went.
        readonly Dictionary<Key, (string code, IRemoteKeyboardSink sink)> held = new Dictionary<Key, (string, IRemoteKeyboardSink)>();
        Keyboard subscribed;

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
        }

        void OnEnable() => Current = this;

        void OnDisable()
        {
            if (Current == this) Current = null;
            Subscribe(null);
            ReleaseHeld();
        }

        /// <summary>Opens the virtual keyboard (under the laptop window it types into), or brings it to the front.</summary>
        public SpatialWindow Open()
        {
            if (KeyboardWindow != null)
            {
                manager.Focus(KeyboardWindow);
                return KeyboardWindow;
            }

            var target = Target;
            KeyboardWindow = manager.Spawn("Keyboard", keyboardSize);
            if (KeyboardWindow == null) return null;
            if (target != null)
            {
                var below = target.Window.transform;
                float drop = (target.Window.Size.y + keyboardSize.y) * 0.5f + gapBelowTarget;
                KeyboardWindow.MoveTo(below.position - below.up * drop);
            }

            KeyboardContent = new GameObject().AddComponent<VirtualKeyboardContent>();
            KeyboardContent.Init(KeyboardWindow, manager.WindowMaterial, Send, () => Target);
            return KeyboardWindow;
        }

        /// <summary>Sends key strokes to the current target; returns false (and sends nothing) without one.</summary>
        public bool Send(IReadOnlyList<KeyStroke> strokes)
        {
            var sink = Target?.KeyboardSink;
            if (sink == null) return false;
            foreach (var stroke in strokes) stroke.SendTo(sink);
            return true;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            Subscribe(keyboard);
            bool forwarding = keyboard != null && IsForwardingPhysicalKeys;
            if (!forwarding)
            {
                typed.Clear();
                ReleaseHeld();
                return;
            }

            var sink = Target.KeyboardSink;
            bool chord = keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed;
            foreach (var pair in PhysicalKeyMap.Codes)
            {
                var control = keyboard[pair.Key];
                if (control.wasPressedThisFrame && !held.ContainsKey(pair.Key))
                {
                    // Characters arrive as text below; with Ctrl or Alt held they're shortcuts, so they go as keys.
                    if (PhysicalKeyMap.TypesCharacter(pair.Key) && !chord) continue;
                    sink.Key(pair.Value, true);
                    held[pair.Key] = (pair.Value, sink);
                }
                else if (!control.isPressed && held.TryGetValue(pair.Key, out var press))
                {
                    press.sink.Key(press.code, false);
                    held.Remove(pair.Key);
                }
            }
            if (typed.Length > 0 && !chord) sink.Text(typed.ToString());
            typed.Clear();
        }

        void Subscribe(Keyboard keyboard)
        {
            if (subscribed == keyboard) return;
            if (subscribed != null) subscribed.onTextInput -= OnTextInput;
            subscribed = keyboard;
            if (subscribed != null) subscribed.onTextInput += OnTextInput;
        }

        void OnTextInput(char character)
        {
            if (!char.IsControl(character)) typed.Append(character);
        }

        void ReleaseHeld()
        {
            foreach (var press in held.Values) press.sink.Key(press.code, false);
            held.Clear();
        }
    }
}
