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
        [Tooltip("Aim depth when nothing is under the mouse.")]
        [SerializeField] float defaultAimDistance = 2f;
        [SerializeField] LayerMask raycastMask = ~0;

        public bool TwoHanded
        {
            get => twoHanded;
            set => twoHanded = value;
        }

        public float Reach => reach;

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

            if (keyboard != null && keyboard[twoHandKey].wasPressedThisFrame)
                twoHanded = !twoHanded;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                reach = Mathf.Clamp(reach + Mathf.Sign(scroll) * scrollStep, reachRange.x, reachRange.y);

            bool rightPinch = mouse.leftButton.isPressed;
            bool leftPinch = twoHanded && mouse.rightButton.isPressed;
            bool aimLeft = twoHanded && keyboard != null && keyboard.shiftKey.isPressed;

            var screen = mouse.position.ReadValue();
            if (IsInView(screen))
            {
                var mouseRay = rayCamera.ScreenPointToRay(screen);
                if (aimLeft) UpdateAim(ref leftAim, mouseRay, leftPinch);
                else UpdateAim(ref rightAim, mouseRay, rightPinch);
            }

            right = rightAim.IsSet ? BuildHand(Handedness.Right, rightAim.Point, rightPinch) : HandState.Untracked;
            left = twoHanded && leftAim.IsSet ? BuildHand(Handedness.Left, leftAim.Point, leftPinch) : HandState.Untracked;
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

        HandState BuildHand(Handedness hand, Vector3 aimPoint, bool pinched)
        {
            var cam = rayCamera.transform;
            float side = hand == Handedness.Right ? 1f : -1f;
            var tip = cam.position + cam.right * (handOffset.x * side) + cam.up * handOffset.y + cam.forward * reach;
            var direction = aimPoint - tip;
            if (direction.sqrMagnitude < 1e-6f) direction = cam.forward;

            return new HandState
            {
                IsTracked = true,
                WristPosition = tip - cam.forward * 0.09f - cam.up * 0.02f,
                IndexTipPosition = tip,
                Pinch = pinched ? PinchState.Pinched : PinchState.Open,
                PinchStrength = pinched ? 1f : 0f,
                PointingRay = new Ray(tip, direction.normalized),
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
