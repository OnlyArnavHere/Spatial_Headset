using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    public enum WindowPartType
    {
        Content,
        TitleBar,
        CloseButton,
        ResizeHandle,
        /// <summary>An interactive element inside the content (button, slider); see <see cref="WindowControl"/>.</summary>
        Control,
        /// <summary>The pill under the window; dragging it moves the window like the title bar.</summary>
        GrabBar
    }

    /// <summary>
    /// Tags a collider as part of a window so input implementations can tell
    /// what was hit with a single raycast.
    /// </summary>
    public class WindowPart : MonoBehaviour
    {
        public SpatialWindow Window { get; private set; }
        public WindowPartType Type { get; private set; }

        public void Init(SpatialWindow window, WindowPartType type)
        {
            Window = window;
            Type = type;
        }
    }
}
