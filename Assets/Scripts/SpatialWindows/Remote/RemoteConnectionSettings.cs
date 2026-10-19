using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>Defaults for the laptop windows (the link itself is set up by pairing, not by this asset).</summary>
    [CreateAssetMenu(menuName = "Spatial Launcher/Remote Connection Settings", fileName = "RemoteConnectionSettings")]
    public class RemoteConnectionSettings : ScriptableObject
    {
        [Tooltip("The laptop screen's resolution, used for the window's aspect until the stream reports its own.")]
        public Vector2Int expectedResolution = new Vector2Int(1920, 1080);
    }
}
