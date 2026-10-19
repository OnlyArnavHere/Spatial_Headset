#if SPATIAL_LAUNCHER_WEBRTC
using System;
using Unity.WebRTC;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The video of one slot (one m-line) of the link session as a content texture. The slot's track exists for the
    /// whole session; a window is bound to it when the agent answers an open (<see cref="Bind"/>) and unbound when it ends.
    /// Drives the texture, <see cref="Tick"/> and <see cref="Changed"/> like <c>WebRtcStreamSource</c>; no reconnect loop
    /// (the session ending ends it). <see cref="Url"/>, <see cref="User"/> and <see cref="Password"/> are unused here.
    /// </summary>
    public sealed class SlotVideoSource : IRemoteVideoSource
    {
        const float FirstFrameTimeout = 10f;

        readonly Vector2Int expectedSize;
        VideoStreamTrack track;
        bool bound;
        float boundAt;
        Vector2Int lastSize;

        public int Slot { get; }
        /// <summary>The m-line's mid, set from the offer; the track is matched to the slot by it.</summary>
        public string Mid { get; internal set; }
        /// <summary>False when the headset's answer rejected this slot's m-line (no H.264 decoder).</summary>
        public bool Usable { get; internal set; } = true;
        public bool IsBound => bound;
        public string Path { get; private set; }
        public string Description { get; private set; } = "";

        public string Url { get; set; }
        public string User { get; set; }
        public string Password { get; set; }

        public Texture Texture { get; private set; }
        public Rect UVRect => new Rect(0f, 0f, 1f, 1f);
        public Vector2Int PixelSize => Texture != null ? new Vector2Int(Texture.width, Texture.height) : Vector2Int.zero;
        public float Aspect => Texture != null && Texture.height > 0
            ? Texture.width / (float)Texture.height
            : expectedSize.x / (float)Mathf.Max(1, expectedSize.y);
        public IContentInput Input => null;
        public bool IsRunning { get; private set; }

        public RemoteVideoState CurrentState { get; private set; } = RemoteVideoState.Stopped;
        public bool IsConnected => CurrentState == RemoteVideoState.Receiving && Texture != null;
        public string StatusText { get; private set; } = "Not connected";
        public int ConnectCount { get; private set; }

        public event Action Changed;
        /// <summary>The window using this slot was closed on the headset (slot): the session sends close{slot}.</summary>
        public event Action<int> StopRequested;

        public SlotVideoSource(int slot, Vector2Int expectedSize)
        {
            Slot = slot;
            this.expectedSize = expectedSize.x > 0 && expectedSize.y > 0 ? expectedSize : new Vector2Int(1920, 1080);
        }

        internal void AttachTrack(VideoStreamTrack video)
        {
            if (track == video) return;
            DetachTrack();
            track = video;
            track.OnVideoReceived += OnFrame;
        }

        internal void DetachTrack()
        {
            if (track == null) return;
            track.OnVideoReceived -= OnFrame;
            LastDetached = track; // only unhooked: the session owns disposal
            track = null;
        }

        internal VideoStreamTrack LastDetached { get; private set; }

        public bool HasTrack => track != null;

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            if (!bound) SetState(RemoteVideoState.Connecting, "Waiting for the laptop…");
        }

        /// <summary>The window closed: tells the agent to free the slot, and stops showing video.</summary>
        public void Stop()
        {
            bool wasBound = bound;
            if (!IsRunning && !wasBound) return;
            IsRunning = false;
            ClearWindow();
            SetState(RemoteVideoState.Stopped, "Not connected");
            if (wasBound) StopRequested?.Invoke(Slot);
        }

        public void SimulateDrop() { } // nothing to reconnect: the session carries every window

        /// <summary>The agent opened a window on this slot.</summary>
        public void Bind(string path, string description, int width, int height, float now)
        {
            bound = true;
            boundAt = now;
            Path = path;
            Description = description ?? "";
            var current = track != null ? track.Texture : null;
            if (current != null)
            {
                // The slot's track keeps its texture across windows and only announces the first frame once.
                Texture = current;
                lastSize = new Vector2Int(current.width, current.height);
                ConnectCount++;
                SetState(RemoteVideoState.Receiving, $"Streaming {current.width}x{current.height}");
            }
            else SetState(RemoteVideoState.WaitingForVideo, "Connected, waiting for video…");
        }

        /// <summary>The window went away on the laptop, or the session ended.</summary>
        public void Unbind(string reason)
        {
            if (!bound && CurrentState == RemoteVideoState.Failed) return;
            ClearWindow();
            SetState(RemoteVideoState.Failed, reason);
        }

        void ClearWindow()
        {
            bound = false;
            Path = null;
            Description = "";
            if (Texture != null)
            {
                Texture = null;
                Changed?.Invoke();
            }
        }

        void OnFrame(Texture texture)
        {
            if (!bound) return;
            Texture = texture;
            lastSize = new Vector2Int(texture.width, texture.height);
            ConnectCount++;
            SetState(RemoteVideoState.Receiving, $"Streaming {texture.width}x{texture.height}");
        }

        /// <summary>Notices resolution changes (the decoder may replace its texture) and a window that sends no video.</summary>
        public void Tick() => Tick(Time.realtimeSinceStartup);

        public void Tick(float now)
        {
            if (!bound) return;
            if (CurrentState == RemoteVideoState.WaitingForVideo && now - boundAt > FirstFrameTimeout)
            {
                SetState(RemoteVideoState.Failed, "No video arrived (is the captured window minimized?)");
                return;
            }
            var current = track != null ? track.Texture : null;
            if (current == null || CurrentState != RemoteVideoState.Receiving) return;
            var size = new Vector2Int(current.width, current.height);
            if (current == Texture && size == lastSize) return;
            Texture = current;
            lastSize = size;
            Changed?.Invoke();
        }

        void SetState(RemoteVideoState state, string text)
        {
            if (state == CurrentState && text == StatusText) return;
            CurrentState = state;
            StatusText = text;
            Changed?.Invoke();
        }
    }
}
#endif
