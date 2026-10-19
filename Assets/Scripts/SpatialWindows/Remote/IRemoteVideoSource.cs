namespace SpatialLauncher.SpatialWindows
{
    public enum RemoteVideoState { Stopped, Connecting, WaitingForVideo, Receiving, Retrying, Failed }

    /// <summary>
    /// Video from the laptop agent for a laptop window. Implemented by <c>SlotVideoSource</c> (one slot of the link
    /// session), which is compiled only with the SPATIAL_LAUNCHER_WEBRTC scripting define and the com.unity.webrtc
    /// package; without them there are no laptop windows.
    /// </summary>
    public interface IRemoteVideoSource : IContentSource, IConnectionStatus
    {
        RemoteVideoState CurrentState { get; }
        /// <summary>How many times the stream has been (re)established since Start.</summary>
        int ConnectCount { get; }
        /// <summary>Call once per frame.</summary>
        void Tick();
        /// <summary>Closes the current connection as if the network had dropped; the source reconnects (for tests).</summary>
        void SimulateDrop();
    }
}
