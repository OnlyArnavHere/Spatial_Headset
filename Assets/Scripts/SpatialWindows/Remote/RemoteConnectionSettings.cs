using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Defaults for connecting the Laptop window to the laptop agent (Tools/LaptopAgent). What the user types into the
    /// connect panel is kept per device in PlayerPrefs (<see cref="RemoteConnectionPrefs"/>), on top of these defaults.
    /// The pairing token is never stored here: it changes every time the agent starts and this asset is committed.
    /// </summary>
    [CreateAssetMenu(menuName = "Spatial Launcher/Remote Connection Settings", fileName = "RemoteConnectionSettings")]
    public class RemoteConnectionSettings : ScriptableObject
    {
        [Tooltip("The laptop's address. 127.0.0.1 when the agent runs on the same machine as the editor.")]
        public string defaultHost = "127.0.0.1";
        [Tooltip("MediaMTX's WebRTC (WHEP) port; agent.py --webrtc-port.")]
        public int defaultVideoPort = 8889;
        [Tooltip("The agent's input WebSocket port; agent.py --input-port.")]
        public int inputPort = 8765;
        [Tooltip("The stream path the agent publishes to.")]
        public string streamPath = "laptop";
        [Tooltip("User name MediaMTX expects for viewers; the password is the pairing token.")]
        public string viewerUser = "viewer";
        [Tooltip("The laptop screen's resolution, used for the window's aspect until the stream reports its own.")]
        public Vector2Int expectedResolution = new Vector2Int(1920, 1080);
    }

    /// <summary>Connect-panel values remembered on this device (PlayerPrefs), falling back to the settings asset.</summary>
    public static class RemoteConnectionPrefs
    {
        const string HostKey = "SpatialLauncher.Remote.Host";
        const string PortKey = "SpatialLauncher.Remote.VideoPort";
        const string TokenKey = "SpatialLauncher.Remote.Token";

        public static string Host(RemoteConnectionSettings settings) =>
            PlayerPrefs.GetString(HostKey, settings != null ? settings.defaultHost : "127.0.0.1");

        public static int VideoPort(RemoteConnectionSettings settings) =>
            PlayerPrefs.GetInt(PortKey, settings != null ? settings.defaultVideoPort : 8889);

        /// <summary>The last token typed on this device (the agent prints a new one every run).</summary>
        public static string Token() => PlayerPrefs.GetString(TokenKey, "");

        public static void Save(string host, int videoPort, string token)
        {
            PlayerPrefs.SetString(HostKey, host);
            PlayerPrefs.SetInt(PortKey, videoPort);
            PlayerPrefs.SetString(TokenKey, token);
            PlayerPrefs.Save();
        }
    }
}
