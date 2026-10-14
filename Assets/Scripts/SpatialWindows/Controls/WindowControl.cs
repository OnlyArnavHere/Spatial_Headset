using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// An interactive element inside window content, such as a button or slider. It needs a collider;
    /// <see cref="Register"/> tags it as a <see cref="WindowPartType.Control"/> part of its window.
    /// Input implementations (mouse now, hands later) call Press, Drag and Release directly;
    /// pressing a control also selects its window like any other part.
    /// </summary>
    public abstract class WindowControl : MonoBehaviour
    {
        public SpatialWindow Window { get; private set; }

        public void Register(SpatialWindow window)
        {
            Window = window;
            if (!TryGetComponent<WindowPart>(out var part))
                part = gameObject.AddComponent<WindowPart>();
            part.Init(window, WindowPartType.Control);
        }

        /// <summary>The pointer went down on this control at a world-space point.</summary>
        public virtual void Press(Vector3 worldPoint) { }

        /// <summary>The pointer moved while held; the point is projected onto the control's plane.</summary>
        public virtual void Drag(Vector3 worldPoint) { }

        /// <summary>The pointer was released; <paramref name="inside"/> is true if it was still over this control.</summary>
        public virtual void Release(bool inside) { }

        /// <summary>The pointer is over this control without pressing; called every frame it stays.</summary>
        public virtual void Hover(Vector3 worldPoint) { }

        /// <summary>The pointer left this control (or input stopped) after hovering.</summary>
        public virtual void HoverExit() { }

        /// <summary>Scroll while hovering, in notches; positive is up / away from the user.</summary>
        public virtual void Scroll(Vector3 worldPoint, float delta) { }
    }
}
