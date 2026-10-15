namespace SpatialLauncher.SpatialWindows
{
    public enum RemoteVideoState { Stopped, Connecting, WaitingForVideo, Receiving, Retrying, Failed }

    /// <summary>
    /// Video from the laptop agent for the Laptop window. Implemented by <c>WebRtcStreamSource</c> (WHEP over Unity
    /// WebRTC), which is compiled only with the SPATIAL_LAUNCHER_WEBRTC scripting define and the com.unity.webrtc
    /// package; without them the Laptop window still connects input and says video is unavailable.
    /// </summary>
    public interface IRemoteVideoSource : IContentSource, IConnectionStatus
    {
        /// <summary>The WHEP endpoint, e.g. http://192.168.1.5:8889/laptop/whep.</summary>
        string Url { get; set; }
        string User { get; set; }
        string Password { get; set; }
        RemoteVideoState CurrentState { get; }
        /// <summary>How many times the stream has been (re)established since Start.</summary>
        int ConnectCount { get; }
        /// <summary>Call once per frame.</summary>
        void Tick();
        /// <summary>Closes the current connection as if the network had dropped; the source reconnects (for tests).</summary>
        void SimulateDrop();
    }
}
