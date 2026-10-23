using System;
using System.Collections.Generic;
using System.Globalization;
using SpatialLauncher.LaptopLink;
using UnityEngine;

namespace SpatialLauncher.LaptopShell
{
    /// <summary>
    /// The headset end of the laptop shell (Tools/LaptopAgent/shell.py): running, pinned and installed apps, their icons,
    /// search, launching apps and window actions, over the session's control channel. Plain C# over a send delegate, so
    /// it knows nothing of WebRTC; <see cref="LaptopShellLink"/> binds it to a session.
    ///
    /// The agent refuses every shell request unless this session holds granted control, so nothing is sent until
    /// <see cref="SetAvailable"/> says so; when control goes away the pending requests are dropped. Only ids the agent
    /// listed are ever sent back. Call <see cref="Tick"/> every frame (timeouts, the search debounce, the running-apps
    /// refresh, the icon queue); everything runs on the main thread.
    /// </summary>
    public sealed class LaptopShellClient
    {
        public const float RequestTimeout = 10f;
        public const float LaunchTimeout = 20f;
        public const float RunningRefreshInterval = 3f;
        public const float SearchDebounce = 0.15f;
        public const int MaxIconsInFlight = 4;
        public const int MaxIconBytes = 16 * 1024;
        public const int MaxIconSide = 128;
        public const int MaxInstalled = 4000;
        public const int MaxListed = 500;
        public const int MaxQuery = 100;

        readonly Action<string> send;
        readonly Func<float> clock;
        readonly Func<byte[], Texture2D> decodeIcon;
        readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();
        readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        readonly HashSet<string> noIcon = new HashSet<string>();
        readonly HashSet<string> iconsInFlight = new HashSet<string>();
        readonly LinkedList<string> iconQueue = new LinkedList<string>();
        readonly List<ShellApp> installed = new List<ShellApp>();
        List<ShellApp> running = new List<ShellApp>();
        List<ShellApp> pinned = new List<ShellApp>();
        List<ShellResult> results = new List<ShellResult>();
        long nextRid = 1;
        float nextRunningRefresh = float.PositiveInfinity;
        string searchSent, searchWanted;
        float searchDueAt = float.PositiveInfinity;
        bool installedLoading;
        long installedNext;

        sealed class Pending
        {
            public string Type;
            public float Deadline;
            public string Id;
            public string Title;
            public Action<Dictionary<string, object>> OnReply;
        }

        public bool Available { get; private set; }
        public ShellOs Os { get; private set; } = ShellOs.Unknown;
        public IReadOnlyList<ShellApp> Running => running;
        public IReadOnlyList<ShellApp> Pinned => pinned;
        public IReadOnlyList<ShellApp> Installed => installed;
        /// <summary>Results for <see cref="Query"/> (empty while the query is empty).</summary>
        public IReadOnlyList<ShellResult> Results => results;
        public string Query { get; private set; } = "";
        /// <summary>The installed list still has pages to come.</summary>
        public bool InstalledLoading => installedLoading;
        /// <summary>Set while the running list refreshes on its own (the shell window is open).</summary>
        public bool AutoRefresh { get; set; } = true;
        /// <summary>The last thing that went wrong, in words, or null.</summary>
        public string LastError { get; private set; }
        public int PendingCount => pending.Count;

        /// <summary>Any list changed.</summary>
        public event Action Changed;
        /// <summary>An icon arrived (the id it belongs to).</summary>
        public event Action<string> IconReady;
        /// <summary>A window should open as a stream: (window id, title). The link opens the stream on a slot.</summary>
        public event Action<string, string> StreamRequested;
        /// <summary>A request was refused or failed: (request type, reason).</summary>
        public event Action<string, string> Failed;
        /// <summary>Something the wearer should read (e.g. "Opened Notepad on the laptop").</summary>
        public event Action<string> Notice;

        public LaptopShellClient(Action<string> send, Func<float> clock = null, Func<byte[], Texture2D> decodeIcon = null)
        {
            this.send = send ?? (_ => { });
            this.clock = clock ?? (() => Time.realtimeSinceStartup);
            this.decodeIcon = decodeIcon ?? DecodePng;
        }

        // --- Availability -------------------------------------------------------------------------------------------

        /// <summary>
        /// The session can use the shell (control granted, the agent offers it) or not. Becoming available loads the
        /// lists; losing it drops what is in flight (the agent would refuse it) but keeps the lists on show.
        /// </summary>
        public void SetAvailable(bool available, ShellOs os)
        {
            Os = os;
            if (available == Available) return;
            Available = available;
            if (available)
            {
                LastError = null;
                Refresh();
            }
            else
            {
                pending.Clear();
                iconsInFlight.Clear();
                installedLoading = false;
                nextRunningRefresh = float.PositiveInfinity;
                searchSent = null;
            }
            Changed?.Invoke();
        }

        /// <summary>The session ended: every id is void now, so everything is forgotten.</summary>
        public void Reset()
        {
            SetAvailable(false, ShellOs.Unknown);
            running = new List<ShellApp>();
            pinned = new List<ShellApp>();
            results = new List<ShellResult>();
            installed.Clear();
            foreach (var texture in icons.Values)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            icons.Clear();
            noIcon.Clear();
            iconQueue.Clear();
            Query = "";
            searchWanted = null;
            LastError = null;
            Changed?.Invoke();
        }

        public static ShellOs ParseOs(string os)
        {
            switch (os)
            {
                case "windows": return ShellOs.Windows;
                case "macos": return ShellOs.MacOS;
                case "linux": return ShellOs.Linux;
                default: return ShellOs.Unknown;
            }
        }

        // --- Lists --------------------------------------------------------------------------------------------------

        /// <summary>Asks for pinned, running and installed apps again.</summary>
        public void Refresh()
        {
            if (!Available) return;
            Request("apps.pinned", null, OnPinned);
            RefreshRunning();
            installed.Clear();
            installedLoading = true;
            installedNext = 0;
            RequestInstalled(0);
        }

        public void RefreshRunning()
        {
            if (!Available || IsPending("apps.running")) return;
            nextRunningRefresh = clock() + RunningRefreshInterval;
            Request("apps.running", null, OnRunning);
        }

        void RequestInstalled(long offset)
        {
            Request("apps.installed", new Dictionary<string, object> { ["offset"] = offset }, OnInstalled);
        }

        /// <summary>
        /// The dock or taskbar: pinned apps in pin order, then running apps that aren't pinned, in the agent's order
        /// (taskbar order on Windows, Dock order on macOS). Each running app carries its windows.
        /// </summary>
        public List<ShellApp> DockItems()
        {
            var byId = new Dictionary<string, ShellApp>();
            foreach (var app in running) byId[app.Id] = app;
            var dock = new List<ShellApp>();
            var seen = new HashSet<string>();
            foreach (var pin in pinned)
            {
                if (!seen.Add(pin.Id)) continue;
                dock.Add(byId.TryGetValue(pin.Id, out var live) ? live : pin);
            }
            foreach (var app in running)
                if (seen.Add(app.Id)) dock.Add(app);
            return dock;
        }

        /// <summary>The app with this id from any list, or null.</summary>
        public ShellApp Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var list in new[] { running, pinned, installed })
                foreach (var app in list)
                    if (app.Id == id) return app;
            return null;
        }

        void OnPinned(Dictionary<string, object> m)
        {
            pinned = ParseApps(m, pinnedFlag: true);
            Changed?.Invoke();
        }

        void OnRunning(Dictionary<string, object> m)
        {
            running = ParseApps(m, pinnedFlag: false, running: true);
            Changed?.Invoke();
        }

        void OnInstalled(Dictionary<string, object> m)
        {
            long offset = Long(m, "offset");
            if (offset != installedNext) return; // a page of an older pass (a refresh started over)
            foreach (var app in ParseApps(m, pinnedFlag: false))
                if (installed.Count < MaxInstalled) installed.Add(app);
            long next = Long(m, "next"); // -1 when null: the last page
            installedLoading = next > offset && installed.Count < MaxInstalled;
            if (installedLoading)
            {
                installedNext = next;
                RequestInstalled(next);
            }
            Changed?.Invoke();
        }

        List<ShellApp> ParseApps(Dictionary<string, object> m, bool pinnedFlag, bool running = false)
        {
            var list = new List<ShellApp>();
            if (!(m.TryGetValue("apps", out var a) && a is List<object> items)) return list;
            foreach (var item in items)
            {
                if (list.Count >= MaxListed) break;
                if (!(item is Dictionary<string, object> d)) continue;
                string id = LinkJson.GetString(d, "id");
                if (!IsAppId(id)) continue;
                var app = new ShellApp
                {
                    Id = id, Name = Text(LinkJson.GetString(d, "name")), Pinned = pinnedFlag || Bool(d, "pinned"),
                    Running = running || Bool(d, "running"), Launchable = !d.ContainsKey("launchable") || Bool(d, "launchable"),
                };
                if (d.TryGetValue("windows", out var w) && w is List<object> windows)
                    foreach (var entry in windows)
                    {
                        if (app.Windows.Count >= 64) break;
                        if (!(entry is Dictionary<string, object> wd)) continue;
                        string windowId = LinkJson.GetString(wd, "id");
                        if (!IsWindowId(windowId)) continue;
                        app.Windows.Add(new ShellWindow
                        {
                            Id = windowId, Title = Text(LinkJson.GetString(wd, "title")), Minimized = Bool(wd, "minimized"),
                            Focused = Bool(wd, "focused"),
                        });
                    }
                list.Add(app);
            }
            return list;
        }

        // --- Search -------------------------------------------------------------------------------------------------

        /// <summary>
        /// What the search field says now (typed or spoken). Sent after a short pause in typing; results for an older
        /// query are ignored. An empty query clears the results without asking.
        /// </summary>
        public void Search(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length > MaxQuery) query = query.Substring(0, MaxQuery);
            if (query == Query && (searchWanted == query || searchSent == query)) return;
            Query = query;
            if (query.Length == 0)
            {
                searchWanted = null;
                searchDueAt = float.PositiveInfinity;
                results = new List<ShellResult>();
                Changed?.Invoke();
                return;
            }
            searchWanted = query;
            searchDueAt = clock() + SearchDebounce;
        }

        void SendSearch()
        {
            string query = searchWanted;
            searchWanted = null;
            searchDueAt = float.PositiveInfinity;
            if (!Available || string.IsNullOrEmpty(query)) return;
            searchSent = query;
            Request("search.query", new Dictionary<string, object> { ["q"] = query }, OnResults);
        }

        void OnResults(Dictionary<string, object> m)
        {
            string q = LinkJson.GetString(m, "q");
            if (q != Query) return; // an older query
            var list = new List<ShellResult>();
            if (m.TryGetValue("results", out var r) && r is List<object> items)
                foreach (var item in items)
                {
                    if (list.Count >= 50) break;
                    if (!(item is Dictionary<string, object> d)) continue;
                    string kind = LinkJson.GetString(d, "kind"), id = LinkJson.GetString(d, "id");
                    if (kind == "app" && IsAppId(id))
                        list.Add(new ShellResult { Kind = ShellResultKind.App, Id = id, Name = Text(LinkJson.GetString(d, "name")), Running = Bool(d, "running") });
                    else if (kind == "window" && IsWindowId(id))
                        list.Add(new ShellResult
                        {
                            Kind = ShellResultKind.Window, Id = id, Name = Text(LinkJson.GetString(d, "title")),
                            AppName = Text(LinkJson.GetString(d, "app")), Running = true,
                        });
                }
            results = list;
            Changed?.Invoke();
        }

        // --- Icons --------------------------------------------------------------------------------------------------

        /// <summary>The icon for an app or window id if it has arrived; otherwise it is queued and null comes back.</summary>
        public Texture2D Icon(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (icons.TryGetValue(id, out var texture)) return texture;
            if (noIcon.Contains(id) || iconsInFlight.Contains(id) || iconQueue.Contains(id)) return null;
            if (!IsAppId(id) && !IsWindowId(id)) return null;
            iconQueue.AddLast(id);
            return null;
        }

        /// <summary>True once the agent said there is no icon (draw the letter tile).</summary>
        public bool HasNoIcon(string id) => id != null && noIcon.Contains(id);

        void PumpIcons()
        {
            while (Available && iconsInFlight.Count < MaxIconsInFlight && iconQueue.Count > 0)
            {
                string id = iconQueue.First.Value;
                iconQueue.RemoveFirst();
                iconsInFlight.Add(id);
                Request("apps.icon", new Dictionary<string, object> { ["id"] = id }, OnIcon, id);
            }
        }

        void OnIcon(Dictionary<string, object> m)
        {
            string id = LinkJson.GetString(m, "id");
            if (id == null) return;
            iconsInFlight.Remove(id);
            byte[] png = null;
            try { png = Convert.FromBase64String(LinkJson.GetString(m, "png") ?? ""); }
            catch (FormatException) { }
            var texture = png != null && IsSmallPng(png) ? decodeIcon(png) : null;
            if (texture == null)
            {
                noIcon.Add(id);
                return;
            }
            texture.name = "LaptopIcon " + id;
            icons[id] = texture;
            IconReady?.Invoke(id);
        }

        /// <summary>A PNG no bigger than <see cref="MaxIconBytes"/> and <see cref="MaxIconSide"/> a side, from its header.</summary>
        public static bool IsSmallPng(byte[] png)
        {
            if (png == null || png.Length < 24 || png.Length > MaxIconBytes) return false;
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < 8; i++)
                if (png[i] != signature[i]) return false;
            if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return false;
            long width = ((long)png[16] << 24) | ((long)png[17] << 16) | ((long)png[18] << 8) | png[19];
            long height = ((long)png[20] << 24) | ((long)png[21] << 16) | ((long)png[22] << 8) | png[23];
            return width > 0 && height > 0 && width <= MaxIconSide && height <= MaxIconSide;
        }

        static Texture2D DecodePng(byte[] png)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (texture.LoadImage(png, true)) return texture;
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        // --- Opening and window actions -----------------------------------------------------------------------------

        /// <summary>
        /// Taps on an app: its window opens as a stream (the focused one, else the first; a minimized one is restored
        /// first); an app with no window is launched and its new window opens. Returns false when nothing can be done.
        /// </summary>
        public bool Open(ShellApp app)
        {
            if (!Available || app == null) return false;
            var live = Find(app.Id) ?? app;
            ShellWindow window = null;
            foreach (var w in live.Windows)
                if (window == null || (w.Focused && !window.Focused) || (window.Minimized && !w.Minimized && !window.Focused)) window = w;
            if (window != null) return OpenWindow(window.Id, window.Title, window.Minimized);
            if (!live.Launchable) return false;
            Request("apps.launch", new Dictionary<string, object> { ["id"] = live.Id }, OnLaunched, live.Id, live.Name);
            Notice?.Invoke($"Opening {live.Name}…");
            return true;
        }

        public bool Open(ShellResult result)
        {
            if (result == null) return false;
            if (result.Kind == ShellResultKind.Window) return OpenWindow(result.Id, result.Name, IsMinimized(result.Id));
            return Open(Find(result.Id) ?? new ShellApp { Id = result.Id, Name = result.Name, Running = result.Running });
        }

        /// <summary>Opens a listed window as a stream; a minimized one is restored first (the agent can't stream it).</summary>
        public bool OpenWindow(string windowId, string title, bool minimized)
        {
            if (!Available || !IsWindowId(windowId)) return false;
            if (!minimized)
            {
                StreamRequested?.Invoke(windowId, title);
                return true;
            }
            Request("window.restore", new Dictionary<string, object> { ["id"] = windowId }, m =>
            {
                if (Bool(m, "ok")) StreamRequested?.Invoke(windowId, title);
                else Fail("window.restore", "failed");
                RefreshRunning();
            }, windowId, title);
            return true;
        }

        void OnLaunched(Dictionary<string, object> m)
        {
            string id = LinkJson.GetString(m, "id");
            var app = Find(id);
            if (m.TryGetValue("window", out var w) && w is Dictionary<string, object> window && IsWindowId(LinkJson.GetString(window, "id")))
                StreamRequested?.Invoke(LinkJson.GetString(window, "id"), Text(LinkJson.GetString(window, "title")));
            else
                Notice?.Invoke($"{app?.Name ?? "The app"} opened on the laptop; its window will show in the dock.");
            RefreshRunning();
        }

        public bool Raise(string windowId) => WindowAction("window.raise", windowId);
        public bool Minimize(string windowId) => WindowAction("window.minimize", windowId);
        public bool Restore(string windowId) => WindowAction("window.restore", windowId);
        public bool Close(string windowId) => WindowAction("window.close", windowId);

        bool WindowAction(string type, string windowId)
        {
            if (!Available || !IsWindowId(windowId)) return false;
            Request(type, new Dictionary<string, object> { ["id"] = windowId }, m =>
            {
                if (!Bool(m, "ok")) Fail(type, "failed");
                RefreshRunning();
            }, windowId);
            return true;
        }

        bool IsMinimized(string windowId)
        {
            foreach (var app in running)
                foreach (var w in app.Windows)
                    if (w.Id == windowId) return w.Minimized;
            return false;
        }

        // --- Wire ---------------------------------------------------------------------------------------------------

        void Request(string type, Dictionary<string, object> fields, Action<Dictionary<string, object>> onReply,
                     string id = null, string title = null)
        {
            long rid = nextRid++;
            var message = new Dictionary<string, object> { ["type"] = type, ["rid"] = rid };
            if (fields != null)
                foreach (var kv in fields) message[kv.Key] = kv.Value;
            pending[rid] = new Pending
            {
                Type = type, Id = id, Title = title, OnReply = onReply,
                Deadline = clock() + (type == "apps.launch" ? LaunchTimeout : RequestTimeout),
            };
            send(LinkJson.Serialize(message));
        }

        bool IsPending(string type)
        {
            foreach (var p in pending.Values)
                if (p.Type == type) return true;
            return false;
        }

        /// <summary>
        /// One shell message from the control channel (types apps.*, window.*, search.results, shell.error). Replies
        /// are matched to their request by rid; anything unasked for is ignored.
        /// </summary>
        public void Receive(Dictionary<string, object> m)
        {
            if (m == null || !(m.TryGetValue("rid", out var r) && r is long rid) || !pending.TryGetValue(rid, out var request)) return;
            pending.Remove(rid);
            string type = LinkJson.GetString(m, "type");
            if (type == "shell.error")
            {
                string reason = LinkJson.GetString(m, "reason") ?? "failed";
                if (request.Type == "apps.icon" && request.Id != null)
                {
                    iconsInFlight.Remove(request.Id);
                    if (reason == "rate limited" || reason == "busy") iconQueue.AddLast(request.Id); // try again later
                    else noIcon.Add(request.Id);
                    return;
                }
                if (request.Type == "apps.installed") installedLoading = false;
                Fail(request.Type, reason);
                return;
            }
            if (!ReplyMatches(request.Type, type)) return;
            request.OnReply?.Invoke(m);
        }

        static bool ReplyMatches(string request, string reply)
        {
            switch (request)
            {
                case "apps.launch": return reply == "apps.launched";
                case "search.query": return reply == "search.results";
                case "window.raise":
                case "window.minimize":
                case "window.restore":
                case "window.close": return reply == "window.done";
                default: return reply == request;
            }
        }

        void Fail(string type, string reason)
        {
            LastError = Describe(type, reason);
            Failed?.Invoke(type, reason);
            Changed?.Invoke();
        }

        /// <summary>A refusal in words for the wearer.</summary>
        public static string Describe(string type, string reason)
        {
            switch (reason)
            {
                case "view only": return "Allow control first to use the laptop's apps.";
                case "cast only": return "This laptop shares media controls only.";
                case "input off": return "The laptop doesn't allow control (start the agent with Allow control).";
                case "stopped": return "Control was stopped on the laptop.";
                case "unsupported": return "This laptop's apps can't be listed yet.";
                case "rate limited": return "Too many requests; try again in a moment.";
                case "busy": return "The laptop is busy; try again.";
                case "not launchable": return "That app can't be started from here.";
                case "unknown id": return "That app or window is gone; the list was refreshed.";
                case "timeout": return "The laptop didn't answer.";
                default: return type == "apps.launch" ? "The app didn't start." : "That didn't work on the laptop.";
            }
        }

        public void Tick()
        {
            float now = clock();
            List<long> expired = null;
            foreach (var kv in pending)
                if (now >= kv.Value.Deadline) (expired ??= new List<long>()).Add(kv.Key);
            if (expired != null)
                foreach (long rid in expired)
                {
                    var request = pending[rid];
                    pending.Remove(rid);
                    if (request.Type == "apps.icon" && request.Id != null)
                    {
                        iconsInFlight.Remove(request.Id);
                        continue; // asked again the next time it is drawn
                    }
                    if (request.Type == "apps.installed") installedLoading = false;
                    Fail(request.Type, "timeout");
                }
            if (!Available) return;
            if (now >= searchDueAt) SendSearch();
            if (AutoRefresh && now >= nextRunningRefresh) RefreshRunning();
            PumpIcons();
        }

        // --- Helpers ------------------------------------------------------------------------------------------------

        public static bool IsAppId(string id)
        {
            if (id == null || id.Length != 20 || !id.StartsWith("app:", StringComparison.Ordinal)) return false;
            for (int i = 4; i < 20; i++)
            {
                char c = id[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        public static bool IsWindowId(string id)
        {
            if (id == null || id.Length < 8 || id.Length > 27 || !id.StartsWith("window:", StringComparison.Ordinal)) return false;
            if (id[7] == '0') return false;
            for (int i = 7; i < id.Length; i++)
                if (id[i] < '0' || id[i] > '9') return false;
            return true;
        }

        /// <summary>Names and titles as shown: control characters out, one line, at most 64 characters.</summary>
        static string Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var chars = new System.Text.StringBuilder(Math.Min(s.Length, 64));
            foreach (char c in s)
            {
                if (chars.Length >= 64) break;
                if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                chars.Append(c);
            }
            return chars.ToString().Trim();
        }

        static bool Bool(Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) && v is bool b && b;

        static long Long(Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) && v is long l ? l : -1;
    }
}
