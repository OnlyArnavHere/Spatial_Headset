using System;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The wire messages for laptop input that don't depend on a transport: the media controls of a Cast window.
    /// Pointer, key and text messages are built by <c>DataChannelInputSink</c> and <see cref="RemoteKeyProtocol"/>.
    /// </summary>
    public static class RemoteInputEncoding
    {
        /// <summary>The media controls a Cast window has, in order, as the agent names them (keys.MEDIA_ACTIONS).</summary>
        public static readonly string[] MediaActions =
            { "previous", "seek_back", "play_pause", "seek_forward", "next", "volume_down", "volume_up", "mute" };

        /// <summary>{"type":"media","action":...} for one of <see cref="MediaActions"/>, or null for anything else.</summary>
        public static string EncodeMedia(string action) =>
            Array.IndexOf(MediaActions, action) >= 0 ? $"{{\"type\":\"media\",\"action\":\"{action}\"}}" : null;
    }
}
