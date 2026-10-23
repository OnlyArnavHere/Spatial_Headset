using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Every threshold the gesture recognizers (<see cref="GestureRecognizers"/>) and the hand-scale depth estimate use,
    /// in one asset (Assets/Settings/GestureConfig.asset), so they can be tuned in one place. Times are seconds,
    /// distances metres, speeds metres per second. The debug overlay (<see cref="GestureDebugOverlay"/>) shows the
    /// values these are compared against, live.
    /// </summary>
    [CreateAssetMenu(menuName = "Spatial Launcher/Gesture Config", fileName = "GestureConfig")]
    public class GestureConfig : ScriptableObject
    {
        [Header("Hand shape")]
        [Tooltip("A hand is a fist at or below this openness (or when the source reports a grab).")]
        [Range(0f, 1f)] public float fistOpenness = 0.25f;
        [Tooltip("A hand is an open palm at or above this openness.")]
        [Range(0f, 1f)] public float openPalmOpenness = 0.65f;
        [Tooltip("A fist must hold this long before it counts (filters a hand passing through a fist).")]
        public float fistHoldTime = 0.15f;
        [Tooltip("An open hand must hold this long to end a grab (filters a flicker of openness).")]
        public float openHoldTime = 0.1f;
        [Tooltip("A hand lost for less than this keeps its last state: gestures carry on through short tracking drops.")]
        public float trackingGrace = 0.25f;

        [Header("Hand-scale depth (no LiDAR)")]
        [Tooltip("Wrist to knuckles of an average adult hand. Depth = this / the hand's apparent size.")]
        public float handLength = 0.09f;
        [Tooltip("Depth smoothing rate per second; higher follows faster and jitters more.")]
        public float depthSmoothing = 10f;
        [Tooltip("The estimate moves at most this fast: one bad frame can't throw it.")]
        public float maxDepthSpeed = 2.5f;
        public float minHandDepth = 0.15f;
        public float maxHandDepth = 1.0f;
        [Tooltip("Smoothing rate per second of the depth speed used to start and stop pushes.")]
        public float velocitySmoothing = 8f;
        [Tooltip("2D joints from Apple Vision to camera viewport: mirror x.")]
        public bool imageFlipX;
        [Tooltip("2D joints from Apple Vision to camera viewport: mirror y.")]
        public bool imageFlipY;
        [Tooltip("2D joints from Apple Vision to camera viewport: swap x and y (portrait image, landscape screen).")]
        public bool imageSwapXY;

        [Header("2D joint smoothing (no LiDAR)")]
        [Tooltip("One-euro filter on Apple Vision's 2D joints (image units, 0..1 across the camera image).")]
        public bool smoothJoints2D = true;
        [Tooltip("Cutoff (Hz) for a still hand: lower is steadier and lags more.")]
        public float jointMinCutoff = 1.5f;
        [Tooltip("How fast the cutoff rises with speed (per image width per second): higher lags less in fast moves.")]
        public float jointBeta = 8f;
        [Tooltip("Cutoff (Hz) for the speed estimate itself.")]
        public float jointDerivativeCutoff = 1f;

        [Header("Two hands")]
        [Tooltip("A hand keeps its identity (left or right) while it moves less than this (image units) between Vision " +
                 "results, even when Vision's own left/right guess flips.")]
        public float identityMaxJump = 0.2f;
        [Tooltip("A hand's identity is kept this long after it was last seen, so a short drop doesn't swap hands.")]
        public float identityMemory = 0.4f;
        [Tooltip("Vision must call a hand the other side this many results in a row before its identity follows.")]
        public int identitySwitchAfter = 6;

        [Header("1. Fist grab")]
        [Tooltip("Metres the window moves in or out per metre the fist moves in or out.")]
        public float grabDepthGain = 3f;
        public float minWindowDistance = 0.4f;
        public float maxWindowDistance = 5f;

        [Header("2. Palm push / pull")]
        [Tooltip("Opening a fist starts a push only if the hand moved in or out at least this fast while opening.")]
        public float pushStartSpeed = 0.35f;
        [Tooltip("A fist that opens within this long of ending still starts a push; also how far back the push speed looks.")]
        public float pushTransitionTime = 0.4f;
        [Tooltip("Metres the window moves per metre the open palm moves in or out.")]
        public float pushGain = 4f;
        [Tooltip("The push ends when the palm moves slower than this ...")]
        public float pushStopSpeed = 0.08f;
        [Tooltip("... for this long.")]
        public float pushStopTime = 0.4f;
        public float pushMaxTime = 4f;

        [Header("3. Two-hand stretch")]
        [Tooltip("The second hand must pinch the same window within this long of the first.")]
        public float stretchStartWindow = 0.6f;
        [Tooltip("A pinch may drop for this long without ending the stretch.")]
        public float pinchReleaseGrace = 0.15f;

        [Header("Pinch and hold (app switcher)")]
        [Tooltip("A pinch held this long while pointing at no window opens the app switcher.")]
        public float pinchHoldTime = 0.8f;
        [Tooltip("After it opens, the gesture waits this long before it can fire again.")]
        public float pinchHoldCooldown = 1f;
        [Tooltip("Hand separation components below this count as this, so the aspect can't explode.")]
        public float stretchMinComponent = 0.05f;
        [Tooltip("The aspect changes by at most this factor either way in one stretch.")]
        public float maxAspectChange = 3f;

        [Header("4. Cross and sweep (hide / show all)")]
        [Tooltip("The left wrist must be at least this far right of the right wrist.")]
        public float crossMargin = 0.03f;
        [Tooltip("And the wrists at most this far apart vertically.")]
        public float crossMaxHeightGap = 0.15f;
        [Tooltip("Crossed for this long arms the gesture.")]
        public float crossHoldTime = 0.3f;
        [Tooltip("Then the wrists must sweep this far apart ...")]
        public float sweepDistance = 0.35f;
        [Tooltip("... within this long of uncrossing.")]
        public float sweepTime = 0.8f;
        [Tooltip("No second trigger for this long.")]
        public float crossCooldown = 1f;
    }
}
