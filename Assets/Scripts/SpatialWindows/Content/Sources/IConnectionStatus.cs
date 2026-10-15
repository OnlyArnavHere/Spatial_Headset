namespace SpatialLauncher.SpatialWindows
{
    /// <summary>A source or link that connects to something remote and can say how that's going.</summary>
    public interface IConnectionStatus
    {
        bool IsConnected { get; }
        /// <summary>One short line for a status display, e.g. "Connecting to 192.168.1.5…" or "Wrong token".</summary>
        string StatusText { get; }
    }
}
