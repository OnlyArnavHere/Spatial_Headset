namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Receives keyboard input aimed at a remote screen (a streamed laptop window) and sends it on. Keys are named as in
    /// <see cref="RemoteKeyProtocol"/> ("A", "Enter", "Ctrl", "Left"...); text is typed as characters.
    /// </summary>
    public interface IRemoteKeyboardSink
    {
        /// <summary>Presses (<paramref name="down"/> true) or releases a named key.</summary>
        void Key(string code, bool down);

        /// <summary>Types printable text, whatever the remote keyboard layout.</summary>
        void Text(string text);
    }
}
