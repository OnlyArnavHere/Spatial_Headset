using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SpatialLauncher.LaptopLink;
using SpatialLauncher.LaptopShell;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test (no Play mode, no network) for the headset's laptop-shell data layer: a LaptopShellClient against a
    /// scripted agent. Nothing is sent before control is granted; the lists load (installed apps page by the agent's own
    /// "next"); the dock is pins then other running apps; ids that aren't the agent's are dropped and never sent back;
    /// icons are queued four at a time, size-checked and cached; search waits for a pause and ignores stale results;
    /// tapping streams a window (restoring a minimized one first) or launches the app; refusals, timeouts and losing
    /// control are handled. With WebRTC compiled in it also checks the control channel: the welcome's shell field,
    /// shell requests going out only with granted control, and replies routed to the shell. Results use a
    /// [LaptopShellTest] prefix.
    /// </summary>
    static class LaptopShellTest
    {
        [MenuItem("Tools/Spatial Windows/Test Laptop Shell")]
        static void Run() => EditorTestRunner.Run("LaptopShellTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Laptop Shell", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        const string Chrome = "app:00000000000000c1", Edge = "app:00000000000000e1", Notepad = "app:000000000000000a",
            Tool = "app:00000000000000f1", Store = "app:0000000000000051";

        sealed class Rig
        {
            public readonly List<Dictionary<string, object>> Sent = new List<Dictionary<string, object>>();
            public float Now;
            public readonly LaptopShellClient Client;
            public readonly List<(string id, string title)> Streams = new List<(string, string)>();
            public readonly List<(string type, string reason)> Failures = new List<(string, string)>();
            public readonly List<string> Notices = new List<string>();
            public readonly List<string> IconsReady = new List<string>();

            public Rig(bool available = true)
            {
                Client = new LaptopShellClient(json => Sent.Add(LinkJson.ParseObject(json)), () => Now);
                Client.StreamRequested += (id, title) => Streams.Add((id, title));
                Client.Failed += (type, reason) => Failures.Add((type, reason));
                Client.Notice += Notices.Add;
                Client.IconReady += IconsReady.Add;
                if (available) Client.SetAvailable(true, ShellOs.Windows);
            }

            public List<Dictionary<string, object>> Of(string type) => Sent.Where(m => (string)m["type"] == type).ToList();
            public Dictionary<string, object> Last(string type) => Of(type).LastOrDefault();

            /// <summary>Answers a request the way the agent would: the reply JSON with the request's rid.</summary>
            public void Reply(Dictionary<string, object> request, string json)
            {
                string body = json.TrimStart('{');
                Client.Receive(LinkJson.ParseObject("{\"rid\":" + (long)request["rid"] + "," + body));
            }

            public void Advance(float seconds)
            {
                Now += seconds;
                Client.Tick();
            }

            /// <summary>Answers the initial pinned, running and one-page installed requests.</summary>
            public void Load()
            {
                Reply(Last("apps.pinned"), "{\"type\":\"apps.pinned\",\"apps\":[" + App(Edge, "Microsoft Edge", running: false) + "," + App(Chrome, "Google Chrome") + "]}");
                Reply(Last("apps.running"), RunningJson);
                Reply(Last("apps.installed"), "{\"type\":\"apps.installed\",\"offset\":0,\"total\":4,\"next\":null,\"apps\":[" +
                      App(Chrome, "Google Chrome") + "," + App(Edge, "Microsoft Edge", running: false) + "," + App(Store, "Microsoft Store", running: false) + "," +
                      App(Notepad, "Notepad") + "]}");
            }

            public const string RunningJson =
                "{\"type\":\"apps.running\",\"apps\":[" +
                "{\"id\":\"" + Chrome + "\",\"name\":\"Google Chrome\",\"pinned\":true,\"launchable\":true,\"windows\":[" +
                "{\"id\":\"window:12\",\"title\":\"GitHub\",\"minimized\":false,\"focused\":false}," +
                "{\"id\":\"window:14\",\"title\":\"Inbox\",\"minimized\":false,\"focused\":true}]}," +
                "{\"id\":\"" + Tool + "\",\"name\":\"Tool\",\"pinned\":false,\"launchable\":false,\"windows\":[" +
                "{\"id\":\"window:13\",\"title\":\"Some tool\",\"minimized\":false,\"focused\":false}]}," +
                "{\"id\":\"" + Notepad + "\",\"name\":\"Notepad\",\"pinned\":false,\"launchable\":true,\"windows\":[" +
                "{\"id\":\"window:11\",\"title\":\"Notes.txt\",\"minimized\":true,\"focused\":false}]}]}";
        }

        static string App(string id, string name, bool running = true) =>
            "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"running\":" + (running ? "true" : "false") + "}";

        static IEnumerator Steps()
        {
            Availability();
            Lists();
            Ids();
            Icons();
            Search();
            Opening();
            Failures();
#if SPATIAL_LAUNCHER_WEBRTC
            ControlChannel();
#else
            Debug.Log("[LaptopShellTest] Control-channel checks skipped: this build target has no SPATIAL_LAUNCHER_WEBRTC.");
#endif
            yield break;
        }

        static void Availability()
        {
            var rig = new Rig(available: false);
            rig.Client.Search("note");
            rig.Client.Icon(Chrome);
            rig.Client.Refresh();
            rig.Advance(5f);
            Check(rig.Sent.Count == 0, "nothing is sent before control is granted (no list, search or icon)");
            Check(!rig.Client.Open(new ShellApp { Id = Chrome, Name = "Chrome" }) && !rig.Client.Close("window:12"), "taps do nothing without control");

            rig.Client.SetAvailable(true, ShellOs.MacOS);
            var types = rig.Sent.Select(m => (string)m["type"]).ToList();
            Check(types.SequenceEqual(new[] { "apps.pinned", "apps.running", "apps.installed" }), $"control granted: pinned, running and installed are asked for ({string.Join(", ", types)})");
            Check(rig.Sent.Select(m => (long)m["rid"]).Distinct().Count() == 3, "every request has its own rid");
            Check((long)rig.Last("apps.installed")["offset"] == 0, "installed starts at offset 0");
            Check(rig.Client.Os == ShellOs.MacOS, "the laptop's OS is kept for the window's style");

            var running = rig.Last("apps.running");
            rig.Client.SetAvailable(false, ShellOs.MacOS);
            Check(rig.Client.PendingCount == 0, "losing control drops what is in flight");
            rig.Reply(running, Rig.RunningJson);
            Check(rig.Client.Running.Count == 0, "a reply after losing control is ignored");
            Check(LaptopShellClient.ParseOs("windows") == ShellOs.Windows && LaptopShellClient.ParseOs("macos") == ShellOs.MacOS &&
                  LaptopShellClient.ParseOs("linux") == ShellOs.Linux && LaptopShellClient.ParseOs("plan9") == ShellOs.Unknown, "OS names");
        }

        static void Lists()
        {
            var rig = new Rig();
            rig.Load();
            Check(rig.Client.Running.Count == 3 && rig.Client.Pinned.Count == 2 && rig.Client.Installed.Count == 4, "running, pinned and installed loaded");
            var dock = rig.Client.DockItems();
            Check(dock.Select(a => a.Name).SequenceEqual(new[] { "Microsoft Edge", "Google Chrome", "Tool", "Notepad" }),
                  $"dock: pins in pin order, then the other running apps ({string.Join(", ", dock.Select(a => a.Name))})");
            Check(dock[1].Windows.Count == 2 && dock[1].Running && !dock[0].Running, "a pinned app that runs carries its windows; a pin that doesn't shows no dot");
            Check(!rig.Client.Find(Tool).Launchable && rig.Client.Find(Notepad).Windows[0].Minimized, "launchable and minimized flags");

            // paging follows the agent's "next", even when an entry is dropped
            rig.Client.Refresh();
            var first = rig.Last("apps.installed");
            rig.Reply(first, "{\"type\":\"apps.installed\",\"offset\":0,\"total\":3,\"next\":2,\"apps\":[" + App(Chrome, "A") + ",{\"id\":\"C:\\\\evil.exe\",\"name\":\"bad\"}]}");
            var second = rig.Last("apps.installed");
            Check(second != first && (long)second["offset"] == 2 && rig.Client.InstalledLoading, "the next page is asked for at the agent's next offset (2), not the kept count (1)");
            rig.Reply(first, "{\"type\":\"apps.installed\",\"offset\":0,\"total\":3,\"next\":2,\"apps\":[" + App(Edge, "dup") + "]}");
            Check(rig.Client.Installed.Count == 1, "a repeated reply for an answered rid is ignored");
            rig.Reply(second, "{\"type\":\"apps.installed\",\"offset\":2,\"total\":3,\"next\":null,\"apps\":[" + App(Store, "Z") + "]}");
            Check(rig.Client.Installed.Count == 2 && !rig.Client.InstalledLoading && rig.Of("apps.installed").Count == 3, "the last page ends loading");

            // the running list refreshes itself while the shell window is open, one request at a time
            rig.Reply(rig.Last("apps.running"), Rig.RunningJson); // the one Refresh() sent
            int before = rig.Of("apps.running").Count;
            rig.Advance(LaptopShellClient.RunningRefreshInterval + 0.1f);
            rig.Advance(LaptopShellClient.RunningRefreshInterval + 0.1f);
            Check(rig.Of("apps.running").Count == before + 1, "running refreshes every few seconds, never two at once");
            rig.Reply(rig.Last("apps.running"), Rig.RunningJson);
            rig.Client.AutoRefresh = false;
            rig.Advance(LaptopShellClient.RunningRefreshInterval * 3);
            Check(rig.Of("apps.running").Count == before + 1, "no refresh with AutoRefresh off");

            rig.Client.Reset();
            Check(rig.Client.Running.Count == 0 && rig.Client.Installed.Count == 0 && rig.Client.Pinned.Count == 0 && !rig.Client.Available,
                  "a session end forgets every list (the ids are void)");
        }

        static void Ids()
        {
            var rig = new Rig();
            rig.Reply(rig.Last("apps.running"), "{\"type\":\"apps.running\",\"apps\":[" +
                "{\"id\":\"app:XYZ\",\"name\":\"bad\"},{\"id\":\"C:\\\\Windows\\\\cmd.exe\",\"name\":\"bad\"},{\"id\":5,\"name\":\"bad\"}," +
                "{\"id\":\"" + Notepad + "\",\"name\":\"N\\u0000ote\\u202epad" + new string('x', 100) + "\",\"windows\":[" +
                "{\"id\":\"window:0\"},{\"id\":\"window:-3\"},{\"id\":\"window:12 \"},{\"id\":\"cmd /c calc\"},{\"id\":\"window:77\",\"title\":\"ok\"}]}]}");
            Check(rig.Client.Running.Count == 1 && rig.Client.Running[0].Windows.Count == 1 && rig.Client.Running[0].Windows[0].Id == "window:77",
                  "entries whose ids aren't the agent's formats are dropped");
            Check(rig.Client.Running[0].Name.StartsWith("Notepad") && rig.Client.Running[0].Name.Length == 64, "names lose control and format characters and are cut to 64");
            Check(LaptopShellClient.IsAppId("app:0123456789abcdef") && !LaptopShellClient.IsAppId("app:0123456789ABCDEF") &&
                  !LaptopShellClient.IsAppId("app:0123456789abcde") && LaptopShellClient.IsWindowId("window:9") &&
                  !LaptopShellClient.IsWindowId("window:09") && !LaptopShellClient.IsWindowId("window:" + new string('1', 21)), "id formats match the agent's");
            int sent = rig.Sent.Count;
            Check(!rig.Client.Close("C:\\evil") && !rig.Client.OpenWindow("window:0", "x", false) && rig.Client.Icon("../../etc/passwd") == null, "made-up ids are refused here");
            rig.Advance(0.1f);
            Check(rig.Sent.Count == sent, "...and nothing is sent for them");
        }

        static void Icons()
        {
            var rig = new Rig();
            rig.Load();
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            string b64 = Convert.ToBase64String(png);

            var ids = new[] { Chrome, Edge, Notepad, Tool, Store, "window:12" };
            foreach (var id in ids) Check(rig.Client.Icon(id) == null, $"no icon yet for {id}: queued");
            rig.Client.Icon(Chrome); // asking twice queues once
            rig.Advance(0.01f);
            var asked = rig.Of("apps.icon");
            Check(asked.Count == LaptopShellClient.MaxIconsInFlight, $"at most {LaptopShellClient.MaxIconsInFlight} icons in flight ({asked.Count})");
            rig.Reply(asked[0], "{\"type\":\"apps.icon\",\"id\":\"" + Chrome + "\",\"size\":8,\"png\":\"" + b64 + "\"}");
            var icon = rig.Client.Icon(Chrome);
            Check(icon != null && icon.width == 8 && rig.IconsReady.Contains(Chrome), "an icon arrives, decodes and is cached");
            rig.Advance(0.01f);
            Check(rig.Of("apps.icon").Count == LaptopShellClient.MaxIconsInFlight + 1, "a finished icon lets the next one go");
            Check(rig.Of("apps.icon").Count(m => (string)m["id"] == Chrome) == 1, "a cached icon is never asked for again");

            rig.Reply(asked[1], "{\"type\":\"apps.icon\",\"id\":\"" + Edge + "\",\"size\":8,\"png\":\"bm90IGEgcG5n\"}");
            Check(rig.Client.Icon(Edge) == null && rig.Client.HasNoIcon(Edge), "something that isn't a PNG becomes the letter tile");
            var big = new byte[LaptopShellClient.MaxIconBytes + 1];
            Array.Copy(png, big, png.Length);
            rig.Reply(asked[2], "{\"type\":\"apps.icon\",\"id\":\"" + Notepad + "\",\"size\":8,\"png\":\"" + Convert.ToBase64String(big) + "\"}");
            Check(rig.Client.HasNoIcon(Notepad), "an oversized icon is refused");
            var huge = (byte[])png.Clone();
            huge[16] = 0; huge[17] = 0; huge[18] = 0x10; huge[19] = 0; // claims 4096 px wide
            Check(!LaptopShellClient.IsSmallPng(huge) && LaptopShellClient.IsSmallPng(png), "a PNG claiming a huge size is refused before decoding");
            rig.Reply(asked[3], "{\"type\":\"shell.error\",\"request\":\"apps.icon\",\"reason\":\"rate limited\"}");
            Check(!rig.Client.HasNoIcon(Tool), "a rate-limited icon is tried again, not given up");
            var store = rig.Of("apps.icon").Last();
            rig.Reply(store, "{\"type\":\"shell.error\",\"request\":\"apps.icon\",\"reason\":\"no icon\"}");
            Check(rig.Client.HasNoIcon((string)store["id"]) && rig.Failures.Count == 0, "no icon: the letter tile, and no error shown");
        }

        static void Search()
        {
            var rig = new Rig();
            rig.Client.Search("n");
            rig.Client.Search("no");
            rig.Client.Search("not");
            rig.Advance(0.05f);
            Check(rig.Of("search.query").Count == 0, "typing doesn't send a query for every key");
            rig.Advance(LaptopShellClient.SearchDebounce);
            var queries = rig.Of("search.query");
            Check(queries.Count == 1 && (string)queries[0]["q"] == "not", "after a pause, only the latest query goes");
            rig.Client.Search("note");
            rig.Advance(LaptopShellClient.SearchDebounce + 0.01f);
            rig.Reply(queries[0], "{\"type\":\"search.results\",\"q\":\"not\",\"results\":[{\"kind\":\"app\",\"id\":\"" + Notepad + "\",\"name\":\"Old\"}]}");
            Check(rig.Client.Results.Count == 0, "results for an older query are ignored");
            rig.Reply(rig.Last("search.query"), "{\"type\":\"search.results\",\"q\":\"note\",\"results\":[" +
                "{\"kind\":\"app\",\"id\":\"" + Notepad + "\",\"name\":\"Notepad\",\"running\":true}," +
                "{\"kind\":\"window\",\"id\":\"window:11\",\"title\":\"Notes.txt\",\"app\":\"Notepad\"}," +
                "{\"kind\":\"file\",\"id\":\"C:\\\\x.txt\",\"name\":\"x\"},{\"kind\":\"app\",\"id\":\"bad\",\"name\":\"x\"}]}");
            var results = rig.Client.Results;
            Check(results.Count == 2 && results[0].Kind == ShellResultKind.App && results[1].Kind == ShellResultKind.Window && results[1].AppName == "Notepad",
                  "app and window results kept, unknown kinds and bad ids dropped");
            int queriesBefore = rig.Of("search.query").Count;
            rig.Client.Search("  ");
            rig.Advance(1f);
            Check(rig.Client.Results.Count == 0 && rig.Of("search.query").Count == queriesBefore, "an empty query clears the results without asking");
            rig.Client.Search(new string('q', 300));
            rig.Advance(1f);
            Check(((string)rig.Last("search.query")["q"]).Length == LaptopShellClient.MaxQuery, "queries are cut to the agent's limit");
        }

        static void Opening()
        {
            var rig = new Rig();
            rig.Load();
            Check(rig.Client.Open(rig.Client.Find(Chrome)) && rig.Streams.Last() == ("window:14", "Inbox"), "tapping a running app streams its focused window");
            Check(rig.Client.Open(rig.Client.Find(Notepad)) && rig.Streams.Count == 1, "a minimized window isn't streamed straight away...");
            var restore = rig.Last("window.restore");
            Check(restore != null && (string)restore["id"] == "window:11", "...it is restored first");
            rig.Reply(restore, "{\"type\":\"window.done\",\"action\":\"restore\",\"id\":\"window:11\",\"ok\":true}");
            Check(rig.Streams.Last() == ("window:11", "Notes.txt"), "...then streamed");

            Check(rig.Client.Open(rig.Client.Find(Edge)), "a pinned app that isn't running...");
            var launch = rig.Last("apps.launch");
            Check(launch != null && (string)launch["id"] == Edge && rig.Notices.Any(n => n.Contains("Microsoft Edge")), "...is launched, with a note");
            rig.Reply(launch, "{\"type\":\"apps.launched\",\"id\":\"" + Edge + "\",\"window\":{\"id\":\"window:99\",\"title\":\"New tab\"}}");
            Check(rig.Streams.Last() == ("window:99", "New tab"), "the launched app's window opens as a stream");
            Check(rig.Client.Open(rig.Client.Find(Store)), "a launch without a window...");
            rig.Reply(rig.Last("apps.launch"), "{\"type\":\"apps.launched\",\"id\":\"" + Store + "\",\"window\":null}");
            Check(rig.Streams.Count == 3 && rig.Notices.Last().Contains("opened on the laptop"), "...says it opened on the laptop");

            int launches = rig.Of("apps.launch").Count;
            Check(!rig.Client.Open(new ShellApp { Id = "app:00000000000000dd", Name = "Gone", Launchable = false }) && rig.Of("apps.launch").Count == launches,
                  "an app that can't be launched and has no window: nothing is sent");
            Check(rig.Client.Open(new ShellResult { Kind = ShellResultKind.Window, Id = "window:12", Name = "GitHub" }) && rig.Streams.Last().id == "window:12",
                  "a window result streams that window");

            Check(rig.Client.Minimize("window:12") && (string)rig.Last("window.minimize")["id"] == "window:12", "minimize");
            Check(rig.Client.Close("window:13") && (string)rig.Last("window.close")["id"] == "window:13", "close");
            Check(rig.Client.Raise("window:12") && rig.Client.Restore("window:11"), "raise and restore");
            rig.Reply(rig.Last("window.close"), "{\"type\":\"window.done\",\"action\":\"close\",\"id\":\"window:13\",\"ok\":false}");
            Check(rig.Failures.Last().type == "window.close" && rig.Client.LastError != null, "a window action that didn't work is reported");
        }

        static void Failures()
        {
            var rig = new Rig();
            rig.Client.AutoRefresh = false; // only the requests this test makes
            rig.Reply(rig.Last("apps.pinned"), "{\"type\":\"shell.error\",\"request\":\"apps.pinned\",\"reason\":\"view only\"}");
            Check(rig.Failures.Last() == ("apps.pinned", "view only") && rig.Client.LastError.Contains("Allow control"), "a refusal is reported in words");
            rig.Reply(rig.Last("apps.running"), "{\"type\":\"apps.pinned\",\"apps\":[" + App(Chrome, "x") + "]}");
            Check(rig.Client.Pinned.Count == 0 && rig.Client.Running.Count == 0, "a reply of the wrong type for its rid is ignored");
            rig.Client.Receive(LinkJson.ParseObject("{\"type\":\"apps.running\",\"rid\":999,\"apps\":[" + App(Chrome, "x") + "]}"));
            rig.Client.Receive(LinkJson.ParseObject("{\"type\":\"apps.running\",\"apps\":[" + App(Chrome, "x") + "]}"));
            Check(rig.Client.Running.Count == 0, "replies nobody asked for are ignored");
            int failures = rig.Failures.Count;
            rig.Advance(LaptopShellClient.RequestTimeout + 1f);
            Check(rig.Failures.Count > failures && rig.Failures.Last().reason == "timeout" && rig.Client.PendingCount == 0, "unanswered requests time out");
            rig.Client.Icon(Chrome);
            rig.Advance(0.01f);
            failures = rig.Failures.Count;
            rig.Advance(LaptopShellClient.RequestTimeout + 1f);
            Check(rig.Failures.Count == failures && !rig.Client.HasNoIcon(Chrome), "an icon that timed out is quietly asked again later");
            foreach (var reason in new[] { "view only", "cast only", "input off", "stopped", "unsupported", "rate limited", "busy", "not launchable", "unknown id", "timeout", "failed" })
                Check(!string.IsNullOrEmpty(LaptopShellClient.Describe("apps.launch", reason)), $"\"{reason}\" has words");
        }

#if SPATIAL_LAUNCHER_WEBRTC
        static void ControlChannel()
        {
            var sent = new List<string>();
            var control = new ControlChannelClient(sent.Add);
            var shell = new LaptopShellClient(json => control.SendShell(json));
            // wired the way LaptopShellLink wires them
            control.StateChanged += () => shell.SetAvailable(control.ShellUsable, LaptopShellClient.ParseOs(control.ShellOs));
            control.ShellReceived += shell.Receive;
            control.BeginConnecting();
            control.Receive("{\"type\":\"welcome\",\"version\":5,\"slots\":[{\"slot\":0,\"mid\":\"0\"}],\"input\":true,\"keyboard\":true," +
                            "\"cast_only\":false,\"media\":true,\"shell\":{\"os\":\"macos\",\"available\":true}}");
            Check(control.ShellOffered && control.ShellOs == "macos" && !control.ShellUsable, "the welcome says the shell is offered; view only can't use it");
            Check(!control.SendShell("{\"type\":\"apps.running\",\"rid\":1}") && sent.Count(s => s.Contains("apps.")) == 0, "view only: shell requests never leave the headset");
            control.ApproveControlFromConsent();
            control.Receive("{\"type\":\"control-granted\",\"keyboard\":true}");
            Check(control.ShellUsable && shell.Available && shell.Os == ShellOs.MacOS, "granted control makes the shell usable");
            Check(sent.Count(s => s.Contains("\"apps.")) == 3, "and the lists are asked for");
            var request = LinkJson.ParseObject(sent.Last(s => s.Contains("apps.pinned")));
            control.Receive("{\"type\":\"apps.pinned\",\"rid\":" + (long)request["rid"] + ",\"apps\":[" + App(Chrome, "Google Chrome") + "]}");
            Check(shell.Pinned.Count == 1, "shell replies are routed from the control channel");
            Check(!control.SendShell("{\"type\":\"search.query\",\"q\":\"" + new string('x', ControlChannelClient.MaxMessageBytes) + "\"}"), "an oversized shell request isn't sent");

            control.ReleaseControl();
            Check(!control.ShellUsable && !shell.Available, "releasing control ends shell use");
            int count = sent.Count;
            shell.Refresh();
            Check(sent.Count == count, "...and nothing more is sent");

            var castSent = new List<string>();
            var cast = new ControlChannelClient(castSent.Add);
            cast.BeginConnecting();
            cast.Receive("{\"type\":\"welcome\",\"version\":5,\"slots\":[],\"input\":false,\"cast_only\":true,\"media\":true,\"shell\":{\"os\":\"windows\",\"available\":true}}");
            Check(!cast.ShellUsable && !cast.SendShell("{\"type\":\"apps.running\"}"), "a cast-only session can't use the shell");

            var old = new ControlChannelClient(_ => { });
            old.BeginConnecting();
            old.Receive("{\"type\":\"welcome\",\"version\":5,\"slots\":[],\"input\":true}");
            Check(!old.ShellOffered && old.ShellOs == null, "an agent without the shell says nothing about it, and it isn't offered");

            control.Receive("{\"type\":\"control-revoked\",\"reason\":\"kill switch\"}");
            Check(!control.ShellUsable, "after the kill switch the shell stays unusable");
        }
#endif
    }
}
