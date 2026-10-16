using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>How a hand aims at windows (Settings > Hands > Pointer).</summary>
    public enum PointerMode
    {
        /// <summary>The ray from the estimated shoulder through the index knuckle, as it is.</summary>
        Absolute,
        /// <summary>The cursor moves by the hand's motion (with a gain) and holds still from the start of a pinch.</summary>
        Relative,
    }

    /// <summary>
    /// A relative pointer for one hand. Each frame the hand's aim (its absolute pointing ray, already filtered) turns
    /// the cursor's direction by the same rotation times <see cref="Gain"/>. From the moment a pinch starts closing
    /// (pinch strength rising faster than <see cref="OnsetRate"/>, or the pinch itself) the cursor freezes, so the
    /// motion of pinching can't knock it off its target. The freeze ends when the pinch opens again (a click: the cursor
    /// stays where it was) or when the pinched hand turns more than <see cref="DragAngle"/> (a drag: the cursor follows
    /// again from where it froze), or after <see cref="FreezeTimeout"/> if the pinch never closed. The cursor stays within
    /// <see cref="MaxOffAngle"/> of where the hand really points, and re-anchors to it whenever the hand is found again
    /// (<see cref="Reset"/>).
    /// </summary>
    public class RelativePointer
    {
        public enum Phase { Free, Frozen, Dragging }

        /// <summary>Cursor rotation per unit of hand rotation.</summary>
        public float Gain = 1.5f;
        /// <summary>Pinch strength rising at least this fast (per second) ...</summary>
        public float OnsetRate = 2.5f;
        /// <summary>... once above this, counts as a pinch starting.</summary>
        public float OnsetStrength = 0.25f;
        /// <summary>A freeze without a pinch ends when the strength falls back below this.</summary>
        public float ReleaseStrength = 0.2f;
        /// <summary>Degrees a pinched hand turns before a frozen cursor becomes a drag.</summary>
        public float DragAngle = 3f;
        /// <summary>Seconds a freeze lasts if the pinch never closes.</summary>
        public float FreezeTimeout = 0.6f;
        /// <summary>The cursor stays within this many degrees of the hand's own aim.</summary>
        public float MaxOffAngle = 30f;

        public Phase State { get; private set; }
        public bool Frozen => State == Phase.Frozen;
        public bool Anchored => anchored;
        /// <summary>The cursor ray from the last update.</summary>
        public Ray Current => State == Phase.Frozen ? frozenRay : new Ray(origin, cursor);

        bool anchored;
        Vector3 lastAim, cursor, origin, freezeAim;
        Ray frozenRay;
        float lastStrength, frozenFor;

        /// <summary>Forgets the cursor: the next update starts it where the hand points (hand lost, mode changed).</summary>
        public void Reset()
        {
            anchored = false;
            State = Phase.Free;
        }

        public Ray Update(Ray aim, float pinchStrength, bool pinched, float deltaTime)
        {
            var direction = aim.direction.normalized;
            if (!anchored)
            {
                anchored = true;
                State = Phase.Free;
                lastAim = cursor = direction;
                origin = aim.origin;
                lastStrength = pinchStrength;
                return Current;
            }

            float rate = deltaTime > 0f ? (pinchStrength - lastStrength) / deltaTime : 0f;
            lastStrength = pinchStrength;

            switch (State)
            {
                case Phase.Free:
                    // A pinch starting freezes the cursor before this frame's motion (the pinch's own) moves it.
                    if (pinched || (rate >= OnsetRate && pinchStrength >= OnsetStrength))
                    {
                        Freeze(direction);
                        break;
                    }
                    Follow(aim, direction);
                    break;

                case Phase.Frozen:
                    frozenFor += deltaTime;
                    if (pinched && Vector3.Angle(freezeAim, direction) > DragAngle)
                    {
                        State = Phase.Dragging; // a drag: follow again from the frozen point
                        cursor = frozenRay.direction;
                        lastAim = direction;
                        origin = aim.origin;
                    }
                    else if (!pinched && (pinchStrength < ReleaseStrength || frozenFor > FreezeTimeout))
                    {
                        State = Phase.Free; // a click (or a pinch that never closed): the cursor stays put
                        cursor = frozenRay.direction;
                        lastAim = direction;
                        origin = aim.origin;
                    }
                    break;

                case Phase.Dragging:
                    if (!pinched) State = Phase.Free;
                    Follow(aim, direction);
                    break;
            }
            return Current;
        }

        void Freeze(Vector3 direction)
        {
            State = Phase.Frozen;
            frozenFor = 0f;
            freezeAim = direction;
            frozenRay = new Ray(origin, cursor);
        }

        void Follow(Ray aim, Vector3 direction)
        {
            var turn = Quaternion.FromToRotation(lastAim, direction);
            turn.ToAngleAxis(out float angle, out var axis);
            if (angle > 180f) angle -= 360f;
            if (Mathf.Abs(angle) > 1e-4f && axis.sqrMagnitude > 0.5f)
                cursor = (Quaternion.AngleAxis(angle * Gain, axis) * cursor).normalized;
            float off = Vector3.Angle(direction, cursor);
            if (off > MaxOffAngle) cursor = Vector3.RotateTowards(direction, cursor, MaxOffAngle * Mathf.Deg2Rad, 0f);
            lastAim = direction;
            origin = aim.origin;
        }
    }
}
