using System.Collections.Generic;
using SpatialLauncher.LaptopShell;
using SpatialLauncher.SpatialWindows;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.Switcher
{
    /// <summary>
    /// The app switcher: brings a window forward, from the headset's own windows and the laptop's (a laptop window that
    /// isn't streamed yet opens as a stream window, through its Laptop Shell). Ways in:
    /// - the TRIGGER (ZOOM) button, when Settings > Display > TRIGGER button is Switcher: a press opens the overview with
    ///   the next window selected, each press moves on, and after <see cref="SwitcherSelection.ConfirmDelay"/> without a
    ///   press the selection comes forward (like letting go of Alt+Tab). While the consent prompt asks, TRIGGER stays ALLOW;
    /// - a pinch held on nothing (<see cref="PinchHoldRecognizer"/>), the Home screen's Switch tile, or F7: the overview,
    ///   which waits for a pinch or click on a card;
    /// - F6: quick switch to the previous window without the overview (repeat within a second to go further back).
    /// The overview's own window, the consent prompt and hidden windows stay as they are; switching to a window while
    /// everything is hidden shows the windows again.
    /// </summary>
    public class AppSwitcher : MonoBehaviour
    {
        [SerializeField] WindowManager manager;
        [SerializeField] Key cycleKey = Key.F6;
        [SerializeField] Key overviewKey = Key.F7;

        public static AppSwitcher Current { get; private set; }

        public WindowManager Manager
        {
            get => manager;
            set => manager = value;
        }

        public SpatialWindow OverviewWindow { get; private set; }
        public SwitcherContent Overview { get; private set; }
        public SwitcherSelection Selection { get; } = new SwitcherSelection();
        public IReadOnlyList<SwitcherEntry> Entries => entries;
        public bool IsOpen => OverviewWindow != null;
        /// <summary>Test seam: unscaled seconds.</summary>
        public System.Func<float> Clock = () => Time.unscaledTime;

        public const string TriggerHint = "TRIGGER: next window. Stop pressing to switch. Pinch a card to switch now.";
        public const string PinchHint = "Pinch a window to switch to it.";

        List<SwitcherEntry> entries = new List<SwitcherEntry>();
        readonly QuickCycle<SpatialWindow> quick = new QuickCycle<SpatialWindow>();

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
        }

        void OnEnable() => Current = this;

        void OnDisable()
        {
            if (Current == this) Current = null;
        }

        // --- Entries -------------------------------------------------------------------------------------------------

        /// <summary>The laptops whose windows can be listed: every open Laptop Shell with control.</summary>
        public static IEnumerable<ShellSource> Shells()
        {
            foreach (var shell in LaptopShellContent.Open)
                if (shell != null && shell.Session != null) yield return new ShellSource(shell.Client, shell.Session.LaptopName);
        }

        List<SwitcherEntry> BuildEntries()
        {
            var exclude = new HashSet<SpatialWindow>();
            if (OverviewWindow != null) exclude.Add(OverviewWindow);
            var gate = ConsentGate.Current;
            if (gate != null && gate.Modal != null) exclude.Add(gate.Modal);
            return SwitcherModel.Build(manager.Windows, exclude, Shells(), StreamedId);
        }

        static string StreamedId(SpatialWindow window)
        {
            var stream = window != null ? window.GetComponentInChildren<RemoteStreamContent>() : null;
            return stream != null && stream.IsLinked && stream.Target != null ? stream.Target.id : null;
        }

        // --- Overview ------------------------------------------------------------------------------------------------

        /// <summary>Opens the overview (or closes it if open). <paramref name="byTrigger"/> switches by itself after a pause.</summary>
        public void ToggleOverview(bool byTrigger = false)
        {
            if (IsOpen) CloseOverview();
            else OpenOverview(byTrigger);
        }

        public bool OpenOverview(bool byTrigger = false)
        {
            if (manager == null) return false;
            if (IsOpen)
            {
                manager.Focus(OverviewWindow);
                return true;
            }
            foreach (var shell in Shells()) shell.Client?.RefreshRunning();
            entries = BuildEntries();
            OverviewWindow = manager.Spawn("Switch windows", SwitcherContent.WindowSize);
            if (OverviewWindow == null) return false;
            Overview = new GameObject().AddComponent<SwitcherContent>();
            Overview.Init(OverviewWindow, manager.WindowMaterial);
            Overview.Picked += Pick;
            Selection.Begin(entries.Count, Clock(), byTrigger);
            Overview.Show(entries, Selection.Selected, byTrigger ? TriggerHint : PinchHint);
            return true;
        }

        public void CloseOverview()
        {
            Selection.End();
            var window = OverviewWindow;
            OverviewWindow = null;
            Overview = null;
            if (window != null) manager.Close(window);
        }

        /// <summary>The TRIGGER (ZOOM) button in Switcher mode: open the overview, or move to the next window.</summary>
        public void TriggerPressed()
        {
            if (!IsOpen)
            {
                OpenOverview(byTrigger: true);
                return;
            }
            if (!Selection.AutoConfirm) Selection.Begin(entries.Count, Clock(), true); // a press takes over a pinch-opened one
            else Selection.Next(Clock());
            Overview.Select(Selection.Selected);
            Overview.Show(entries, Selection.Selected, TriggerHint);
        }

        /// <summary>A card was picked (pinch, click), or the selection timed out.</summary>
        public void Pick(int index)
        {
            if (index < 0 || index >= entries.Count) return;
            var entry = entries[index];
            CloseOverview();
            SwitchTo(entry);
        }

        /// <summary>Brings an entry forward: focuses a headset window, or opens a laptop window as a stream.</summary>
        public bool SwitchTo(SwitcherEntry entry)
        {
            if (entry == null) return false;
            if (!entry.IsLaptop)
            {
                if (entry.Window == null) return false;
                if (manager.WindowsHidden) manager.ShowAll();
                manager.Focus(entry.Window);
                return true;
            }
            return entry.Client != null && entry.Client.OpenWindow(entry.LaptopWindowId, entry.Title, entry.Minimized);
        }

        /// <summary>F6: the previous window comes forward (repeat soon after to go further back), no overview.</summary>
        public SpatialWindow CycleNext()
        {
            if (manager == null) return null;
            var order = new List<SpatialWindow>();
            foreach (var entry in BuildEntries())
                if (!entry.IsLaptop) order.Add(entry.Window);
            var next = quick.Step(order, Clock());
            for (int i = 0; i < order.Count && next != null && !Alive(next); i++) next = quick.Step(order, Clock());
            if (next == null || !Alive(next)) return null;
            if (manager.WindowsHidden) manager.ShowAll();
            manager.Focus(next);
            return next;
        }

        bool Alive(SpatialWindow window)
        {
            if (window == null) return false;
            foreach (var open in manager.Windows)
                if (open == window) return true;
            return false;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && !KeyboardShortcuts.Blocked)
            {
                if (keyboard[cycleKey].wasPressedThisFrame) CycleNext();
                if (keyboard[overviewKey].wasPressedThisFrame) ToggleOverview();
            }
            if (OverviewWindow == null)
            {
                if (Selection.Open) Selection.End(); // closed with its own close button
                Overview = null;
                return;
            }
            // Laptop icons arrive after the overview opens; windows may close meanwhile.
            Overview.RefreshIcons();
            if (Selection.Due(Clock())) Pick(Selection.Selected);
        }
    }
}
