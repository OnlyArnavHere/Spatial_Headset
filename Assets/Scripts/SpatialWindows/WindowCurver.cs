using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Draws a window curved (<see cref="SpatialWindow.Curve"/>, <see cref="WindowCurve"/>). Every quad under the
    /// window's visual root is redrawn as a strip of columns bent onto the cylinder, and every TextMeshPro label's
    /// vertices are bent as the text is generated. Only what is drawn changes: transforms and colliders stay flat in
    /// layout space, which is what content layout and <see cref="WindowRaycast"/> rely on. A quad or label is re-bent
    /// when its place in the layout or the curve changes; at curve 0 the original meshes come back.
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

        void Awake() => window = GetComponent<SpatialWindow>();

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
                var placement = toLayout * text.transform.localToWorldMatrix;
                if (!labels.TryGetValue(text, out var label))
                {
                    var target = text;
                    label = new Label();
                    label.Hook = info => BendText(target, info);
                    text.OnPreRenderText += label.Hook;
                    labels[text] = label;
                }
                else if (label.Radius == radius && Same(label.Placement, placement)) continue;
                label.Placement = placement;
                label.Radius = radius;
                text.ForceMeshUpdate();
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

        // Runs while TextMeshPro generates the label's mesh, before it is uploaded.
        void BendText(TMP_Text text, TMP_TextInfo info)
        {
            if (window == null || window.Curve <= 0f || window.Visual == null) return;
            float radius = window.CurveRadius;
            var placement = window.Visual.worldToLocalMatrix * text.transform.localToWorldMatrix;
            var fromLayout = placement.inverse;
            for (int m = 0; m < info.meshInfo.Length; m++)
            {
                var vertices = info.meshInfo[m].vertices;
                if (vertices == null) continue;
                int used = info.meshInfo[m].vertexCount;
                for (int v = 0; v < used; v++)
                    vertices[v] = fromLayout.MultiplyPoint3x4(WindowCurve.Bend(placement.MultiplyPoint3x4(vertices[v]), radius));
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
                if (pair.Key == null) continue;
                pair.Key.OnPreRenderText -= pair.Value.Hook;
                pair.Key.ForceMeshUpdate();
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
