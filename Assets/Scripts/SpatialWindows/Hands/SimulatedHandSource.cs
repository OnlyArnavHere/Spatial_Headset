using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Editor stand-in for tracked hands. The right hand's fingertip floats below-right of the camera and
    /// points at whatever is under the mouse; the left mouse button pinches. Scroll moves the hand
    /// towards or away from you, which pushes a pinched window in or out.
    /// Two-hand mode (T): a mirrored left hand is added; hold Shift to aim it with the mouse and use the
    /// right mouse button to pinch with it. Each hand keeps its last aim while the mouse aims the other.
    /// Hold G for a fist (for the gesture recognizers): the fist sits on the mouse ray at reach distance, so
    /// it moves with the mouse, and scrolling moves it in and out. The hand's apparent size follows its distance.
    /// </summary>
    public class SimulatedHandSource : MonoBehaviour, IHandSource
    {
        [Tooltip("Camera the mouse aims through. Defaults to Camera.main.")]
        [SerializeField] Camera rayCamera;
        [Tooltip("Right index fingertip relative to the camera, in metres (x right, y up). The left hand mirrors x.")]
        [SerializeField] Vector2 handOffset = new Vector2(0.15f, -0.2f);
        [Tooltip("How far in front of the camera the hands are. Scroll to change it while testing.")]
        [SerializeField] float reach = 0.35f;
        [SerializeField] Vector2 reachRange = new Vector2(0.15f, 0.65f);
        [SerializeField] float scrollStep = 0.02f;
        [SerializeField] bool twoHanded;
        [SerializeField] Key twoHandKey = Key.T;
        [SerializeField] Key fistKey = Key.G;
        [Tooltip("Wrist to middle knuckle, for the apparent size gesture depth is estimated from.")]
        [SerializeField] float handLength = 0.09f;
        [Tooltip("Aim depth when nothing is under the mouse.")]
        [SerializeField] float defaultAimDistance = 2f;
        [SerializeField] LayerMask raycastMask = ~0;

        public bool TwoHanded
        {
            get => twoHanded;
            set => twoHanded = value;
        }

        public float Reach => reach;

        /// <summary>The hand simulated in one-hand mode (Settings > Hands); the other joins in two-hand mode.</summary>
        public Handedness DominantHand { get; set; } = Handedness.Right;

        HandState left, right;
        Aim leftAim, rightAim;

        struct Aim
        {
            public bool IsSet;
            public float Distance;
            public Vector3 Point;
        }

        public HandState GetHand(Handedness hand) => hand == Handedness.Left ? left : right;

        void Update()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (rayCamera == null) rayCamera = Camera.main;
            if (mouse == null || rayCamera == null)
            {
                left = right = HandState.Untracked;
                return;
            }

            if (keyboard != null && !KeyboardShortcuts.Blocked && keyboard[twoHandKey].wasPressedThisFrame)
                twoHanded = !twoHanded;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                reach = Mathf.Clamp(reach + Mathf.Sign(scroll) * scrollStep, reachRange.x, reachRange.y);

            bool fist = keyboard != null && !KeyboardShortcuts.Blocked && keyboard[fistKey].isPressed;
            bool rightPinch = mouse.leftButton.isPressed && !fist;
            bool leftPinch = twoHanded && mouse.rightButton.isPressed;
            bool aimLeft = twoHanded && keyboard != null && keyboard.shiftKey.isPressed;

            var screen = mouse.position.ReadValue();
            if (IsInView(screen))
            {
                var mouseRay = rayCamera.ScreenPointToRay(screen);
                if (aimLeft) UpdateAim(ref leftAim, mouseRay, leftPinch);
                else UpdateAim(ref rightAim, mouseRay, rightPinch);
            }

            bool aimsRight = !aimLeft;
            right = rightAim.IsSet ? BuildHand(Handedness.Right, rightAim.Point, rightPinch, fist && aimsRight) : HandState.Untracked;
            left = twoHanded && leftAim.IsSet ? BuildHand(Handedness.Left, leftAim.Point, leftPinch, fist && aimLeft) : HandState.Untracked;
            // One-handed and left-handed: the mouse drives the left hand instead.
            if (!twoHanded && DominantHand == Handedness.Left)
            {
                left = rightAim.IsSet ? BuildHand(Handedness.Left, rightAim.Point, rightPinch, fist) : HandState.Untracked;
                right = HandState.Untracked;
            }
        }

        void UpdateAim(ref Aim aim, Ray mouseRay, bool pinched)
        {
            // While pinched the aim depth stays fixed, so a dragged window moving under the mouse
            // doesn't feed back into where the hand points.
            if (!pinched || !aim.IsSet)
            {
                aim.Distance = WindowRaycast.TryHit(mouseRay, 20f, raycastMask, out var hit, out _)
                    ? hit.distance
                    : defaultAimDistance;
            }
            aim.Point = mouseRay.GetPoint(aim.Distance);
            aim.IsSet = true;
        }

        HandState BuildHand(Handedness hand, Vector3 aimPoint, bool pinched, bool fist)
        {
            var cam = rayCamera.transform;
            float side = hand == Handedness.Right ? 1f : -1f;
            // A fist sits on the mouse ray, so it moves with the mouse; an open hand floats at its usual place.
            var tip = fist
                ? cam.position + (aimPoint - cam.position).normalized * reach
                : cam.position + cam.right * (handOffset.x * side) + cam.up * handOffset.y + cam.forward * reach;
            var direction = aimPoint - tip;
            if (direction.sqrMagnitude < 1e-6f) direction = cam.forward;
            var palm = tip - cam.forward * 0.05f;

            return new HandState
            {
                IsTracked = true,
                WristPosition = tip - cam.forward * 0.09f - cam.up * 0.02f,
                IndexTipPosition = tip,
                Pinch = pinched ? PinchState.Pinched : PinchState.Open,
                PinchStrength = pinched ? 1f : 0f,
                PointingRay = new Ray(tip, direction.normalized),
                Grab = fist,
                Openness = fist ? 0f : 1f,
                PalmCenter = palm,
                ApparentSize = handLength / Mathf.Max(0.05f, Vector3.Distance(palm, cam.position)),
                // Typical raw measures, so Hand Setup can run in the editor (G for the fist, the mouse button to pinch).
                PinchRatio = pinched ? 0.1f : 0.9f,
                FingerExtension = fist ? 0.95f : 1.8f,
                HandSize = handLength,
                MetricDepth = true,
            };
        }

        bool IsInView(Vector2 screenPosition)
        {
            if (!float.IsFinite(screenPosition.x) || !float.IsFinite(screenPosition.y)) return false;
            if (screenPosition.x < 0f || screenPosition.y < 0f
                || screenPosition.x >= Screen.width || screenPosition.y >= Screen.height) return false;
            return rayCamera.pixelRect.Contains(screenPosition);
        }
    }
}
