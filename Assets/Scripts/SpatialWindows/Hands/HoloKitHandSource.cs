using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
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
    /// says two (DeviceMain: two), on every <see cref="VisionFrameInterval"/>-th ARKit frame. Each Vision result's hands
    /// are matched to the previous result's by <see cref="HandIdentityTracker"/>, so a hand keeps its identity when
    /// Vision's left/right guess flips. Untested on a device so far: thresholds may need tuning.
    ///
    /// Phones without LiDAR (iPhone 16): with Depth Source on Auto (the default) hands are built from Apple Vision's 2D
    /// pose: each joint is placed on its viewing ray at a depth estimated from the hand's
    /// apparent size (<see cref="HandDepthEstimator"/>; thresholds in <see cref="GestureConfig"/>, which also says how
    /// Vision's image coordinates map to the camera's viewport, to be checked on the device). Those 2D joints are
    /// one-euro filtered per hand (<see cref="OneEuroFilter2D"/>, settings in the config). HoloKit fills its 3D joints
    /// even without LiDAR (on a fixed 0.65 m plane), so Auto takes them only when the AR camera has scene depth
    /// (<see cref="ResolveDepth"/>).
    ///
    /// <see cref="Status"/> says how far the device pipeline gets (HoloKit's hand tracking found, enabled and started; AR
    /// frames; Vision requests; hands seen and mapped), for <see cref="DeviceStatusHud"/>.
    /// </summary>
    public class HoloKitHandSource : MonoBehaviour, IHandSource
    {
        [Tooltip("HoloKit's HandTrackingManager (iOS only).")]
        [SerializeField] MonoBehaviour handTracking;
        [Tooltip("The user's head: the tracked AR camera. The pointing ray starts from a shoulder estimated from it.")]
        [SerializeField] Transform head;
        [Tooltip("Auto: LiDAR joints when HoloKit has them, else 2D joints at a depth estimated from the hand's size " +
                 "(phones without LiDAR). LiDAR or Hand Scale forces one.")]
        [SerializeField] DepthSource depthSource = DepthSource.Auto;
        [Tooltip("Hand-scale depth thresholds and Vision's image-to-viewport mapping. Defaults if empty.")]
        [SerializeField] GestureConfig gestureConfig;

        /// <summary>Pinch starts below this thumb-to-index distance, as a fraction of the hand's size (wrist to middle knuckle, about 9 cm).</summary>
        public const float PinchOn = 0.25f;
        /// <summary>A pinch ends above this fraction, so it doesn't flicker at the threshold.</summary>
        public const float PinchOff = 0.40f;
        const float StrengthZeroAt = 0.6f, StrengthOneAt = 0.2f;
        /// <summary>A finger is curled when its tip is no further from the wrist than this times its knuckle.</summary>
        const float CurledRatio = 1.15f;
        /// <summary>A finger is fully extended at this ratio (for <see cref="HandState.Openness"/>).</summary>
        const float ExtendedRatio = 1.7f;
        /// <summary>The right shoulder relative to the head, in the head's axes (x mirrored for the left hand).</summary>
        static readonly Vector3 ShoulderOffset = new Vector3(0.15f, -0.18f, 0f);

        HandState left, right;
        readonly HandDepthEstimator leftDepth = new HandDepthEstimator(), rightDepth = new HandDepthEstimator();
        ARCameraManager arCamera;
        AROcclusionManager occlusion;
        HandTrackingStatus status;
        int visionFrameInterval = DefaultVisionFrameInterval;
        readonly HandIdentityTracker identities = new HandIdentityTracker();
        readonly List<HandIdentityTracker.Observation> observations = new List<HandIdentityTracker.Observation>();
        readonly List<Handedness> assigned = new List<Handedness>();
        readonly OneEuroFilter2D[][] jointFilters = { NewJointFilters(), NewJointFilters() };
        double lastResultTime = double.NaN;

        /// <summary>Vision runs on every this-many-th ARKit frame unless Settings says otherwise (2: about 30 Hz at 60 fps).</summary>
        public const int DefaultVisionFrameInterval = 2;

        public HandState GetHand(Handedness hand) => hand == Handedness.Left ? left : right;

        public enum DepthSource { Auto, LiDAR, HandScale }

        /// <summary>How far the device's hand pipeline gets, updated every frame.</summary>
        public HandTrackingStatus Status => status;

        /// <summary>
        /// Vision runs on every this-many-th ARKit frame (1..3; HoloKit's HandTrackingManager.FrameInterval). Lower tracks
        /// hands more often and heats the phone more (Settings > Hands > Tracking rate).
        /// </summary>
        public int VisionFrameInterval
        {
            get => visionFrameInterval;
            set => visionFrameInterval = Mathf.Clamp(value, 1, 3);
        }

        /// <summary>Matches hands to identities from one result to the next (two-hand tracking).</summary>
        public HandIdentityTracker Identities => identities;

        static OneEuroFilter2D[] NewJointFilters()
        {
            var filters = new OneEuroFilter2D[21];
            for (int i = 0; i < filters.Length; i++) filters[i] = new OneEuroFilter2D();
            return filters;
        }

        void ResetJointFilters(Handedness hand)
        {
            foreach (var filter in jointFilters[(int)hand]) filter.Reset();
        }

        /// <summary>
        /// Where a hand's joint depth comes from: LiDAR when forced, or on Auto when the AR camera has scene depth (a
        /// LiDAR iPhone); otherwise the hand's apparent size. Without LiDAR HoloKit's 3D joints sit on a fixed plane, so
        /// Auto mustn't take them just because they exist.
        /// </summary>
        public static DepthSource ResolveDepth(DepthSource setting, bool sceneDepthSupported) =>
            setting == DepthSource.Auto ? (sceneDepthSupported ? DepthSource.LiDAR : DepthSource.HandScale) : setting;

        public DepthSource Depth
        {
            get => depthSource;
            set => depthSource = value;
        }

        GestureConfig Config
        {
            get
            {
                if (gestureConfig == null) gestureConfig = ScriptableObject.CreateInstance<GestureConfig>();
                return gestureConfig;
            }
        }

        /// <summary>A Vision image point (0..1) as a camera viewport point, per the config's flips and swap.</summary>
        public static Vector2 ImageToViewport(Vector2 image, GestureConfig config)
        {
            if (config.imageSwapXY) image = new Vector2(image.y, image.x);
            if (config.imageFlipX) image.x = 1f - image.x;
            if (config.imageFlipY) image.y = 1f - image.y;
            return image;
        }

        void OnEnable()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            arCamera = head != null ? head.GetComponent<ARCameraManager>() : null;
            if (arCamera == null) arCamera = FindFirstObjectByType<ARCameraManager>();
            occlusion = arCamera != null ? arCamera.GetComponent<AROcclusionManager>() : null;
            if (arCamera != null) arCamera.frameReceived += OnFrameReceived;
        }

        void OnDisable()
        {
            if (arCamera != null) arCamera.frameReceived -= OnFrameReceived;
        }

        void OnFrameReceived(ARCameraFrameEventArgs args) => status.ArFrames++;

        void Update()
        {
            var nextLeft = HandState.Untracked;
            var nextRight = HandState.Untracked;
            status.SceneDepth = occlusion != null && occlusion.descriptor?.environmentDepthImageSupported == Supported.Supported;
            status.DepthPath = "-";
            status.HandsSeen = status.HandsRejected = 0;
            status.VisionFrameInterval = visionFrameInterval;
            status.Smoothing2D = Config.smoothJoints2D;
#if UNITY_IOS
            var tracking = handTracking as HandTrackingManager;
            status.TrackingFound = tracking != null;
            status.TrackingEnabled = tracking != null && tracking.isActiveAndEnabled;
            status.DetectorCreated = tracking != null && tracking.HandPoseDetector != null;
            if (status.DetectorCreated) status.VisionRequests = tracking.HandPoseDetector.CompletedRequests;
            if (tracking != null && tracking.FrameInterval != visionFrameInterval) tracking.FrameInterval = visionFrameInterval;
            status.VisionFrameInterval = tracking != null ? tracking.FrameInterval : visionFrameInterval;
            status.MaxHands = MaxHandsOf(tracking);
            if (!Application.isEditor && status.DetectorCreated && head != null)
            {
                var detector = tracking.HandPoseDetector;
                var use = ResolveDepth(depthSource, status.SceneDepth);
                var headPose = new Pose(head.position, head.rotation);
                var camera = head.GetComponent<Camera>();
                var poses3D = tracking.HandPoses3D;
                var poses2D = detector.HandPoses2D;
                var config = Config;
                status.HandsSeen = tracking.HandCount;

                // A new Vision result: match its hands to the identities they had in the last one.
                double resultTime = detector.LastUpdateTime;
                bool newResult = resultTime != lastResultTime;
                if (newResult && tracking.HandCount > 0)
                {
                    lastResultTime = resultTime;
                    observations.Clear();
                    for (int i = 0; i < tracking.HandCount && poses2D != null && i < poses2D.Count; i++)
                    {
                        int chirality = i < detector.HandChiralities.Count ? (int)detector.HandChiralities[i] : 0;
                        observations.Add(new HandIdentityTracker.Observation(PalmImagePoint(poses2D[i]), chirality));
                    }
                    identities.MaxJump = config.identityMaxJump;
                    identities.Memory = config.identityMemory;
                    identities.SwitchAfter = config.identitySwitchAfter;
                    identities.Update(observations, resultTime, assigned);
                    foreach (var started in GestureRecognizers.Hands)
                        if (identities.Started(started))
                        {
                            ResetJointFilters(started);
                            (started == Handedness.Left ? leftDepth : rightDepth).Reset();
                        }
                }

                for (int i = 0; i < tracking.HandCount && i < assigned.Count; i++)
                {
                    var hand = assigned[i];
                    HandJoints joints;
                    if (use == DepthSource.LiDAR && poses3D != null && i < poses3D.Count && TryGetJoints(poses3D[i], out joints))
                    {
                        status.DepthPath = "LiDAR";
                    }
                    else if (depthSource != DepthSource.LiDAR && camera != null && poses2D != null && i < poses2D.Count
                             && TryGetDirections(poses2D[i], camera, config.smoothJoints2D ? jointFilters[(int)hand] : null,
                                 resultTime, out var directions))
                    {
                        var estimator = hand == Handedness.Left ? leftDepth : rightDepth;
                        float depth = estimator.Update(ApparentSizeOf(directions), Time.deltaTime, config);
                        joints = JointsAtDepth(directions, head.position, depth);
                        status.DepthPath = "hand size";
                    }
                    else
                    {
                        status.HandsRejected++;
                        continue;
                    }
                    var state = FromJoints(joints, hand, headPose, GetHand(hand).IsPinched);
                    if (!state.IsTracked) status.HandsRejected++; // collapsed joints
                    else if (hand == Handedness.Left) nextLeft = state;
                    else nextRight = state;
                }
            }
#endif
            if (!nextLeft.IsTracked) leftDepth.Reset();
            if (!nextRight.IsTracked) rightDepth.Reset();
            left = nextLeft;
            right = nextRight;
        }

#if UNITY_IOS
        // Viewing directions (from the camera) of the 2D joints; the struct's positions hold directions here. With
        // filters, each joint's image point is one-euro smoothed first (time = the Vision result's).
        bool TryGetDirections(Dictionary<JointName, Vector2> pose, Camera camera, OneEuroFilter2D[] filters, double time,
            out HandJoints directions)
        {
            directions = default;
            if (pose == null) return false;
            bool ok = true;
            var config = Config;
            Vector3 Direction(JointName joint)
            {
                if (!pose.TryGetValue(joint, out var image))
                {
                    ok = false;
                    return Vector3.forward;
                }
                if (filters != null)
                    image = filters[(int)joint].Filter(image, time, config.jointMinCutoff, config.jointBeta, config.jointDerivativeCutoff);
                return camera.ViewportPointToRay(ImageToViewport(image, config)).direction;
            }
            directions.Wrist = Direction(JointName.Wrist);
            directions.ThumbTip = Direction(JointName.ThumbTip);
            directions.IndexMCP = Direction(JointName.IndexMCP);
            directions.IndexTip = Direction(JointName.IndexTip);
            directions.MiddleMCP = Direction(JointName.MiddleMCP);
            directions.MiddleTip = Direction(JointName.MiddleTip);
            directions.RingMCP = Direction(JointName.RingMCP);
            directions.RingTip = Direction(JointName.RingTip);
            directions.LittleMCP = Direction(JointName.LittleMCP);
            directions.LittleTip = Direction(JointName.LittleTip);
            return ok;
        }

        // Where a hand is in the image, for matching identities: between the wrist and the middle knuckle.
        static Vector2 PalmImagePoint(Dictionary<JointName, Vector2> pose)
        {
            if (pose == null || !pose.TryGetValue(JointName.Wrist, out var wrist)) return new Vector2(0.5f, 0.5f);
            return pose.TryGetValue(JointName.MiddleMCP, out var middle) ? (wrist + middle) * 0.5f : wrist;
        }

        static System.Reflection.FieldInfo maxHandCountField;

        // HoloKit keeps its Max Hand Count private; read it for the status line.
        static int MaxHandsOf(HandTrackingManager tracking)
        {
            if (tracking == null) return 0;
            maxHandCountField ??= typeof(HandTrackingManager).GetField("m_MaxHandCount",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return maxHandCountField != null ? System.Convert.ToInt32(maxHandCountField.GetValue(tracking)) : 0;
        }

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

            float Extension(Vector3 tip, Vector3 knuckle) =>
                Mathf.InverseLerp(CurledRatio, ExtendedRatio, Vector3.Distance(wrist, tip) / Mathf.Max(1e-4f, Vector3.Distance(wrist, knuckle)));
            float openness = (Extension(j.IndexTip, j.IndexMCP) + Extension(j.MiddleTip, j.MiddleMCP) +
                              Extension(j.RingTip, j.RingMCP) + Extension(j.LittleTip, j.LittleMCP)) * 0.25f;

            return new HandState
            {
                IsTracked = true,
                WristPosition = wrist,
                IndexTipPosition = j.IndexTip,
                Pinch = pinched ? PinchState.Pinched : PinchState.Open,
                PinchStrength = Mathf.InverseLerp(StrengthZeroAt, StrengthOneAt, pinchDistance),
                PointingRay = new Ray(j.IndexMCP, direction.normalized),
                Grab = grab,
                PalmCenter = (wrist + j.IndexMCP + j.MiddleMCP + j.RingMCP + j.LittleMCP) / 5f,
                Openness = openness,
                ApparentSize = ApparentSize(wrist - head.position, j.IndexMCP - head.position, j.MiddleMCP - head.position,
                    j.RingMCP - head.position, j.LittleMCP - head.position),
            };
        }

        /// <summary>
        /// A hand's joints from 2D tracking (no LiDAR): each joint lies on its viewing ray from the head, and the depth is
        /// estimated from the hand's apparent size (<see cref="HandDepthEstimator"/>). The hand is taken as flat at that
        /// depth, which keeps what <see cref="FromJoints"/> measures as ratios (pinch, curl, openness) close to right.
        /// </summary>
        public static HandJoints JointsAtDepth(in HandJoints directions, Vector3 head, float depth)
        {
            Vector3 At(Vector3 direction) => head + direction.normalized * depth;
            return new HandJoints
            {
                Wrist = At(directions.Wrist), ThumbTip = At(directions.ThumbTip),
                IndexMCP = At(directions.IndexMCP), IndexTip = At(directions.IndexTip),
                MiddleMCP = At(directions.MiddleMCP), MiddleTip = At(directions.MiddleTip),
                RingMCP = At(directions.RingMCP), RingTip = At(directions.RingTip),
                LittleMCP = At(directions.LittleMCP), LittleTip = At(directions.LittleTip),
            };
        }

        /// <summary>The hand's apparent size from the joints' viewing directions (see <see cref="HandState.ApparentSize"/>).</summary>
        public static float ApparentSizeOf(in HandJoints directions) =>
            ApparentSize(directions.Wrist, directions.IndexMCP, directions.MiddleMCP, directions.RingMCP, directions.LittleMCP);

        /// <summary>An adult hand's wrist-to-knuckles length is about this many times its knuckle span (index to little).</summary>
        public const float LengthToSpan = 1.4f;

        // The hand's length as seen from the head (radians). A hand seen at an angle looks shorter along the axis it is
        // tilted about, so this takes the larger of two perpendicular measures: the mean angle from the wrist to the four
        // knuckles (four measurements average out joint noise), and the knuckle span scaled to a length.
        static float ApparentSize(Vector3 wrist, Vector3 index, Vector3 middle, Vector3 ring, Vector3 little)
        {
            float length = (Vector3.Angle(wrist, index) + Vector3.Angle(wrist, middle) + Vector3.Angle(wrist, ring)
                            + Vector3.Angle(wrist, little)) * 0.25f;
            float span = Vector3.Angle(index, little) * LengthToSpan;
            return Mathf.Max(length, span) * Mathf.Deg2Rad;
        }
    }

    /// <summary>How far <see cref="HoloKitHandSource"/>'s device pipeline gets (shown by <see cref="DeviceStatusHud"/>).</summary>
    public struct HandTrackingStatus
    {
        /// <summary>HoloKit's HandTrackingManager is assigned.</summary>
        public bool TrackingFound;
        /// <summary>
        /// It is enabled. HoloKit's stereo bar can switch it off (OPTIONS > HANDS), and Unity disables it if its Awake
        /// throws (in the editor, where HoloKit's native Vision code doesn't exist).
        /// </summary>
        public bool TrackingEnabled;
        /// <summary>Its Vision detector exists: its Awake found an ARCameraManager and an AROcclusionManager.</summary>
        public bool DetectorCreated;
        /// <summary>AR camera frames received while this source was enabled (Vision runs on every 3rd).</summary>
        public int ArFrames;
        /// <summary>Vision requests finished, with or without a hand.</summary>
        public int VisionRequests;
        /// <summary>Hands Vision reports this frame, and how many of them didn't become a tracked hand.</summary>
        public int HandsSeen, HandsRejected;
        /// <summary>The AR camera has LiDAR scene depth.</summary>
        public bool SceneDepth;
        /// <summary>Where the last mapped hand's depth came from: "LiDAR", "hand size", or "-" for none this frame.</summary>
        public string DepthPath;
        /// <summary>Vision runs on every this-many-th ARKit frame.</summary>
        public int VisionFrameInterval;
        /// <summary>HoloKit's Max Hand Count (1 or 2; 0 if unknown).</summary>
        public int MaxHands;
        /// <summary>The 2D joints are one-euro filtered.</summary>
        public bool Smoothing2D;

        /// <summary>Hand tracking runs: the manager is assigned and enabled, and its detector was created.</summary>
        public bool Started => TrackingFound && TrackingEnabled && DetectorCreated;

        /// <summary>Why hand tracking hasn't started, or "" once it has.</summary>
        public string NotStartedReason =>
            !TrackingFound ? "no HandTrackingManager assigned"
            : !TrackingEnabled ? "HandTrackingManager disabled (its Awake failed, or stereo bar OPTIONS > HANDS is off)"
            : !DetectorCreated ? "no Vision detector (needs ARCameraManager and AROcclusionManager at start)"
            : "";
    }
}
