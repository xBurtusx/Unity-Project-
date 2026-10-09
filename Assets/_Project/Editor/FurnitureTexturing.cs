using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Gives the flat-coloured Kenney furniture real surfaces.
    ///
    /// The kit's materials are plain colours named by kind (furn_wood, furn_woodDark, carpetWhite,
    /// metalDark ...). Each is replaced by a material that keeps the same colour as a tint but
    /// adds a real photographed texture, so a table stays the brown it was and gains grain.
    ///
    /// Two details matter:
    ///  * Palette UVs: the kit's UVs point every face at a swatch, so nothing tiles. Textured
    ///    pieces get a copy of their mesh with box-projected UVs in world metres, so grain is the
    ///    same size on a chair as on a table. Originals are never modified.
    ///  * Tint compensation: a dark texture multiplied by a mid-brown goes near-black. Each tint is
    ///    divided by the texture's average colour first, so the piece keeps its overall colour and
    ///    only gains variation.
    ///
    /// Metals are not textured. The available metal textures are rusty industrial plate, which
    /// would make a fridge look filthy; instead they get believable enamel/steel shine.
    /// </summary>
    public static class FurnitureTexturing
    {
        const string TexDir  = "Assets/_Project/Textures/PolyHaven";
        const string MatOut  = "Assets/_Project/Materials/Furniture";
        const string MeshOut = "Assets/_Project/Meshes/Furniture";

        class Look
        {
            public string set;                 // texture set, or null for a plain (untextured) material
            public float tile = 1f;            // metres per texture repeat
            public bool gray;                  // use only the texture's light/dark, not its colours
            public float metallic, smooth;     // plain materials only
            public bool Textured => set != null;
        }

        static readonly Dictionary<string, Color> avgCache = new Dictionary<string, Color>();

        [MenuItem("Horror House/Texture Furniture", priority = 76)]
        public static void Run()
        {
            EnsureFolder("Assets/_Project/Materials", "Furniture");
            EnsureFolder("Assets/_Project", "Meshes");
            EnsureFolder("Assets/_Project/Meshes", "Furniture");
            RealismPass.ImportTextures();      // configures importers and builds mask maps for every set

            int renderers = 0, meshes = 0;
            var usedMats = new HashSet<Material>();
            var usedMeshes = new HashSet<Mesh>();

            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                string path = PathOf(mr.transform);
                if (!path.Contains("Interior")) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var mats = mr.sharedMaterials;
                var next = (Material[])mats.Clone();
                bool changed = false, needUv = false;
                float tile = 1f;

                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    var look = Classify(m);
                    if (look == null) continue;

                    Material made = look.Textured ? TexturedMaterial(m, look) : PlainMaterial(m, look);
                    if (made == null) continue;
                    next[i] = made;
                    usedMats.Add(made);
                    changed = true;
                    if (look.Textured && !needUv) { needUv = true; tile = look.tile; }
                }
                if (!changed) continue;

                if (needUv)
                {
                    var src = mf.sharedMesh;
                    if (!AssetDatabase.GetAssetPath(src).StartsWith(MeshOut))       // not already one of ours
                    {
                        var made = TexturedMesh(src, mr.transform.lossyScale, tile);
                        Undo.RecordObject(mf, "Texture furniture mesh");
                        mf.sharedMesh = made;
                        usedMeshes.Add(made);
                        EditorUtility.SetDirty(mf);
                    }
                    else usedMeshes.Add(src);
                }

                Undo.RecordObject(mr, "Texture furniture materials");
                mr.sharedMaterials = next;
                EditorUtility.SetDirty(mr);
                renderers++;
            }
            meshes = usedMeshes.Count;
            RetintCarpetFloor();

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Furniture] Retextured {renderers} renderers using {usedMats.Count} materials and {meshes} UV-mapped meshes.");
        }

        // ------------------------------------------------------------ classification

        static Look Classify(Material m)
        {
            string n = m.name.ToLowerInvariant();
            if (n.StartsWith("furn_wooddark")) return new Look { set = "dark_wood", tile = 1.2f };
            if (n.StartsWith("furn_wood")) return new Look { set = "oak_veneer_01", tile = 1.2f };
            // Fabric and carpet textures carry their own coloured threads; tinted to a piece's colour
            // those read as stray yellow stitching. Grey keeps the weave and drops the colour.
            if (n.StartsWith("furn_carpet")) return new Look { set = "dirty_carpet", tile = 0.7f, gray = true };
            if (n.StartsWith("carpetwhite")) return new Look { set = "knitted_fleece", tile = 0.3f, gray = true };
            if (n.StartsWith("metaldark")) return new Look { metallic = 0.85f, smooth = 0.45f };
            if (n.StartsWith("metallight")) return new Look { metallic = 0.65f, smooth = 0.72f };
            if (n.StartsWith("metalmedium")) return new Look { metallic = 0.75f, smooth = 0.55f };
            if (n.StartsWith("metal")) return new Look { metallic = 0.12f, smooth = 0.62f };   // painted enamel: fridge, stove
            return null;
        }

        // ------------------------------------------------------------ materials

        static Material TexturedMaterial(Material orig, Look look)
        {
            string set = look.set;
            string albedoPath = look.gray ? GrayVariant(set) : $"{TexDir}/{set}/{set}_Diffuse_2k.jpg";
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{set}/{set}_nor_gl_2k.jpg");
            var mask   = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{set}/{set}_Mask.png");
            if (albedo == null || normal == null)
            {
                Debug.LogWarning($"[Furniture] Texture set '{set}' is not imported yet; leaving {orig.name} as it was.");
                return null;
            }

            Color c = orig.HasProperty("_BaseColor") ? orig.GetColor("_BaseColor") : orig.color;
            string key = $"{set}{(look.gray ? "G" : "")}_{Mathf.RoundToInt(c.r * 255)}-{Mathf.RoundToInt(c.g * 255)}-{Mathf.RoundToInt(c.b * 255)}";
            string path = $"{MatOut}/Fn_{key}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            // Tint = wanted colour / texture's average colour, in linear space.
            Color avg = Average(albedoPath);
            Color lin = c.linear;
            var tint = new Color(
                Mathf.Clamp(lin.r / avg.r, 0f, 4f),
                Mathf.Clamp(lin.g / avg.g, 0f, 4f),
                Mathf.Clamp(lin.b / avg.b, 0f, 4f), 1f).gamma;
            mat.SetColor("_BaseColor", tint);
            mat.SetTexture("_BaseMap", albedo);
            mat.SetTextureScale("_BaseMap", Vector2.one);
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", 1f);
            mat.EnableKeyword("_NORMALMAP");
            if (mask != null)
            {
                mat.SetTexture("_MetallicGlossMap", mask);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetTexture("_OcclusionMap", mask);
                mat.SetFloat("_OcclusionStrength", 1f);
                mat.EnableKeyword("_OCCLUSIONMAP");
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material PlainMaterial(Material orig, Look look)
        {
            string path = $"{MatOut}/Fn_{Sanitize(orig.name)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(orig);          // keeps the original colour
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetFloat("_Metallic", look.metallic);
            mat.SetFloat("_Smoothness", look.smooth);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// A luminance-only copy of a set's diffuse map, written next to it. Keeps every fibre and
        /// shadow of the weave but none of its colour, so the piece's own colour stays in charge.
        /// </summary>
        static string GrayVariant(string set)
        {
            string assetPath = $"{TexDir}/{set}/{set}_Gray_2k.jpg";
            if (File.Exists(assetPath)) return assetPath;

            string src = Path.Combine(Application.dataPath, "..", TexDir, set, $"{set}_Diffuse_2k.jpg");
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.LoadImage(File.ReadAllBytes(src));
            var px = t.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(0.299f * px[i].r + 0.587f * px[i].g + 0.114f * px[i].b), 0, 255);
                px[i] = new Color32(g, g, g, 255);
            }
            t.SetPixels32(px);
            File.WriteAllBytes(assetPath, t.EncodeToJPG(92));
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(assetPath);
            var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (ti != null)
            {
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            return assetPath;
        }

        /// <summary>The bedroom carpet floor uses the same texture and had the same stray streaks.</summary>
        static void RetintCarpetFloor()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>("Assets/NeighbourhoodHouse/Materials/N_carpet.mat");
            if (m == null) return;
            string gray = GrayVariant("dirty_carpet");
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(gray);
            if (tex == null) return;
            Color avg = Average(gray);
            Color want = new Color(0.40f, 0.37f, 0.34f).linear;          // a warm mid-grey carpet
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", new Color(want.r / avg.r, want.g / avg.g, want.b / avg.b, 1f).gamma);
            EditorUtility.SetDirty(m);
        }

        static Color Average(string assetPath)
        {
            if (avgCache.TryGetValue(assetPath, out var cached)) return cached;
            string set = assetPath;
            string file = Path.Combine(Application.dataPath, "..", assetPath);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.LoadImage(File.ReadAllBytes(file));
            var px = t.GetPixels32();
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < px.Length; i += 7)
            {
                r += Mathf.GammaToLinearSpace(px[i].r / 255f);
                g += Mathf.GammaToLinearSpace(px[i].g / 255f);
                b += Mathf.GammaToLinearSpace(px[i].b / 255f);
            }
            int n = (px.Length + 6) / 7;
            Object.DestroyImmediate(t);
            var avg = new Color(Mathf.Max(0.02f, (float)(r / n)), Mathf.Max(0.02f, (float)(g / n)), Mathf.Max(0.02f, (float)(b / n)), 1f);
            avgCache[set] = avg;
            return avg;
        }

        // ------------------------------------------------------------ meshes

        /// <summary>A copy of <paramref name="src"/> with box-projected UVs measured in metres / tile.</summary>
        static Mesh TexturedMesh(Mesh src, Vector3 scale, float tile)
        {
            string key = Sanitize($"{src.name}_{scale.x:F2}x{scale.y:F2}x{scale.z:F2}_{tile:F2}");
            string path = $"{MeshOut}/{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) return existing;

            var m = Object.Instantiate(src);
            m.name = key;
            var v = m.vertices;
            var nrm = m.normals;
            if (nrm.Length != v.Length) { m.RecalculateNormals(); nrm = m.normals; }

            var uv = new Vector2[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 p = Vector3.Scale(v[i], scale) / tile;
                Vector3 a = new Vector3(Mathf.Abs(nrm[i].x), Mathf.Abs(nrm[i].y), Mathf.Abs(nrm[i].z));
                // Project along whichever axis the surface faces most, so a flat top and a flat
                // side each get an undistorted slice of the texture.
                if (a.x >= a.y && a.x >= a.z) uv[i] = new Vector2(p.z, p.y);
                else if (a.y >= a.z) uv[i] = new Vector2(p.x, p.z);
                else uv[i] = new Vector2(p.x, p.y);
            }
            m.uv = uv;
            m.RecalculateTangents();
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // ------------------------------------------------------------ helpers

        static string Sanitize(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '-');
            return s.Replace(' ', '_').Replace('(', '-').Replace(')', '-').Replace(',', '-');
        }

        static string PathOf(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

        static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{name}")) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
