using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HoloKit;
using SpatialLauncher.LaptopLink;
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
    /// buttons (simulated mouse), and the Laptop picker, which connects nothing until a laptop is tapped. In DeviceMain it
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
                yield return CheckLinkConsent(gate, launcher);
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
            var a = new FakeSession { ModeLabel = "view-only" };
            var first = Ask(gate, a);
            gate.Approve();
            gate.BeginLive(a);
            var sameSource = Ask(gate, new FakeSession { SourceName = "Clock", ModeLabel = "view-only" });
            var otherMode = Ask(gate, new FakeSession());
            yield return 0.1f;
            Check(first.Approved && sameSource.Approved && !otherMode.Answered && gate.HasPending,
                "while a source is live, another view-only screen from it is covered; full control always asks");
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

        // The laptop link's sessions: full control always asks, an unanswered `other` covers nothing, modes are asked
        // separately, the live indicator follows the control state, and a panic stop cancels a waiting picker.
        static IEnumerator CheckLinkConsent(ConsentGate gate, LauncherWindow launcher)
        {
            var full = new FakeSession();
            Ask(gate, full);
            gate.Approve();
            gate.BeginLive(full);
            var again = Ask(gate, new FakeSession());
            yield return 0.1f;
            Check(!again.Answered && gate.HasPending, "a full-control session is live, and another full-control request still asks");
            gate.Deny();
            var viewOnly = Ask(gate, new FakeSession { ModeLabel = "view-only" });
            yield return 0.1f;
            Check(!viewOnly.Answered && gate.HasPending, "view-only and full control are asked separately: a live full-control session doesn't cover a view-only one");
            gate.Deny();
            gate.EndLive(full);

            var view = new FakeSession { ModeLabel = "view-only" };
            var viewAnswer = Ask(gate, view);
            gate.BeginLive(view); // live, yet its own question is still pending: it is an `other` that has not been answered
            var second = Ask(gate, new FakeSession { SourceName = "Clock", ModeLabel = "view-only" });
            yield return 0.1f;
            Check(!second.Approved && gate.HasPending && gate.Question.Contains("Notepad"), "an `other` session still pending means nothing is auto-approved");
            while (gate.HasPending) gate.Deny();
            gate.EndLive(view);
            Check(viewAnswer.Denied == "denied", "(that pending question was refused)");

#if SPATIAL_LAUNCHER_WEBRTC
            // The session's live entry (LinkAnswerer) is private and needs a real WebRTC session; this stand-in has the same
            // mode rule: "full control" in Control and Requested, otherwise "view-only".
            var control = new ControlChannelClient(null);
            control.BeginConnecting();
            control.Receive("{\"type\":\"welcome\",\"version\":" + ControlChannelClient.ProtocolVersion + ",\"input\":true}");
            var entry = new LiveEntry(control);
            gate.BeginLive(entry);
            string idle = gate.LiveText;
            control.ApproveControlFromConsent();
            string requested = gate.LiveText;
            control.Receive("{\"type\":\"control-granted\",\"keyboard\":true}");
            string granted = gate.LiveText;
            control.Receive("{\"type\":\"control-revoked\",\"reason\":\"stop\"}");
            string revoked = gate.LiveText;
            Check(idle.StartsWith("LIVE: Laptop") && idle.Contains("view-only") && requested.Contains("full control") && granted.Contains("full control") && revoked.Contains("view-only"),
                $"the LIVE indicator is on while a session is live and shows full control in Requested and Control (\"{idle}\" / \"{requested}\" / \"{granted}\" / \"{revoked}\")");
            gate.PanicStop();
            Check(entry.Stops == 1 && gate.LiveText == "", "a panic stop stops the live session and clears the indicator");

            yield return CheckPickerPanic(gate, launcher);
#else
            yield break;
#endif
        }

#if SPATIAL_LAUNCHER_WEBRTC
        sealed class LiveEntry : IRemoteSession
        {
            readonly ControlChannelClient control;
            public int Stops;
            public LiveEntry(ControlChannelClient control) { this.control = control; }
            public string SourceName => "Laptop";
            public string Address => "10.0.0.5";
            public string ModeLabel => control.State == LinkSessionState.Control || control.State == LinkSessionState.Requested ? "full control" : "view-only";
            public SpatialWindow Window => null;
            public void StopForPanic() => Stops++;
        }

        // Tapping a paired laptop makes the picker wait for it; a panic stop (which raises Panicked) puts it back to the list.
        static IEnumerator CheckPickerPanic(ConsentGate gate, LauncherWindow launcher)
        {
            string storeDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "consent-pairs-" + System.Guid.NewGuid().ToString("N"));
            var store = new PairStore(storeDir);
            if (LinkService.Current?.Host == null || LinkService.Current.Answerer == null)
            {
                Check(false, "the link service runs, so the picker can be driven");
                yield break;
            }
            const string name = "Consent Test Laptop";
            bool added = false;
            SpatialWindow window = null;
            bool raised = false;
            System.Action onPanic = () => raised = true;
            gate.Panicked += onPanic;
            try
            {
                LaptopPickerContent.PairStoreOverride = store;
                added = store.Add(name, LaptopLinkCoreTest.SpkiA, out _) == PairAddResult.Added;
                window = launcher.OpenLaptopPicker();
                yield return 0.4f;
                var picker = window.GetComponentInChildren<LaptopPickerContent>();
                if (picker == null || !added || picker.EntryButtons.Count == 0 || picker.EntryButtons.Count > LaptopPickerContent.RowsPerPage)
                {
                    Check(false, "the picker lists the temporary paired laptop on its first page");
                    yield break;
                }
                Press(picker.EntryButtons[picker.EntryButtons.Count - 1]);
                yield return 0.1f;
                bool waiting = picker.Mode == LaptopPickerContent.PickerMode.Waiting;
                gate.PanicStop();
                yield return 0.2f;
                Check(waiting && raised && picker.Mode == LaptopPickerContent.PickerMode.List, "a panic stop raises Panicked and a Waiting picker goes back to the list");
            }
            finally
            {
                gate.Panicked -= onPanic;
                if (window != null) launcher.Settings.Manager.Close(window);
                LaptopPickerContent.PairStoreOverride = null;
                try { System.IO.Directory.Delete(storeDir, true); } catch { }
            }
        }
#endif

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
            var window = launcher.OpenLaptopPicker();
            try
            {
                yield return 0.2f;
                var picker = window.GetComponentInChildren<LaptopPickerContent>();
                Check(picker != null && picker.Mode == LaptopPickerContent.PickerMode.List && !gate.HasPending && !gate.IsActive,
                    "opening the Laptop picker connects nothing and asks nothing: the question comes when a paired laptop is tapped");
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
