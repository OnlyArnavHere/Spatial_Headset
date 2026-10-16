using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HoloKit;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for the consent gate: the question and indicator text, Approve and Deny on the modal (as a pinch or
    /// click would), the 30 s timeout (shortened here), closing the modal, approval covering a source only while it is
    /// live and never remembered, panic stop (disconnects, closes, refuses what waits, returns Home), the Mono touch
    /// buttons (simulated mouse), and a real laptop stream window that connects nothing until approved. In DeviceMain it
    /// also approves with the stereo bar's TRIGGER (ALLOW) and checks it doesn't also step the zoom. Results use a
    /// [ConsentGateTest] prefix.
    /// </summary>
    static class ConsentGateTest
    {
        [MenuItem("Tools/Spatial Windows/Test Consent Gate")]
        static void Run() => EditorTestRunner.Run("ConsentGateTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Consent Gate", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        sealed class FakeSession : IRemoteSession
        {
            public string SourceName { get; set; } = "Notepad";
            public string Address { get; set; } = "192.168.1.20";
            public string ModeLabel { get; set; } = "full control";
            public SpatialWindow Window { get; set; }
            public int Stops;
            public void StopForPanic() => Stops++;
        }

        sealed class Outcome
        {
            public bool Approved;
            public string Denied;
            public bool Answered => Approved || Denied != null;
        }

        static Outcome Ask(ConsentGate gate, IRemoteSession session)
        {
            var outcome = new Outcome();
            gate.Request(session, () => outcome.Approved = true, reason => outcome.Denied = reason);
            return outcome;
        }

        static IEnumerator Steps()
        {
            var errors = new List<string>();
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && !message.Contains("[ConsentGateTest]")) errors.Add(message);
            };
            Application.logMessageReceived += onLog;
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var gate = launcher != null ? launcher.GetComponent<ConsentGate>() : null;
            Check(gate != null && ConsentGate.Current == gate && Mathf.Approximately(gate.TimeoutSeconds, 30f) &&
                  Mathf.Approximately(ConsentGate.DefaultTimeout, 30f),
                "the launcher adds the consent gate, with a 30 s timeout");
            if (gate == null)
            {
                Application.logMessageReceived -= onLog;
                yield break;
            }
            try
            {
                CheckText();
                yield return CheckModal(gate, launcher);
                yield return CheckTimeoutAndClose(gate);
                yield return CheckSessions(gate);
                yield return CheckPanic(gate, launcher);
                yield return CheckTouch(gate);
                yield return CheckStreamWindow(gate, launcher);
                yield return CheckTrigger(gate);
            }
            finally
            {
                Application.logMessageReceived -= onLog;
                gate.TimeoutSeconds = ConsentGate.DefaultTimeout;
                while (gate.HasPending) gate.Deny();
                foreach (var session in new List<IRemoteSession>(gate.Live)) gate.EndLive(session);
            }
            Check(errors.Count == 0, $"no errors logged during the test ({errors.Count}{(errors.Count > 0 ? ": " + errors[0] : "")})");
        }

        static void CheckText()
        {
            var s = new FakeSession();
            Check(ConsentGate.QuestionFor(s) == "Allow Notepad (192.168.1.20) to show its screen on this headset? (full control)",
                $"the question names the source, its address and the mode (\"{ConsentGate.QuestionFor(s)}\")");
            var two = new List<IRemoteSession> { s, new FakeSession { SourceName = "Clock" } };
            Check(ConsentGate.LiveTextFor(two) == "LIVE: Notepad (192.168.1.20, full control) +1 more", $"the indicator names what is live ({ConsentGate.LiveTextFor(two)})");
            var screen = new Vector2(2556f, 1179f);
            ConsentGate.ButtonRects(screen, out var approve, out var deny);
            var zoom = PassthroughZoomControl.ButtonRectFor(screen);
            var stop = ConsentGate.StopRect(screen);
            Check(!approve.Overlaps(deny) && !approve.Overlaps(zoom) && !deny.Overlaps(zoom) && approve.yMax < screen.y && approve.height > 80f &&
                  stop.yMin >= 0f && stop.xMax < screen.x,
                "the Mono touch buttons (Approve, Deny, Stop) are on screen, big enough, and clear of the zoom button");
        }

        static void Press(WindowButton button)
        {
            button.Press(button.transform.position);
            button.Release(true);
        }

        static IEnumerator CheckModal(ConsentGate gate, LauncherWindow launcher)
        {
            var manager = launcher.Settings.Manager;
            var s = new FakeSession();
            var outcome = Ask(gate, s);
            yield return 0.2f;
            var content = gate.Modal != null ? gate.Modal.GetComponentInChildren<ConsentContent>() : null;
            Check(gate.HasPending && content != null && manager.FocusedWindow == gate.Modal && content.Text == ConsentGate.QuestionFor(s) &&
                  content.Countdown.StartsWith("Denies in 30") && !outcome.Answered,
                $"a request opens the modal in front with the question and a countdown (\"{content?.Countdown}\")");
            if (content == null) yield break;
            Press(content.ApproveButton);
            yield return null;
            Check(outcome.Approved && !gate.HasPending && gate.Modal == null, "Approve (pinch or click) allows it and closes the modal");

            var second = Ask(gate, new FakeSession { Address = "10.0.0.9" });
            yield return 0.1f;
            Press(gate.Modal.GetComponentInChildren<ConsentContent>().DenyButton);
            yield return null;
            Check(second.Denied == "denied" && !second.Approved && !gate.HasPending, "Deny refuses it: nothing connects");
        }

        static IEnumerator CheckTimeoutAndClose(ConsentGate gate)
        {
            gate.TimeoutSeconds = 1f;
            var outcome = Ask(gate, new FakeSession());
            yield return 0.6f;
            bool waiting = gate.HasPending && !outcome.Answered;
            yield return 0.7f;
            Check(waiting && outcome.Denied == "timed out" && gate.Modal == null,
                "no answer within the timeout counts as Deny (shortened to 1 s here; 30 s in use)");
            gate.TimeoutSeconds = ConsentGate.DefaultTimeout;

            var closed = Ask(gate, new FakeSession());
            yield return 0.1f;
            Object.FindFirstObjectByType<LauncherWindow>().Settings.Manager.Close(gate.Modal);
            yield return null;
            Check(closed.Denied == "closed" && !gate.HasPending, "closing the modal with its close button is a Deny");
        }

        static IEnumerator CheckSessions(ConsentGate gate)
        {
            var a = new FakeSession();
            var first = Ask(gate, a);
            gate.Approve();
            gate.BeginLive(a);
            var sameSource = Ask(gate, new FakeSession { SourceName = "Clock" });
            var otherMode = Ask(gate, new FakeSession { ModeLabel = "view-only" });
            yield return 0.1f;
            Check(first.Approved && sameSource.Approved && !otherMode.Answered && gate.HasPending,
                "while a source is live, another screen from it in the same mode is covered; a different mode asks again");
            gate.Deny();
            Check(gate.LiveText.StartsWith("LIVE: Notepad"), $"the live indicator shows ({gate.LiveText})");

            gate.EndLive(a);
            var again = Ask(gate, new FakeSession());
            yield return 0.1f;
            Check(!again.Answered && gate.HasPending && gate.LiveText == "", "once nothing from it is live, it asks again (nothing is remembered)");

            var queued = Ask(gate, new FakeSession { Address = "10.0.0.7" });
            gate.Withdraw(new FakeSession()); // a different object: nothing happens
            Check(gate.HasPending && !queued.Answered, "a second request waits its turn");
            gate.Deny();
            yield return 0.1f;
            Check(again.Denied == "denied" && gate.HasPending && gate.Question.Contains("10.0.0.7"), "then it is asked");
            gate.Deny();
        }

        static IEnumerator CheckPanic(ConsentGate gate, LauncherWindow launcher)
        {
            var manager = launcher.Settings.Manager;
            var window = manager.Spawn("Fake stream");
            var live = new FakeSession { Window = window };
            Ask(gate, live);
            gate.Approve();
            gate.BeginLive(live);
            var waiting = Ask(gate, new FakeSession { Address = "10.1.1.1" });
            manager.HideAll(launcher.Launcher);
            int before = gate.PanicCount;
            gate.PanicStop();
            yield return 0.2f;
            Check(gate.PanicCount == before + 1 && live.Stops == 1 && window == null && waiting.Denied == "panic stop" && !gate.IsActive &&
                  gate.LiveText == "" && !manager.WindowsHidden && launcher.Launcher != null && manager.FocusedWindow == launcher.Launcher,
                "panic stop disconnects every live screen, closes its window, refuses what waits and brings Home to the front");
        }

        static IEnumerator Click(Vector2 screenPoint)
        {
            var mouse = Mouse.current;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPoint }.WithButton(MouseButton.Left));
            yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPoint });
            yield return null;
        }

        static IEnumerator CheckTouch(ConsentGate gate)
        {
            if (Mouse.current == null)
            {
                Check(false, "a mouse device exists for the touch-button check");
                yield break;
            }
            var screen = new Vector2(Screen.width, Screen.height);
            ConsentGate.ButtonRects(screen, out var approve, out var deny);
            var tapApprove = Ask(gate, new FakeSession());
            yield return 0.1f;
            Check(gate.TouchButtonsVisible, "in Mono the on-screen Approve and Deny buttons show while asking");
            yield return Click(new Vector2(approve.center.x, screen.y - approve.center.y));
            yield return 0.1f;
            var tapDeny = Ask(gate, new FakeSession());
            yield return 0.1f;
            yield return Click(new Vector2(deny.center.x, screen.y - deny.center.y));
            yield return 0.1f;
            Check(tapApprove.Approved && tapDeny.Denied == "denied", "a tap on Approve allows it; a tap on Deny refuses it");
            while (gate.HasPending) gate.Deny();
        }

        static IEnumerator CheckStreamWindow(ConsentGate gate, LauncherWindow launcher)
        {
            var manager = launcher.Settings.Manager;
            var target = new LaptopTarget { id = "test", title = "Consent test window", path = "test", width = 1280, height = 720 };
            var window = launcher.OpenLaptopStream(target);
            try
            {
                yield return 0.2f;
                var stream = window.GetComponentInChildren<RemoteStreamContent>();
                // Port 9 on this machine: nothing listens, so even an approved stream connects to nothing real.
                stream.HostField.SetText("127.0.0.1");
                stream.PortField.SetText("9");
                stream.TokenField.SetText("consent-test");
                stream.Connect();
                yield return 0.2f;
                Check(stream.AwaitingConsent && !stream.IsActive && stream.InputLink.CurrentState == WebSocketRemoteInputSink.State.Disconnected &&
                      (stream.Video == null || !stream.Video.IsConnected) && gate.Question.Contains("Consent test window (127.0.0.1)") &&
                      gate.Question.Contains("full control") && stream.StatusLine.StartsWith("Waiting for approval"),
                    $"a laptop stream asks before anything connects (\"{gate.Question}\"; \"{stream.StatusLine}\")");
                gate.Deny();
                yield return 0.2f;
                Check(!stream.IsActive && !stream.AwaitingConsent && stream.ConsentOutcome == "denied" &&
                      stream.InputLink.CurrentState == WebSocketRemoteInputSink.State.Disconnected && stream.StatusLine.StartsWith("Not allowed (denied)"),
                    $"denied, it stays disconnected and says so (\"{stream.StatusLine}\")");

                stream.Connect();
                yield return 0.1f;
                gate.Approve();
                yield return 0.2f;
                Check(stream.IsActive && stream.ConsentOutcome == "approved" && stream.InputLink.CurrentState != WebSocketRemoteInputSink.State.Disconnected &&
                      gate.Live.Contains(stream) && gate.LiveText.Contains("Consent test window"),
                    $"approved, it connects and shows as live ({stream.InputLink.CurrentState}; \"{gate.LiveText}\")");

                stream.Disconnect();
                Check(!gate.Live.Contains(stream) && gate.LiveText == "", "disconnecting ends the live session");
                stream.Connect();
                yield return 0.1f;
                Check(stream.AwaitingConsent && gate.HasPending, "and the next connect asks again");
                manager.Close(window);
                yield return 0.1f;
                Check(!gate.HasPending, "closing a window that waits withdraws its question");
            }
            finally
            {
                if (window != null) manager.Close(window);
            }
        }

        static IEnumerator CheckTrigger(ConsentGate gate)
        {
            var holoKit = Object.FindFirstObjectByType<HoloKitCameraManager>();
            if (holoKit == null) yield break; // DeviceMain only
            float zoom = SettingsStore.Current.display.passthroughZoom;
            holoKit.ScreenRenderMode = ScreenRenderMode.Stereo;
            try
            {
                yield return 0.5f;
                var outcome = Ask(gate, new FakeSession());
                yield return 0.2f;
                Check(holoKit.StereoTriggerLabel == "ALLOW" && !gate.TouchButtonsVisible,
                    $"in Stereo, while asking, the stereo bar's TRIGGER reads ALLOW (\"{holoKit.StereoTriggerLabel}\") and no touch buttons show");
                if (holoKit.StereoBar != null) holoKit.StereoBar.FireTrigger();
                yield return 0.2f;
                Check(outcome.Approved && Mathf.Approximately(SettingsStore.Current.display.passthroughZoom, zoom),
                    "TRIGGER approves, and doesn't also step the passthrough zoom");
                yield return 0.2f;
                Check(holoKit.StereoTriggerLabel == "ZOOM", "afterwards TRIGGER is ZOOM again");
            }
            finally
            {
                holoKit.ScreenRenderMode = ScreenRenderMode.Mono;
            }
        }
    }
}
