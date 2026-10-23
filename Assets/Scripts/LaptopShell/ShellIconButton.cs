using System;
using SpatialLauncher.SpatialWindows;
using UnityEngine;

namespace SpatialLauncher.LaptopShell
{
    /// <summary>
    /// An app icon in the Laptop Shell: a tap (released over it) opens the app; holding it for
    /// <see cref="HoldSeconds"/> shows the app's windows instead (like a right-click on a taskbar or dock icon). A hold
    /// fires while still held, and the release after it doesn't also tap.
    /// </summary>
    public class ShellIconButton : WindowControl
    {
        public const float HoldSeconds = 0.5f;

        public event Action Clicked;
        public event Action Held;

        float pressedAt = -1f;
        bool holdFired;

        /// <summary>Test seam: the clock holds are timed with (unscaled seconds).</summary>
        public Func<float> Clock = () => Time.unscaledTime;

        public bool IsPressing => pressedAt >= 0f;

        public override void Press(Vector3 worldPoint)
        {
            if (!BeginPress()) return;
            pressedAt = Clock();
            holdFired = false;
        }

        public override void Release(bool inside)
        {
            EndPress();
            bool wasPressing = pressedAt >= 0f;
            pressedAt = -1f;
            if (!wasPressing || holdFired) return;
            if (inside && Interactable) Clicked?.Invoke();
        }

        protected override void Update()
        {
            base.Update();
            CheckHold();
        }

        /// <summary>Fires <see cref="Held"/> once the press has lasted long enough (called every frame).</summary>
        public void CheckHold()
        {
            if (pressedAt < 0f || holdFired || !Interactable) return;
            if (Clock() - pressedAt < HoldSeconds) return;
            holdFired = true;
            Held?.Invoke();
        }

        void OnDisable()
        {
            pressedAt = -1f;
            holdFired = false;
        }
    }
}
