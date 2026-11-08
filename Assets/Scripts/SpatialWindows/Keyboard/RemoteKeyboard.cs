using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Keyboard input for laptop stream windows. <see cref="Open"/> shows the virtual keyboard
    /// (<see cref="VirtualKeyboardContent"/>), usable with the mouse or a pinch, docked under the window it types into
    /// (<see cref="WindowManager.Dock"/>): pressing its keys never takes focus from that window, so neither one moves
    /// back or forth, and it follows that window around. <see cref="OpenFor"/> with automatic: true is how it pops up by
    /// itself when a text field gets focus (a local <see cref="WindowTextField"/>, or a text field on the laptop, which
    /// the agent reports); <see cref="CloseIfAutomatic"/> puts such a keyboard away again when the focus leaves. Its keys
    /// go to the window it is docked to (else the front-most stream window, <see cref="KeyboardRouting"/>). In the editor the physical keyboard is also forwarded while a stream window
    /// (or the virtual keyboard) has focus; single-key shortcuts stand down meanwhile (<see cref="KeyboardShortcuts"/>).
    /// What a stream window does with the keys is up to its sink: the laptop agent types them only when started with
    /// --input --keyboard.
    /// </summary>
    public class RemoteKeyboard : MonoBehaviour
    {
        [SerializeField] WindowManager manager;
        [Tooltip("In the editor, forward the physical keyboard to the focused laptop window.")]
        [SerializeField] bool forwardPhysicalKeyboard = true;
        [Tooltip("Gap between the laptop window and the keyboard opened under it, in metres.")]
        [SerializeField] float gapBelowTarget = 0.1f;

        /// <summary>The keyboard window size: the content size that gives 44 mm keys, plus the window padding.</summary>
        Vector2 keyboardSize => VirtualKeyboardContent.WindowSize;

        /// <summary>The enabled instance, for <see cref="KeyboardShortcuts"/>.</summary>
        public static RemoteKeyboard Current { get; private set; }

        public SpatialWindow KeyboardWindow { get; private set; }
        public VirtualKeyboardContent KeyboardContent { get; private set; }
        /// <summary>The stream window keys go to now, or null: the one the keyboard is docked to, else the front-most.</summary>
        public RemoteStreamContent Target
        {
            get
            {
                var owner = KeyboardWindow != null ? KeyboardWindow.DockedTo : null;
                var docked = owner != null ? owner.GetComponentInChildren<RemoteStreamContent>() : null;
                if (docked != null) return docked;
                return manager != null ? KeyboardRouting.FindTarget(manager.Windows) : null;
            }
        }

        /// <summary>The keyboard was opened by a text field getting focus (not by the Keys button), so it closes when the focus leaves.</summary>
        public bool OpenedAutomatically { get; private set; }

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

        void OnEnable()
        {
            Current = this;
            WindowTextField.FocusChanged += OnTextFieldFocus;
        }

        void OnDisable()
        {
            if (Current == this) Current = null;
            WindowTextField.FocusChanged -= OnTextFieldFocus;
            Subscribe(null);
            ReleaseHeld();
        }

        /// <summary>The Keys button: opens the keyboard under the front-most laptop window (or wherever it is already).</summary>
        public SpatialWindow Open()
        {
            if (KeyboardWindow != null)
            {
                OpenedAutomatically = false; // asked for: it stays until closed
                manager.Focus(KeyboardWindow);
                return KeyboardWindow;
            }
            var target = Target;
            return OpenFor(target != null ? target.Window : null, automatic: false);
        }

        /// <summary>
        /// Opens the keyboard docked under <paramref name="owner"/> (or moves it there if it is open under another
        /// window). <paramref name="automatic"/>: a text field got focus; the keyboard then closes again when it loses it.
        /// </summary>
        public SpatialWindow OpenFor(SpatialWindow owner, bool automatic)
        {
            if (manager == null) return null;
            if (owner == KeyboardWindow) owner = null;
            if (KeyboardWindow == null)
            {
                KeyboardWindow = manager.Spawn("Keyboard", keyboardSize);
                if (KeyboardWindow == null) return null;
                KeyboardContent = new GameObject().AddComponent<VirtualKeyboardContent>();
                KeyboardContent.Init(KeyboardWindow, manager.WindowMaterial, Send, () => Target, GetComponent<VoiceTyping>());
                manager.WindowClosed += OnWindowClosed;
                OpenedAutomatically = automatic;
            }
            else if (!automatic) OpenedAutomatically = false;
            if (owner != null && KeyboardWindow.DockedTo != owner)
            {
                var below = owner.transform;
                float drop = (owner.Size.y + keyboardSize.y) * 0.5f + gapBelowTarget;
                KeyboardWindow.MoveTo(below.position - below.up * drop);
                manager.Dock(KeyboardWindow, owner);
            }
            else if (owner == null && KeyboardWindow.DockedTo == null) manager.Focus(KeyboardWindow);
            return KeyboardWindow;
        }

        /// <summary>Closes the keyboard if a text field opened it (and it is docked to <paramref name="owner"/>, when given).</summary>
        public void CloseIfAutomatic(SpatialWindow owner = null)
        {
            if (KeyboardWindow == null || !OpenedAutomatically) return;
            if (owner != null && KeyboardWindow.DockedTo != owner) return;
            if (VoiceTyping.Current != null && VoiceTyping.Current.IsListening) return; // still dictating
            manager.Close(KeyboardWindow);
        }

        void OnWindowClosed(SpatialWindow window)
        {
            if (window != KeyboardWindow) return;
            manager.WindowClosed -= OnWindowClosed;
            KeyboardWindow = null;
            KeyboardContent = null;
            OpenedAutomatically = false;
        }

        // A local text field (Laptop Shell search, a Settings field) got or lost focus.
        void OnTextFieldFocus(WindowTextField field)
        {
            if (field != null)
            {
                if (field.Window != null && field.Window != KeyboardWindow) OpenFor(field.Window, automatic: true);
            }
            else CloseIfAutomatic();
        }

        /// <summary>
        /// Sends key strokes to the current target; returns false (and sends nothing) without one. A focused local text
        /// field (the Laptop Shell's search, a Settings field) takes them instead, as it does voice typing.
        /// </summary>
        public bool Send(IReadOnlyList<KeyStroke> strokes)
        {
            var field = WindowTextField.Focused;
            if (field != null)
            {
                foreach (var stroke in strokes)
                {
                    if (WindowTextField.Focused != field) break; // Enter or Esc left the field: the rest isn't for it
                    field.ApplyStroke(stroke);
                }
                return true;
            }
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
            var reserved = VoiceTyping.ReservedKey;
            foreach (var pair in PhysicalKeyMap.Codes)
            {
                if (pair.Key == reserved) continue; // the editor's voice-typing key
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
