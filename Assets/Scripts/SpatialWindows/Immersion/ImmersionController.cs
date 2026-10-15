using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// How immersed the user is: <see cref="Level"/> 0 is full passthrough (the camera feed on device, the
    /// <c>CameraBackgroundFallback</c> clear colour or XR Simulation's room in the editor), 1 is the full environment, and
    /// in between the environment blends over passthrough. One of three <see cref="ImmersiveEnvironment"/>s is selected
    /// (<see cref="ImmersiveEnvironments"/> builds them on first use). Changes apply in the same frame.
    ///
    /// Windows are never touched and stay readable at every level: environments draw before windows and write no depth,
    /// so a window is always drawn in full on top. The editor keys [ and ] step the level by <see cref="KeyStep"/>; the
    /// dial window (<see cref="ImmersionDialContent"/>) has a slider and the environment choice.
    /// </summary>
    public class ImmersionController : MonoBehaviour
    {
        public const float KeyStep = 0.1f;

        [Tooltip("The user's head. Defaults to Camera.main.")]
        [SerializeField] Transform head;
        [Tooltip("Base material for environments (URP Unlit); made transparent at runtime. Defaults to the window material.")]
        [SerializeField] Material environmentMaterial;
        [SerializeField] WindowManager manager;
        [SerializeField] ImmersiveEnvironment environment = ImmersiveEnvironment.Void;
        [Range(0f, 1f)]
        [SerializeField] float level;
        [SerializeField] Key lessKey = Key.LeftBracket;
        [SerializeField] Key moreKey = Key.RightBracket;
        [Tooltip("Height of the user's eyes above the floor, in metres: environments put their floor this far below the head.")]
        [SerializeField] float eyeHeight = 1.5f;
        [Tooltip("Radius of sky domes; kept inside the camera's far clip plane.")]
        [SerializeField] float skyRadius = 18f;

        /// <summary>0 = passthrough, 1 = full environment.</summary>
        public float Level => level;
        public ImmersiveEnvironment Environment => environment;
        /// <summary>The selected environment once built (it is built the first time the level rises above 0).</summary>
        public EnvironmentScene Active => scenes.TryGetValue(environment, out var scene) ? scene : null;
        public IReadOnlyDictionary<ImmersiveEnvironment, EnvironmentScene> Built => scenes;
        public WindowManager Manager
        {
            get => manager;
            set => manager = value;
        }

        /// <summary>The level or the environment changed.</summary>
        public event Action Changed;

        readonly Dictionary<ImmersiveEnvironment, EnvironmentScene> scenes = new Dictionary<ImmersiveEnvironment, EnvironmentScene>();
        Material template;
        bool ownsTemplate;

        /// <summary>How opaque the environment is at a level: the blend curve (linear, so the dial does what it shows).</summary>
        public static float Opacity(float immersionLevel) => Mathf.Clamp01(immersionLevel);

        Transform Head
        {
            get
            {
                if (head == null && Camera.main != null) head = Camera.main.transform;
                return head;
            }
        }

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
        }

        void Start() => Apply(placeIfNew: true);

        /// <summary>Sets the level (clamped to 0..1) and applies it at once.</summary>
        public void SetLevel(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(value, level)) return;
            bool wasOff = level <= 0f;
            level = value;
            Apply(placeIfNew: wasOff);
            Changed?.Invoke();
        }

        public void Step(float delta) => SetLevel(level + delta);

        /// <summary>Switches environment, keeping the level; the new one is placed around the user.</summary>
        public void SetEnvironment(ImmersiveEnvironment value)
        {
            if (value == environment) return;
            environment = value;
            Apply(placeIfNew: true);
            Changed?.Invoke();
        }

        /// <summary>Puts the current environment's floor under the user, facing where the user faces.</summary>
        public void Recentre()
        {
            var scene = Active;
            var h = Head;
            if (scene == null || h == null) return;
            var forward = Vector3.ProjectOnPlane(h.forward, Vector3.up);
            var yaw = forward.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(forward, Vector3.up);
            scene.Root.transform.SetPositionAndRotation(h.position - Vector3.up * eyeHeight, yaw);
            FollowHead(scene);
        }

        void Apply(bool placeIfNew)
        {
            float opacity = Opacity(level);
            if (opacity > 0f && !scenes.ContainsKey(environment))
            {
                var scene = ImmersiveEnvironments.Build(environment, Template(), eyeHeight, SkyRadius());
                scene.Root.transform.SetParent(transform, false);
                scenes[environment] = scene;
                placeIfNew = true;
            }
            if (placeIfNew && opacity > 0f) Recentre();
            foreach (var pair in scenes)
                pair.Value.SetOpacity(pair.Key == environment ? opacity : 0f);
        }

        float SkyRadius()
        {
            var cam = Head != null ? Head.GetComponent<Camera>() : null;
            return cam != null ? Mathf.Min(skyRadius, cam.farClipPlane * 0.9f) : skyRadius;
        }

        Material Template()
        {
            if (template != null) return template;
            template = environmentMaterial != null ? environmentMaterial : manager != null ? manager.WindowMaterial : null;
            if (template == null)
            {
                // Shader.Find only works in builds if something else references the shader; assign a material instead.
                template = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Environment (runtime)" };
                ownsTemplate = true;
            }
            return template;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || KeyboardShortcuts.Blocked) return;
            if (keyboard[lessKey].wasPressedThisFrame) Step(-KeyStep);
            if (keyboard[moreKey].wasPressedThisFrame) Step(KeyStep);
        }

        void LateUpdate()
        {
            var scene = Active;
            if (scene != null && scene.Opacity > 0f) FollowHead(scene);
        }

        // The sky is "at infinity": it moves with the head so walking never reaches it.
        void FollowHead(EnvironmentScene scene)
        {
            if (Head != null) scene.SkyRoot.position = Head.position;
        }

        void OnDestroy()
        {
            foreach (var scene in scenes.Values) scene.Destroy();
            scenes.Clear();
            if (ownsTemplate && template != null) Destroy(template);
        }
    }
}
