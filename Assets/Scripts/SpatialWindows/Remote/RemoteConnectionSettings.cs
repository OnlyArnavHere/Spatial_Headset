using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Defaults for connecting the Laptop window to the laptop agent (Tools/LaptopAgent). What the user types into the
    /// connect panel is kept per device in the settings file (<see cref="RemoteConnectionPrefs"/>, <see cref="SettingsStore"/>).
    /// The pairing token is never stored here: it changes every time the agent starts and this asset is committed.
    /// </summary>
    [CreateAssetMenu(menuName = "Spatial Launcher/Remote Connection Settings", fileName = "RemoteConnectionSettings")]
    public class RemoteConnectionSettings : ScriptableObject
    {
        [Tooltip("The laptop's address. 127.0.0.1 when the agent runs on the same machine as the editor.")]
        public string defaultHost = "127.0.0.1";
        [Tooltip("MediaMTX's WebRTC (WHEP) port; agent.py --webrtc-port.")]
        public int defaultVideoPort = 8889;
        [Tooltip("The agent's control and input WebSocket port; agent.py --input-port.")]
        public int inputPort = 8765;
        [Tooltip("User name MediaMTX expects for viewers; the password is the pairing token.")]
        public string viewerUser = "viewer";
        [Tooltip("The laptop screen's resolution, used for the window's aspect until the stream reports its own.")]
        public Vector2Int expectedResolution = new Vector2Int(1920, 1080);
    }

    /// <summary>
    /// Connect-panel values remembered on this device: the Laptop section of the settings file
    /// (<see cref="SettingsStore"/>), so the Settings window, the picker and stream windows share them.
    /// </summary>
    public static class RemoteConnectionPrefs
    {
        // Where earlier versions kept these; read once into the settings file.
        const string HostKey = "SpatialLauncher.Remote.Host";
        const string PortKey = "SpatialLauncher.Remote.VideoPort";
        const string TokenKey = "SpatialLauncher.Remote.Token";

        public static string Host(RemoteConnectionSettings settings)
        {
            string host = SettingsStore.Current.laptop.host;
            return !string.IsNullOrEmpty(host) ? host : settings != null ? settings.defaultHost : "127.0.0.1";
        }

        public static int VideoPort(RemoteConnectionSettings settings) => SettingsStore.Current.laptop.videoPort;

        /// <summary>The last token typed on this device (the agent prints a new one every run).</summary>
        public static string Token() => SettingsStore.Current.laptop.pairCode;

        public static void Save(string host, int videoPort, string token)
        {
            SettingsStore.Update(s =>
            {
                s.laptop.host = host;
                s.laptop.videoPort = videoPort;
                s.laptop.pairCode = token;
            });
            SettingsStore.Save();
        }

        /// <summary>Moves values saved by earlier versions (PlayerPrefs) into a settings file that doesn't exist yet.</summary>
        public static void MigrateFromPlayerPrefs()
        {
            if (System.IO.File.Exists(SettingsStore.Path) || !PlayerPrefs.HasKey(HostKey)) return;
            Save(PlayerPrefs.GetString(HostKey, "127.0.0.1"), PlayerPrefs.GetInt(PortKey, 8889), PlayerPrefs.GetString(TokenKey, ""));
            PlayerPrefs.DeleteKey(HostKey);
            PlayerPrefs.DeleteKey(PortKey);
            PlayerPrefs.DeleteKey(TokenKey);
        }
    }
}
