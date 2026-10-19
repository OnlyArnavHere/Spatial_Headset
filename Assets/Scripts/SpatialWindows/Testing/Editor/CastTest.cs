using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for Cast and Wired (USB) mode. Cast: the launcher's Cast icon opens the picker, the media
    /// message whitelist, and (with SPATIAL_LAUNCHER_WEBRTC) the input sink's cast-only gating against a fake control
    /// channel. Wired: the hotspot help and the Settings > Laptop rows.
    /// Results use a [CastTest] prefix.
    /// </summary>
    static class CastTest
    {
        [MenuItem("Tools/Spatial Windows/Test Cast and Wired")]
        static void Run() => EditorTestRunner.Run("CastTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Cast and Wired", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var errors = new List<string>();
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && !message.Contains("[CastTest]")) errors.Add(message);
            };
            Application.logMessageReceived += onLog;
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var gate = ConsentGate.Current;
            bool savedWired = SettingsStore.Current.laptop.wired;
            var opened = new List<SpatialWindow>();
            try
            {
                if (launcher == null || gate == null)
                {
                    Check(false, "scene has a LauncherWindow with a consent gate");
                    yield break;
                }
                CheckProtocol();
                CheckGating();
                yield return CheckCastPicker(launcher, opened);
                CheckHotspotHelp();
                yield return CheckWiredSettings(launcher);
            }
            finally
            {
                Application.logMessageReceived -= onLog;
                while (gate != null && gate.HasPending) gate.Deny();
                foreach (var window in opened)
                    if (window != null) launcher.Settings.Manager.Close(window);
                SettingsStore.Update(s =>
                {
                    s.laptop.wired = savedWired;
                });
            }
            Check(errors.Count == 0, $"no errors logged during the test ({errors.Count}{(errors.Count > 0 ? ": " + errors[0] : "")})");
        }

        static void CheckProtocol()
        {
            var actions = RemoteInputEncoding.MediaActions;
            Check(actions.Length == 8 && actions.Contains("play_pause") && actions.Contains("seek_back") && actions.Contains("seek_forward") &&
                  actions.Contains("next") && actions.Contains("previous") && actions.Contains("volume_up") && actions.Contains("volume_down") &&
                  actions.Contains("mute"), "a cast has exactly play/pause, seek back/forward, next/previous, volume up/down and mute");
            Check(RemoteInputEncoding.EncodeMedia("play_pause") == "{\"type\":\"media\",\"action\":\"play_pause\"}" &&
                  RemoteInputEncoding.EncodeMedia("power") == null && RemoteInputEncoding.EncodeMedia("Alt+F4") == null &&
                  RemoteInputEncoding.EncodeMedia(null) == null,
                "only those actions can be encoded; anything else isn't sent at all");
        }

        // The input sink sends media only when the agent is cast-only (or takes media with control granted), and
        // never pointer or keys without control. A fake control channel captures what would go on the wire.
        static void CheckGating()
        {
#if SPATIAL_LAUNCHER_WEBRTC
            var control = new ControlChannelClient(null);
            control.Receive("{\"type\":\"welcome\",\"version\":" + ControlChannelClient.ProtocolVersion +
                            ",\"slots\":[{\"slot\":0,\"mid\":\"0\"}],\"input\":true,\"keyboard\":true,\"cast_only\":true,\"media\":true}");
            var sent = new List<string>();
            var sink = new DataChannelInputSink(0, control, sent.Add);
            Check(sink.SendMedia("play_pause") && sent.Count == 1 && sent[0] == "{\"type\":\"media\",\"action\":\"play_pause\"}",
                "a cast-only agent takes a media control in view-only mode");
            Check(!sink.SendMedia("power") && !sink.SendMedia("Alt+F4") && sent.Count == 1, "anything off the whitelist is not sent");
            sink.PointerDown(new Vector2(0.5f, 0.5f));
            sink.Key("KeyA", true);
            sink.Text("hello");
            sink.Scroll(new Vector2(0.5f, 0.5f), 1f);
            Check(sent.Count == 1, "view-only: pointer, keys, text and scroll are dropped (only the media message went out)");
            foreach (var action in DataChannelInputSink.MediaActions) sink.SendMedia(action);
            Check(sink.MediaSentCount == 1 + DataChannelInputSink.MediaActions.Length && sent.Count == 1 + DataChannelInputSink.MediaActions.Length,
                "every media button sends exactly its own action");

            var plain = new ControlChannelClient(null);
            plain.Receive("{\"type\":\"welcome\",\"version\":" + ControlChannelClient.ProtocolVersion +
                          ",\"slots\":[{\"slot\":0,\"mid\":\"0\"}],\"input\":true,\"keyboard\":true,\"cast_only\":false,\"media\":true}");
            var plainSent = new List<string>();
            var viewOnly = new DataChannelInputSink(0, plain, plainSent.Add);
            Check(!viewOnly.SendMedia("play_pause") && plainSent.Count == 0, "without --cast-only and without granted control, media is not sent");
#endif
        }

        static IEnumerator CheckCastPicker(LauncherWindow launcher, List<SpatialWindow> opened)
        {
            Check(launcher.LauncherContent != null && launcher.LauncherContent.EntryLabel(9) == "Cast", "the launcher has a Cast icon");
            var picker = launcher.OpenCastPicker();
            opened.Add(picker);
            yield return 0.2f;
            Check(picker != null && picker.Title == "Cast" && picker.GetComponentInChildren<LaptopPickerContent>() != null,
                "the Cast icon opens the laptop picker for a cast");
        }

        static void CheckHotspotHelp()
        {
            Check(UsbHotspot.Help.Contains("Personal Hotspot") && UsbHotspot.Help.Contains("172.20.10.1") && UsbHotspot.PhoneAddress == "172.20.10.1",
                "the wired help says to turn on Personal Hotspot, plug in by USB and that agent.py dials 172.20.10.1");
        }

        static IEnumerator CheckWiredSettings(LauncherWindow launcher)
        {
            var window = launcher.OpenSettings();
            try
            {
                yield return 0.3f;
                var content = Object.FindFirstObjectByType<SettingsContent>();
                content.Select("Laptop");
                yield return 0.2f;
                var wired = content.Control("Laptop", "Link", "Wired (USB)");
                Check(wired != null && content.ValueText("Laptop", "Wired help") != null && content.ValueText("Laptop", "Status") != null,
                    "Settings > Laptop has Link (Wi-Fi / Wired (USB)), the link status and a help line");
                if (wired == null) yield break;
                wired.Press(wired.transform.position);
                wired.Release(true);
                yield return 0.1f;
                Check(SettingsStore.Current.laptop.wired && content.ValueText("Laptop", "Wired help").Contains("agent.py dials 172.20.10.1"),
                    $"choosing Wired shows how to connect (\"{content.ValueText("Laptop", "Wired help")}\")");
                Check(content.Control("Laptop", "Connection", "Open Laptop picker") != null, "Connection opens the Laptop picker");

                var value = FindDeep(content.transform, "Row Wired help").Find("Value");
                float bottom = content.transform.InverseTransformPoint(value.position).y - 0.018f;
                Check(bottom >= -SettingsContent.PreferredSize.y * 0.5f, $"the Laptop rows fit in the Settings window ({bottom:F3} m from the middle)");
            }
            finally
            {
                if (window != null) launcher.Settings.Manager.Close(window);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }
    }
}
