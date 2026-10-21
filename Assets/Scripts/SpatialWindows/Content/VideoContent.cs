using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.Video;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Plays a VideoClip, a local file or a URL (a <see cref="VideoPlayerSource"/>), kept at the video's
    /// aspect ratio, with a control bar underneath: play/pause, a seek bar and the time.
    /// </summary>
    public class VideoContent : WindowContent
    {
        const float BarHeight = 0.056f;
        const float BarGap = 0.01f;
        const float Margin = 0.008f;
        const float TimeLabelWidth = 0.12f;
        const float TrackHeightRatio = 0.35f;

        /// <summary>Extra window height the control bar needs below the video, in metres.</summary>
        public const float ControlBarHeight = BarHeight + BarGap;

        static readonly Color BarColor = UITheme.GlassRaised;
        static readonly Color ButtonColor = UITheme.GlassRaised;
        static readonly Color TrackColor = UITheme.GlassRaised;
        static readonly Color FillColor = UITheme.Accent;

        public VideoPlayerSource Source { get; private set; }
        public VideoPlayer Player => Source?.Player;
        public WindowButton PlayPauseButton { get; private set; }
        public WindowSlider SeekBar { get; private set; }
        public bool IsPrepared => Source != null && Source.IsPrepared;
        public bool IsPlaying => Source != null && Source.IsPlaying;
        /// <summary>Width / height of the video; the clip's (or 16:9 for a URL) until the video is prepared.</summary>
        public float Aspect => Source?.Aspect ?? 16f / 9f;
        public Vector2 DisplaySize => screen != null ? screen.DisplaySize : Vector2.zero;

        ContentSurface screen;
        Transform bar, fill;
        TextMeshPro playGlyph, timeLabel;
        float trackLeft, trackWidth, trackY, trackHeight;

        public void Init(SpatialWindow window, Material baseMaterial, VideoClip clip)
        {
            Attach(window, baseMaterial);
            Build(new VideoPlayerSource(gameObject, clip));
        }

        /// <param name="url">An http(s) or file URL, or a local file path.</param>
        public void Init(SpatialWindow window, Material baseMaterial, string url)
        {
            Attach(window, baseMaterial);
            Build(new VideoPlayerSource(gameObject, url));
        }

        public void TogglePlay() => Source?.TogglePlay();

        /// <summary>Seeks to a fraction (0..1) of the video's length.</summary>
        public void Seek(float normalized) => Source?.Seek(normalized);

        void Build(VideoPlayerSource source)
        {
            Source = source;
            screen = CreateSurface("Screen", transform, source);

            bar = CreateQuad("ControlBar", transform, CreateMaterial(BarColor));
            Round(bar, UITheme.ControlRadius);
            PlayPauseButton = CreateControl<WindowButton>("PlayPause", transform, CreateMaterial(ButtonColor));
            playGlyph = CreateLabel("PlayGlyph", transform, "II", TextRole.Headline, TextAlignmentOptions.Center);
            SeekBar = CreateControl<WindowSlider>("SeekBar", transform, CreateMaterial(TrackColor));
            AddKnob(SeekBar);
            // The fill has no collider, so clicks on it reach the seek bar behind it.
            fill = CreateQuad("SeekFill", transform, CreateMaterial(FillColor), FillLayer);
            Round(fill, 0f, capsule: true);
            timeLabel = CreateLabel("Time", transform, "0:00 / 0:00", TextRole.Body, TextAlignmentOptions.MidlineRight);

            PlayPauseButton.Clicked += TogglePlay;
            SeekBar.ValueChanged += Seek;

            Relayout();
            source.Start();
        }

        protected override void Layout(Vector2 size)
        {
            // Video fills the space above the control bar.
            float videoHeight = Mathf.Max(0.01f, size.y - ControlBarHeight);
            screen.transform.localPosition = new Vector3(0f, size.y * 0.5f - videoHeight * 0.5f, 0f);
            screen.Fit(new Vector2(size.x, videoHeight));

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
            double length = Source != null ? Source.Length : 0;
            return length > 0 ? (float)(Source.Time / length) : 0f;
        }

        void UpdateFill(float progress)
        {
            float width = trackWidth * Mathf.Clamp01(progress);
            fill.gameObject.SetActive(width > 0.0005f);
            fill.localPosition = new Vector3(trackLeft + width * 0.5f, trackY, -0.003f);
            fill.localScale = new Vector3(Mathf.Max(width, 0.0001f), trackHeight, 1f);
            SeekBar.SetValue(progress);
        }

        void Update()
        {
            if (Source == null) return;
            UpdateFill(Progress());
            playGlyph.text = Source.WantsPlaying ? "II" : ">";
            timeLabel.text = $"{FormatTime(Source.Time)} / {FormatTime(Source.Length)}";
        }

        static string FormatTime(double seconds)
        {
            int total = Mathf.Max(0, (int)seconds);
            return $"{total / 60}:{total % 60:00}";
        }

        protected override void OnDestroy()
        {
            Source?.Stop();
            base.OnDestroy();
        }
    }
}
