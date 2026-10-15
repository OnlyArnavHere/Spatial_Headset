using System.Collections.Generic;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Which window keyboard input goes to: the front-most laptop stream window. Windows are in focus order (last = front),
    /// so clicking the virtual keyboard (which brings it to the front) keeps typing into the stream window focused before.
    /// </summary>
    public static class KeyboardRouting
    {
        public static RemoteStreamContent FindTarget(IReadOnlyList<SpatialWindow> windows)
        {
            if (windows == null) return null;
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                var window = windows[i];
                if (window == null) continue;
                var stream = window.GetComponentInChildren<RemoteStreamContent>();
                if (stream != null) return stream;
            }
            return null;
        }
    }

    /// <summary>Single-key editor shortcuts (H, N, B, L, I, T, [ and ]) stand down while the keyboard is in use.</summary>
    public static class KeyboardShortcuts
    {
        /// <summary>True while typing into a text field or while physical keys are forwarded to a laptop window.</summary>
        public static bool Blocked => WindowTextField.IsTyping || RemoteKeyboard.Current != null && RemoteKeyboard.Current.IsForwardingPhysicalKeys;
    }
}
