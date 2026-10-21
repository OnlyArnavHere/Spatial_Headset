using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>A button that clicks when released over itself.</summary>
    public class WindowButton : WindowControl
    {
        public event Action Clicked;

        public override void Press(Vector3 worldPoint) => BeginPress();

        public override void Release(bool inside)
        {
            EndPress();
            if (inside && Interactable) Clicked?.Invoke();
        }
    }
}
