using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>Ray picking shared by all window input implementations.</summary>
    public static class WindowRaycast
    {
        static readonly RaycastHit[] HitBuffer = new RaycastHit[32];

        /// <summary>
        /// Finds the nearest window part along the ray. Other colliders are skipped rather than
        /// blocking: in AR (and XR Simulation) the real world is drawn as background, so a window
        /// can appear in front of a wall or table whose collider is physically closer.
        /// </summary>
        public static bool TryHit(Ray ray, float maxDistance, LayerMask mask, out RaycastHit hit, out WindowPart part)
        {
            hit = default;
            part = null;
            int count = Physics.RaycastNonAlloc(ray, HitBuffer, maxDistance, mask, QueryTriggerInteraction.Collide);
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (HitBuffer[i].distance >= nearest) continue;
                var candidate = HitBuffer[i].collider.GetComponent<WindowPart>();
                if (candidate == null || candidate.Window == null) continue;
                nearest = HitBuffer[i].distance;
                hit = HitBuffer[i];
                part = candidate;
            }
            return part != null;
        }
    }
}
