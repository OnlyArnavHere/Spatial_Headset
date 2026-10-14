using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Source of window interactions. The editor uses <see cref="MouseWindowInput"/>;
    /// a hand-tracking implementation will replace it on device.
    /// Implementations only report intent; <see cref="WindowManager"/> applies it.
    /// </summary>
    public interface IWindowInput
    {
        /// <summary>The user selected (focused) a window.</summary>
        event Action<SpatialWindow> SelectRequested;

        /// <summary>The user is dragging a window; the argument is its new world position.</summary>
        event Action<SpatialWindow, Vector3> DragRequested;

        /// <summary>The user is resizing a window; the argument is its new size in metres.</summary>
        event Action<SpatialWindow, Vector2> ResizeRequested;

        /// <summary>The user asked to close a window.</summary>
        event Action<SpatialWindow> CloseRequested;
    }
}
