using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Video;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Plays a VideoClip, a local file or a URL through a VideoPlayer into a RenderTexture, kept at the
    /// video's aspect ratio, with a control bar underneath: play/pause, a seek bar and the time.
    /// </summary>
    public class VideoContent : WindowContent
    {
        const float BarHeight = 0.045f;
        const float BarGap = 0.01f;
        const float Margin = 0.008f;
        const float TimeLabelWidth = 0.12f;
        const float TrackHeightRatio = 0.35f;

        /// <summary>Extra window height the control bar needs below the video, in metres.</summary>
        public const float ControlBarHeight = BarHeight + BarGap;

        static readonly Color BarColor = new Color(0.16f, 0.16f, 0.19f);
        static readonly Color ButtonColor = new Color(0.30f, 0.30f, 0.35f);
        static readonly Color TrackColor = new Color(0.32f, 0.32f, 0.36f);
        static readonly Color FillColor = new Color(0.20f, 0.78f, 0.90f);

        public VideoPlayer Player { get; private set; }
        public WindowButton PlayPauseButton { get; private set; }
        public WindowSlider SeekBar { get; private set; }
        public bool IsPrepared => Player != null && Player.isPrepared;
        public bool IsPlaying => Player != null && Player.isPlaying;
        /// <summary>Width / height of the video; 16:9 until the video is prepared.</summary>
        public float Aspect { get; private set; } = 16f / 9f;
        public Vector2 DisplaySize { get; private set; }

        Transform screen, bar, fill;
        TextMeshPro playGlyph, timeLabel;
        Material screenMaterial;
        RenderTexture renderTexture;
        bool wantPlaying = true;
        float trackLeft, trackWidth, trackY, trackHeight;

        public void Init(SpatialWindow window, Material baseMaterial, VideoClip clip)
        {
            Build(window, baseMaterial);
            if (clip != null) Aspect = clip.width / (float)clip.height;
            Player.source = VideoSource.VideoClip;
            Player.clip = clip;
            Prepare();
        }

        /// <param name="url">An http(s) or file URL, or a local file path.</param>
        public void Init(SpatialWindow window, Material baseMaterial, string url)
        {
            Build(window, baseMaterial);
            Player.source = VideoSource.Url;
            Player.url = url.Contains("://") ? url : Path.GetFullPath(url);
            Prepare();
        }

        public void TogglePlay()
        {
            wantPlaying = !wantPlaying;
            if (!IsPrepared) return;
            if (wantPlaying) Player.Play();
            else Player.Pause();
        }

        /// <summary>Seeks to a fraction (0..1) of the video's length.</summary>
        public void Seek(float normalized)
        {
            if (!IsPrepared || !Player.canSetTime || Player.length <= 0) return;
            Player.time = Mathf.Clamp01(normalized) * Player.length;
        }

        void Build(SpatialWindow window, Material baseMaterial)
        {
            Attach(window, baseMaterial);

            screenMaterial = CreateMaterial(Color.black);
            screen = CreateQuad("Screen", transform, screenMaterial);

            bar = CreateQuad("ControlBar", transform, CreateMaterial(BarColor));
            PlayPauseButton = CreateControl<WindowButton>("PlayPause", transform, CreateMaterial(ButtonColor));
            playGlyph = CreateLabel("PlayGlyph", transform, "II", 0.25f, TextAlignmentOptions.Center);
            SeekBar = CreateControl<WindowSlider>("SeekBar", transform, CreateMaterial(TrackColor));
            // The fill has no collider, so clicks on it reach the seek bar behind it.
            fill = CreateQuad("SeekFill", transform, CreateMaterial(FillColor));
            timeLabel = CreateLabel("Time", transform, "0:00 / 0:00", 0.2f, TextAlignmentOptions.MidlineRight);

            PlayPauseButton.Clicked += TogglePlay;
            SeekBar.ValueChanged += Seek;

            Player = gameObject.AddComponent<VideoPlayer>();
            Player.playOnAwake = false;
            Player.isLooping = true;
            Player.skipOnDrop = true;
            Player.renderMode = VideoRenderMode.RenderTexture;
            Player.audioOutputMode = VideoAudioOutputMode.Direct;
            Player.prepareCompleted += OnPrepared;
            Player.errorReceived += OnError;

            Relayout();
        }

        void Prepare() => Player.Prepare();

        void OnPrepared(VideoPlayer player)
        {
            int width = Mathf.Max(1, (int)player.width);
            int height = Mathf.Max(1, (int)player.height);
            Aspect = width / (float)height;

            renderTexture = new RenderTexture(width, height, 0) { name = "VideoContent" };
            renderTexture.Create();
            player.targetTexture = renderTexture;
            screenMaterial.mainTexture = renderTexture;
            screenMaterial.color = Color.white;
            Relayout();

            if (wantPlaying) player.Play();
        }

        void OnError(VideoPlayer player, string message)
        {
            Debug.LogError($"VideoContent ({Window?.Title}): {message}", this);
        }

        protected override void Layout(Vector2 size)
        {
            // Video fills the space above the control bar.
            float videoHeight = Mathf.Max(0.01f, size.y - ControlBarHeight);
            DisplaySize = FitAspect(new Vector2(size.x, videoHeight), Aspect);
            screen.localPosition = new Vector3(0f, size.y * 0.5f - videoHeight * 0.5f, 0f);
            screen.localScale = new Vector3(DisplaySize.x, DisplaySize.y, 1f);

            float barY = -size.y * 0.5f + BarHeight * 0.5f;
            bar.localPosition = new Vector3(0f, barY, -0.001f);
            bar.localScale = new Vector3(size.x, BarHeight, 1f);

            float button = BarHeight * 0.8f;
            float buttonX = -size.x * 0.5f + Margin + button * 0.5f;
            PlayPauseButton.transform.localPosition = new Vector3(buttonX, barY, -0.002f);
            PlayPauseButton.transform.localScale = new Vector3(button, button, 1f);
            playGlyph.transform.localPosition = new Vector3(buttonX, barY, -0.003f);
            playGlyph.rectTransform.sizeDelta = new Vector2(button, button);

            float timeX = size.x * 0.5f - Margin - TimeLabelWidth * 0.5f;
            timeLabel.transform.localPosition = new Vector3(timeX, barY, -0.002f);
            timeLabel.rectTransform.sizeDelta = new Vector2(TimeLabelWidth, BarHeight);

            trackLeft = buttonX + button * 0.5f + Margin;
            float trackRight = timeX - TimeLabelWidth * 0.5f - Margin;
            trackWidth = Mathf.Max(0.01f, trackRight - trackLeft);
            trackHeight = BarHeight * TrackHeightRatio;
            trackY = barY;
            SeekBar.transform.localPosition = new Vector3(trackLeft + trackWidth * 0.5f, trackY, -0.002f);
            SeekBar.transform.localScale = new Vector3(trackWidth, trackHeight, 1f);

            UpdateFill(Progress());
        }

        float Progress()
        {
            if (SeekBar != null && SeekBar.IsDragging) return SeekBar.DragValue;
            if (!IsPrepared || Player.length <= 0) return 0f;
            return (float)(Player.time / Player.length);
        }

        void UpdateFill(float progress)
        {
            float width = trackWidth * Mathf.Clamp01(progress);
            fill.gameObject.SetActive(width > 0.0005f);
            fill.localPosition = new Vector3(trackLeft + width * 0.5f, trackY, -0.003f);
            fill.localScale = new Vector3(Mathf.Max(width, 0.0001f), trackHeight, 1f);
        }

        void Update()
        {
            if (Player == null) return;
            UpdateFill(Progress());
            playGlyph.text = wantPlaying ? "II" : ">";
            double length = IsPrepared ? Player.length : 0;
            timeLabel.text = $"{FormatTime(Player.time)} / {FormatTime(length)}";
        }

        static string FormatTime(double seconds)
        {
            int total = Mathf.Max(0, (int)seconds);
            return $"{total / 60}:{total % 60:00}";
        }

        protected override void OnDestroy()
        {
            if (Player != null)
            {
                Player.Stop();
                Player.targetTexture = null;
            }
            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
            }
            base.OnDestroy();
        }
    }
}
