using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// How far a hand is from the head, from how big it looks: depth = <see cref="GestureConfig.handLength"/> / the
    /// hand's apparent size (<see cref="HandState.ApparentSize"/>). Without LiDAR (iPhone 16) this is the only depth there
    /// is, and it is noisy, so it is clamped to a plausible range, rate-limited (one bad frame moves it at most
    /// <see cref="GestureConfig.maxDepthSpeed"/> × dt) and exponentially smoothed. <see cref="Velocity"/> (positive =
    /// moving away) is smoothed separately for starting and stopping pushes.
    /// </summary>
    public class HandDepthEstimator
    {
        public float Depth { get; private set; }
        public float Velocity { get; private set; }
        public float RawDepth { get; private set; }
        public bool HasValue { get; private set; }

        public void Reset()
        {
            HasValue = false;
            Velocity = 0f;
        }

        /// <summary>The raw estimate for an apparent size, clamped to the configured range.</summary>
        public static float DepthFor(float apparentSize, GestureConfig config) =>
            Mathf.Clamp(config.handLength / Mathf.Max(apparentSize, 1e-3f), config.minHandDepth, config.maxHandDepth);

        public float Update(float apparentSize, float deltaTime, GestureConfig config)
        {
            RawDepth = DepthFor(apparentSize, config);
            if (!HasValue || deltaTime <= 0f)
            {
                Depth = RawDepth;
                Velocity = 0f;
                HasValue = true;
                return Depth;
            }

            // Smooth towards the raw estimate, then cap how far the output may move this frame. (Capping first and then
            // smoothing would cap the speed at maxDepthSpeed × the smoothing factor: far too slow to see a push.)
            float previous = Depth;
            float step = config.maxDepthSpeed * deltaTime;
            float smoothed = Mathf.Lerp(Depth, RawDepth, 1f - Mathf.Exp(-config.depthSmoothing * deltaTime));
            Depth += Mathf.Clamp(smoothed - Depth, -step, step);
            float speed = (Depth - previous) / deltaTime;
            Velocity = Mathf.Lerp(Velocity, speed, 1f - Mathf.Exp(-config.velocitySmoothing * deltaTime));
            return Depth;
        }
    }
}
