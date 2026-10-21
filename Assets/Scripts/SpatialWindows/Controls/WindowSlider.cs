using System;
using SpatialLauncher.SpatialWindows.Theme;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A horizontal slider on a quad: the value is the pointer's position along the quad's local X, 0 at the
    /// left edge and 1 at the right. Press to jump, drag to scrub.
    /// </summary>
    public class WindowSlider : WindowControl
    {
        /// <summary>Diameter of the round knob (pt).</summary>
        public const float KnobPt = 28f;

        public event Action<float> ValueChanged;

        /// <summary>The knob quad, if one was attached; it is a child of the track and always drawn <see cref="KnobPt"/> across.</summary>
        public Transform Knob { get; private set; }
        /// <summary>Where the knob sits, 0..1 (set by <see cref="SetValue"/>; follows the pointer while dragging).</summary>
        public float Value { get; private set; }

        public bool IsDragging { get; private set; }
        /// <summary>The value under the pointer during the current or last drag.</summary>
        public float DragValue { get; private set; }

        /// <summary>Moves the knob to a value (0..1).</summary>
        public void SetValue(float value)
        {
            Value = Mathf.Clamp01(value);
            Refit();
        }

        public void AttachKnob(Transform knob)
        {
            Knob = knob;
            if (knob.TryGetComponent<MeshRenderer>(out var knobRenderer)) { BindRenderer(knobRenderer); OwnShapeReacts = false; }
            if (knob.TryGetComponent<RoundedShape>(out var shape))
            {
                shape.Capsule = true;
                shape.FixedSize = Vector2.one * UITheme.PtToMetres(KnobPt);
            }
            Refit();
        }

        /// <summary>
        /// Keeps the hit box at least 44 mm tall however thin the drawn track is, and the knob on the value at its own size
        /// (the track's scale is divided out).
        /// </summary>
        public void Refit()
        {
            var scale = transform.localScale;
            if (scale.x <= 0f || scale.y <= 0f) return;
            if (TryGetComponent<BoxCollider>(out var box))
            {
                var size = box.size;
                size.y = Mathf.Max(1f, UITheme.PtToMetres(UITheme.MinHitTargetMm) / scale.y);
                box.size = size;
            }
            if (Knob == null) return;
            float diameter = UITheme.PtToMetres(KnobPt);
            Knob.localScale = new Vector3(diameter / scale.x, diameter / scale.y, 1f);
            Knob.localPosition = new Vector3(Value - 0.5f, 0f, -0.001f);
        }

        Vector3 fittedScale;

        void LateUpdate()
        {
            if (transform.localScale != fittedScale)
            {
                fittedScale = transform.localScale;
                Refit();
            }
        }

        public override void Press(Vector3 worldPoint)
        {
            if (!BeginPress()) return;
            IsDragging = true;
            SetFromPoint(worldPoint);
        }

        public override void Drag(Vector3 worldPoint)
        {
            if (IsDragging) SetFromPoint(worldPoint);
        }

        public override void Release(bool inside)
        {
            EndPress();
            IsDragging = false;
        }

        void SetFromPoint(Vector3 worldPoint)
        {
            // A quad spans -0.5..0.5 in local space, whatever its scale.
            DragValue = Mathf.Clamp01(transform.InverseTransformPoint(worldPoint).x + 0.5f);
            SetValue(DragValue);
            ValueChanged?.Invoke(DragValue);
        }
    }
}
