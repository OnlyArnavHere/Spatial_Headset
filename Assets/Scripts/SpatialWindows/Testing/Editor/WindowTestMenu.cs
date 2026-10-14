using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>Menu items for driving the window test harness without keyboard input (for example from MCP).</summary>
    static class WindowTestMenu
    {
        [MenuItem("Tools/Spatial Windows/Spawn Test Window")]
        static void SpawnTestWindow()
        {
            var tester = Object.FindFirstObjectByType<WindowSpawnTester>();
            if (tester == null)
            {
                Debug.LogWarning("No WindowSpawnTester in the open scene.");
                return;
            }
            tester.SpawnTestWindow();
        }

        [MenuItem("Tools/Spatial Windows/Spawn Test Window", true)]
        static bool CanSpawnTestWindow() => Application.isPlaying;

        // Title bar height plus gap above the body, matching SpatialWindow's layout.
        const float TitleBarExtent = 0.065f;

        /// <summary>
        /// Clicks (press + release, no movement) a point of the window just behind the front one that is
        /// visibly uncovered, through MouseWindowInput, and logs what the physics ray hit along the way.
        /// </summary>
        [MenuItem("Tools/Spatial Windows/Click Window Behind Front")]
        static void ClickWindowBehindFront()
        {
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var input = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (manager == null || input == null || cam == null || manager.Windows.Count < 2)
            {
                Debug.LogWarning("[ClickTest] Needs a WindowManager, a MouseWindowInput, a main camera and at least 2 windows.");
                return;
            }

            var target = manager.Windows[manager.Windows.Count - 2];
            if (!TryFindVisiblePoint(manager, target, cam, out var screenPoint))
            {
                Debug.LogWarning($"[ClickTest] No visible point found on {target.Title}.");
                return;
            }

            var ray = cam.ScreenPointToRay(screenPoint);
            var hits = Physics.RaycastAll(ray, 20f, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var hitList = new System.Text.StringBuilder();
            foreach (var hit in hits)
            {
                var part = hit.collider.GetComponent<WindowPart>();
                hitList.Append($"\n  {hit.distance:F3} m  {hit.collider.name}" +
                               (part != null ? $" ({part.Window.Title} {part.Type})" : $" [not a window, layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}]"));
            }
            Debug.Log($"[ClickTest] Clicking {target.Title} at screen {screenPoint}; front was {manager.FocusedWindow.Title}. Ray hits:{hitList}");

            input.PressAt(screenPoint);
            input.ReleaseAt(screenPoint);

            var focused = manager.FocusedWindow;
            Debug.Log($"[ClickTest] Result: focused = {focused.Title}, title colour = {(focused.IsFocused ? "focused" : "unfocused")} -> " +
                      (focused == target ? "PASS" : "FAIL"));
        }

        [MenuItem("Tools/Spatial Windows/Click Window Behind Front", true)]
        static bool CanClickWindowBehindFront() => Application.isPlaying;

        // Finds a screen point on the target where it is the visually front-most window,
        // using the windows' drawn rectangles rather than physics.
        static bool TryFindVisiblePoint(WindowManager manager, SpatialWindow target, Camera cam, out Vector2 screenPoint)
        {
            const int steps = 9;
            var visual = target.ContentRoot.parent;
            for (int ix = 1; ix < steps; ix++)
            for (int iy = 1; iy < steps; iy++)
            {
                float x = Mathf.Lerp(-target.Size.x * 0.5f, target.Size.x * 0.5f, ix / (float)steps);
                float y = Mathf.Lerp(-target.Size.y * 0.5f, target.Size.y * 0.5f + TitleBarExtent, iy / (float)steps);
                Vector3 screen = cam.WorldToScreenPoint(visual.TransformPoint(new Vector3(x, y, 0f)));
                if (screen.z <= 0f || screen.x < 0f || screen.y < 0f || screen.x >= Screen.width || screen.y >= Screen.height)
                    continue;
                screenPoint = screen;
                if (VisualPick(manager, cam.ScreenPointToRay(screenPoint)) == target)
                    return true;
            }
            screenPoint = default;
            return false;
        }

        static SpatialWindow VisualPick(WindowManager manager, Ray ray)
        {
            SpatialWindow best = null;
            float bestDistance = float.MaxValue;
            foreach (var window in manager.Windows)
            {
                var visual = window.ContentRoot.parent;
                var plane = new Plane(visual.forward, visual.position);
                if (!plane.Raycast(ray, out float distance) || distance >= bestDistance) continue;
                var local = visual.InverseTransformPoint(ray.GetPoint(distance));
                bool inside = Mathf.Abs(local.x) <= window.Size.x * 0.5f
                              && local.y >= -window.Size.y * 0.5f && local.y <= window.Size.y * 0.5f + TitleBarExtent;
                if (!inside) continue;
                best = window;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>Saves the Game view to Temp/GameView.png in the project folder.</summary>
        [MenuItem("Tools/Spatial Windows/Capture Game View")]
        static void CaptureGameView()
        {
            ScreenCapture.CaptureScreenshot("Temp/GameView.png");
            Debug.Log("[WindowDiag] Game view capture requested: Temp/GameView.png");
        }

        /// <summary>Logs every window part's renderer, shader and colours, plus where it sits relative to the camera.</summary>
        [MenuItem("Tools/Spatial Windows/Log Window Rendering")]
        static void LogWindowRendering()
        {
            var cam = Camera.main;
            foreach (var window in Object.FindObjectsByType<SpatialWindow>(FindObjectsSortMode.None))
            {
                foreach (var renderer in window.GetComponentsInChildren<Renderer>())
                {
                    var m = renderer.sharedMaterial;
                    string shader = m == null ? "<no material>" : m.shader == null ? "<no shader>" : m.shader.name;
                    string baseColor = m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : "-";
                    string color = m != null && m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "-";
                    string facing = cam == null ? "-" :
                        Vector3.Dot(renderer.transform.forward, renderer.transform.position - cam.transform.position).ToString("F2");
                    Debug.Log($"[WindowDiag] {window.Title}/{renderer.name}: shader={shader} supported={m != null && m.shader != null && m.shader.isSupported} " +
                              $"_BaseColor={baseColor} _Color={color} queue={(m != null ? m.renderQueue : 0)} " +
                              $"scale={renderer.transform.lossyScale} facingDot={facing} visible={renderer.isVisible}");
                }
            }
            if (cam != null)
                Debug.Log($"[WindowDiag] camera pos={cam.transform.position} fwd={cam.transform.forward} near={cam.nearClipPlane} clear={cam.clearFlags} bg={cam.backgroundColor}");
        }
    }
}
