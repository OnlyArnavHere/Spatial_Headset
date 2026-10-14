using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    public enum Handedness
    {
        Left,
        Right
    }

    public enum PinchState
    {
        Open,
        Pinched
    }

    /// <summary>One hand's state for the current frame. All positions are in world space.</summary>
    public struct HandState
    {
        public bool IsTracked;
        public Vector3 WristPosition;
        public Vector3 IndexTipPosition;
        public PinchState Pinch;
        /// <summary>0 = fully open, 1 = fingers touching. Sources without a continuous value report 0 or 1.</summary>
        public float PinchStrength;
        /// <summary>Where the hand is pointing. The origin is usually near the index fingertip or knuckle.</summary>
        public Ray PointingRay;
        /// <summary>A fist (all fingers curled). Reported by device sources; <see cref="HandWindowInput"/> doesn't use it yet.</summary>
        public bool Grab;

        public bool IsPinched => IsTracked && Pinch == PinchState.Pinched;
        public bool IsGrabbing => IsTracked && Grab;

        public static HandState Untracked => default;
    }

    /// <summary>
    /// Supplies hand poses and pinch state, for example from HoloKit's hand tracking on device or
    /// <see cref="SimulatedHandSource"/> in the editor. Read once per frame by <see cref="HandWindowInput"/>.
    /// </summary>
    public interface IHandSource
    {
        HandState GetHand(Handedness hand);
    }
}
