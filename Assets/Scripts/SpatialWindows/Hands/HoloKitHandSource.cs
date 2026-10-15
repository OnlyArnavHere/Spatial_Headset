using System.Collections.Generic;
using UnityEngine;
#if UNITY_IOS
using HoloKit.iOS;
#endif

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>The hand joints <see cref="HoloKitHandSource"/> uses, in world space (from Apple Vision's 21).</summary>
    public struct HandJoints
    {
        public Vector3 Wrist, ThumbTip;
        public Vector3 IndexMCP, IndexTip, MiddleMCP, MiddleTip, RingMCP, RingTip, LittleMCP, LittleTip;
    }

    /// <summary>
    /// Hands from HoloKit's hand tracking on the device: Apple Vision's joints, placed in 3D with LiDAR depth by
    /// HoloKit's HandTrackingManager, mapped to <see cref="HandState"/> by <see cref="FromJoints"/>:
    /// point is a ray from an estimated shoulder through the index knuckle (which stays still while the fingers
    /// pinch); pinch is the thumb tip on the index tip, measured against the hand's size, with hysteresis; grab is a
    /// fist, and a fist is never also a pinch.
    ///
    /// Reading HoloKit needs its iOS API (UNITY_IOS). Without it, and in the editor, both hands are untracked, so
    /// editor scenes keep using <see cref="SimulatedHandSource"/>. HoloKit tracks one hand unless its Max Hand Count
    /// says two, at about 20 Hz. Untested on a device so far: thresholds may need tuning.
    /// </summary>
    public class HoloKitHandSource : MonoBehaviour, IHandSource
    {
        [Tooltip("HoloKit's HandTrackingManager (iOS only).")]
        [SerializeField] MonoBehaviour handTracking;
        [Tooltip("The user's head: the tracked AR camera. The pointing ray starts from a shoulder estimated from it.")]
        [SerializeField] Transform head;

        /// <summary>Pinch starts below this thumb-to-index distance, as a fraction of the hand's size (wrist to middle knuckle, about 9 cm).</summary>
        public const float PinchOn = 0.25f;
        /// <summary>A pinch ends above this fraction, so it doesn't flicker at the threshold.</summary>
        public const float PinchOff = 0.40f;
        const float StrengthZeroAt = 0.6f, StrengthOneAt = 0.2f;
        /// <summary>A finger is curled when its tip is no further from the wrist than this times its knuckle.</summary>
        const float CurledRatio = 1.15f;
        /// <summary>The right shoulder relative to the head, in the head's axes (x mirrored for the left hand).</summary>
        static readonly Vector3 ShoulderOffset = new Vector3(0.15f, -0.18f, 0f);

        HandState left, right;

        public HandState GetHand(Handedness hand) => hand == Handedness.Left ? left : right;

        void Update()
        {
            var nextLeft = HandState.Untracked;
            var nextRight = HandState.Untracked;
#if UNITY_IOS
            if (!Application.isEditor && handTracking is HandTrackingManager tracking && tracking.HandPoseDetector != null && head != null)
            {
                var poses = tracking.HandPoses3D;
                var chiralities = tracking.HandPoseDetector.HandChiralities;
                var headPose = new Pose(head.position, head.rotation);
                for (int i = 0; i < Mathf.Min(tracking.HandCount, poses.Count); i++)
                {
                    if (!TryGetJoints(poses[i], out var joints)) continue;
                    bool isLeft = i < chiralities.Count && chiralities[i] == HandChirality.Left;
                    if (isLeft && !nextLeft.IsTracked) nextLeft = FromJoints(joints, Handedness.Left, headPose, left.IsPinched);
                    else if (!isLeft && !nextRight.IsTracked) nextRight = FromJoints(joints, Handedness.Right, headPose, right.IsPinched);
                }
            }
#endif
            left = nextLeft;
            right = nextRight;
        }

#if UNITY_IOS
        static bool TryGetJoints(Dictionary<JointName, Vector3> pose, out HandJoints joints)
        {
            joints = default;
            return pose != null
                && pose.TryGetValue(JointName.Wrist, out joints.Wrist) && pose.TryGetValue(JointName.ThumbTip, out joints.ThumbTip)
                && pose.TryGetValue(JointName.IndexMCP, out joints.IndexMCP) && pose.TryGetValue(JointName.IndexTip, out joints.IndexTip)
                && pose.TryGetValue(JointName.MiddleMCP, out joints.MiddleMCP) && pose.TryGetValue(JointName.MiddleTip, out joints.MiddleTip)
                && pose.TryGetValue(JointName.RingMCP, out joints.RingMCP) && pose.TryGetValue(JointName.RingTip, out joints.RingTip)
                && pose.TryGetValue(JointName.LittleMCP, out joints.LittleMCP) && pose.TryGetValue(JointName.LittleTip, out joints.LittleTip);
        }
#endif

        /// <summary>
        /// One hand's state from its joints. <paramref name="wasPinched"/> is the hand's pinch in the previous update
        /// (for the hysteresis); <paramref name="head"/> is the head pose the pointing ray's shoulder is estimated from.
        /// </summary>
        public static HandState FromJoints(in HandJoints j, Handedness hand, Pose head, bool wasPinched)
        {
            var wrist = j.Wrist;
            float size = Vector3.Distance(wrist, j.MiddleMCP);
            if (size < 0.03f) return HandState.Untracked; // collapsed joints: not a real hand

            bool Curled(Vector3 tip, Vector3 knuckle) => Vector3.Distance(wrist, tip) < Vector3.Distance(wrist, knuckle) * CurledRatio;
            int otherCurled = (Curled(j.MiddleTip, j.MiddleMCP) ? 1 : 0) + (Curled(j.RingTip, j.RingMCP) ? 1 : 0) +
                              (Curled(j.LittleTip, j.LittleMCP) ? 1 : 0);
            // The index must be curled too: people often pinch with the other fingers folded.
            bool grab = Curled(j.IndexTip, j.IndexMCP) && otherCurled >= 2;

            float pinchDistance = Vector3.Distance(j.ThumbTip, j.IndexTip) / size;
            bool pinched = !grab && pinchDistance < (wasPinched ? PinchOff : PinchOn);

            float side = hand == Handedness.Right ? 1f : -1f;
            var shoulder = head.position + head.rotation * new Vector3(ShoulderOffset.x * side, ShoulderOffset.y, ShoulderOffset.z);
            var direction = j.IndexMCP - shoulder;
            if (direction.sqrMagnitude < 1e-6f) direction = head.forward;

            return new HandState
            {
                IsTracked = true,
                WristPosition = wrist,
                IndexTipPosition = j.IndexTip,
                Pinch = pinched ? PinchState.Pinched : PinchState.Open,
                PinchStrength = Mathf.InverseLerp(StrengthZeroAt, StrengthOneAt, pinchDistance),
                PointingRay = new Ray(j.IndexMCP, direction.normalized),
                Grab = grab,
            };
        }
    }
}
