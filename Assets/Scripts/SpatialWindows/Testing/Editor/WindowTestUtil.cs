using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>Geometry helpers for window tests, based on what is drawn rather than on physics.</summary>
    static class WindowTestUtil
    {
        // Title bar height plus gap above the body, matching SpatialWindow's layout.
        const float TitleBarExtent = 0.065f;

        public static Transform Visual(SpatialWindow window) => window.ContentRoot.parent;

        /// <summary>
        /// Makes the input of type T drive the manager (only the active input's events reach it).
        /// Returns the previously active index so a test can restore it, or -1 if T isn't an input source.
        /// </summary>
        public static int SelectInput<T>(WindowManager manager) where T : MonoBehaviour, IWindowInput
        {
            int previous = manager.ActiveInputIndex;
            var sources = manager.InputSources;
            for (int i = 0; i < sources.Count; i++)
            {
                if (!(sources[i] is T)) continue;
                manager.SetActiveInput(i);
                return previous;
            }
            return -1;
        }

        /// <summary>A named part of the window's chrome: "Body", "TitleBar", "CloseButton" or "ResizeHandle".</summary>
        public static Transform Part(SpatialWindow window, string name) => Visual(window).Find(name);

        /// <summary>
        /// Finds a point on the target that is visibly front-most when seen from the viewpoint,
        /// using the windows' drawn rectangles. Returns false if the target is fully covered.
        /// </summary>
        public static bool TryFindVisiblePoint(WindowManager manager, SpatialWindow target, Vector3 viewpoint, out Vector3 worldPoint)
        {
            const int steps = 9;
            var visual = Visual(target);
            for (int ix = 1; ix < steps; ix++)
            for (int iy = 1; iy < steps; iy++)
            {
                float x = Mathf.Lerp(-target.Size.x * 0.5f, target.Size.x * 0.5f, ix / (float)steps);
                float y = Mathf.Lerp(-target.Size.y * 0.5f, target.Size.y * 0.5f + TitleBarExtent, iy / (float)steps);
                worldPoint = visual.TransformPoint(new Vector3(x, y, 0f));
                var ray = new Ray(viewpoint, worldPoint - viewpoint);
                if (VisualPick(manager, ray) == target)
                    return true;
            }
            worldPoint = default;
            return false;
        }

        /// <summary>The window whose drawn rectangle the ray meets first.</summary>
        public static SpatialWindow VisualPick(WindowManager manager, Ray ray)
        {
            SpatialWindow best = null;
            float bestDistance = float.MaxValue;
            foreach (var window in manager.Windows)
            {
                var visual = Visual(window);
                var plane = new Plane(visual.forward, visual.position);
                if (!plane.Raycast(ray, out float distance) || distance >= bestDistance) continue;
                var local = visual.InverseTransformPoint(ray.GetPoint(distance));
                bool inside = Mathf.Abs(local.x) <= window.Size.x * 0.5f
                              && local.y >= -window.Size.y * 0.5f && local.y <= window.Size.y * 0.5f + TitleBarExtent;
                if (!inside) continue;
                best = window;
                bestDistance = distance;
            }
            return best;
        }
    }
}
