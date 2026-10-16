using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Draws a window curved (<see cref="SpatialWindow.Curve"/>, <see cref="WindowCurve"/>). Every quad under the
    /// window's visual root is redrawn as a strip of columns bent onto the cylinder, and every TextMeshPro label's
    /// character quads are bent. Only what is drawn changes: transforms and colliders stay flat in layout space, which is
    /// what content layout and <see cref="WindowRaycast"/> rely on. A quad or label is re-bent when its place in the
    /// layout or the curve changes; at curve 0 the original meshes come back.
    /// <para>
    /// Text is bent as TextMeshPro generates it (<see cref="TMP_Text.OnPreRenderText"/>), where a copy of the flat
    /// vertices is kept; a curve or layout change re-bends that copy and uploads it, without regenerating the text, so
    /// bending never compounds. Only visible characters are bent, found through each character's material (mesh) and
    /// vertex index: TextMeshPro's per-mesh <c>vertexCount</c> can't be trusted (for a label cut short with an
    /// ellipsis it can report more vertices than the mesh has). Underline, strikethrough and highlight geometry isn't
    /// bent; no window uses them.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(SpatialWindow))]
    public class WindowCurver : MonoBehaviour
    {
        /// <summary>Columns are about this wide (metres), so a 0.6 m body is 40 straight segments.</summary>
        const float ColumnWidth = 0.015f;
        const int MaxColumns = 64;
        const float MatrixTolerance = 1e-5f;

        sealed class Bent
        {
            public Mesh Original, Mesh;
            public Matrix4x4 Placement;
            public float Radius;
        }

        sealed class Label
        {
            public System.Action<TMP_TextInfo> Hook;
            public Matrix4x4 Placement;
            public float Radius;
            /// <summary>False until the label's text has been generated (or found generated) while hooked.</summary>
            public bool Captured;
            /// <summary>Captured during the generation now finishing (see <see cref="OnAnyTextChanged"/>).</summary>
            public bool Fresh;
            /// <summary>The flat vertices of the last generated text, per mesh (main mesh, then fallback sub-meshes).</summary>
            public Vector3[][] Flat = new Vector3[0][];
            /// <summary>The first vertex of each visible character's quad: (mesh, vertex).</summary>
            public readonly List<(int mesh, int vertex)> Quads = new List<(int, int)>();
        }

        SpatialWindow window;
        readonly Dictionary<MeshFilter, Bent> quads = new Dictionary<MeshFilter, Bent>();
        readonly Dictionary<TMP_Text, Label> labels = new Dictionary<TMP_Text, Label>();
        readonly List<MeshFilter> filterBuffer = new List<MeshFilter>();
        readonly List<TMP_Text> labelBuffer = new List<TMP_Text>();
        readonly List<MeshFilter> staleQuads = new List<MeshFilter>();
        readonly List<TMP_Text> staleLabels = new List<TMP_Text>();

        /// <summary>How many quads are currently drawn bent (for tests and diagnostics).</summary>
        public int BentQuadCount => quads.Count;

        /// <summary>The bent mesh drawn for a quad, or null while it is flat.</summary>
        public Mesh BentMeshOf(MeshFilter filter) => quads.TryGetValue(filter, out var bent) ? bent.Mesh : null;

        System.Action<Object> textChanged;

        void Awake() => window = GetComponent<SpatialWindow>();

        void OnEnable() => TMPro_EventManager.TEXT_CHANGED_EVENT.Add(textChanged ??= OnAnyTextChanged);

        void OnDisable() => TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(textChanged);

        void LateUpdate()
        {
            var visual = window.Visual;
            if (visual == null) return;
            if (window.Curve <= 0f)
            {
                if (quads.Count > 0 || labels.Count > 0) RestoreAll();
                return;
            }

            float radius = window.CurveRadius;
            var toLayout = visual.worldToLocalMatrix;

            visual.GetComponentsInChildren(true, filterBuffer);
            foreach (var filter in filterBuffer)
            {
                if (filter == null) continue;
                var placement = toLayout * filter.transform.localToWorldMatrix;
                if (!quads.TryGetValue(filter, out var bent))
                {
                    // Text meshes are bent through TextMeshPro below (a one-letter label also has 4 vertices).
                    if (!IsQuad(filter.sharedMesh) || filter.TryGetComponent<TMP_Text>(out _) || filter.TryGetComponent<TMP_SubMesh>(out _))
                        continue;
                    bent = new Bent { Original = filter.sharedMesh, Mesh = new Mesh { name = "Curved quad" } };
                    bent.Mesh.MarkDynamic();
                    quads[filter] = bent;
                    Rebuild(filter, bent, placement, radius);
                }
                else if (bent.Radius != radius || !Same(bent.Placement, placement))
                {
                    Rebuild(filter, bent, placement, radius);
                }
            }

            visual.GetComponentsInChildren(true, labelBuffer);
            foreach (var text in labelBuffer)
            {
                if (text == null) continue;
                if (!labels.TryGetValue(text, out var label))
                {
                    var target = text;
                    var added = label = new Label();
                    label.Hook = info => OnTextGenerated(target, added, info);
                    text.OnPreRenderText += label.Hook;
                    labels[text] = label;
                    // Text generated before it was hooked: TextMeshPro's text info still holds it, flat.
                    if (text.textInfo != null && text.textInfo.characterCount > 0) Capture(label, text.textInfo);
                }
                // Not generated yet, or hidden: it is bent when TextMeshPro next generates it.
                if (!label.Captured || !text.isActiveAndEnabled) continue;
                var placement = toLayout * text.transform.localToWorldMatrix;
                if (label.Radius == radius && Same(label.Placement, placement)) continue;
                label.Placement = placement;
                label.Radius = radius;
                Bend(label, text.textInfo);
                text.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
            }

            Forget();
        }

        // Drops entries whose objects were destroyed (content rebuilt, windows closed).
        void Forget()
        {
            staleQuads.Clear();
            foreach (var pair in quads)
                if (pair.Key == null) staleQuads.Add(pair.Key);
            foreach (var key in staleQuads)
            {
                Destroy(quads[key].Mesh);
                quads.Remove(key);
            }
            staleLabels.Clear();
            foreach (var pair in labels)
                if (pair.Key == null) staleLabels.Add(pair.Key);
            foreach (var key in staleLabels) labels.Remove(key);
        }

        void Rebuild(MeshFilter filter, Bent bent, Matrix4x4 placement, float radius)
        {
            bent.Placement = placement;
            bent.Radius = radius;
            float width = placement.MultiplyVector(Vector3.right).magnitude;
            int columns = Mathf.Clamp(Mathf.CeilToInt(width / ColumnWidth), 1, MaxColumns);
            var fromLayout = placement.inverse;

            int count = (columns + 1) * 2;
            var vertices = new Vector3[count];
            var uvs = new Vector2[count];
            var normals = new Vector3[count];
            var triangles = new int[columns * 6];
            for (int i = 0; i <= columns; i++)
            {
                float u = i / (float)columns;
                for (int j = 0; j < 2; j++)
                {
                    int index = i * 2 + j;
                    var layout = placement.MultiplyPoint3x4(new Vector3(u - 0.5f, j - 0.5f, 0f));
                    vertices[index] = fromLayout.MultiplyPoint3x4(WindowCurve.Bend(layout, radius));
                    normals[index] = fromLayout.MultiplyVector(WindowCurve.Normal(layout, radius)).normalized;
                    uvs[index] = new Vector2(u, j);
                }
                if (i == columns) continue;
                // Same winding as Unity's quad (bottom-left, top-right, bottom-right; top-right, bottom-left, top-left).
                int bl = i * 2, tl = bl + 1, br = bl + 2, tr = bl + 3, t = i * 6;
                triangles[t] = bl; triangles[t + 1] = tr; triangles[t + 2] = br;
                triangles[t + 3] = tr; triangles[t + 4] = bl; triangles[t + 5] = tl;
            }

            var mesh = bent.Mesh;
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            filter.sharedMesh = mesh;
        }

        // Runs while TextMeshPro generates the label's mesh, before it is uploaded: the vertices are new and flat.
        void OnTextGenerated(TMP_Text text, Label label, TMP_TextInfo info)
        {
            Capture(label, info);
            label.Fresh = true;
            if (window == null || window.Curve <= 0f || window.Visual == null) return;
            label.Placement = window.Visual.worldToLocalMatrix * text.transform.localToWorldMatrix;
            label.Radius = window.CurveRadius;
            Bend(label, info);
        }

        // Runs after every generation of any text, including ones that skip OnPreRenderText (text set to empty, or
        // generated while inactive): a label generated without being captured has a stale copy.
        void OnAnyTextChanged(Object changed)
        {
            if (!(changed is TMP_Text text) || !labels.TryGetValue(text, out var label)) return;
            if (label.Fresh) label.Fresh = false;
            else
            {
                label.Captured = false;
                label.Quads.Clear();
            }
        }

        // Keeps a copy of the flat vertices and where each visible character's quad is.
        static void Capture(Label label, TMP_TextInfo info)
        {
            int meshes = Mathf.Min(info.materialCount, info.meshInfo.Length);
            if (label.Flat.Length < meshes) System.Array.Resize(ref label.Flat, meshes);
            for (int m = 0; m < meshes; m++)
            {
                var vertices = info.meshInfo[m].vertices;
                if (vertices == null) continue;
                if (label.Flat[m] == null || label.Flat[m].Length < vertices.Length) label.Flat[m] = new Vector3[vertices.Length];
                System.Array.Copy(vertices, label.Flat[m], vertices.Length);
            }
            label.Quads.Clear();
            var characters = info.characterInfo;
            for (int c = 0; c < info.characterCount && c < characters.Length; c++)
            {
                if (!characters[c].isVisible) continue;
                int m = characters[c].materialReferenceIndex, v = characters[c].vertexIndex;
                if (m < 0 || m >= meshes || v < 0 || label.Flat[m] == null || v + 3 >= label.Flat[m].Length) continue;
                label.Quads.Add((m, v));
            }
            label.Captured = true;
        }

        // Writes the label's flat copy, bent for its placement and radius (flat at an infinite radius), into the text
        // info's vertex buffers.
        static void Bend(Label label, TMP_TextInfo info)
        {
            var placement = label.Placement;
            var fromLayout = placement.inverse;
            float radius = label.Radius;
            bool flat = float.IsInfinity(radius);
            foreach (var (m, v) in label.Quads)
            {
                if (m >= info.meshInfo.Length) continue;
                var source = label.Flat[m];
                var target = info.meshInfo[m].vertices;
                if (target == null || v + 3 >= target.Length) continue;
                for (int k = v; k < v + 4; k++)
                    target[k] = flat ? source[k] : fromLayout.MultiplyPoint3x4(WindowCurve.Bend(placement.MultiplyPoint3x4(source[k]), radius));
            }
        }

        void RestoreAll()
        {
            foreach (var pair in quads)
            {
                if (pair.Key != null) pair.Key.sharedMesh = pair.Value.Original;
                Destroy(pair.Value.Mesh);
            }
            quads.Clear();
            foreach (var pair in labels)
            {
                var text = pair.Key;
                if (text == null) continue;
                text.OnPreRenderText -= pair.Value.Hook;
                if (!pair.Value.Captured) continue;
                pair.Value.Radius = float.PositiveInfinity;
                Bend(pair.Value, text.textInfo);
                text.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
            }
            labels.Clear();
        }

        // Unity's built-in quad (GameObject.CreatePrimitive), which all window parts are drawn with.
        static bool IsQuad(Mesh mesh) => mesh != null && mesh.name == "Quad";

        static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > MatrixTolerance) return false;
            return true;
        }

        void OnDestroy()
        {
            foreach (var bent in quads.Values)
                if (bent.Mesh != null) Destroy(bent.Mesh);
            quads.Clear();
            foreach (var pair in labels)
                if (pair.Key != null) pair.Key.OnPreRenderText -= pair.Value.Hook;
            labels.Clear();
        }
    }
}
