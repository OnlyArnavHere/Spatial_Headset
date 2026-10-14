using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Steadies a pointing ray against hand tremor in two stages. First the raw ray is exponentially
    /// smoothed, which averages tremor down to a fraction of its size. Then a dead zone ignores the
    /// smoothed ray's movement smaller than <see cref="DeadZoneAngle"/> / <see cref="DeadZoneDistance"/>;
    /// beyond it the output follows by the excess only, so there is no jump at the edge.
    /// (Smoothing first matters: a dead zone on the raw ray lets noise in new directions keep nudging the
    /// output across the zone's edge, so a held window creeps.)
    /// </summary>
    public class RayFilter
    {
        /// <summary>Smoothing rate per second; higher follows faster. 18 settles 95% in about 0.17 s.</summary>
        public float Smoothing = 18f;
        public float DeadZoneAngle = 0.4f;
        public float DeadZoneDistance = 0.004f;

        bool initialized;
        Vector3 smoothOrigin, smoothDirection;
        Vector3 origin, direction;

        public Ray Current => new Ray(origin, direction);

        public void Reset() => initialized = false;

        public Ray Update(Ray raw, float deltaTime)
        {
            var rawDirection = raw.direction.normalized;
            if (!initialized)
            {
                smoothOrigin = origin = raw.origin;
                smoothDirection = direction = rawDirection;
                initialized = true;
                return Current;
            }

            float t = 1f - Mathf.Exp(-Smoothing * deltaTime);
            smoothOrigin = Vector3.Lerp(smoothOrigin, raw.origin, t);
            smoothDirection = Vector3.Slerp(smoothDirection, rawDirection, t).normalized;

            float angle = Vector3.Angle(direction, smoothDirection);
            if (angle > DeadZoneAngle)
                direction = Vector3.RotateTowards(direction, smoothDirection, (angle - DeadZoneAngle) * Mathf.Deg2Rad, 0f);

            var offset = smoothOrigin - origin;
            float distance = offset.magnitude;
            if (distance > DeadZoneDistance)
                origin += offset * ((distance - DeadZoneDistance) / distance);

            return Current;
        }
    }

    /// <summary>The same smoothing and dead zone for a single value (for example hand distance).</summary>
    public class ScalarFilter
    {
        public float Smoothing = 18f;
        public float DeadZone = 0.005f;

        bool initialized;
        float smooth, value;

        public float Value => value;

        public void Reset() => initialized = false;

        public float Update(float raw, float deltaTime)
        {
            if (!initialized)
            {
                smooth = value = raw;
                initialized = true;
                return value;
            }

            smooth = Mathf.Lerp(smooth, raw, 1f - Mathf.Exp(-Smoothing * deltaTime));
            float delta = smooth - value;
            if (Mathf.Abs(delta) > DeadZone)
                value += delta - Mathf.Sign(delta) * DeadZone;
            return value;
        }
    }
}
