using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows.Testing
{
    /// <summary>
    /// Editor test harness. N or the on-screen button spawns a window with placeholder content.
    /// B toggles billboarding and L toggles world-locking on the focused window. I switches the
    /// window input source (mouse / simulated hands).
    /// </summary>
    public class WindowSpawnTester : MonoBehaviour
    {
        [SerializeField] WindowManager manager;
        [SerializeField] Key spawnKey = Key.N;
        [SerializeField] Key toggleBillboardKey = Key.B;
        [SerializeField] Key toggleWorldLockKey = Key.L;
        [SerializeField] Key switchInputKey = Key.I;
        [SerializeField] bool showOnScreenButton = true;
        [Tooltip("Base material for the placeholder panels. Defaults to URP Unlit.")]
        [SerializeField] Material contentMaterial;

        // Avoids the window chrome colours (blue title, red close, amber handle).
        static readonly Color[] Palette =
        {
            new Color(0.15f, 0.60f, 0.55f), // teal
            new Color(0.30f, 0.65f, 0.30f), // green
            new Color(0.55f, 0.35f, 0.80f), // purple
            new Color(0.80f, 0.35f, 0.60f), // pink
            new Color(0.40f, 0.55f, 0.65f), // steel
            new Color(0.60f, 0.55f, 0.30f), // olive
        };

        int spawned;

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
            if (contentMaterial == null)
            {
                Debug.LogWarning("WindowSpawnTester: no content material assigned; falling back to Shader.Find, which may be stripped from builds.", this);
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null)
                    contentMaterial = new Material(shader) { name = "Placeholder (runtime)" };
            }
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || manager == null) return;

            if (keyboard[spawnKey].wasPressedThisFrame)
                SpawnTestWindow();
            if (keyboard[switchInputKey].wasPressedThisFrame)
                manager.CycleInput();

            var focused = manager.FocusedWindow;
            if (focused == null) return;
            if (keyboard[toggleBillboardKey].wasPressedThisFrame)
                focused.Billboard = !focused.Billboard;
            if (keyboard[toggleWorldLockKey].wasPressedThisFrame)
                focused.WorldLocked = !focused.WorldLocked;
        }

        public void SpawnTestWindow()
        {
            spawned++;
            var window = manager.Spawn($"Window {spawned}");
            if (window == null || contentMaterial == null) return;

            var content = new GameObject("Placeholder").AddComponent<PlaceholderContent>();
            content.Init(window, contentMaterial, Palette[(spawned - 1) % Palette.Length], $"Placeholder {spawned}");
        }

        void OnGUI()
        {
            if (!showOnScreenButton || manager == null) return;

            GUILayout.BeginArea(new Rect(16, 16, 340, 220));
            if (GUILayout.Button($"Spawn Window ({spawnKey})", GUILayout.Height(36)))
                SpawnTestWindow();

            GUILayout.Label($"Input ({switchInputKey}): {manager.ActiveInputName}");
            if (manager.ActiveInput is HandWindowInput hands && hands.HandSource is SimulatedHandSource simulated)
            {
                GUILayout.Label(simulated.TwoHanded
                    ? "Two hands (T): LMB pinches right, RMB pinches left, hold Shift to aim left"
                    : "One hand (T): LMB pinches");
                GUILayout.Label($"Hand reach (scroll): {simulated.Reach:F2} m");
            }

            var focused = manager.FocusedWindow;
            GUILayout.Label($"Windows: {manager.Windows.Count}");
            if (focused != null)
            {
                GUILayout.Label($"Focused: {focused.Title}");
                GUILayout.Label($"Billboard ({toggleBillboardKey}): {focused.Billboard}   World-locked ({toggleWorldLockKey}): {focused.WorldLocked}");
            }
            GUILayout.EndArea();
        }

        void OnDestroy()
        {
            if (contentMaterial != null && contentMaterial.name == "Placeholder (runtime)")
                Destroy(contentMaterial);
        }
    }
}
