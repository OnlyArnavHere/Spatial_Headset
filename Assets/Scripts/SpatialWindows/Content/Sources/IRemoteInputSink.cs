using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Receives pointer input aimed at a remote screen (for example a streamed laptop desktop) and sends it on.
    /// Positions are normalized over the remote screen in screen convention: (0,0) top-left, (1,1) bottom-right;
    /// multiply by the remote resolution for pixels.
    /// </summary>
    public interface IRemoteInputSink
    {
        void PointerMove(Vector2 position);
        void PointerDown(Vector2 position);
        void PointerUp(Vector2 position);
        /// <summary>Scroll by <paramref name="delta"/> notches; positive is up.</summary>
        void Scroll(Vector2 position, float delta);
    }
}
