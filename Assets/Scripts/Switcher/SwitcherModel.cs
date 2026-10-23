using System.Collections.Generic;
using SpatialLauncher.LaptopShell;
using SpatialLauncher.SpatialWindows;
using SpatialLauncher.SpatialWindows.Theme;

namespace SpatialLauncher.Switcher
{
    /// <summary>
    /// One window the switcher can bring forward: a headset window (<see cref="Window"/>), or a laptop window that isn't
    /// streamed yet (<see cref="Client"/> and <see cref="LaptopWindowId"/>), which switching opens as a stream window.
    /// </summary>
    public sealed class SwitcherEntry
    {
        public SpatialWindow Window;
        public LaptopShellClient Client;
        public string LaptopWindowId;
        public bool Minimized;
        public string Title = "";
        public string Detail = "";
        /// <summary>A Material Icons glyph when there is no laptop icon.</summary>
        public string Glyph = UIIcons.Apps;
        /// <summary>The laptop shell id whose icon to show (an app or window id), or null.</summary>
        public string IconId;
        public bool IsLaptop => Window == null;
    }

    /// <summary>A laptop the switcher can list windows of: its shell client while control is granted.</summary>
    public readonly struct ShellSource
    {
        public readonly LaptopShellClient Client;
        public readonly string LaptopName;

        public ShellSource(LaptopShellClient client, string laptopName)
        {
            Client = client;
            LaptopName = laptopName;
        }
    }

    /// <summary>What the switcher lists and in which order.</summary>
    public static class SwitcherModel
    {
        /// <summary>
        /// The headset's windows, most recently used first (the manager keeps them in focus order, front last), without
        /// the switcher itself and the consent prompt; then each laptop's windows that aren't streamed already, in the
        /// laptop's taskbar or Dock order. <paramref name="streamedId"/> gives the laptop window a headset window shows.
        /// </summary>
        public static List<SwitcherEntry> Build(IReadOnlyList<SpatialWindow> focusOrder, ICollection<SpatialWindow> exclude,
            IEnumerable<ShellSource> shells, System.Func<SpatialWindow, string> streamedId)
        {
            var entries = new List<SwitcherEntry>();
            var streamed = new HashSet<string>();
            for (int i = focusOrder.Count - 1; i >= 0; i--)
            {
                var window = focusOrder[i];
                if (window == null || (exclude != null && exclude.Contains(window))) continue;
                string id = streamedId?.Invoke(window);
                if (!string.IsNullOrEmpty(id)) streamed.Add(id);
                entries.Add(new SwitcherEntry
                {
                    Window = window, Title = window.Title, Detail = id != null ? "Laptop window" : "In the headset",
                    Glyph = GlyphFor(window.Title, id != null), IconId = id,
                });
            }
            if (shells == null) return entries;
            foreach (var shell in shells)
            {
                if (shell.Client == null || !shell.Client.Available) continue;
                string laptop = string.IsNullOrEmpty(shell.LaptopName) ? "the laptop" : shell.LaptopName;
                foreach (var app in shell.Client.Running)
                    foreach (var w in app.Windows)
                    {
                        if (streamed.Contains(w.Id)) continue;
                        // A stream window's icon belongs to the laptop window it shows; reuse it for that window's card.
                        entries.Add(new SwitcherEntry
                        {
                            Client = shell.Client, LaptopWindowId = w.Id, Minimized = w.Minimized,
                            Title = string.IsNullOrEmpty(w.Title) ? app.Name : w.Title,
                            Detail = $"{app.Name} on {laptop}" + (w.Minimized ? ", minimized" : ""), Glyph = UIIcons.Laptop, IconId = w.Id,
                        });
                    }
            }
            return entries;
        }

        /// <summary>A glyph for a headset window by its title.</summary>
        public static string GlyphFor(string title, bool laptop)
        {
            if (laptop) return UIIcons.Laptop;
            switch (title)
            {
                case "Home": return UIIcons.Home;
                case "Settings": return UIIcons.Settings;
                case "Keyboard": return UIIcons.Keyboard;
                case "Laptop apps": return UIIcons.Apps;
                default: return LauncherContent.IconFor(title);
            }
        }
    }

    /// <summary>
    /// The overview's selection, like Alt+Tab for one button: opening selects the window after the front one, each
    /// press moves on (wrapping), and with <see cref="AutoConfirm"/> the selection is switched to once no press came for
    /// <see cref="ConfirmDelay"/> seconds. A pinch on a card switches at once instead.
    /// </summary>
    public sealed class SwitcherSelection
    {
        public const float DefaultConfirmDelay = 1.5f;

        public float ConfirmDelay = DefaultConfirmDelay;
        public bool Open { get; private set; }
        public bool AutoConfirm { get; private set; }
        public int Count { get; private set; }
        public int Selected { get; private set; }
        float lastPress;

        public void Begin(int count, float now, bool autoConfirm)
        {
            Open = true;
            Count = count;
            AutoConfirm = autoConfirm;
            Selected = count > 1 ? 1 : 0;
            lastPress = now;
        }

        public void Next(float now)
        {
            if (!Open || Count == 0) return;
            Selected = (Selected + 1) % Count;
            lastPress = now;
        }

        /// <summary>The list changed while open (a window closed): keep the selection inside it.</summary>
        public void Resize(int count)
        {
            Count = count;
            if (Selected >= count) Selected = count > 0 ? count - 1 : 0;
        }

        public void Select(int index, float now)
        {
            if (index < 0 || index >= Count) return;
            Selected = index;
            lastPress = now;
        }

        /// <summary>Time to switch to the selection by itself.</summary>
        public bool Due(float now) => Open && AutoConfirm && Count > 0 && now - lastPress >= ConfirmDelay;

        public void End() => Open = false;
    }

    /// <summary>
    /// Quick switching without the overview (F6): each step brings the next most recently used window forward. Steps
    /// within <see cref="Window"/> seconds walk one fixed snapshot of the order (so it doesn't flip between two windows);
    /// after a pause, or once a window opened or closed, the snapshot is taken again.
    /// </summary>
    public sealed class QuickCycle<T> where T : class
    {
        public float Window = 1.2f;
        readonly List<T> snapshot = new List<T>();
        int index;
        float lastStep = float.NegativeInfinity;

        bool SameItems(IReadOnlyList<T> items)
        {
            if (items.Count != snapshot.Count) return false;
            foreach (var item in items)
                if (!snapshot.Contains(item)) return false;
            return true;
        }

        /// <summary>The item to bring forward; the walk restarts from <paramref name="mostRecentFirst"/> when it is new.</summary>
        public T Step(IReadOnlyList<T> mostRecentFirst, float now)
        {
            if (now - lastStep > Window || now < lastStep || snapshot.Count == 0 || !SameItems(mostRecentFirst))
            {
                snapshot.Clear();
                foreach (var item in mostRecentFirst) snapshot.Add(item);
                index = 0;
            }
            lastStep = now;
            if (snapshot.Count < 2) return snapshot.Count == 1 ? snapshot[0] : null;
            for (int tries = 0; tries < snapshot.Count; tries++)
            {
                index = (index + 1) % snapshot.Count;
                if (snapshot[index] != null) return snapshot[index];
            }
            return null;
        }
    }
}
