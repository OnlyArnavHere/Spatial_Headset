using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The geometry of a curved window, in the window's visual space (its layout space: x right, y up, the flat window
    /// at z = 0, the viewer towards -z). Curving wraps the layout around a vertical cylinder of radius
    /// <see cref="Radius"/> whose axis is on the viewer's side, so the window's edges come towards the viewer and its
    /// centre stays put. A layout point (x, y, z) lands at angle x / R, at radius R + z (z is a small layer offset).
    /// Hit-testing intersects the ray with the cylinder analytically and maps the hit back to the layout, where the
    /// window's flat colliders and controls work as before.
    /// </summary>
    public static class WindowCurve
    {
        /// <summary>The cylinder radius at curve 1: a gentle wrap (a 0.6 m window's edges come 4.5 cm closer).</summary>
        public const float MinRadius = 1f;

        /// <summary>The radius for a curve amount (0 = flat: infinite).</summary>
        public static float Radius(float curve) => curve <= 0f ? float.PositiveInfinity : MinRadius / Mathf.Clamp01(curve);

        /// <summary>Where a layout point is drawn on a window curved with radius <paramref name="radius"/>.</summary>
        public static Vector3 Bend(Vector3 layout, float radius)
        {
            if (float.IsInfinity(radius)) return layout;
            float angle = layout.x / radius;
            float r = radius + layout.z;
            return new Vector3(r * Mathf.Sin(angle), layout.y, r * Mathf.Cos(angle) - radius);
        }

        /// <summary>The layout point (on the window plane, z = 0) of a point on or near the cylinder.</summary>
        public static Vector3 Unbend(Vector3 bent, float radius)
        {
            if (float.IsInfinity(radius)) return new Vector3(bent.x, bent.y, 0f);
            return new Vector3(radius * Mathf.Atan2(bent.x, bent.z + radius), bent.y, 0f);
        }

        /// <summary>The surface normal at a layout point, pointing towards the viewer (-z when flat).</summary>
        public static Vector3 Normal(Vector3 layout, float radius)
        {
            if (float.IsInfinity(radius)) return Vector3.back;
            float angle = layout.x / radius;
            return new Vector3(-Mathf.Sin(angle), 0f, -Mathf.Cos(angle));
        }

        /// <summary>
        /// Intersects a ray (in visual space; t is measured in the ray's own units) with the window surface at layer
        /// depth <paramref name="layerZ"/> (0 for the body; content sits a few millimetres in front, at negative z). For a
        /// flat window that is the plane z = layerZ; for a curved one, the front sheet of the cylinder that layer is
        /// drawn on (radius R + layerZ around the same axis; the sheet facing the viewer, within half a turn of the
        /// centre). Returns the nearest t ≥ 0 whose layout point satisfies <paramref name="inside"/> (null accepts any)
        /// and that layout point (with z = layerZ).
        /// </summary>
        public static bool Intersect(Vector3 origin, Vector3 direction, float radius, float layerZ, System.Func<Vector3, bool> inside,
            out float t, out Vector3 layout)
        {
            t = 0f;
            layout = default;
            if (float.IsInfinity(radius))
            {
                if (Mathf.Abs(direction.z) < 1e-8f) return false;
                t = (layerZ - origin.z) / direction.z;
                layout = origin + direction * t;
                layout.z = layerZ;
                return t >= 0f && (inside == null || inside(layout));
            }

            // x² + (z + R)² = (R + layerZ)², with the ray o + t d.
            float layerRadius = radius + layerZ;
            float oz = origin.z + radius;
            float a = direction.x * direction.x + direction.z * direction.z;
            if (a < 1e-12f) return false; // parallel to the axis
            float b = 2f * (origin.x * direction.x + oz * direction.z);
            float c = origin.x * origin.x + oz * oz - layerRadius * layerRadius;
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f) return false;
            float root = Mathf.Sqrt(discriminant);
            float near = (-b - root) / (2f * a), far = (-b + root) / (2f * a); // a > 0, so near <= far
            for (int i = 0; i < 2; i++)
            {
                float candidate = i == 0 ? near : far;
                if (candidate < 0f) continue;
                var point = origin + direction * candidate;
                if (point.z + radius <= 0f) continue; // the cylinder's far sheet, behind its axis
                var flat = Unbend(point, radius);
                flat.z = layerZ;
                if (inside != null && !inside(flat)) continue;
                t = candidate;
                layout = flat;
                return true;
            }
            return false;
        }
    }
}
