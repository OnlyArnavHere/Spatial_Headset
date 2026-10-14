using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>A button that clicks when released over itself.</summary>
    public class WindowButton : WindowControl
    {
        public event Action Clicked;

        public override void Release(bool inside)
        {
            if (inside) Clicked?.Invoke();
        }
    }
}
