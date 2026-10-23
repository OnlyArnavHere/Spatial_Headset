using System;
using System.Collections.Generic;
using System.Globalization;
using SpatialLauncher.LaptopLink;
using SpatialLauncher.LaptopShell;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// A made-up laptop for the Laptop Shell's tests and the UI gallery: <see cref="FakeAgent"/> answers the shell
    /// protocol (shell.py) straight away with invented apps (no real product names) and drawn icons, and
    /// <see cref="FakeSession"/> stands in for a linked session whose lock the test sets.
    /// </summary>
    static class ShellFixture
    {
        public sealed class FakeWindow
        {
            public int Handle;
            public string Title;
            public bool Minimized, Focused;
            public string Id => "window:" + Handle.ToString(CultureInfo.InvariantCulture);
        }

        public sealed class FakeApp
        {
            public string Id, Name;
            public Color Color;
            public bool Pinned, Installed = true, HasIcon = true, Launchable = true;
            public readonly List<FakeWindow> Windows = new List<FakeWindow>();
        }

        static readonly string[] Names =
        {
            "Files", "Browser", "Mail", "Notes", "Terminal", "Code Editor", "Music", "Photos", "Calendar", "Calculator",
            "Maps", "Paint", "Chat", "Video Call", "Spreadsheet", "Slides", "Docs", "Weather", "Clock", "Camera", "Store",
            "Settings", "Recorder", "Reader", "Game Hub", "3D Studio", "Archive", "Audio Editor", "Screen Capture",
            "Podcasts", "News", "Translate", "Contacts", "Reminders", "Voice Memos", "Stocks",
        };

        public sealed class FakeAgent
        {
            public readonly List<Dictionary<string, object>> Requests = new List<Dictionary<string, object>>();
            public readonly List<FakeApp> Apps = new List<FakeApp>();
            public readonly ShellOs Os;
            public float Now;
            public bool AutoReply = true;
            public LaptopShellClient Client { get; }
            int nextHandle = 100;

            public FakeAgent(ShellOs os, bool decodeIcons = true)
            {
                Os = os;
                Client = new LaptopShellClient(OnSend, () => Now, decodeIcons ? null : (Func<byte[], Texture2D>)(_ => null));
                for (int i = 0; i < Names.Length; i++)
                    Apps.Add(new FakeApp
                    {
                        Id = "app:" + (0xa000 + i).ToString("x16", CultureInfo.InvariantCulture), Name = Names[i],
                        Color = Color.HSVToRGB(i * 0.137f % 1f, 0.55f, 0.85f), HasIcon = i % 9 != 4,
                    });
                if (os == ShellOs.MacOS)
                {
                    foreach (var name in new[] { "Files", "Browser", "Mail", "Music", "Notes", "Calendar", "Settings" }) App(name).Pinned = true;
                    AddWindow("Files", "Documents");
                    AddWindow("Browser", "Start page", focused: true);
                    AddWindow("Browser", "Project board");
                    AddWindow("Terminal", "zsh");
                    AddWindow("Code Editor", "main.cs", minimized: true);
                }
                else
                {
                    foreach (var name in new[] { "Files", "Browser", "Store", "Notes", "Mail" }) App(name).Pinned = true;
                    AddWindow("Browser", "Start page", focused: true);
                    AddWindow("Browser", "Project board");
                    AddWindow("Notes", "Shopping list", minimized: true);
                    AddWindow("Code Editor", "main.cs");
                    AddWindow("Terminal", "PowerShell");
                }
            }

            public FakeApp App(string name) => Apps.Find(a => a.Name == name);

            public FakeWindow AddWindow(string app, string title, bool focused = false, bool minimized = false)
            {
                var window = new FakeWindow { Handle = nextHandle++, Title = title, Focused = focused, Minimized = minimized };
                if (focused)
                    foreach (var other in Apps)
                        foreach (var w in other.Windows) w.Focused = false;
                App(app).Windows.Add(window);
                return window;
            }

            public FakeWindow FindWindow(string id)
            {
                foreach (var app in Apps)
                    foreach (var w in app.Windows)
                        if (w.Id == id) return w;
                return null;
            }

            /// <summary>Lets a few seconds pass for the client (search debounce, icon queue), with every reply at once.</summary>
            public void Settle(int ticks = 20)
            {
                for (int i = 0; i < ticks; i++)
                {
                    Now += 0.2f;
                    Client.Tick();
                }
            }

            public List<Dictionary<string, object>> Of(string type) => Requests.FindAll(m => (string)m["type"] == type);

            void OnSend(string json)
            {
                var request = LinkJson.ParseObject(json);
                Requests.Add(request);
                if (AutoReply) Reply(request);
            }

            public void Reply(Dictionary<string, object> request)
            {
                var reply = Answer(request);
                if (reply == null) return;
                reply["rid"] = request["rid"];
                Client.Receive(LinkJson.ParseObject(LinkJson.Serialize(reply)));
            }

            Dictionary<string, object> Answer(Dictionary<string, object> request)
            {
                string type = (string)request["type"];
                string id = request.TryGetValue("id", out var v) ? v as string : null;
                switch (type)
                {
                    case "apps.pinned":
                        return Msg(type, ("apps", Apps.FindAll(a => a.Pinned).ConvertAll(a => (object)Entry(a))));
                    case "apps.running":
                        return Msg(type, ("apps", RunningOrder().ConvertAll(a => (object)RunningEntry(a))));
                    case "apps.installed":
                    {
                        var all = Apps.FindAll(a => a.Installed);
                        all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                        int offset = (int)(long)request["offset"];
                        var page = all.GetRange(Math.Min(offset, all.Count), Math.Max(0, Math.Min(15, all.Count - offset)));
                        object next = offset + page.Count < all.Count ? (object)(long)(offset + page.Count) : null;
                        return Msg(type, ("offset", (long)offset), ("total", (long)all.Count), ("next", next),
                            ("apps", page.ConvertAll(a => (object)Entry(a))));
                    }
                    case "apps.icon":
                    {
                        var app = Apps.Find(a => a.Id == id) ?? Apps.Find(a => a.Windows.Exists(w => w.Id == id));
                        if (app == null || !app.HasIcon) return Msg("shell.error", ("request", type), ("reason", "no icon"));
                        return Msg(type, ("id", id), ("size", 48L), ("png", Convert.ToBase64String(IconPng(app, Os))));
                    }
                    case "apps.launch":
                    {
                        var app = Apps.Find(a => a.Id == id);
                        if (app == null) return Msg("shell.error", ("request", type), ("reason", "unknown id"));
                        var window = AddWindow(app.Name, app.Name, focused: true);
                        return Msg("apps.launched", ("id", id), ("window", new Dictionary<string, object> { ["id"] = window.Id, ["title"] = window.Title }));
                    }
                    case "window.raise":
                    case "window.minimize":
                    case "window.restore":
                    case "window.close":
                    {
                        var window = FindWindow(id);
                        if (window == null) return Msg("shell.error", ("request", type), ("reason", "unknown id"));
                        string action = type.Substring(7);
                        if (action == "minimize") window.Minimized = true;
                        else if (action == "restore" || action == "raise") window.Minimized = false;
                        else foreach (var app in Apps) app.Windows.Remove(window);
                        return Msg("window.done", ("action", action), ("id", id), ("ok", true));
                    }
                    case "search.query":
                    {
                        string q = ((string)request["q"]).ToLowerInvariant();
                        var results = new List<object>();
                        foreach (var app in Apps)
                            if (app.Name.ToLowerInvariant().Contains(q))
                                results.Add(new Dictionary<string, object> { ["kind"] = "app", ["id"] = app.Id, ["name"] = app.Name, ["running"] = app.Windows.Count > 0 });
                        foreach (var app in Apps)
                            foreach (var w in app.Windows)
                                if (w.Title.ToLowerInvariant().Contains(q))
                                    results.Add(new Dictionary<string, object> { ["kind"] = "window", ["id"] = w.Id, ["title"] = w.Title, ["app"] = app.Name });
                        return Msg("search.results", ("q", request["q"]), ("results", results));
                    }
                    default:
                        return null;
                }
            }

            // Pinned apps that run, in pin order, then the others: what the real agent's order looks like.
            List<FakeApp> RunningOrder()
            {
                var order = Apps.FindAll(a => a.Pinned && a.Windows.Count > 0);
                order.AddRange(Apps.FindAll(a => !a.Pinned && a.Windows.Count > 0));
                return order;
            }

            Dictionary<string, object> Entry(FakeApp app) => new Dictionary<string, object>
            {
                ["id"] = app.Id, ["name"] = app.Name, ["running"] = app.Windows.Count > 0,
            };

            Dictionary<string, object> RunningEntry(FakeApp app)
            {
                var windows = new List<object>();
                foreach (var w in app.Windows)
                    windows.Add(new Dictionary<string, object> { ["id"] = w.Id, ["title"] = w.Title, ["minimized"] = w.Minimized, ["focused"] = w.Focused });
                return new Dictionary<string, object>
                {
                    ["id"] = app.Id, ["name"] = app.Name, ["pinned"] = app.Pinned, ["launchable"] = app.Launchable, ["windows"] = windows,
                };
            }

            static Dictionary<string, object> Msg(string type, params (string key, object value)[] fields)
            {
                var m = new Dictionary<string, object> { ["type"] = type };
                foreach (var (key, value) in fields) m[key] = value;
                return m;
            }
        }

        /// <summary>A drawn 48 px icon with transparent corners: a rounded square on macOS, a disc on Windows, with a white mark.</summary>
        public static byte[] IconPng(FakeApp app, ShellOs os)
        {
            const int size = 48;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            Color top = app.Color, bottom = app.Color * 0.7f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size - 0.5f, v = (y + 0.5f) / size - 0.5f;
                    float inside;
                    if (os == ShellOs.MacOS)
                    {
                        float qx = Mathf.Max(Mathf.Abs(u) - 0.30f, 0f), qy = Mathf.Max(Mathf.Abs(v) - 0.30f, 0f);
                        inside = Mathf.Clamp01((0.14f - Mathf.Sqrt(qx * qx + qy * qy)) * size);
                    }
                    else inside = Mathf.Clamp01((0.44f - Mathf.Sqrt(u * u + v * v)) * size);
                    var c = Color.Lerp(bottom, top, (v + 0.5f));
                    float mark = Mathf.Clamp01((0.10f - Mathf.Sqrt(u * u + (v - 0.03f) * (v - 0.03f))) * size);
                    c = Color.Lerp(c, Color.white, mark * 0.9f);
                    c.a = inside;
                    pixels[y * size + x] = c;
                }
            texture.SetPixels32(pixels);
            texture.Apply(false);
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }

        /// <summary>A linked session stand-in: the lock is whatever the test says; asking for control is counted.</summary>
        public sealed class FakeSession : IShellSession
        {
            public FakeSession(LaptopShellClient client, string laptopName = "Studio laptop")
            {
                Client = client;
                LaptopName = laptopName;
            }

            public string LaptopName { get; }
            public LaptopShellClient Client { get; }
            public ShellLock Lock { get; set; } = ShellLock.None;
            public SpatialWindow ShellWindow { get; set; }
            public int ControlRequests { get; private set; }
            public bool Disposed { get; private set; }

            public bool RequestControl()
            {
                if (Lock != ShellLock.NeedsControl) return false;
                ControlRequests++;
                Lock = ShellLock.Asking;
                return true;
            }

            public void Tick() => Client.Tick();

            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                Client.Reset();
            }
        }
    }
}
