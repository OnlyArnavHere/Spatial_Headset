using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpatialLauncher.SpatialWindows
{
    public enum ImmersiveEnvironment { Void, Cinema, Sky }

    /// <summary>
    /// One built environment: world-locked geometry under <see cref="Root"/> (floor, room) and head-following geometry
    /// under <see cref="SkyRoot"/> (sky dome, sun), drawn with transparent materials whose alpha follows the immersion
    /// level. Everything is drawn before windows (render queue below <see cref="ImmersiveEnvironments.MaxQueue"/>) and
    /// writes no depth, so windows are always drawn in full on top of it, whatever the level.
    /// </summary>
    public sealed class EnvironmentScene
    {
        public ImmersiveEnvironment Kind { get; }
        public GameObject Root { get; }
        /// <summary>Kept centred on the head: things at "infinity".</summary>
        public Transform SkyRoot { get; }
        public float Opacity { get; private set; }

        readonly List<(Material material, Color color)> layers = new List<(Material, Color)>();
        readonly List<Object> owned = new List<Object>();

        public EnvironmentScene(ImmersiveEnvironment kind)
        {
            Kind = kind;
            Root = new GameObject($"Environment - {kind}");
            SkyRoot = new GameObject("Sky").transform;
            SkyRoot.SetParent(Root.transform, false);
        }

        public IEnumerable<Material> Materials
        {
            get
            {
                foreach (var layer in layers) yield return layer.material;
            }
        }

        public IEnumerable<Renderer> Renderers => Root.GetComponentsInChildren<Renderer>(true);

        internal void AddLayer(Material material)
        {
            layers.Add((material, material.color));
            owned.Add(material);
        }

        internal void Own(Object asset) => owned.Add(asset);

        /// <summary>0 hides it (and stops drawing it), 1 shows it fully; in between it blends over passthrough.</summary>
        public void SetOpacity(float opacity)
        {
            Opacity = Mathf.Clamp01(opacity);
            foreach (var (material, color) in layers)
                material.color = new Color(color.r, color.g, color.b, color.a * Opacity);
            if (Root.activeSelf != Opacity > 0f) Root.SetActive(Opacity > 0f);
        }

        public void Destroy()
        {
            Object.Destroy(Root);
            foreach (var asset in owned)
                if (asset != null) Object.Destroy(asset);
            owned.Clear();
            layers.Clear();
        }
    }

    /// <summary>
    /// Builds the three environments from primitives and generated textures (no imported assets): a dark void with a
    /// soft grid floor, a cinema room with a big screen and rows of seats, and a gradient sky with a horizon and sun.
    /// Positions are relative to the floor under the user, facing the way the user faced when it was placed.
    /// </summary>
    public static class ImmersiveEnvironments
    {
        /// <summary>Environment materials use render queues below this (windows draw at 2000, "Geometry").</summary>
        public const int MaxQueue = 1500;
        const int SkyQueue = 1400, RoomQueue = 1410, ScreenQueue = 1420, FloorQueue = 1430, SeatQueue = 1440;

        public static EnvironmentScene Build(ImmersiveEnvironment kind, Material template, float eyeHeight, float skyRadius)
        {
            var scene = new EnvironmentScene(kind);
            switch (kind)
            {
                case ImmersiveEnvironment.Void: BuildVoid(scene, template, skyRadius); break;
                case ImmersiveEnvironment.Cinema: BuildCinema(scene, template, eyeHeight); break;
                case ImmersiveEnvironment.Sky: BuildSky(scene, template, skyRadius); break;
            }
            return scene;
        }

        static void BuildVoid(EnvironmentScene scene, Material template, float radius)
        {
            Dome(scene, template, radius, new Color(0.02f, 0.025f, 0.04f), null);
            var grid = GridTexture(1024, 32, new Color(0.35f, 0.55f, 0.95f));
            scene.Own(grid);
            float size = radius * 1.8f;
            var floor = Quad(scene, "Grid Floor", scene.Root.transform, Layer(scene, template, Color.white, grid, FloorQueue));
            floor.localRotation = Quaternion.Euler(90f, 0f, 0f);
            floor.localScale = new Vector3(size, size, 1f);
        }

        static void BuildCinema(EnvironmentScene scene, Material template, float eyeHeight)
        {
            const float width = 12f, height = 5f, front = 9f, back = -4f;
            var root = scene.Root.transform;
            var wall = Layer(scene, template, new Color(0.20f, 0.05f, 0.07f), null, RoomQueue);
            var floorMaterial = Layer(scene, template, new Color(0.07f, 0.045f, 0.05f), null, RoomQueue);
            var ceiling = Layer(scene, template, new Color(0.05f, 0.03f, 0.04f), null, RoomQueue);
            float depth = front - back, middle = (front + back) * 0.5f;

            Panel(scene, "Floor", root, floorMaterial, new Vector3(0f, 0f, middle), new Vector3(90f, 0f, 0f), new Vector2(width, depth));
            Panel(scene, "Ceiling", root, ceiling, new Vector3(0f, height, middle), new Vector3(-90f, 0f, 0f), new Vector2(width, depth));
            Panel(scene, "Screen Wall", root, wall, new Vector3(0f, height * 0.5f, front), Vector3.zero, new Vector2(width, height));
            Panel(scene, "Back Wall", root, wall, new Vector3(0f, height * 0.5f, back), new Vector3(0f, 180f, 0f), new Vector2(width, height));
            Panel(scene, "Left Wall", root, wall, new Vector3(-width * 0.5f, height * 0.5f, middle), new Vector3(0f, -90f, 0f), new Vector2(depth, height));
            Panel(scene, "Right Wall", root, wall, new Vector3(width * 0.5f, height * 0.5f, middle), new Vector3(0f, 90f, 0f), new Vector2(depth, height));

            // A softly lit screen, its centre a little above eye level.
            var glow = RadialTexture(256, new Color(0.92f, 0.94f, 0.98f), new Color(0.70f, 0.74f, 0.82f), 1f, 1f);
            scene.Own(glow);
            Panel(scene, "Screen", root, Layer(scene, template, Color.white, glow, ScreenQueue),
                new Vector3(0f, eyeHeight + 0.7f, front - 0.05f), Vector3.zero, new Vector2(9f, 9f / 2.39f));

            // Rows of seat backs between the user and the screen, farthest drawn first.
            var seat = new Color(0.12f, 0.03f, 0.05f);
            for (int row = 0; row < 4; row++)
            {
                float z = 2.5f + row * 1.3f;
                var material = Layer(scene, template, Color.Lerp(seat, Color.black, row * 0.15f), null, SeatQueue + (3 - row));
                Panel(scene, $"Seats {row + 1}", root, material, new Vector3(0f, 0.45f + row * 0.12f, z), Vector3.zero, new Vector2(width - 2f, 0.9f));
            }
        }

        static void BuildSky(EnvironmentScene scene, Material template, float radius)
        {
            var gradient = SkyTexture(256);
            scene.Own(gradient);
            Dome(scene, template, radius, Color.white, gradient);

            var sunTexture = RadialTexture(128, new Color(1f, 0.95f, 0.80f), new Color(1f, 0.85f, 0.60f), 1f, 0f);
            scene.Own(sunTexture);
            var sun = Quad(scene, "Sun", scene.SkyRoot, Layer(scene, template, Color.white, sunTexture, ScreenQueue));
            var direction = new Vector3(0.35f, 0.22f, 1f).normalized;
            sun.localPosition = direction * radius * 0.9f;
            sun.localRotation = Quaternion.LookRotation(direction);
            sun.localScale = Vector3.one * radius * 0.12f;

            var ground = RadialTexture(256, new Color(0.30f, 0.36f, 0.27f), new Color(0.62f, 0.60f, 0.55f), 1f, 0f);
            scene.Own(ground);
            float size = radius * 1.8f;
            var floor = Quad(scene, "Ground", scene.Root.transform, Layer(scene, template, Color.white, ground, FloorQueue));
            floor.localRotation = Quaternion.Euler(90f, 0f, 0f);
            floor.localScale = new Vector3(size, size, 1f);
        }

        // --- Pieces ------------------------------------------------------------------------------------------------

        static void Dome(EnvironmentScene scene, Material template, float radius, Color color, Texture texture)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Dome";
            Object.Destroy(sphere.GetComponent<Collider>());
            sphere.transform.SetParent(scene.SkyRoot, false);
            sphere.transform.localScale = Vector3.one * radius * 2f;
            Prepare(sphere.GetComponent<MeshRenderer>(), Layer(scene, template, color, texture, SkyQueue));
        }

        static Transform Panel(EnvironmentScene scene, string name, Transform parent, Material material, Vector3 position,
            Vector3 euler, Vector2 size)
        {
            var quad = Quad(scene, name, parent, material);
            quad.localPosition = position;
            quad.localRotation = Quaternion.Euler(euler);
            quad.localScale = new Vector3(size.x, size.y, 1f);
            return quad;
        }

        static Transform Quad(EnvironmentScene scene, string name, Transform parent, Material material)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Object.Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(parent, false);
            Prepare(quad.GetComponent<MeshRenderer>(), material);
            return quad.transform;
        }

        static void Prepare(MeshRenderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        static Material Layer(EnvironmentScene scene, Material template, Color color, Texture texture, int queue)
        {
            var material = new Material(template) { name = $"Environment {queue}", color = color };
            material.mainTexture = texture;
            MakeTransparent(material, queue);
            scene.AddLayer(material);
            return material;
        }

        /// <summary>URP Unlit, alpha-blended, both faces, no depth writes, drawn in <paramref name="queue"/>.</summary>
        public static void MakeTransparent(Material material, int queue)
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = queue;
        }

        // --- Generated textures ------------------------------------------------------------------------------------

        /// <summary>Soft grid lines that fade out towards the edge (alpha only where the lines are).</summary>
        static Texture2D GridTexture(int size, int cells, Color line)
        {
            var texture = NewTexture(size, size, TextureWrapMode.Clamp);
            var pixels = new Color32[size * size];
            float cell = size / (float)cells;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Distance (in pixels) to the nearest grid line, softened over 1.5 px.
                float dx = Mathf.Abs(Mathf.Repeat(x + 0.5f, cell) - cell * 0.5f);
                float dy = Mathf.Abs(Mathf.Repeat(y + 0.5f, cell) - cell * 0.5f);
                float onLine = Mathf.Clamp01(1f - (cell * 0.5f - Mathf.Max(dx, dy)) / 1.5f);
                float r = new Vector2(x - size * 0.5f, y - size * 0.5f).magnitude / (size * 0.5f);
                float fade = 1f - Mathf.SmoothStep(0.25f, 1f, r);
                // Lines fade out with distance; a faint glow under the user keeps the floor readable as a floor.
                float alpha = onLine * fade * 0.8f + Mathf.Max(0f, 1f - r) * 0.04f;
                pixels[y * size + x] = new Color(line.r, line.g, line.b, Mathf.Clamp01(alpha));
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>Vertical sky gradient for a sphere's UVs (v = 0 at the bottom pole, 0.5 at the horizon).</summary>
        static Texture2D SkyTexture(int height)
        {
            var texture = NewTexture(1, height, TextureWrapMode.Clamp);
            var below = new Color(0.40f, 0.42f, 0.40f);
            var horizon = new Color(0.97f, 0.86f, 0.74f);
            var low = new Color(0.55f, 0.72f, 0.92f);
            var zenith = new Color(0.10f, 0.26f, 0.60f);
            var pixels = new Color[height];
            for (int i = 0; i < height; i++)
            {
                float v = (i + 0.5f) / height;
                Color c;
                if (v < 0.5f) c = Color.Lerp(below, horizon, Mathf.SmoothStep(0.30f, 0.5f, v));
                else if (v < 0.58f) c = Color.Lerp(horizon, low, Mathf.InverseLerp(0.5f, 0.58f, v));
                else c = Color.Lerp(low, zenith, Mathf.SmoothStep(0.58f, 1f, v));
                pixels[i] = c;
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>A disc fading from <paramref name="centre"/> to <paramref name="edge"/>, alpha from centreAlpha to edgeAlpha.</summary>
        static Texture2D RadialTexture(int size, Color centre, Color edge, float centreAlpha, float edgeAlpha)
        {
            var texture = NewTexture(size, size, TextureWrapMode.Clamp);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = Mathf.Clamp01(new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f).magnitude / (size * 0.5f));
                float t = Mathf.SmoothStep(0f, 1f, r);
                var c = Color.Lerp(centre, edge, t);
                c.a = Mathf.Lerp(centreAlpha, edgeAlpha, t);
                pixels[y * size + x] = c;
            }
            texture.SetPixels(pixels);
            texture.Apply(true, true);
            return texture;
        }

        static Texture2D NewTexture(int width, int height, TextureWrapMode wrap) =>
            new Texture2D(width, height, TextureFormat.RGBA32, width > 1) { wrapMode = wrap, filterMode = FilterMode.Bilinear, name = "Environment (generated)" };
    }
}
