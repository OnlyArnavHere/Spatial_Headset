using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A remote screen streamed into a window (milestone M6: a PC desktop over Sunshine/Moonlight).
    /// Stub: there is no transport yet, so it never connects and has no texture. Pointer input on the
    /// window is already converted to the remote screen's convention and forwarded to <see cref="Sink"/>.
    /// </summary>
    public class RemoteStreamSource : IContentSource, IContentInput
    {
        public Texture Texture => null; // a decoded stream frame once connected
        public Rect UVRect => new Rect(0f, 0f, 1f, 1f);
        public Vector2Int PixelSize { get; }
        public float Aspect => PixelSize.x / (float)PixelSize.y;
        public IContentInput Input => this;
        public bool IsRunning { get; private set; }

        public bool IsConnected => false;
        public string Status => IsRunning ? "Not connected" : "Stopped";

        /// <summary>Where pointer input goes; null drops it.</summary>
        public IRemoteInputSink Sink { get; set; }

        public event Action Changed;

        /// <param name="expectedSize">The remote screen's resolution, used for the window's aspect until a stream reports its own.</param>
        public RemoteStreamSource(IRemoteInputSink sink, Vector2Int expectedSize)
        {
            Sink = sink;
            PixelSize = expectedSize.x > 0 && expectedSize.y > 0 ? expectedSize : new Vector2Int(1920, 1080);
        }

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true; // a real implementation starts connecting here
            Changed?.Invoke();
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            Changed?.Invoke();
        }

        // Window input arrives in texture convention (origin bottom-left); remote screens use top-left.
        static Vector2 ToScreen(Vector2 position) => new Vector2(position.x, 1f - position.y);

        public void PointerMove(Vector2 position) => Sink?.PointerMove(ToScreen(position));
        public void PointerDown(Vector2 position) => Sink?.PointerDown(ToScreen(position));
        public void PointerUp(Vector2 position) => Sink?.PointerUp(ToScreen(position));
        public void Scroll(Vector2 position, float delta) => Sink?.Scroll(ToScreen(position), delta);
    }
}
