using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A floating panel: a body quad that holds content, a title bar above it with an options
    /// button and a close button, and a resize handle in the bottom-right corner. The options button
    /// opens a strip under the body with the window's curve and aspect preset (<see cref="WindowOptions"/>).
    /// The window's +Z points away from the viewer, so its quads face the user.
    /// Built at runtime by <see cref="WindowManager"/>; place content under <see cref="ContentRoot"/>.
    ///
    /// <see cref="Curve"/> wraps the whole window (chrome and content) around a gentle vertical cylinder
    /// (<see cref="WindowCurve"/>): <see cref="WindowCurver"/> bends what is drawn, while colliders stay flat in
    /// layout space and <see cref="WindowRaycast"/> maps rays onto the cylinder analytically.
    /// </summary>
    public class SpatialWindow : MonoBehaviour
    {
        public const float TitleBarHeight = 0.06f;
        public const float TitleBarGap = 0.005f;
        public const float CloseButtonSize = 0.045f;
        const float ResizeHandleSize = 0.04f;
        public const float Margin = 0.008f;
        // Thin, so raycasts agree with what's drawn; a thick box would reach past the 1 cm focus push-forward.
        const float ColliderDepth = 0.001f;
        // Layers in front of the body, towards the viewer (-Z).
        const float DecorationDepth = 0.002f;
        const float ContentDepth = 0.003f;

        // Each part gets a distinct colour so it stays recognisable over any content.
        static readonly Color BodyColor = new Color(0.10f, 0.10f, 0.12f);         // near-black frame
        static readonly Color TitleColor = new Color(0.32f, 0.34f, 0.40f);        // slate, unfocused
        static readonly Color TitleFocusedColor = new Color(0.16f, 0.36f, 0.95f); // bright blue, focused
        static readonly Color CloseColor = new Color(0.90f, 0.22f, 0.22f);        // red
        static readonly Color HandleColor = new Color(1.00f, 0.75f, 0.15f);       // amber

        [SerializeField] bool billboard = true;
        [Tooltip("Only rotate around the vertical axis when billboarding, so windows stay upright.")]
        [SerializeField] bool billboardYawOnly = true;
        [Tooltip("World-locked windows stay put; otherwise the window follows the head (body-locked).")]
        [SerializeField] bool worldLocked = true;
        [SerializeField] float followSmoothing = 8f;
        [Tooltip("How quickly the window animates towards its focus depth and scale.")]
        [SerializeField] float presentationSpeed = 10f;

        /// <summary>Raised when the window's size changes, so content can lay itself out again.</summary>
        public event Action<SpatialWindow> SizeChanged;
        /// <summary>The user picked an aspect preset in the options strip; <see cref="WindowManager"/> applies it.</summary>
        public event Action<SpatialWindow, AspectPreset> AspectRequested;
        /// <summary><see cref="Curve"/> or <see cref="Aspect"/> changed.</summary>
        public event Action<SpatialWindow> ShapeChanged;

        /// <summary>Windows with a curve above 0, which <see cref="WindowRaycast"/> hit-tests analytically.</summary>
        public static IReadOnlyList<SpatialWindow> CurvedWindows => curvedWindows;
        static readonly List<SpatialWindow> curvedWindows = new List<SpatialWindow>();

        public string Title { get; private set; }
        /// <summary>Size of the content body in metres (the title bar is extra).</summary>
        public Vector2 Size { get; private set; }
        public bool IsFocused { get; private set; }
        /// <summary>Parent for window content. Origin is the body centre, unscaled, just in front of the body.</summary>
        public Transform ContentRoot { get; private set; }
        /// <summary>Parent of everything drawn: its local space is the window's layout space (see <see cref="WindowCurve"/>).</summary>
        public Transform Visual => visualRoot;
        public WindowOptions Options { get; private set; }

        /// <summary>0 = flat, 1 = wrapped around a cylinder of <see cref="WindowCurve.MinRadius"/>.</summary>
        public float Curve { get; private set; }
        public float CurveRadius => WindowCurve.Radius(Curve);
        /// <summary>The fixed aspect of the content area, or Free. Resizing keeps it.</summary>
        public AspectPreset Aspect { get; private set; }

        /// <summary>Everything the window draws, in layout space: body, title bar, and the options strip when open.</summary>
        public Rect LayoutBounds
        {
            get
            {
                float top = Size.y * 0.5f + TitleBarGap + TitleBarHeight;
                float bottom = -Size.y * 0.5f - (Options != null ? Options.ExtraHeight : 0f);
                return Rect.MinMaxRect(-Size.x * 0.5f, bottom, Size.x * 0.5f, top);
            }
        }

        public bool Billboard
        {
            get => billboard;
            set { billboard = value; if (value) FaceHead(); }
        }

        public bool WorldLocked
        {
            get => worldLocked;
            set { worldLocked = value; if (!value) CaptureHeadOffset(); }
        }

        Transform head;
        Transform visualRoot;
        Transform body, titleBar, closeButton, resizeHandle;
        TextMeshPro titleText;
        Material titleMaterial;
        readonly List<Material> materials = new List<Material>();

        // Focus presentation: how far the visuals sit towards the viewer, and their scale.
        float targetDepth, currentDepth;
        float targetScale = 1f, currentScale = 1f;

        // Body-locked pose, relative to the head's yaw frame.
        Vector3 headLocalPosition;
        Quaternion headLocalRotation;

        public void Initialize(string title, Vector2 size, Material baseMaterial, Transform headTransform,
            bool useBillboard, bool useWorldLock)
        {
            Title = title;
            head = headTransform;
            billboard = useBillboard;
            name = $"Window - {title}";

            Build(baseMaterial);
            SetSize(size, keepTopLeft: false);
            FaceHead();
            WorldLocked = useWorldLock;
        }

        void Build(Material baseMaterial)
        {
            visualRoot = new GameObject("Visual").transform;
            visualRoot.SetParent(transform, false);

            body = CreateQuad("Body", visualRoot, MakeMaterial(baseMaterial, BodyColor), WindowPartType.Content);
            titleMaterial = MakeMaterial(baseMaterial, TitleColor);
            titleBar = CreateQuad("TitleBar", visualRoot, titleMaterial, WindowPartType.TitleBar);
            closeButton = CreateQuad("CloseButton", visualRoot, MakeMaterial(baseMaterial, CloseColor), WindowPartType.CloseButton);
            resizeHandle = CreateQuad("ResizeHandle", visualRoot, MakeMaterial(baseMaterial, HandleColor), WindowPartType.ResizeHandle);

            titleText = CreateText("TitleText", visualRoot, Title, 0.3f, TextAlignmentOptions.MidlineLeft);
            var closeGlyph = CreateText("CloseGlyph", closeButton, "X", 0.3f, TextAlignmentOptions.Center);
            // The close button quad is scaled; undo it so the glyph keeps its size.
            closeGlyph.transform.localScale = new Vector3(1f / CloseButtonSize, 1f / CloseButtonSize, 1f);
            closeGlyph.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            closeGlyph.rectTransform.sizeDelta = new Vector2(CloseButtonSize, CloseButtonSize);

            ContentRoot = new GameObject("Content").transform;
            ContentRoot.SetParent(visualRoot, false);
            ContentRoot.localPosition = new Vector3(0f, 0f, -ContentDepth);

            Options = gameObject.AddComponent<WindowOptions>();
            Options.Build(this, baseMaterial);
            gameObject.AddComponent<WindowCurver>();
        }

        /// <summary>Curves the window (0 = flat, 1 = the gentlest cylinder radius, <see cref="WindowCurve.MinRadius"/>).</summary>
        public void SetCurve(float curve)
        {
            curve = Mathf.Clamp01(curve);
            if (Mathf.Approximately(curve, Curve)) return;
            Curve = curve;
            curvedWindows.Remove(this);
            if (curve > 0f) curvedWindows.Add(this);
            ShapeChanged?.Invoke(this);
        }

        /// <summary>Records the aspect preset (sizing is <see cref="WindowManager.SetAspect"/>'s job).</summary>
        public void SetAspectPreset(AspectPreset preset)
        {
            if (preset == Aspect) return;
            Aspect = preset;
            ShapeChanged?.Invoke(this);
        }

        internal void RequestAspect(AspectPreset preset) => AspectRequested?.Invoke(this, preset);

        Transform CreateQuad(string quadName, Transform parent, Material material, WindowPartType partType)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = quadName;
            quad.transform.SetParent(parent, false);
            DestroyImmediate(quad.GetComponent<MeshCollider>());
            var box = quad.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 1f, ColliderDepth);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            quad.AddComponent<WindowPart>().Init(this, partType);
            return quad.transform;
        }

        static TextMeshPro CreateText(string textName, Transform parent, string text, float fontSize,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(textName);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        Material MakeMaterial(Material baseMaterial, Color color)
        {
            var material = new Material(baseMaterial) { color = color };
            materials.Add(material);
            return material;
        }

        /// <summary>
        /// Resizes the body. With keepTopLeft the body's top-left corner stays fixed in the world,
        /// which is what a bottom-right resize handle should do.
        /// </summary>
        public void SetSize(Vector2 size, bool keepTopLeft = true)
        {
            if (keepTopLeft && Size != Vector2.zero)
            {
                var topLeft = transform.position - transform.right * (Size.x * 0.5f) + transform.up * (Size.y * 0.5f);
                transform.position = topLeft + transform.right * (size.x * 0.5f) - transform.up * (size.y * 0.5f);
                if (!worldLocked) CaptureHeadOffset();
            }

            Size = size;
            Layout();
            SizeChanged?.Invoke(this);
        }

        void Layout()
        {
            float w = Size.x, h = Size.y;

            body.localPosition = Vector3.zero;
            body.localScale = new Vector3(w, h, 1f);

            float titleY = h * 0.5f + TitleBarGap + TitleBarHeight * 0.5f;
            titleBar.localPosition = new Vector3(0f, titleY, 0f);
            titleBar.localScale = new Vector3(w, TitleBarHeight, 1f);

            closeButton.localPosition = new Vector3(w * 0.5f - Margin - CloseButtonSize * 0.5f, titleY, -DecorationDepth);
            closeButton.localScale = new Vector3(CloseButtonSize, CloseButtonSize, 1f);

            // Room for the options and close buttons on the right.
            float textWidth = Mathf.Max(0.01f, w - CloseButtonSize * 2f - Margin * 4f);
            titleText.rectTransform.sizeDelta = new Vector2(textWidth, TitleBarHeight);
            titleText.transform.localPosition = new Vector3(-w * 0.5f + Margin + textWidth * 0.5f, titleY, -DecorationDepth);

            resizeHandle.localPosition = new Vector3(w * 0.5f - ResizeHandleSize * 0.5f, -h * 0.5f + ResizeHandleSize * 0.5f, -DecorationDepth);
            resizeHandle.localScale = new Vector3(ResizeHandleSize, ResizeHandleSize, 1f);

            Options.Layout(Size, titleY, w * 0.5f - Margin * 2f - CloseButtonSize * 1.5f);
        }

        /// <summary>Moves the window. Body-locked windows keep the new offset from the head.</summary>
        public void MoveTo(Vector3 position)
        {
            transform.position = position;
            if (billboard) FaceHead();
            if (!worldLocked) CaptureHeadOffset();
        }

        /// <summary>
        /// Extra text shown after the title in the title bar, e.g. a latency readout ("Laptop · 12 ms").
        /// <see cref="Title"/> itself doesn't change. Empty or null shows the title alone.
        /// </summary>
        public void SetTitleDetail(string detail)
        {
            titleText.text = string.IsNullOrEmpty(detail) ? Title : $"{Title} · {detail}";
        }

        public void SetFocused(bool focused)
        {
            IsFocused = focused;
            titleMaterial.color = focused ? TitleFocusedColor : TitleColor;
        }

        /// <summary>
        /// Sets where the visuals sit towards the viewer (metres) and their scale. The window animates
        /// there, so focusing one window grows it and brings it forward while the previous one recedes.
        /// The window's own position and size are unchanged; only its visuals (and colliders) move.
        /// </summary>
        public void SetPresentation(float depthOffset, float scale, bool instant = false)
        {
            targetDepth = depthOffset;
            targetScale = scale;
            if (instant)
            {
                currentDepth = targetDepth;
                currentScale = targetScale;
                ApplyPresentation();
            }
        }

        void ApplyPresentation()
        {
            visualRoot.localPosition = new Vector3(0f, 0f, -currentDepth);
            visualRoot.localScale = new Vector3(currentScale, currentScale, 1f);
        }

        void AnimatePresentation()
        {
            if (Mathf.Approximately(currentDepth, targetDepth) && Mathf.Approximately(currentScale, targetScale))
                return;
            float t = 1f - Mathf.Exp(-presentationSpeed * Time.deltaTime);
            currentDepth = Mathf.Abs(targetDepth - currentDepth) < 1e-4f ? targetDepth : Mathf.Lerp(currentDepth, targetDepth, t);
            currentScale = Mathf.Abs(targetScale - currentScale) < 1e-4f ? targetScale : Mathf.Lerp(currentScale, targetScale, t);
            ApplyPresentation();
        }

        void LateUpdate()
        {
            AnimatePresentation();
            if (head == null) return;

            if (!worldLocked)
            {
                var yaw = HeadYaw();
                var targetPosition = head.position + yaw * headLocalPosition;
                float t = 1f - Mathf.Exp(-followSmoothing * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, targetPosition, t);
                if (!billboard)
                    transform.rotation = Quaternion.Slerp(transform.rotation, yaw * headLocalRotation, t);
            }

            if (billboard) FaceHead();
        }

        void FaceHead()
        {
            if (head == null) return;
            var away = transform.position - head.position;
            if (billboardYawOnly) away.y = 0f;
            if (away.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        void CaptureHeadOffset()
        {
            if (head == null) return;
            var inverseYaw = Quaternion.Inverse(HeadYaw());
            headLocalPosition = inverseYaw * (transform.position - head.position);
            headLocalRotation = inverseYaw * transform.rotation;
        }

        Quaternion HeadYaw()
        {
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            return forward.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(forward, Vector3.up);
        }

        void OnDestroy()
        {
            curvedWindows.Remove(this);
            foreach (var material in materials)
                Destroy(material);
            materials.Clear();
        }
    }
}
