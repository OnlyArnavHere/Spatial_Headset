using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Something a window can show: a still image, a video, a remote desktop stream. A source owns producing
    /// pixels; <see cref="ContentSurface"/> owns displaying them in a window. Content components
    /// (<see cref="ImageContent"/>, <see cref="VideoContent"/>, <see cref="RemoteStreamContent"/>) pair the two.
    /// </summary>
    public interface IContentSource
    {
        /// <summary>The current frame, or null while there is nothing to show (not loaded, not connected).</summary>
        Texture Texture { get; }

        /// <summary>The part of <see cref="Texture"/> to show, in UV space (full texture: 0,0,1,1).</summary>
        Rect UVRect { get; }

        /// <summary>Natural size in pixels, or zero while unknown.</summary>
        Vector2Int PixelSize { get; }

        /// <summary>Width / height to display at. Has a sensible value even before <see cref="PixelSize"/> is known.</summary>
        float Aspect { get; }

        /// <summary>Raised when <see cref="Texture"/>, <see cref="UVRect"/>, <see cref="PixelSize"/> or <see cref="Aspect"/> changes.</summary>
        event Action Changed;

        /// <summary>Pointer input for interactive sources; null for display-only ones.</summary>
        IContentInput Input { get; }

        bool IsRunning { get; }

        /// <summary>Begins producing frames (load, prepare, connect). Safe to call when already running.</summary>
        void Start();

        /// <summary>Stops producing frames and releases what Start acquired. Safe to call when stopped.</summary>
        void Stop();
    }

    /// <summary>
    /// Pointer input on a source's displayed image. Positions are normalized over the displayed image in
    /// texture convention: (0,0) bottom-left, (1,1) top-right.
    /// </summary>
    public interface IContentInput
    {
        void PointerMove(Vector2 position);
        void PointerDown(Vector2 position);
        void PointerUp(Vector2 position);
        /// <summary>Scroll by <paramref name="delta"/> notches; positive is up / away from the user.</summary>
        void Scroll(Vector2 position, float delta);
    }
}
