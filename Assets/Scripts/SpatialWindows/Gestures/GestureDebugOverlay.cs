using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A text panel for tuning gestures on the device: each hand's state (tracked or held, openness, fist and pinch
    /// timers, apparent size, estimated depth and speed, which recognizer owns it), each recognizer's state and last
    /// output, and the thresholds they are compared with. Turn it on in Settings > Hands > Debug overlay (or F2 in the
    /// editor). Drawn on screen with IMGUI, so in HoloKit's stereo view it spans both eyes: read it in mono mode.
    /// </summary>
    public class GestureDebugOverlay : MonoBehaviour
    {
        [SerializeField] GestureRecognizers recognizers;
        [SerializeField] bool visible;
        [SerializeField] Key toggleKey = Key.F2;
        [SerializeField] int fontSize = 15;

        public bool Visible
        {
            get => visible;
            set => visible = value;
        }

        GUIStyle style;
        float nextText;
        string text = "";

        void Awake()
        {
            if (recognizers == null) recognizers = GetComponent<GestureRecognizers>();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && !KeyboardShortcuts.Blocked && keyboard[toggleKey].wasPressedThisFrame) visible = !visible;
            if (visible && recognizers != null && Time.unscaledTime >= nextText)
            {
                nextText = Time.unscaledTime + 0.1f;
                text = Describe(recognizers);
            }
        }

        void OnGUI()
        {
            if (!visible || string.IsNullOrEmpty(text)) return;
            style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = fontSize, richText = false, wordWrap = false };
            var size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(Screen.width - size.x - 16f, 16f, size.x, size.y), text, style);
        }

        /// <summary>The overlay's text (also used by tests).</summary>
        public static string Describe(GestureRecognizers host)
        {
            var c = host.Config;
            var text = new StringBuilder();
            text.AppendLine($"Gestures  ({c.name}; {(host.IsRunning ? "running" : "idle: hand input not active")})");
            foreach (var hand in GestureRecognizers.Hands)
            {
                var f = host.Features(hand);
                var owner = host.Owner(hand);
                string tracking = !f.Tracked ? "not tracked" : f.Fresh ? "tracked" : $"held {f.LostFor:F2} s";
                text.AppendLine($"{hand,-5}: {tracking}  open {f.State.Openness:F2}{(f.Fist ? $"  FIST {f.FistFor:F2}s" : "")}" +
                                $"{(f.Open ? $"  PALM {f.OpenFor:F2}s" : "")}{(f.Pinched ? $"  PINCH {f.PinchFor:F2}s" : "")}");
                if (f.Tracked)
                    text.AppendLine($"       size {f.State.ApparentSize * Mathf.Rad2Deg:F1}°  depth {f.Depth:F2} m (raw {f.Estimator.RawDepth:F2})" +
                                    $"  speed {f.DepthVelocity:+0.00;-0.00} (peak {f.PeakVelocity:+0.00;-0.00}) m/s  owner {(owner != null ? owner.Name : "-")}");
            }
            foreach (var recognizer in host.Recognizers)
            {
                string last = string.IsNullOrEmpty(recognizer.LastEvent) ? "" : $"   last: {recognizer.LastEvent} ({host.Now - recognizer.LastEventTime:F1} s ago)";
                text.AppendLine($"{recognizer.Name,-17}{(recognizer.Enabled ? "" : " (off)")}: {recognizer.State}{last}");
            }
            text.Append($"fist <= {c.fistOpenness:F2} for {c.fistHoldTime:F2} s   palm >= {c.openPalmOpenness:F2}   push >= {c.pushStartSpeed:F2} m/s" +
                        $"   cross {c.crossHoldTime:F2} s, sweep {c.sweepDistance:F2} m in {c.sweepTime:F2} s   grace {c.trackingGrace:F2} s");
            return text.ToString();
        }
    }
}
