#if SPATIAL_LAUNCHER_WEBRTC
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test (no Play mode, no real WebRTC) for the link session's control and input gating: a
    /// ControlChannelClient with a captured send delegate and a DataChannelInputSink. View-only drops pointer, keys, text
    /// and scroll; media goes only with cast_only, or media and Control; nothing after Revoked; full control is requested
    /// only after the consent approval, once; denied goes back to view-only; revoked is final; a window close sends
    /// close{slot} even if the video was never started; an old welcome or a message over 16 KiB ends the session.
    /// Results use a [LaptopLinkSessionTest] prefix.
    /// </summary>
    static class LaptopLinkSessionTest
    {
        [MenuItem("Tools/Spatial Windows/Test Laptop Link Session")]
        static void Run() => EditorTestRunner.Run("LaptopLinkSessionTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Laptop Link Session", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        sealed class Rig
        {
            public readonly List<string> Control = new List<string>();
            public readonly List<string> Input = new List<string>();
            public readonly ControlChannelClient Client;
            public readonly DataChannelInputSink Sink;

            public Rig(bool input = true, bool keyboard = true, bool castOnly = false, bool media = false, int version = ControlChannelClient.ProtocolVersion, bool welcome = true)
            {
                Client = new ControlChannelClient(Control.Add);
                Sink = new DataChannelInputSink(0, Client, Input.Add);
                Client.BeginConnecting();
                if (welcome)
                    Client.Receive("{\"type\":\"welcome\",\"version\":" + version + ",\"slots\":[{\"slot\":0,\"mid\":\"0\"}],\"input\":" + Bool(input) + ",\"keyboard\":" + Bool(keyboard) +
                                   ",\"cast_only\":" + Bool(castOnly) + ",\"media\":" + Bool(media) + "}");
            }

            static string Bool(bool v) => v ? "true" : "false";

            public void Approve(bool keyboard = true)
            {
                Client.ApproveControlFromConsent();
                Client.Receive("{\"type\":\"control-granted\",\"keyboard\":" + Bool(keyboard) + "}");
            }

            public void Revoke() => Client.Receive("{\"type\":\"control-revoked\",\"reason\":\"kill switch\"}");

            /// <summary>Every kind of input the sink can send, as a window would produce it.</summary>
            public int InputSent()
            {
                int before = Input.Count;
                var p = new Vector2(0.5f, 0.5f);
                Sink.PointerMove(p);
                Sink.PointerDown(p);
                Sink.PointerUp(p);
                Sink.Scroll(p, 1f);
                Sink.Key("A", true);
                Sink.Key("A", false);
                Sink.Text("hi");
                Sink.Tick(100f);
                return Input.Count - before;
            }
        }

        static IEnumerator Steps()
        {
            ViewOnly();
            Media();
            Requests();
            Closing();
            Limits();
            yield break;
        }

        static void ViewOnly()
        {
            var rig = new Rig();
            Check(rig.Client.State == LinkSessionState.ViewOnly && rig.InputSent() == 0 && rig.Control.Count == 0, "view-only drops pointer, keys, text and scroll: nothing is sent");
            rig.Approve();
            int sent = rig.InputSent();
            Check(rig.Client.State == LinkSessionState.Control && sent >= 6, $"with control granted the same input is sent ({sent} messages)");
            rig.Revoke();
            Check(rig.Client.State == LinkSessionState.Revoked && rig.InputSent() == 0, "nothing is sent after the laptop revoked control");
            var noKeys = new Rig(keyboard: true);
            noKeys.Approve(keyboard: false);
            noKeys.Input.Clear();
            noKeys.Sink.Key("A", true);
            noKeys.Sink.Text("x");
            Check(noKeys.Input.Count == 0 && noKeys.Sink.InputAllowed && !noKeys.Sink.KeyboardAllowed, "control without the keyboard grant sends no keys or text");
            var refused = new Rig(input: false);
            refused.Approve();
            Check(refused.InputSent() == 0, "an agent that takes no input gets none, even in Control");
        }

        static void Media()
        {
            var rig = new Rig(castOnly: true, media: true);
            bool viewOnly = rig.Sink.SendMedia("play_pause");
            Check(viewOnly && rig.Input.Count == 1 && rig.Input[0] == "{\"type\":\"media\",\"action\":\"play_pause\"}", "a cast-only agent takes media controls in view-only mode");
            Check(!rig.Sink.SendMedia("power") && !rig.Sink.SendMedia(null) && rig.Input.Count == 1, "an action off the list sends nothing");
            Check(rig.InputSent() == 0, "cast-only view-only still drops pointer and keys");
            rig.Revoke();
            Check(!rig.Sink.SendMedia("mute") && rig.Input.Count == 1, "nothing, media included, after Revoked");

            var needs = new Rig(media: true);
            bool before = needs.Sink.SendMedia("next");
            needs.Approve();
            bool after = needs.Sink.SendMedia("next");
            Check(!before && after && needs.Input.Count == 1, "without cast_only, media needs media and Control");
            var noMedia = new Rig(media: false);
            noMedia.Approve();
            Check(!noMedia.Sink.SendMedia("next") && noMedia.Input.Count == 0, "Control alone doesn't allow media if the agent doesn't take it");
            var ended = new Rig(castOnly: true);
            ended.Client.End("gone");
            Check(!ended.Sink.SendMedia("next") && ended.Input.Count == 0, "no media after the session ended");
        }

        static void Requests()
        {
            var rig = new Rig();
            Check(!rig.Client.RequestControl() && rig.Control.Count == 0 && rig.Client.State == LinkSessionState.ViewOnly, "RequestControl sends nothing without the consent approval");
            rig.Client.ApproveControlFromConsent();
            Check(rig.Control.Count == 1 && rig.Control[0] == "{\"type\":\"control-request\",\"consent\":\"full control\"}" && rig.Client.State == LinkSessionState.Requested,
                "the consent approval sends the control request");
            rig.Client.ReleaseControl();
            rig.Control.Clear();
            Check(!rig.Client.RequestControl() && rig.Control.Count == 0, "the approval is used up: a second request sends nothing");

            rig.Client.ApproveControlFromConsent();
            rig.Client.Receive("{\"type\":\"control-denied\",\"reason\":\"not now\"}");
            Check(rig.Client.State == LinkSessionState.ViewOnly && rig.Client.ControlDeniedReason == "not now", "denied returns to view-only with the reason");

            var early = new Rig(welcome: false);
            early.Client.ApproveControlFromConsent();
            early.Client.Receive("{\"type\":\"welcome\",\"version\":5,\"input\":true}");
            Check(early.Control.Count == 0 && !early.Client.RequestControl(), "an approval given before the welcome doesn't leave a request behind");

            var final = new Rig();
            final.Approve();
            final.Revoke();
            final.Control.Clear();
            final.Client.ApproveControlFromConsent();
            final.Client.Receive("{\"type\":\"control-granted\",\"keyboard\":true}");
            Check(final.Client.State == LinkSessionState.Revoked && final.Control.Count == 0 && !final.Sink.InputAllowed && final.Client.RevokedReason == "kill switch",
                "revoked is final: no new request, a late grant is ignored");
            var welcomed = new Rig(welcome: false);
            welcomed.Client.Receive("{\"type\":\"welcome\",\"version\":5,\"input\":true,\"control\":\"revoked\"}");
            Check(welcomed.Client.State == LinkSessionState.Revoked && !welcomed.Client.InputAllowed, "a welcome that says control is revoked starts revoked");
        }

        static void Closing()
        {
            var rig = new Rig();
            var slot = new SlotVideoSource(3, new Vector2Int(1920, 1080));
            slot.StopRequested += rig.Client.CloseSlot;
            slot.Bind("path", "window", 100, 100, 0f); // opened by the agent, but its video was never started
            slot.Stop();
            Check(rig.Control.SequenceEqual(new[] { "{\"type\":\"close\",\"slot\":3}" }), "closing a window sends close{slot} even if its video was never started");
            slot.Stop();
            var unbound = new SlotVideoSource(4, new Vector2Int(1920, 1080));
            unbound.StopRequested += rig.Client.CloseSlot;
            unbound.Stop();
            Check(rig.Control.Count == 1, "a slot with no window sends nothing, and closing twice sends once");
        }

        static void Limits()
        {
            var old = new Rig(version: 4);
            Check(old.Client.State == LinkSessionState.Ended && old.Client.EndReason == "laptop agent out of date", "a welcome older than protocol 5 ends the session");
            var none = new Rig(welcome: false);
            none.Client.Receive("{\"type\":\"welcome\",\"slots\":[]}");
            Check(none.Client.State == LinkSessionState.Ended, "a welcome without a version ends it too");

            const string head = "{\"type\":\"x\",\"pad\":\"";
            string Padded(int bytes) => head + new string('a', bytes - head.Length - 2) + "\"}";
            var ok = new Rig();
            ok.Client.Receive(Padded(ControlChannelClient.MaxMessageBytes));
            var tooBig = new Rig();
            tooBig.Client.Receive(Padded(ControlChannelClient.MaxMessageBytes + 1));
            var bytes = new Rig();
            bytes.Client.Receive(System.Text.Encoding.UTF8.GetBytes(Padded(ControlChannelClient.MaxMessageBytes + 1)));
            Check(ControlChannelClient.MaxMessageBytes == 16 * 1024 && ok.Client.State == LinkSessionState.ViewOnly && tooBig.Client.State == LinkSessionState.Ended &&
                  tooBig.Client.EndReason == "control message too large" && bytes.Client.State == LinkSessionState.Ended, "a control message of 16 KiB passes; one byte more ends the session");
            tooBig.Client.Receive("{\"type\":\"welcome\",\"version\":5}");
            Check(tooBig.Client.State == LinkSessionState.Ended && !tooBig.Client.IsLive && !tooBig.Sink.SendMedia("next"), "an ended session stays ended and sends nothing");
        }
    }
}
#endif
