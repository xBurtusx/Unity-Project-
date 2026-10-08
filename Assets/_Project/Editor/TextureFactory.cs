using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Generates tiling surface textures procedurally and assigns them to the generated
    /// materials. Nothing here is downloaded or hand-painted, so the whole look can be re-dialled
    /// by changing a few numbers and re-running.
    ///
    /// Two things matter for this game's look:
    ///
    /// GRIME. A clean surface reads as a 3D model; an unevenly dirty one reads as a place someone
    /// lives in. Every material gets a low-frequency blotch layer that darkens and desaturates it
    /// unevenly. Dial it with <see cref="Grime"/>.
    ///
    /// TILING IN WORLD UNITS. The mesh UVs the house builder writes are in metres, so a texture
    /// set to a scale of 1 repeats once per metre no matter how big the wall is. That means a
    /// plank is the same size in the bedroom as in the garage, which is what stops a building
    /// from looking like a collage.
    /// </summary>
    public static class TextureFactory
    {
        const string TexDir = "Assets/_Project/Textures";
        const string MatDir = "Assets/_Project/Materials";
        const int Size = 512;

        /// <summary>0 = showroom clean, 1 = filthy. The brief asks for "a little bit dirty".</summary>
        const float Grime = 0.45f;

        [MenuItem("Horror House/Stage 5 - Generate Textures", priority = 60)]
        public static void Generate()
        {
            Directory.CreateDirectory(TexDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                int i = 0, total = 13;
                void Step(string name) =>
                    EditorUtility.DisplayProgressBar("Generating textures", name, i++ / (float)total);

                Step("Drywall");    Surface("Drywall",    Drywall,    0.28f, 0.08f, scale: 0.35f);
                Step("Ceiling");    Surface("Ceiling",    Ceiling,    0.22f, 0.10f, scale: 0.35f);
                Step("Siding");     Surface("Siding",     Siding,     0.30f, 0.45f, scale: 0.55f);
                Step("Trim");       Surface("Trim",       Trim,       0.40f, 0.05f, scale: 0.5f);
                Step("Wood floor"); Surface("Floor_Wood", WoodFloor,  0.45f, 0.35f, scale: 0.7f);
                Step("Tile");       Surface("Floor_Tile", TileFloor,  0.55f, 0.30f, scale: 0.8f);
                Step("Carpet");     Surface("Floor_Carpet", Carpet,   0.08f, 0.55f, scale: 1.2f);
                Step("Concrete");   Surface("Floor_Concrete", Concrete, 0.18f, 0.40f, scale: 0.5f);
                Step("Shingle");    Surface("Roof_Shingle", Shingle,  0.12f, 0.70f, scale: 0.9f);
                Step("Grass");      Surface("Grass",      Grass,      0.06f, 0.65f, scale: 1.6f);
                Step("Asphalt");    Surface("Asphalt",    Asphalt,    0.10f, 0.55f, scale: 1.1f);
                Step("Paving");     Surface("Paving",     Paving,     0.20f, 0.35f, scale: 0.6f);
                Step("Fence");      Surface("Fence_Wood", FenceWood,  0.22f, 0.50f, scale: 0.8f);
            }
            finally { EditorUtility.ClearProgressBar(); }

            AssetDatabase.Refresh();
            Debug.Log($"[TextureFactory] Generated 13 texture sets in {sw.ElapsedMilliseconds} ms " +
                      $"at grime {Grime:0.00}. Materials updated.");
        }

        // ------------------------------------------------------------ pipeline

        /// <summary>
        /// Builds the albedo and normal map for one surface and wires them into M_&lt;name&gt;.mat.
        /// </summary>
        static void Surface(string name, Func<float, float, (Color albedo, float height)> fn,
                            float smoothness, float normalStrength, float scale)
        {
            var albedo = new Texture2D(Size, Size, TextureFormat.RGBA32, true);
            var heights = new float[Size, Size];
            var pixels = new Color[Size * Size];

            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float u = x / (float)Size, v = y / (float)Size;
                    var (c, h) = fn(u, v);
                    c = ApplyGrime(c, u, v);
                    pixels[y * Size + x] = c;
                    heights[x, y] = h;
                }

            albedo.SetPixels(pixels);
            albedo.Apply();
            string albedoPath = Save(albedo, $"T_{name}_Albedo", isNormal: false);

            var normal = BuildNormal(heights, normalStrength);
            string normalPath = Save(normal, $"T_{name}_Normal", isNormal: true);

            Apply(name, albedoPath, normalPath, smoothness, scale);
        }

        /// <summary>
        /// Uneven darkening. Real dirt collects in patches, not in an even film, so this is a
        /// low-frequency blotch rather than a uniform tint - and it desaturates as well as
        /// darkens, because grime is grey.
        /// </summary>
        static Color ApplyGrime(Color c, float u, float v)
        {
            if (Grime <= 0f) return c;

            float blotch = Fbm(u, v, 3, 4);                 // broad patches
            float streak = Fbm(u * 0.5f, v * 4f, 4, 3);     // faint vertical weathering
            float dirt = Mathf.Clamp01(blotch * 0.75f + streak * 0.25f);
            dirt = Mathf.SmoothStep(0.35f, 0.95f, dirt) * Grime;

            float grey = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            var muddied = Color.Lerp(c, new Color(grey, grey, grey) * 0.72f, dirt * 0.55f);
            return new Color(muddied.r * (1f - dirt * 0.30f),
                             muddied.g * (1f - dirt * 0.30f),
                             muddied.b * (1f - dirt * 0.28f), 1f);
        }

        /// <summary>Converts a height field into a tangent-space normal map by central differences.</summary>
        static Texture2D BuildNormal(float[,] h, float strength)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true);
            var px = new Color[Size * Size];

            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    // Wrap at the edges so the normal map tiles as seamlessly as the albedo.
                    float l = h[(x - 1 + Size) % Size, y], r = h[(x + 1) % Size, y];
                    float d = h[x, (y - 1 + Size) % Size], u = h[x, (y + 1) % Size];

                    var n = new Vector3((l - r) * strength * 8f, (d - u) * strength * 8f, 1f).normalized;
                    px[y * Size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }

            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        static string Save(Texture2D tex, string name, bool isNormal)
        {
            string path = $"{TexDir}/{name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = !isNormal;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = FilterMode.Trilinear;
            imp.anisoLevel = 8;
            imp.mipmapEnabled = true;
            imp.SaveAndReimport();
            return path;
        }

        static void Apply(string name, string albedoPath, string normalPath, float smoothness, float scale)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/M_{name}.mat");
            if (mat == null)
            {
                Debug.LogWarning($"[TextureFactory] No material M_{name}.mat - build the house first.");
                return;
            }

            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath));
            mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath));
            mat.EnableKeyword("_NORMALMAP");
            mat.SetFloat("_BumpScale", 1f);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetColor("_BaseColor", Color.white);   // tint now lives in the texture
            mat.SetTextureScale("_BaseMap", Vector2.one * scale);
            mat.SetTextureScale("_BumpMap", Vector2.one * scale);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
        }

        // ------------------------------------------------------------ surfaces

        static (Color, float) Drywall(float u, float v)
        {
            // Painted plaster: almost flat, with a faint roll texture and long-period unevenness.
            float fine = Fbm(u, v, 48, 3);
            float broad = Fbm(u, v, 4, 3);
            float t = 0.88f + fine * 0.05f + broad * 0.05f;
            return (new Color(t * 0.93f, t * 0.92f, t * 0.885f), fine * 0.35f + broad * 0.1f);
        }

        static (Color, float) Ceiling(float u, float v)
        {
            float stipple = Fbm(u, v, 96, 2);
            float t = 0.90f + stipple * 0.08f;
            return (new Color(t, t, t * 0.985f), stipple);
        }

        static (Color, float) Siding(float u, float v)
        {
            // Horizontal lap boards. The shadow line under each lap is what reads as siding.
            const float boards = 7f;
            float row = v * boards;
            float within = row - Mathf.Floor(row);
            float lapShadow = within < 0.09f ? Mathf.SmoothStep(0f, 1f, within / 0.09f) : 1f;

            float grain = Fbm(u * 3f, v * boards, 40, 3);
            float perBoard = Hash01(Mathf.FloorToInt(row), 17) * 0.05f;
            float t = (0.60f + perBoard + grain * 0.05f) * (0.55f + 0.45f * lapShadow);

            return (new Color(t * 0.92f, t * 0.95f, t), lapShadow * 0.7f + grain * 0.3f);
        }

        static (Color, float) Trim(float u, float v)
        {
            float g = Fbm(u * 2f, v * 20f, 60, 2);
            float t = 0.93f + g * 0.05f;
            return (new Color(t, t, t * 0.97f), g * 0.4f);
        }

        static (Color, float) WoodFloor(float u, float v)
        {
            // Planks running along U, staggered end joints, per-plank tone variation.
            const float planks = 6f;
            float row = v * planks;
            int plankIndex = Mathf.FloorToInt(row);
            float within = row - plankIndex;

            float offset = Hash01(plankIndex, 3) * 0.5f;
            float along = u + offset;
            int boardIndex = Mathf.FloorToInt(along * 2.2f);

            float tone = 0.72f + Hash01(plankIndex * 131 + boardIndex, 7) * 0.28f;
            float grain = Fbm(along * 14f, row * 1.6f, 64, 4);
            float joint = (within < 0.035f || within > 0.965f) ? 0.45f : 1f;
            float endJoint = Mathf.Abs(along * 2.2f - Mathf.Round(along * 2.2f)) < 0.012f ? 0.5f : 1f;

            float t = tone * (0.80f + grain * 0.30f) * joint * endJoint;
            return (new Color(t * 0.52f, t * 0.35f, t * 0.21f), grain * 0.5f + joint * 0.5f);
        }

        static (Color, float) TileFloor(float u, float v)
        {
            const float tiles = 4f;
            float gx = Mathf.Abs((u * tiles) % 1f - 0.5f);
            float gy = Mathf.Abs((v * tiles) % 1f - 0.5f);
            bool grout = gx > 0.47f || gy > 0.47f;

            int id = Mathf.FloorToInt(u * tiles) * 31 + Mathf.FloorToInt(v * tiles);
            float tone = 0.70f + Hash01(id, 11) * 0.12f;
            float mottle = Fbm(u * 6f, v * 6f, 32, 3);

            float t = grout ? 0.42f : tone * (0.90f + mottle * 0.18f);
            return (new Color(t, t * 0.985f, t * 0.95f), grout ? 0f : 0.7f + mottle * 0.3f);
        }

        static (Color, float) Carpet(float u, float v)
        {
            float fibre = Fbm(u, v, 180, 3);
            float mottle = Fbm(u, v, 7, 3);
            float t = 0.34f + fibre * 0.14f + mottle * 0.10f;
            return (new Color(t * 1.02f, t * 0.95f, t * 0.88f), fibre);
        }

        static (Color, float) Concrete(float u, float v)
        {
            float mottle = Fbm(u, v, 9, 4);
            float speck = Fbm(u, v, 150, 2);
            float t = 0.46f + mottle * 0.14f + speck * 0.07f;
            return (new Color(t, t * 0.995f, t * 0.97f), speck * 0.6f + mottle * 0.4f);
        }

        static (Color, float) Shingle(float u, float v)
        {
            // Rows of tabs, every other row offset by half a tab.
            const float rows = 16f, tabs = 10f;
            float row = v * rows;
            int rowIndex = Mathf.FloorToInt(row);
            float within = row - rowIndex;

            float shift = (rowIndex % 2 == 0) ? 0f : 0.5f;
            float col = (u + shift / tabs) * tabs;
            int tabIndex = Mathf.FloorToInt(col);
            float withinTab = col - tabIndex;

            float butt = within < 0.12f ? 0.45f : 1f;                   // shadow under each course
            float slot = Mathf.Abs(withinTab - 0.5f) > 0.48f ? 0.55f : 1f;
            float tone = 0.80f + Hash01(rowIndex * 71 + tabIndex, 5) * 0.20f;
            float grit = Fbm(u * 20f, v * 20f, 128, 2);

            float t = 0.30f * tone * butt * slot * (0.85f + grit * 0.30f);
            return (new Color(t, t * 0.97f, t * 0.94f), butt * 0.6f + grit * 0.4f);
        }

        static (Color, float) Grass(float u, float v)
        {
            float blades = Fbm(u, v, 140, 3);
            float patch = Fbm(u, v, 5, 3);
            float dry = Mathf.SmoothStep(0.55f, 0.9f, patch);

            var green = new Color(0.20f + blades * 0.12f, 0.33f + blades * 0.16f, 0.13f + blades * 0.08f);
            var straw = new Color(0.38f, 0.34f, 0.17f);
            var c = Color.Lerp(green, straw, dry * 0.5f);
            return (c, blades);
        }

        static (Color, float) Asphalt(float u, float v)
        {
            float aggregate = Fbm(u, v, 110, 3);
            float wear = Fbm(u, v, 6, 3);
            float t = 0.17f + aggregate * 0.11f + wear * 0.05f;
            return (new Color(t, t * 1.01f, t * 1.04f), aggregate);
        }

        static (Color, float) Paving(float u, float v)
        {
            // Poured concrete with control joints, like a real path or driveway.
            const float slabs = 3f;
            float gx = Mathf.Abs((u * slabs) % 1f - 0.5f);
            bool joint = gx > 0.492f;
            float mottle = Fbm(u, v, 11, 4);
            float t = (0.52f + mottle * 0.14f) * (joint ? 0.72f : 1f);
            return (new Color(t, t * 0.99f, t * 0.96f), joint ? 0f : mottle);
        }

        static (Color, float) FenceWood(float u, float v)
        {
            const float boards = 9f;
            float col = u * boards;
            int boardIndex = Mathf.FloorToInt(col);
            float within = col - boardIndex;

            float gap = (within < 0.03f || within > 0.97f) ? 0.35f : 1f;
            float tone = 0.72f + Hash01(boardIndex, 23) * 0.28f;
            float grain = Fbm(u * boards * 0.4f, v * 10f, 80, 4);

            float t = tone * gap * (0.80f + grain * 0.32f);
            return (new Color(t * 0.52f, t * 0.40f, t * 0.28f), grain * 0.6f + gap * 0.4f);
        }

        // ------------------------------------------------------------ noise

        /// <summary>
        /// Value noise on a wrapping lattice. The wrap is what makes the result tile: sample
        /// coordinates are taken modulo the period, so the right edge meets the left exactly.
        /// </summary>
        static float Noise(float x, float y, int period)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            int xa = Mod(x0, period), xb = Mod(x0 + 1, period);
            int ya = Mod(y0, period), yb = Mod(y0 + 1, period);

            float v00 = Hash01(xa * 73856093 ^ ya * 19349663, period);
            float v10 = Hash01(xb * 73856093 ^ ya * 19349663, period);
            float v01 = Hash01(xa * 73856093 ^ yb * 19349663, period);
            float v11 = Hash01(xb * 73856093 ^ yb * 19349663, period);

            return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
        }

        static float Fbm(float u, float v, int period, int octaves)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            int p = period;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Noise(u * p, v * p, p);
                norm += amp;
                amp *= 0.5f;
                p *= 2;
            }
            return sum / Mathf.Max(norm, 0.0001f);
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;

        static float Hash01(int a, int seed)
        {
            unchecked
            {
                uint h = (uint)(a * 374761393 + seed * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }
    }
}
