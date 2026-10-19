using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A remote screen in a window: the picture comes from a video source (for example <c>WebRtcStreamSource</c>),
    /// and pointer input on the window is converted to the remote screen's convention and forwarded to <see cref="Sink"/>.
    /// Without a video source (or before it has a frame) there is no texture and the status reads "Not connected".
    /// Starting and stopping this source doesn't connect or disconnect the video; its owner does that.
    /// </summary>
    public class RemoteStreamSource : IContentSource, IContentInput, IConnectionStatus
    {
        readonly Vector2Int expectedSize;

        /// <summary>Where the picture comes from; null for none.</summary>
        public IContentSource Video { get; }

        public Texture Texture => Video?.Texture;
        public Rect UVRect => Video?.UVRect ?? new Rect(0f, 0f, 1f, 1f);
        public Vector2Int PixelSize => Video != null && Video.PixelSize != Vector2Int.zero ? Video.PixelSize : expectedSize;
        public float Aspect => Video?.Texture != null ? Video.Aspect : expectedSize.x / (float)expectedSize.y;
        public IContentInput Input => ViewOnly ? null : this;
        /// <summary>A Cast window: the screen takes no pointer input at all (no collider; clicks only select the window).</summary>
        public bool ViewOnly { get; set; }
        public bool IsRunning { get; private set; }

        public bool IsConnected => Video is IConnectionStatus status ? status.IsConnected : Video?.Texture != null;

        public string StatusText
        {
            get
            {
                if (!IsRunning) return "Stopped";
                if (Video is IConnectionStatus status) return status.StatusText;
                return Video?.Texture != null ? "Connected" : "Not connected";
            }
        }

        /// <summary>Older name for <see cref="StatusText"/>.</summary>
        public string Status => StatusText;

        /// <summary>Where pointer input goes; null drops it.</summary>
        public IRemoteInputSink Sink { get; set; }

        public event Action Changed;

        /// <param name="expectedSize">The remote screen's resolution, used for the aspect until the video reports its own.</param>
        public RemoteStreamSource(IRemoteInputSink sink, Vector2Int expectedSize) : this(null, sink, expectedSize) { }

        public RemoteStreamSource(IContentSource video, IRemoteInputSink sink, Vector2Int expectedSize)
        {
            Video = video;
            Sink = sink;
            this.expectedSize = expectedSize.x > 0 && expectedSize.y > 0 ? expectedSize : new Vector2Int(1920, 1080);
            if (Video != null) Video.Changed += OnVideoChanged;
        }

        void OnVideoChanged() => Changed?.Invoke();

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
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
