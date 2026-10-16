using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A one-euro filter for a 2D point (Casiez et al. 2012): a low-pass filter whose cutoff rises with speed, so a
    /// still point stops jittering and a moving one lags little. Samples are timestamped; a sample with the same (or an
    /// earlier) time as the last one returns the last output, so it can be called every frame with results that arrive
    /// less often.
    /// </summary>
    public sealed class OneEuroFilter2D
    {
        bool hasValue;
        Vector2 value, speed;
        double time;

        public bool HasValue => hasValue;
        public Vector2 Value => value;

        public void Reset() => hasValue = false;

        /// <param name="minCutoff">Cutoff (Hz) at rest.</param>
        /// <param name="beta">Cutoff increase per unit of speed (units per second).</param>
        /// <param name="derivativeCutoff">Cutoff (Hz) for the speed estimate.</param>
        public Vector2 Filter(Vector2 sample, double sampleTime, float minCutoff, float beta, float derivativeCutoff)
        {
            if (!hasValue)
            {
                hasValue = true;
                value = sample;
                speed = Vector2.zero;
                time = sampleTime;
                return value;
            }
            float dt = (float)(sampleTime - time);
            if (dt <= 0f) return value;
            time = sampleTime;
            speed = Vector2.Lerp(speed, (sample - value) / dt, Alpha(derivativeCutoff, dt));
            float cutoff = minCutoff + beta * speed.magnitude;
            value = Vector2.Lerp(value, sample, Alpha(cutoff, dt));
            return value;
        }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-4f));
            return 1f / (1f + tau / dt);
        }
    }
}
