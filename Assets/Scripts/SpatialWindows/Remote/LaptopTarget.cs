using System;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Something the laptop agent can stream, as listed by its control server: the whole desktop or one window.
    /// Each has its own stream path (named after the title), so several can be watched at once.
    /// </summary>
    [Serializable]
    public class LaptopTarget
    {
        /// <summary>The agent's id: "desktop", "monitor:N" or "window:HWND".</summary>
        public string id;
        /// <summary>The MediaMTX path the agent streams it on.</summary>
        public string path;
        public string title;
        /// <summary>"window" or "monitor".</summary>
        public string kind;
        public string description;
        /// <summary>Its size on the laptop in pixels (0 if unknown).</summary>
        public int width, height;
    }
}
