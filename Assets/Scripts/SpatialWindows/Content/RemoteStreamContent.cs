using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Shows a remote screen (a <see cref="RemoteStreamSource"/>) at its aspect ratio. While there is no stream it
    /// shows a "Not connected" placeholder. The surface is interactive: pointer move, press, release and scroll
    /// on it are forwarded to the source's <see cref="IRemoteInputSink"/> in remote-screen coordinates.
    /// </summary>
    public class RemoteStreamContent : WindowContent
    {
        static readonly Color PlaceholderColor = new Color(0.07f, 0.08f, 0.10f);

        public RemoteStreamSource Source { get; private set; }
        public ContentSurface Surface { get; private set; }
        public Vector2 DisplaySize => Surface != null ? Surface.DisplaySize : Vector2.zero;
        /// <summary>The placeholder text currently shown, or empty when the stream is showing.</summary>
        public string StatusText => statusLabel != null && statusLabel.gameObject.activeSelf ? statusLabel.text : "";

        TextMeshPro statusLabel;

        /// <param name="expectedSize">The remote screen's resolution (aspect until a stream reports its own).</param>
        public void Init(SpatialWindow window, Material baseMaterial, IRemoteInputSink sink, Vector2Int expectedSize)
        {
            Attach(window, baseMaterial);
            Source = new RemoteStreamSource(sink, expectedSize);
            Surface = CreateSurface("RemoteScreen", transform, Source);
            Surface.EmptyColor = PlaceholderColor;

            statusLabel = CreateLabel("Status", transform, "", 0.5f, TextAlignmentOptions.Center);
            statusLabel.transform.localPosition = new Vector3(0f, 0f, -0.002f);
            statusLabel.color = new Color(0.75f, 0.77f, 0.82f);

            Source.Changed += UpdateStatus;
            Source.Start();
            UpdateStatus();
            Relayout();
        }

        void UpdateStatus()
        {
            bool showPlaceholder = !Source.IsConnected || Source.Texture == null;
            statusLabel.gameObject.SetActive(showPlaceholder);
            statusLabel.text = Source.Status;
        }

        protected override void Layout(Vector2 size)
        {
            Surface.Fit(size);
            statusLabel.rectTransform.sizeDelta = Surface.DisplaySize;
        }

        protected override void OnDestroy()
        {
            if (Source != null)
            {
                Source.Changed -= UpdateStatus;
                Source.Stop();
            }
            base.OnDestroy();
        }
    }
}
