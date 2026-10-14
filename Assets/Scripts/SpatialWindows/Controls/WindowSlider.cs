using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A horizontal slider on a quad: the value is the pointer's position along the quad's local X, 0 at the
    /// left edge and 1 at the right. Press to jump, drag to scrub.
    /// </summary>
    public class WindowSlider : WindowControl
    {
        public event Action<float> ValueChanged;

        public bool IsDragging { get; private set; }
        /// <summary>The value under the pointer during the current or last drag.</summary>
        public float DragValue { get; private set; }

        public override void Press(Vector3 worldPoint)
        {
            IsDragging = true;
            SetFromPoint(worldPoint);
        }

        public override void Drag(Vector3 worldPoint)
        {
            if (IsDragging) SetFromPoint(worldPoint);
        }

        public override void Release(bool inside) => IsDragging = false;

        void SetFromPoint(Vector3 worldPoint)
        {
            // A quad spans -0.5..0.5 in local space, whatever its scale.
            DragValue = Mathf.Clamp01(transform.InverseTransformPoint(worldPoint).x + 0.5f);
            ValueChanged?.Invoke(DragValue);
        }
    }
}
