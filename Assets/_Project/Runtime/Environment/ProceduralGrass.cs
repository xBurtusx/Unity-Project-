using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HorrorHouse
{
    /// <summary>
    /// A lawn of real grass blades, generated when the component enables.
    ///
    /// The blades are built at runtime (and in the editor, because of ExecuteAlways) rather than
    /// saved into the scene. A lawn this size is hundreds of thousands of vertices, and baking
    /// that into the scene file would bloat it and make every diff meaningless. Only the handful
    /// of numbers below are serialized; the meshes are hidden, unsaved children that vanish on
    /// disable.
    ///
    /// Each blade is a bent, tapered ribbon of three triangles. The material samples a small
    /// gradient texture - U picks a tint variant per blade, V runs dark at the root to pale at the
    /// tip - so one material gives a lawn that is not a single flat green.
    /// </summary>
    [ExecuteAlways]
    public class ProceduralGrass : MonoBehaviour
    {
        [Header("Area (metres, local space)")]
        public Vector2 min = new Vector2(-10, -10);
        public Vector2 max = new Vector2(10, 10);
        [Tooltip("Rectangles (xMin, zMin, xMax, zMax) where no grass grows: the house, paving, porches.")]
        public List<Vector4> exclusions = new List<Vector4>();
        public float groundY = 0f;

        [Header("Look")]
        [Range(20f, 400f)] public float bladesPerSquareMetre = 140f;
        public Vector2 heightRange = new Vector2(0.14f, 0.34f);
        public float bladeWidth = 0.028f;
        public float chunkSize = 8f;
        public int seed = 8102026;
        public Color rootColour = new Color(0.035f, 0.06f, 0.03f);
        public Color tipColour  = new Color(0.30f, 0.38f, 0.20f);

        const string ChildName = "~GrassChunks";
        Material material;
        Texture2D texture;

        void OnEnable() => Rebuild();
        void OnDisable() => Clear();

        void Clear()
        {
            var t = transform.Find(ChildName);
            if (t != null) Kill(t.gameObject);
            if (material != null) Kill(material);
            if (texture != null) Kill(texture);
            material = null; texture = null;
        }

        static void Kill(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
        }

        bool Excluded(float x, float z)
        {
            for (int i = 0; i < exclusions.Count; i++)
            {
                var e = exclusions[i];
                if (x > e.x && x < e.z && z > e.y && z < e.w) return true;
            }
            return false;
        }

        [ContextMenu("Rebuild Grass")]
        public void Rebuild()
        {
            Clear();
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return;

            texture = BuildTexture();
            material = new Material(shader) { name = "M_GrassBlades_Runtime", hideFlags = HideFlags.HideAndDontSave };
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Cull", 0f);          // two-sided
            material.SetFloat("_Smoothness", 0.08f);
            material.SetFloat("_Metallic", 0f);

            var root = new GameObject(ChildName) { hideFlags = HideFlags.HideAndDontSave };
            root.transform.SetParent(transform, false);

            var rng = new System.Random(seed);
            int cellsX = Mathf.CeilToInt((max.x - min.x) / chunkSize);
            int cellsZ = Mathf.CeilToInt((max.y - min.y) / chunkSize);

            var verts = new List<Vector3>(); var norms = new List<Vector3>();
            var uvs = new List<Vector2>(); var tris = new List<int>();

            for (int cz = 0; cz < cellsZ; cz++)
            for (int cx = 0; cx < cellsX; cx++)
            {
                float x0 = min.x + cx * chunkSize, z0 = min.y + cz * chunkSize;
                float x1 = Mathf.Min(x0 + chunkSize, max.x), z1 = Mathf.Min(z0 + chunkSize, max.y);
                // Oversample, then thin by a slowly varying noise field: real lawn is thick in some
                // places and thin or bare in others, never a uniform carpet.
                int n = Mathf.RoundToInt((x1 - x0) * (z1 - z0) * bladesPerSquareMetre * 2.2f);
                verts.Clear(); norms.Clear(); uvs.Clear(); tris.Clear();

                // Blades grow in clumps: a share of them are scattered around a few centres.
                int clumps = Mathf.Max(1, n / 40);
                var cxs = new float[clumps]; var czs = new float[clumps];
                for (int k = 0; k < clumps; k++)
                {
                    cxs[k] = Mathf.Lerp(x0, x1, (float)rng.NextDouble());
                    czs[k] = Mathf.Lerp(z0, z1, (float)rng.NextDouble());
                }

                for (int i = 0; i < n; i++)
                {
                    float x, z;
                    if (rng.NextDouble() < 0.4)
                    {
                        int k = rng.Next(clumps);
                        float r = 0.28f * Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(0.001f, (float)rng.NextDouble())));
                        float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                        x = cxs[k] + Mathf.Cos(a) * r; z = czs[k] + Mathf.Sin(a) * r;
                    }
                    else
                    {
                        x = Mathf.Lerp(x0, x1, (float)rng.NextDouble());
                        z = Mathf.Lerp(z0, z1, (float)rng.NextDouble());
                    }
                    if (Excluded(x, z)) continue;

                    float o = (seed % 1000) * 0.37f;   // keeps the noise coordinates small enough to stay precise
                    float thick = Mathf.PerlinNoise(x * 0.33f + o, z * 0.33f + o);          // patches a few metres across
                    float fine  = Mathf.PerlinNoise(x * 1.9f + o * 2f, z * 1.9f - o);       // small-scale unevenness
                    float keep = Mathf.Lerp(0.18f, 1f, Mathf.SmoothStep(0.12f, 0.7f, thick)) * Mathf.Lerp(0.6f, 1f, fine) * 0.62f;
                    if (rng.NextDouble() > keep) continue;

                    float tall = Mathf.Lerp(0.55f, 1.45f, Mathf.PerlinNoise(x * 0.17f - o, z * 0.17f + o));
                    bool weed = rng.NextDouble() < 0.02;
                    float dryField = Mathf.PerlinNoise(x * 0.11f + 90f + o, z * 0.11f + 40f);
                    bool dry = rng.NextDouble() < (dryField > 0.62f ? 0.8 : dryField > 0.5f ? 0.22 : 0.04);
                    AddBlade(rng, x, z, tall * (weed ? 2.2f : 1f), dry, verts, norms, uvs, tris);
                }
                if (verts.Count == 0) continue;

                var mesh = new Mesh { name = $"Grass_{cx}_{cz}", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
                mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();

                var go = new GameObject(mesh.name) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = ShadowCastingMode.Off;   // far cheaper, and invisible at night
                mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
        }

        void AddBlade(System.Random rng, float x, float z, float heightScale, bool dry, List<Vector3> v, List<Vector3> nrm, List<Vector2> uv, List<int> t)
        {
            float h = Mathf.Lerp(heightRange.x, heightRange.y, (float)rng.NextDouble()) * heightScale;
            float w = bladeWidth * Mathf.Lerp(0.7f, 1.2f, (float)rng.NextDouble());
            float yaw = (float)rng.NextDouble() * Mathf.PI * 2f;
            float lean = h * Mathf.Lerp(0.05f, 0.45f, (float)rng.NextDouble());
            float leanYaw = (float)rng.NextDouble() * Mathf.PI * 2f;

            var side = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw)) * (w * 0.5f);
            var bend = new Vector3(Mathf.Cos(leanYaw), 0, Mathf.Sin(leanYaw)) * lean;
            var root = new Vector3(x, groundY - 0.03f, z);

            float u = (dry ? 0.52f : 0.02f) + (float)rng.NextDouble() * 0.46f;   // left half of the texture is green, right half dry
            int b = v.Count;

            v.Add(root - side);                                                   uv.Add(new Vector2(u, 0f));
            v.Add(root + side);                                                   uv.Add(new Vector2(u, 0f));
            v.Add(root + Vector3.up * (h * 0.55f) + bend * 0.25f - side * 0.7f);  uv.Add(new Vector2(u, 0.55f));
            v.Add(root + Vector3.up * (h * 0.55f) + bend * 0.25f + side * 0.7f);  uv.Add(new Vector2(u, 0.55f));
            v.Add(root + Vector3.up * h + bend);                                  uv.Add(new Vector2(u, 1f));
            for (int i = 0; i < 5; i++) nrm.Add(Vector3.up);   // lit like a lawn, not like thousands of flat cards

            t.Add(b);     t.Add(b + 2); t.Add(b + 1);
            t.Add(b + 1); t.Add(b + 2); t.Add(b + 3);
            t.Add(b + 2); t.Add(b + 4); t.Add(b + 3);
        }

        Texture2D BuildTexture()
        {
            const int W = 16, H = 16;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave, name = "T_GrassBlade_Runtime"
            };
            var rng = new System.Random(seed + 1);
            for (int x = 0; x < W; x++)
            {
                // Per-variant shift: some blades drier and yellower, some brighter or darker.
                bool dryHalf = x >= W / 2;
                float dry = dryHalf ? 0.75f + (float)rng.NextDouble() * 0.25f : (float)rng.NextDouble() * 0.25f;
                var tipV = Color.Lerp(tipColour, new Color(0.62f, 0.55f, 0.28f), dry * 0.85f);
                var rootV = Color.Lerp(rootColour, new Color(0.20f, 0.17f, 0.08f), dry * 0.7f);
                float bright = Mathf.Lerp(0.8f, 1.15f, (float)rng.NextDouble());
                for (int y = 0; y < H; y++)
                {
                    float f = y / (float)(H - 1);
                    var c = Color.Lerp(rootV, tipV, Mathf.SmoothStep(0f, 1f, f)) * bright;
                    c.a = 1f;
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply(false);
            return tex;
        }
    }
}
