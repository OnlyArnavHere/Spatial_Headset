using System;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A VideoClip, local file or URL played by a VideoPlayer (added to the host GameObject) into a
    /// RenderTexture sized to the video. <see cref="Start"/> prepares and plays; <see cref="Stop"/> stops and
    /// frees the RenderTexture. Play/pause and seeking are separate, for playback controls.
    /// </summary>
    public class VideoPlayerSource : IContentSource
    {
        public VideoPlayer Player { get; }
        public Texture Texture => renderTexture;
        public Rect UVRect => new Rect(0f, 0f, 1f, 1f);
        public Vector2Int PixelSize { get; private set; }
        /// <summary>From the prepared video; from the clip before that; 16:9 for a URL that isn't prepared yet.</summary>
        public float Aspect { get; private set; } = 16f / 9f;
        public IContentInput Input => null;
        public bool IsRunning { get; private set; }

        public bool IsPrepared => Player != null && Player.isPrepared;
        public bool IsPlaying => Player != null && Player.isPlaying;
        /// <summary>Whether playback should run once prepared (the play/pause state the user chose).</summary>
        public bool WantsPlaying { get; private set; } = true;

        public event Action Changed;

        RenderTexture renderTexture;

        public VideoPlayerSource(GameObject host, VideoClip clip) : this(host)
        {
            Player.source = UnityEngine.Video.VideoSource.VideoClip;
            Player.clip = clip;
            if (clip != null && clip.height > 0) Aspect = clip.width / (float)clip.height;
        }

        /// <param name="url">An http(s) or file URL, or a local file path.</param>
        public VideoPlayerSource(GameObject host, string url) : this(host)
        {
            Player.source = UnityEngine.Video.VideoSource.Url;
            Player.url = url.Contains("://") ? url : Path.GetFullPath(url);
        }

        VideoPlayerSource(GameObject host)
        {
            Player = host.AddComponent<VideoPlayer>();
            Player.playOnAwake = false;
            Player.isLooping = true;
            Player.skipOnDrop = true;
            Player.renderMode = VideoRenderMode.RenderTexture;
            Player.audioOutputMode = VideoAudioOutputMode.Direct;
            Player.prepareCompleted += OnPrepared;
            Player.errorReceived += (_, message) => Debug.LogError($"VideoPlayerSource: {message}", host);
        }

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            if (IsPrepared) Resume();
            else Player.Prepare();
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (Player != null)
            {
                Player.Stop();
                Player.targetTexture = null;
            }
            ReleaseTexture();
            Changed?.Invoke();
        }

        public void Play()
        {
            WantsPlaying = true;
            if (IsRunning && IsPrepared) Player.Play();
        }

        public void Pause()
        {
            WantsPlaying = false;
            if (IsPrepared) Player.Pause();
        }

        public void TogglePlay()
        {
            if (WantsPlaying) Pause();
            else Play();
        }

        public double Time => Player != null ? Player.time : 0;
        public double Length => IsPrepared ? Player.length : 0;

        /// <summary>Seeks to a fraction (0..1) of the length.</summary>
        public void Seek(float normalized)
        {
            if (!IsPrepared || !Player.canSetTime || Player.length <= 0) return;
            Player.time = Mathf.Clamp01(normalized) * Player.length;
        }

        void OnPrepared(VideoPlayer player)
        {
            if (!IsRunning) return; // stopped while preparing
            PixelSize = new Vector2Int(Mathf.Max(1, (int)player.width), Mathf.Max(1, (int)player.height));
            Aspect = PixelSize.x / (float)PixelSize.y;
            Resume();
        }

        void Resume()
        {
            if (renderTexture == null)
            {
                renderTexture = new RenderTexture(PixelSize.x, PixelSize.y, 0) { name = "VideoPlayerSource" };
                renderTexture.Create();
                Player.targetTexture = renderTexture;
                Changed?.Invoke();
            }
            if (WantsPlaying) Player.Play();
        }

        void ReleaseTexture()
        {
            if (renderTexture == null) return;
            renderTexture.Release();
            UnityEngine.Object.Destroy(renderTexture);
            renderTexture = null;
        }
    }
}
