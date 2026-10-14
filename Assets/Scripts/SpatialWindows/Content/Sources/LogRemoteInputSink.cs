using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Stand-in <see cref="IRemoteInputSink"/> until there is a streaming transport: logs presses, releases and
    /// scrolls (moves only update <see cref="LastPosition"/>, to keep the console readable).
    /// </summary>
    public class LogRemoteInputSink : MonoBehaviour, IRemoteInputSink
    {
        [SerializeField] bool logMoves;

        public Vector2 LastPosition { get; private set; }
        public int MoveCount { get; private set; }

        public void PointerMove(Vector2 position)
        {
            LastPosition = position;
            MoveCount++;
            if (logMoves) Debug.Log($"[RemoteInput] move {Format(position)}");
        }

        public void PointerDown(Vector2 position)
        {
            LastPosition = position;
            Debug.Log($"[RemoteInput] down {Format(position)}");
        }

        public void PointerUp(Vector2 position)
        {
            LastPosition = position;
            Debug.Log($"[RemoteInput] up {Format(position)}");
        }

        public void Scroll(Vector2 position, float delta)
        {
            LastPosition = position;
            Debug.Log($"[RemoteInput] scroll {delta:+0.##;-0.##} at {Format(position)}");
        }

        static string Format(Vector2 p) => $"({p.x:F3}, {p.y:F3})";
    }
}
