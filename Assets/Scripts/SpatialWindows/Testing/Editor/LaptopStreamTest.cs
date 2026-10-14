using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// End-to-end test of the Laptop window against a running laptop agent on this machine:
    ///
    ///   cd Tools/LaptopAgent
    ///   python clock.py
    ///   python agent.py --window "Spatial Launcher Test Clock" --input --dry-run-input
    ///
    /// then, in Play mode, Tools > Spatial Windows > Test Laptop Stream. It reads the current pairing token from the
    /// agent's runtime config (Tools/LaptopAgent/.runtime/mediamtx.yml), connects through the panel, and checks video,
    /// aspect, input round-trip time, the laptop clock readout, input mapping (dry run: nothing is clicked),
    /// automatic reconnection and wrong-token handling. Results are logged with a [LaptopTest] prefix.
    /// </summary>
    static class LaptopStreamTest
    {
        const string AgentConfig = "Tools/LaptopAgent/.runtime/mediamtx.yml";
        const float Settle = 0.7f;

        [MenuItem("Tools/Spatial Windows/Test Laptop Stream (agent running)")]
        static void Run() => EditorTestRunner.Run("LaptopTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Laptop Stream (agent running)", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            string token = ReadAgentToken();
            Check(token != null, $"the agent is running (token found in {AgentConfig})");
            if (launcher == null || manager == null || mouse == null || cam == null || token == null) yield break;

            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            string savedHost = RemoteConnectionPrefs.Host(null), savedToken = RemoteConnectionPrefs.Token();
            int savedPort = RemoteConnectionPrefs.VideoPort(null);
            var body = Body(launcher, manager, mouse, cam, token);
            try
            {
                while (body.MoveNext()) yield return body.Current;
            }
            finally
            {
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
                RemoteConnectionPrefs.Save(savedHost, savedPort, savedToken);
            }
        }

        static IEnumerator Body(LauncherWindow launcher, WindowManager manager, MouseWindowInput mouse, Camera cam, string token)
        {
            var window = launcher.OpenLaptopWindow();
            yield return Settle;
            var laptop = window != null ? window.GetComponentInChildren<RemoteStreamContent>() : null;
            Check(laptop != null && laptop.Video != null, "the Laptop window is in WebRTC mode (LauncherWindow > Laptop Streaming)");
            if (laptop == null || laptop.Video == null) yield break;

            // Connect through the panel.
            laptop.HostField.SetText("127.0.0.1");
            laptop.PortField.SetText("8889");
            laptop.TokenField.SetText(token);
            Click(mouse, cam, laptop.ConnectButton.transform.position);

            double deadline = EditorTestRunner.Now + 15.0;
            while (!laptop.Video.IsConnected && EditorTestRunner.Now < deadline) yield return 0.1f;
            Check(laptop.Video.IsConnected, $"video arrives over WHEP ({laptop.Video.StatusText})");
            if (!laptop.Video.IsConnected) yield break;

            deadline = EditorTestRunner.Now + 5.0;
            while (!(laptop.InputLink.IsConnected && laptop.InputLink.RoundTripMs.HasValue) && EditorTestRunner.Now < deadline) yield return 0.1f;
            Check(laptop.InputLink.IsConnected && laptop.InputLink.AgentDryRun,
                $"input connects with the token ({laptop.InputLink.StatusText}; streaming {laptop.InputLink.TargetDescription})");

            var size = laptop.Video.PixelSize;
            float aspect = size.x / (float)size.y;
            float shown = laptop.DisplaySize.x / laptop.DisplaySize.y;
            Check(Mathf.Abs(shown - aspect) < aspect * 0.01f, $"the window shows the {size.x}x{size.y} stream at its aspect ({shown:F3})");

            yield return 1.5f; // a few pings
            var rtt = laptop.InputLink.RoundTripMs;
            Check(rtt.HasValue && window.GetComponentInChildren<TMPro.TextMeshPro>() != null,
                $"round-trip time is measured ({rtt:0.0} ms, best {laptop.InputLink.BestRoundTripMs:0.0} ms)");
            var title = FindTitle(window);
            Check(title != null && title.Contains("ms"), $"the title bar shows it (\"{title}\")");
            Check(laptop.StatusLine.Contains("Laptop clock"), $"the status line shows the laptop clock (\"{laptop.StatusLine}\")");
            var offsetMs = (laptop.InputLink.LaptopNow.Value - System.DateTime.UtcNow).TotalMilliseconds;
            Check(Mathf.Abs((float)offsetMs) < 50f, $"same machine: the estimated laptop clock matches this one ({offsetMs:+0.0;-0.0} ms)");

            // Input mapping (the agent is in dry-run mode, so these clicks are only logged).
            manager.Focus(window);
            yield return Settle;
            var surface = laptop.Surface.transform;
            Vector2Int? a = null, b = null;
            yield return ClickAndAck(mouse, cam, laptop, surface.TransformPoint(new Vector3(0.25f, 0.25f, 0f)), p => a = p);
            yield return ClickAndAck(mouse, cam, laptop, surface.TransformPoint(new Vector3(-0.25f, -0.25f, 0f)), p => b = p);
            if (a.HasValue && b.HasValue)
            {
                // Screen (0.75, 0.25) to (0.25, 0.75): half the width left, half the height down, in laptop pixels.
                var delta = b.Value - a.Value;
                var expected = new Vector2Int(-size.x / 2, size.y / 2);
                Check(Mathf.Abs(delta.x - expected.x) <= 3 && Mathf.Abs(delta.y - expected.y) <= 3,
                    $"clicks map to the laptop's pixels: moved {delta} between two clicks, expected {expected}");
            }
            else Check(false, "the agent acknowledged both clicks");

            // Automatic reconnection after a dropped connection.
            int before = laptop.Video.ConnectCount;
            laptop.Video.SimulateDrop();
            deadline = EditorTestRunner.Now + 3.0;
            while (laptop.Video.IsConnected && EditorTestRunner.Now < deadline) yield return 0.05f;
            bool dropped = !laptop.Video.IsConnected;
            deadline = EditorTestRunner.Now + 15.0;
            while (!laptop.Video.IsConnected && EditorTestRunner.Now < deadline) yield return 0.1f;
            Check(dropped && laptop.Video.IsConnected && laptop.Video.ConnectCount == before + 1,
                $"a dropped stream reconnects on its own ({laptop.Video.StatusText})");

            // Disconnect, then a wrong token: a clear message and no endless retrying.
            Click(mouse, cam, laptop.ConnectButton.transform.position);
            yield return 0.3f;
            Check(!laptop.IsActive && laptop.Video.Texture == null && laptop.StatusText == "Not connected", "Disconnect stops the stream");

            laptop.TokenField.SetText("wrong-token");
            Click(mouse, cam, laptop.ConnectButton.transform.position);
            deadline = EditorTestRunner.Now + 8.0;
            while ((laptop.Video.CurrentState != RemoteVideoState.Failed ||
                    laptop.InputLink.CurrentState != WebSocketRemoteInputSink.State.Rejected) && EditorTestRunner.Now < deadline)
                yield return 0.1f;
            Check(laptop.Video.CurrentState == RemoteVideoState.Failed && laptop.Video.StatusText.StartsWith("Wrong token"),
                $"a wrong token stops the video with a clear message (\"{laptop.Video.StatusText}\")");
            Check(laptop.InputLink.CurrentState == WebSocketRemoteInputSink.State.Rejected,
                $"and the input link stops retrying (\"{laptop.InputLink.StatusText}\")");
            Click(mouse, cam, laptop.ConnectButton.transform.position); // Disconnect
            yield return 0.2f;
            manager.Close(window);
        }

        static IEnumerator ClickAndAck(MouseWindowInput mouse, Camera cam, RemoteStreamContent laptop, Vector3 point,
            System.Action<Vector2Int> result)
        {
            int acks = laptop.InputLink.AckCount;
            Click(mouse, cam, point);
            double deadline = EditorTestRunner.Now + 3.0;
            while (laptop.InputLink.AckCount < acks + 2 && EditorTestRunner.Now < deadline) yield return 0.05f; // down + up
            if (laptop.InputLink.AckCount >= acks + 2 && laptop.InputLink.LastAck.HasValue) result(laptop.InputLink.LastAck.Value);
        }

        static void Click(MouseWindowInput mouse, Camera cam, Vector3 worldPoint)
        {
            Vector3 screen = cam.WorldToScreenPoint(worldPoint);
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
        }

        static string FindTitle(SpatialWindow window)
        {
            var title = WindowTestUtil.Visual(window).Find("TitleText");
            return title != null ? title.GetComponent<TMPro.TextMeshPro>().text : null;
        }

        // The agent writes this run's token into its MediaMTX config (git-ignored, deleted with .runtime).
        static string ReadAgentToken()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), AgentConfig);
            if (!File.Exists(path)) return null;
            var match = Regex.Match(File.ReadAllText(path), @"pass:\s*'([^']+)'");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
