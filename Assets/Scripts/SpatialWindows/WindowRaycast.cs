using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>Ray picking shared by all window input implementations.</summary>
    public static class WindowRaycast
    {
        static readonly RaycastHit[] HitBuffer = new RaycastHit[32];
        static readonly RaycastHit[] ProbeBuffer = new RaycastHit[16];
        // How far in front of and behind the layout plane the probe looks for a curved window's parts (layers are mm apart).
        const float ProbeDepth = 0.05f;

        /// <summary>
        /// Finds the nearest window part along the ray. Other colliders are skipped rather than
        /// blocking: in AR (and XR Simulation) the real world is drawn as background, so a window
        /// can appear in front of a wall or table whose collider is physically closer.
        /// </summary>
        public static bool TryHit(Ray ray, float maxDistance, LayerMask mask, out RaycastHit hit, out WindowPart part) =>
            TryHit(ray, maxDistance, mask, out hit, out part, out _);

        /// <summary>
        /// As <see cref="TryHit(Ray,float,LayerMask,out RaycastHit,out WindowPart)"/>. Flat windows are hit through their
        /// colliders. Curved windows (<see cref="SpatialWindow.CurvedWindows"/>) are hit analytically: the ray meets the
        /// window's cylinder, the hit is mapped back to the flat layout, and the part there is found with a short probe
        /// through the window's own (flat) colliders. Either way <paramref name="hit"/>.point is the layout-space point
        /// (where controls and content expect it), <paramref name="hit"/>.distance the true distance along the ray, and
        /// <paramref name="surfacePoint"/> where the ray meets what is drawn (for cursors).
        /// </summary>
        public static bool TryHit(Ray ray, float maxDistance, LayerMask mask, out RaycastHit hit, out WindowPart part,
            out Vector3 surfacePoint)
        {
            hit = default;
            part = null;
            surfacePoint = default;
            float nearest = float.MaxValue;

            int count = Physics.RaycastNonAlloc(ray, HitBuffer, maxDistance, mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                if (HitBuffer[i].distance >= nearest) continue;
                var candidate = HitBuffer[i].collider.GetComponent<WindowPart>();
                if (candidate == null || candidate.Window == null) continue;
                if (candidate.Window.Curve > 0f) continue; // hit-tested analytically below
                nearest = HitBuffer[i].distance;
                hit = HitBuffer[i];
                part = candidate;
                surfacePoint = hit.point;
            }

            var curved = SpatialWindow.CurvedWindows;
            for (int w = 0; w < curved.Count; w++)
            {
                var window = curved[w];
                if (window == null || !window.isActiveAndEnabled) continue;
                // First against the body's sheet to find the part, then against the sheet that part is drawn on, so the
                // point matches what the user sees (content is a few millimetres in front of the body).
                if (!TryIntersect(window, ray, true, 0f, out float distance, out var layout) || distance >= nearest || distance > maxDistance) continue;
                if (!TryProbe(window, layout, mask, out var probe, out var probePart)) continue;
                float layer = window.Visual.InverseTransformPoint(probePart.transform.position).z;
                if (TryIntersect(window, ray, true, layer, out float layerDistance, out var layerLayout)
                    && TryProbe(window, layerLayout, mask, out var layerProbe, out var layerPart))
                {
                    distance = layerDistance;
                    probe = layerProbe;
                    probePart = layerPart;
                }
                if (distance >= nearest || distance > maxDistance) continue;
                nearest = distance;
                probe.distance = distance;
                hit = probe;
                part = probePart;
                surfacePoint = ray.GetPoint(distance);
            }
            return part != null;
        }

        /// <summary>
        /// Where a ray meets the window's surface (its plane, or its cylinder when curved), as a world-space point on the
        /// flat layout: what a control being dragged on that window expects. Unlike hit-testing it ignores the window's
        /// edges, so a drag can run past them.
        /// </summary>
        public static bool TryProjectToLayout(SpatialWindow window, Ray ray, out Vector3 worldPoint) =>
            TryProjectToLayout(window, ray, null, out worldPoint);

        /// <summary>As above, onto the layer <paramref name="part"/> is drawn on (a dragged control's own depth).</summary>
        public static bool TryProjectToLayout(SpatialWindow window, Ray ray, Transform part, out Vector3 worldPoint)
        {
            worldPoint = default;
            if (window == null) return false;
            float layer = part != null ? window.Visual.InverseTransformPoint(part.position).z : 0f;
            if (!TryIntersect(window, ray, false, layer, out _, out var layout)) return false;
            worldPoint = window.Visual.TransformPoint(layout);
            return true;
        }

        /// <summary>
        /// The ray against the window's surface at a layer depth (0 = the body), in its layout space; distance is the
        /// world distance along the ray.
        /// </summary>
        public static bool TryIntersect(SpatialWindow window, Ray ray, bool withinBounds, float layerZ, out float distance, out Vector3 layout)
        {
            var visual = window.Visual;
            var toLayout = visual.worldToLocalMatrix;
            // An affine map keeps the ray's parameter, so t found in layout space is the world distance.
            var origin = toLayout.MultiplyPoint3x4(ray.origin);
            var direction = toLayout.MultiplyVector(ray.direction.normalized);
            var bounds = window.LayoutBounds;
            return WindowCurve.Intersect(origin, direction, window.CurveRadius, layerZ,
                withinBounds ? p => bounds.Contains(new Vector2(p.x, p.y)) : null, out distance, out layout);
        }

        // The front-most part of this window at a layout point: a short ray straight through the layout there.
        static bool TryProbe(SpatialWindow window, Vector3 layout, LayerMask mask, out RaycastHit hit, out WindowPart part)
        {
            hit = default;
            part = null;
            var visual = window.Visual;
            var start = visual.TransformPoint(new Vector3(layout.x, layout.y, -ProbeDepth));
            var end = visual.TransformPoint(new Vector3(layout.x, layout.y, ProbeDepth));
            var direction = end - start;
            int count = Physics.RaycastNonAlloc(new Ray(start, direction.normalized), ProbeBuffer, direction.magnitude, mask,
                QueryTriggerInteraction.Collide);
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (ProbeBuffer[i].distance >= nearest) continue;
                var candidate = ProbeBuffer[i].collider.GetComponent<WindowPart>();
                if (candidate == null || candidate.Window != window) continue;
                nearest = ProbeBuffer[i].distance;
                hit = ProbeBuffer[i];
                part = candidate;
            }
            return part != null;
        }
    }
}
