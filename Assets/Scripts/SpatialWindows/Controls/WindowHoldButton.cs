using System;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>A button that reports being held: pressed when the pointer or pinch goes down, released when it lets go.</summary>
    public class WindowHoldButton : WindowControl
    {
        public event Action Pressed;
        public event Action Released;

        public bool IsHeld { get; private set; }

        public override void Press(UnityEngine.Vector3 worldPoint)
        {
            if (!BeginPress()) return;
            IsHeld = true;
            Pressed?.Invoke();
        }

        public override void Release(bool inside)
        {
            EndPress();
            if (!IsHeld) return;
            IsHeld = false;
            Released?.Invoke();
        }

        void OnDisable() => Release(false);
    }
}
