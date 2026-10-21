using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A floating panel: a body quad that holds content and a title bar above it. Under it sit a grab-bar pill that
    /// moves the window and round close and options controls; a round resize control sits outside the bottom-right
    /// corner. The controls fade in while the pointer is over the window (<see cref="SetPointerOver"/>). The options
    /// button opens a strip under the body with the window's curve and aspect preset (<see cref="WindowOptions"/>).
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
        // Thin, so raycasts agree with what's drawn; a thick box would reach past the 1 cm focus push-forward.
        const float ColliderDepth = 0.001f;
        // Layers in front of the body, towards the viewer (-Z).
        const float DecorationDepth = 0.002f;
        const float ContentDepth = 0.003f;

        // The shadow quad sits this far behind the body, so it sorts behind it.
        const float ShadowDepth = 0.002f;
        // Render queue of the shadow: before the glass (Transparent-50 = 2950) and every other transparent thing.
        const int ShadowQueue = 2940;
        // The title text sits this far in from the left and down from the top of the glass.
        static readonly float TitleInset = UITheme.PtToMetres(24f);
        // Height of the glass slab above the body: the gap and the title row.
        const float SlabExtra = TitleBarGap + TitleBarHeight;
        // A focused window's rim is this much stronger (same factor as the hover rim in the design spec).
        const float FocusRimBoost = 1.5f;

        static readonly Color BodyColor = new Color(0.10f, 0.10f, 0.12f);  // only if no glass material is assigned
        static readonly Color FlatControlColor = new Color(0.32f, 0.34f, 0.40f);  // controls without glass

        // Controls fade in quickly and out slowly (time constants of the exponential approach, seconds).
        const float RevealTauIn = 0.15f;
        const float RevealTauOut = 0.6f;
        // Grab bar: white at this alpha, brighter while the pointer is over the window.
        const float PillAlpha = 0.45f;
        const float PillAlphaHover = 0.8f;
        // The close control's hit area sits one minimum gap (8 mm) left of the grab bar's, the options control's one more to the left.
        // (The bar itself hangs 12 pt below the slab or the open options strip, see ChromeControl.PillRisePt.)

        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int FallbackId = Shader.PropertyToID("_Fallback");
        static readonly int RimTopId = Shader.PropertyToID("_RimTop");
        static readonly int RimBottomId = Shader.PropertyToID("_RimBottom");
        static readonly int RimWidthId = Shader.PropertyToID("_RimWidth");
        static readonly int ShadowColorId = Shader.PropertyToID("_ShadowColor");
        static readonly int ShadowBlurId = Shader.PropertyToID("_ShadowBlur");
        static readonly int ShadowOffsetId = Shader.PropertyToID("_ShadowOffset");
        static readonly int RadiusId = Shader.PropertyToID("_Radius");
        static readonly int SizeId = Shader.PropertyToID("_Size");
        static readonly int QuadId = Shader.PropertyToID("_Quad");
        static readonly int SolidId = Shader.PropertyToID("_Solid");
        static readonly int ShadowOnlyId = Shader.PropertyToID("_ShadowOnly");

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
                float hit = UITheme.PtToMetres(ChromeControl.HitPt);
                float bottom = -Size.y * 0.5f - (Options != null ? Options.ExtraHeight : 0f) - hit;
                float left = Mathf.Min(-Size.x * 0.5f, OptionsControlX - hit * 0.5f);
                return Rect.MinMaxRect(left, bottom, Size.x * 0.5f + hit, top);
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
        Transform body, slab, shadow, titleBar;
        ChromeControl closeControl, resizeControl, grabBar;
        Material chromeMaterial;
        /// <summary>The glass material the window was built with (null for a plain window); content shapes are drawn with its shader.</summary>
        public Material GlassMaterial { get; private set; }
        // Pointers (mouse, each hand) currently over the window; the window counts as hovered while any is.
        readonly HashSet<object> pointers = new HashSet<object>();
        static readonly object DefaultPointer = new object();
        bool alwaysShowControls;
        float reveal;
        TextMeshPro titleText;
        MeshRenderer bodyRenderer, slabRenderer, shadowRenderer;
        MaterialPropertyBlock glassBlock;
        bool solid;
        readonly List<Material> materials = new List<Material>();

        // Focus presentation: how far the visuals sit towards the viewer, and their scale.
        float targetDepth, currentDepth;
        float targetScale = 1f, currentScale = 1f;

        // Body-locked pose, relative to the head's yaw frame.
        Vector3 headLocalPosition;
        Quaternion headLocalRotation;

        public void Initialize(string title, Vector2 size, Material baseMaterial, Transform headTransform,
            bool useBillboard, bool useWorldLock, Material glassMaterial = null)
        {
            Title = title;
            head = headTransform;
            billboard = useBillboard;
            name = $"Window - {title}";

            Build(baseMaterial, glassMaterial);
            SetSize(size, keepTopLeft: false);
            FaceHead();
            WorldLocked = useWorldLock;
        }

        void Build(Material baseMaterial, Material glassMaterial)
        {
            visualRoot = new GameObject("Visual").transform;
            visualRoot.SetParent(transform, false);

            GlassMaterial = glassMaterial;
            bool glass = glassMaterial != null;
            body = CreateQuad("Body", visualRoot, glass ? glassMaterial : MakeMaterial(baseMaterial, BodyColor), WindowPartType.Content);
            if (glass)
            {
                glassBlock = new MaterialPropertyBlock();
                // One glass slab covers the title row and the body; the body keeps its collider and material but isn't drawn.
                bodyRenderer = body.GetComponent<MeshRenderer>();
                bodyRenderer.enabled = false;
                slab = CreateVisualQuad("Glass", glassMaterial, out slabRenderer);
                var shadowMaterial = new Material(glassMaterial) { name = "SpatialGlass Shadow", renderQueue = ShadowQueue };
                shadowMaterial.SetFloat("_ZWrite", 0f);
                materials.Add(shadowMaterial);
                shadow = CreateVisualQuad("Shadow", shadowMaterial, out shadowRenderer);
            }
            // The title band is only a hit area now: the title is drawn as text on the glass.
            titleBar = CreateQuad("TitleBar", visualRoot, baseMaterial, WindowPartType.TitleBar);
            titleBar.GetComponent<MeshRenderer>().enabled = false;

            // Round controls reuse the glass shader without depth write (so a hidden one doesn't block anything).
            if (glass)
            {
                chromeMaterial = new Material(glassMaterial) { name = "SpatialGlass Chrome" };
                chromeMaterial.SetFloat("_ZWrite", 0f);
                materials.Add(chromeMaterial);
            }
            else chromeMaterial = MakeMaterial(baseMaterial, FlatControlColor);
            var disc = new Vector2(ChromeControl.DiscPt, ChromeControl.DiscPt);
            var hitSquare = new Vector2(ChromeControl.HitPt, ChromeControl.HitPt);
            closeControl = new ChromeControl(CreateQuad("CloseButton", visualRoot, baseMaterial, WindowPartType.CloseButton),
                visualRoot, chromeMaterial, hitSquare, disc, 0f, ChromeControl.DiscFill, true, UIIcons.Close);
            resizeControl = new ChromeControl(CreateQuad("ResizeHandle", visualRoot, baseMaterial, WindowPartType.ResizeHandle),
                visualRoot, chromeMaterial, hitSquare, disc, 0f, ChromeControl.DiscFill, true, UIIcons.OpenInFull);
            grabBar = new ChromeControl(CreateQuad("GrabBar", visualRoot, baseMaterial, WindowPartType.GrabBar),
                visualRoot, chromeMaterial, new Vector2(ChromeControl.PillHitWidthPt, ChromeControl.HitPt),
                new Vector2(ChromeControl.PillWidthPt, ChromeControl.PillHeightPt), ChromeControl.PillRisePt, Color.white, false, null);
            grabBar.OutlinePt = ChromeControl.PillOutlinePt;

            titleText = CreateText("TitleText", visualRoot, Title, TextRole.Title, TextAlignmentOptions.TopLeft);

            ContentRoot = new GameObject("Content").transform;
            ContentRoot.SetParent(visualRoot, false);
            ContentRoot.localPosition = new Vector3(0f, 0f, -ContentDepth);

            Options = gameObject.AddComponent<WindowOptions>();
            Options.Build(this, baseMaterial, chromeMaterial);
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

        // A drawn-only quad (no collider) for the glass slab or its shadow.
        Transform CreateVisualQuad(string quadName, Material material, out MeshRenderer renderer)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = quadName;
            quad.transform.SetParent(visualRoot, false);
            DestroyImmediate(quad.GetComponent<MeshCollider>());
            renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return quad.transform;
        }

        /// <summary>Metres the shadow quad extends past the body on every side.</summary>
        public static float ShadowMargin =>
            UITheme.PtToMetres(UITheme.ShadowBlur) + Mathf.Abs(UITheme.PtToMetres(UITheme.ShadowOffsetY));

        public bool IsSolid => solid;

        /// <summary>
        /// Makes the window a solid dark panel instead of glass (the consent window, where clarity beats style):
        /// opaque <c>glass.fallback</c> colour with a 2 pt rim at alpha 0.5, and no shadow.
        /// </summary>
        public void SetSolid(bool value)
        {
            solid = value;
            AlwaysShowControls = value;
            UpdateGlass();
        }

        /// <summary>Keeps the close, options and resize controls visible even when the pointer is elsewhere (set by <see cref="SetSolid"/>).</summary>
        public bool AlwaysShowControls
        {
            get => alwaysShowControls;
            set { alwaysShowControls = value; ApplyReveal(); }
        }

        /// <summary>How far the pointer-over reveal has come, 0 (hidden) to 1 (shown).</summary>
        public float Reveal => reveal;

        /// <summary>Alpha the round controls are drawn with: 1 when <see cref="AlwaysShowControls"/>, else <see cref="Reveal"/>.</summary>
        public float ControlsAlpha => alwaysShowControls ? 1f : reveal;

        public bool IsPointerOver => pointers.Count > 0;

        /// <summary>Called by input every frame: true while a pointer is over the window or working on it, false once it leaves.</summary>
        public void SetPointerOver(bool over) => SetPointerOver(DefaultPointer, over);

        /// <summary>As <see cref="SetPointerOver(bool)"/> for one pointer (the mouse, a hand); the window stays hovered while any pointer is over it.</summary>
        public void SetPointerOver(object pointer, bool over)
        {
            if (over) pointers.Add(pointer);
            else pointers.Remove(pointer);
        }

        /// <summary>Moves <see cref="Reveal"/> towards the pointer state: fast in (150 ms), slow out (600 ms), whatever the frame rate.</summary>
        public void UpdateReveal(float deltaTime)
        {
            float target = IsPointerOver ? 1f : 0f;
            float tau = target > reveal ? RevealTauIn : RevealTauOut;
            reveal += (target - reveal) * (1f - Mathf.Exp(-deltaTime / tau));
            ApplyReveal();
        }

        /// <summary>Jumps the reveal to a value (the UI gallery's Hover and Idle shots) and makes the pointer state agree.</summary>
        public void SetReveal(float value)
        {
            reveal = Mathf.Clamp01(value);
            pointers.Clear();
            if (reveal >= 0.5f) pointers.Add(DefaultPointer);
            ApplyReveal();
        }

        void ApplyReveal()
        {
            if (closeControl == null || Options == null || Options.Toggle == null) return;
            float alpha = ControlsAlpha;
            closeControl.Apply(alpha);
            resizeControl.Apply(alpha);
            Options.Toggle.Apply(alpha);
            grabBar.Apply(1f, Mathf.Lerp(PillAlpha, PillAlphaHover, reveal));
        }

        public ChromeControl GrabBarControl => grabBar;
        public ChromeControl CloseControl => closeControl;
        public ChromeControl ResizeControl => resizeControl;

        // Pushes the theme values and the current size to the body and shadow renderers.
        void UpdateGlass()
        {
            if (glassBlock == null || slab == null) return;
            float boost = IsFocused ? FocusRimBoost : 1f;
            var rimTop = solid ? new Color(1f, 1f, 1f, 0.5f) : UITheme.RimTop;
            var rimBottom = solid ? rimTop : UITheme.RimBottom;
            rimTop.a = Mathf.Min(1f, rimTop.a * boost);
            rimBottom.a = Mathf.Min(1f, rimBottom.a * boost);
            float margin = ShadowMargin;
            var size = new Vector4(Size.x, Size.y + SlabExtra, 0f, 0f);

            glassBlock.Clear();
            glassBlock.SetColor(TintId, UITheme.GlassTint);
            glassBlock.SetColor(FallbackId, UITheme.GlassFallback);
            glassBlock.SetColor(RimTopId, rimTop);
            glassBlock.SetColor(RimBottomId, rimBottom);
            glassBlock.SetFloat(RimWidthId, solid ? UITheme.PtToMetres(2f) : 0f);
            glassBlock.SetColor(ShadowColorId, UITheme.Shadow);
            glassBlock.SetFloat(ShadowBlurId, UITheme.PtToMetres(UITheme.ShadowBlur));
            glassBlock.SetVector(ShadowOffsetId, new Vector4(0f, UITheme.PtToMetres(UITheme.ShadowOffsetY), 0f, 0f));
            glassBlock.SetFloat(RadiusId, UITheme.PtToMetres(UITheme.WindowRadius));
            glassBlock.SetVector(SizeId, size);
            glassBlock.SetFloat(SolidId, solid ? 1f : 0f);

            glassBlock.SetVector(QuadId, size);
            glassBlock.SetFloat(ShadowOnlyId, 0f);
            bodyRenderer.SetPropertyBlock(glassBlock);
            slabRenderer.SetPropertyBlock(glassBlock);

            glassBlock.SetVector(QuadId, new Vector4(size.x + 2f * margin, size.y + 2f * margin, 0f, 0f));
            glassBlock.SetFloat(ShadowOnlyId, 1f);
            shadowRenderer.SetPropertyBlock(glassBlock);
            shadowRenderer.enabled = !solid;
        }

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

        static TextMeshPro CreateText(string textName, Transform parent, string text, TextRole role,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(textName);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.alignment = alignment;
            ThemeApplier.Apply(tmp, role);
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
            if (slab != null)
            {
                float margin = ShadowMargin, slabHeight = h + SlabExtra, slabY = SlabExtra * 0.5f;
                slab.localPosition = new Vector3(0f, slabY, 0f);
                slab.localScale = new Vector3(w, slabHeight, 1f);
                shadow.localPosition = new Vector3(0f, slabY, ShadowDepth);
                shadow.localScale = new Vector3(w + 2f * margin, slabHeight + 2f * margin, 1f);
                UpdateGlass();
            }

            float titleY = h * 0.5f + TitleBarGap + TitleBarHeight * 0.5f;
            titleBar.localPosition = new Vector3(0f, titleY, 0f);
            titleBar.localScale = new Vector3(w, TitleBarHeight, 1f);

            float textWidth = Mathf.Max(0.01f, w - TitleInset * 2f);
            float textHeight = TitleBarHeight * 0.5f;
            titleText.rectTransform.sizeDelta = new Vector2(textWidth, textHeight);
            titleText.transform.localPosition = new Vector3(-w * 0.5f + TitleInset + textWidth * 0.5f,
                h * 0.5f + SlabExtra - TitleInset - textHeight * 0.5f, -DecorationDepth);

            Options.Layout(Size);
            LayoutChrome();
        }

        // Centre of the close control; the options control is one step further left.
        static float CloseControlX => -UITheme.PtToMetres(ChromeControl.PillHitWidthPt * 0.5f + UITheme.MinHitGapMm + ChromeControl.HitPt * 0.5f);
        static float OptionsControlX => CloseControlX - UITheme.PtToMetres(ChromeControl.HitPt + UITheme.MinHitGapMm);

        /// <summary>
        /// Places the grab bar and the close and options controls in a row under the slab (or under the open options
        /// strip), and the resize control outside the bottom-right corner. The hit areas hang from the edge they sit
        /// under; the drawn shapes sit a little higher, 12 pt below the edge.
        /// </summary>
        internal void LayoutChrome()
        {
            if (closeControl == null || Size == Vector2.zero) return;
            float hit = UITheme.PtToMetres(ChromeControl.HitPt);
            float hitY = -Size.y * 0.5f - Options.ExtraHeight - hit * 0.5f;
            grabBar.Place(0f, hitY, -DecorationDepth);
            closeControl.Place(CloseControlX, hitY, -DecorationDepth);
            Options.Toggle.Place(OptionsControlX, hitY, -DecorationDepth);
            resizeControl.Place(Size.x * 0.5f + hit * 0.5f, -Size.y * 0.5f - hit * 0.5f, -DecorationDepth);
            ApplyReveal();
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
            UpdateGlass();
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
            UpdateReveal(Time.unscaledDeltaTime);
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
            {
                // Edit-mode tests and the gallery destroy windows outside Play mode, where Destroy is not allowed.
                if (Application.isPlaying) Destroy(material);
                else DestroyImmediate(material);
            }
            materials.Clear();
        }
    }
}
