using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// The realism pass: an HDRI sky that actually lights the scene, a sun aligned to that sky,
    /// physically-plausible surface response (roughness + ambient occlusion) and a cleaner,
    /// filmic renderer setup.
    ///
    /// Everything here is idempotent - run it as often as you like. Trigger it with the AutoRun
    /// marker ("realism") or from the Horror House menu. It deliberately overrides the flat
    /// three-colour ambient that AtmosphereSetup.BuildDaylight leaves behind; re-running
    /// AtmosphereSetup will undo the sky, so run this one afterwards.
    ///
    /// Raw Poly Haven sources (all maps) live in AssetSource/PolyHaven, which is not versioned.
    /// Only the diffuse + normal maps and the generated mask maps are imported into Assets.
    /// </summary>
    public static class RealismPass
    {
        const string TexDir      = "Assets/_Project/Textures/PolyHaven";
        const string RawDir      = "AssetSource/PolyHaven";
        const string HdriPath    = "Assets/_Project/Settings/Sky/kloofendal_48d_partly_cloudy_puresky_4k.hdr";
        const string SkyMatPath  = "Assets/_Project/Settings/M_HdriSky.mat";
        const string MatDir      = "Assets/NeighbourhoodHouse/Materials";
        const string UrpAsset    = "Assets/Settings/PC_RPAsset.asset";

        static readonly string[] Sets =
        {
            "asphalt_02", "grass_ground", "weathered_plank_siding", "brick_4", "roof_slates_03",
            "herringbone_parquet", "square_tiles_02", "dirty_carpet", "beige_wall_001", "concrete_pavement",
            "oak_veneer_01", "dark_wood", "knitted_fleece",      // furniture
        };

        [MenuItem("Horror House/Realism Pass", priority = 70)]
        public static void Run()
        {
            AssetDatabase.Refresh();
            Step("textures", ImportTextures);
            Step("sky + sun", BuildSkyAndSun);
            Step("materials", UpgradeMaterials);
            Step("renderer quality", TuneRenderer);
            Step("ambient occlusion", TuneSsao);
            Step("post-processing", TunePost);

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[Realism] Pass complete.");
        }

        static void Step(string name, System.Action action)
        {
            try { action(); Debug.Log($"[Realism] {name}: ok"); }
            catch (System.Exception e) { Debug.LogError($"[Realism] Step '{name}' failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }

        // ------------------------------------------------------------ textures

        internal static void ImportTextures()
        {
            foreach (var id in Sets)
            {
                string dir = $"{TexDir}/{id}";
                string diffuse = $"{dir}/{id}_Diffuse_2k.jpg";
                string normal  = $"{dir}/{id}_nor_gl_2k.jpg";

                ConfigureTexture(diffuse, TextureImporterType.Default, true);
                ConfigureTexture(normal, TextureImporterType.NormalMap, false);
                BuildMask(id);
            }
        }

        static void ConfigureTexture(string path, TextureImporterType type, bool srgb)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) { Debug.LogWarning($"[Realism] Missing texture {path}"); return; }
            bool dirty = ti.textureType != type || ti.sRGBTexture != srgb || ti.anisoLevel != 8 || ti.maxTextureSize != 2048;
            ti.textureType = type;
            ti.sRGBTexture = srgb;
            ti.mipmapEnabled = true;
            ti.anisoLevel = 8;
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            if (dirty) ti.SaveAndReimport();
        }

        /// <summary>
        /// URP's Lit shader wants smoothness in the alpha of the metallic map and occlusion in
        /// green. Poly Haven ships roughness and AO as separate greyscale JPGs, so pack them:
        /// R = metallic (0, these are all dielectrics), G = AO, B = 0, A = 1 - roughness.
        /// </summary>
        static void BuildMask(string id)
        {
            string outPath = $"{TexDir}/{id}/{id}_Mask.png";
            string raw = Path.Combine(Application.dataPath, "..", RawDir, id);
            string roughFile = Path.Combine(raw, $"{id}_Rough_2k.jpg");
            string aoFile    = Path.Combine(raw, $"{id}_AO_2k.jpg");

            if (!File.Exists(outPath))
            {
                if (!File.Exists(roughFile) || !File.Exists(aoFile))
                {
                    Debug.LogWarning($"[Realism] No raw rough/AO for {id} in {RawDir} - mask skipped.");
                    return;
                }
                var rough = Load(roughFile);
                var ao    = Load(aoFile);
                int w = ao.width, h = ao.height;
                var rp = rough.GetPixels32();
                var ap = ao.GetPixels32();
                var op = new Color32[w * h];
                bool sameSize = rough.width == w && rough.height == h;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    byte r = sameSize ? rp[i].r : rp[Mathf.Min(rough.height - 1, y * rough.height / h) * rough.width + Mathf.Min(rough.width - 1, x * rough.width / w)].r;
                    op[i] = new Color32(0, ap[i].r, 0, (byte)(255 - r));
                }
                var mask = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                mask.SetPixels32(op);
                File.WriteAllBytes(outPath, mask.EncodeToPNG());
                Object.DestroyImmediate(mask); Object.DestroyImmediate(rough); Object.DestroyImmediate(ao);
                AssetDatabase.ImportAsset(outPath);
            }

            var ti = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (ti == null) return;
            if (ti.sRGBTexture || ti.alphaIsTransparency || ti.maxTextureSize != 2048)
            {
                ti.sRGBTexture = false;           // data, not colour
                ti.alphaIsTransparency = false;
                ti.mipmapEnabled = true;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.SaveAndReimport();
            }
        }

        static Texture2D Load(string file)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            t.LoadImage(File.ReadAllBytes(file));
            return t;
        }

        // ------------------------------------------------------------ sky + sun

        static void BuildSkyAndSun()
        {
            var ti = AssetImporter.GetAtPath(HdriPath) as TextureImporter;
            if (ti == null) throw new FileNotFoundException("HDRI not imported yet: " + HdriPath);
            if (ti.sRGBTexture || ti.maxTextureSize != 4096 || ti.wrapModeV != TextureWrapMode.Clamp)
            {
                ti.textureShape = TextureImporterShape.Texture2D;
                ti.sRGBTexture = false;
                ti.mipmapEnabled = true;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.filterMode = FilterMode.Bilinear;
                ti.maxTextureSize = 4096;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.SaveAndReimport();
            }

            var shader = Shader.Find("Skybox/Panoramic");
            if (shader == null) throw new System.Exception("Skybox/Panoramic shader not found");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyMatPath);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, SkyMatPath); }
            else if (mat.shader != shader) mat.shader = shader;
            mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(HdriPath));
            mat.SetFloat("_Mapping", 1f);      // latitude-longitude
            mat.SetFloat("_ImageType", 0f);    // full 360
            mat.SetFloat("_Exposure", 1f);
            mat.SetFloat("_Rotation", 0f);
            EditorUtility.SetDirty(mat);

            RenderSettings.skybox = mat;
            // Light the world FROM the sky: ambient and reflections now carry real colour and
            // direction (blue from above, warm bounce from the horizon) instead of a flat trilight.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
            RenderSettings.defaultReflectionResolution = 256;
            RenderSettings.reflectionIntensity = 1f;

            // Light haze for depth: far houses fade toward the horizon colour like real air.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.66f, 0.74f, 0.84f);
            RenderSettings.fogDensity = 0.0016f;

            AlignSun(Path.GetFullPath(HdriPath));
            DynamicGI.UpdateEnvironment();
        }

        /// <summary>
        /// Finds the sun in the HDRI (its brightest region) and points the scene's directional
        /// light the same way, so the shadows fall away from the sun you can actually see.
        /// </summary>
        static void AlignSun(string hdrFile)
        {
            var go = GameObject.Find("Sun");
            if (go == null) { go = new GameObject("Sun"); Undo.RegisterCreatedObjectUndo(go, "Create Sun"); }
            if (!go.TryGetComponent(out Light sun)) sun = go.AddComponent<Light>();

            if (TryFindSun(hdrFile, out Vector3 toSun))
            {
                go.transform.rotation = Quaternion.LookRotation(-toSun);
                Debug.Log($"[Realism] Sun found in HDRI at elevation {Mathf.Asin(toSun.y) * Mathf.Rad2Deg:F1} deg, " +
                          $"azimuth {Mathf.Atan2(toSun.x, toSun.z) * Mathf.Rad2Deg:F1} deg.");
            }
            else Debug.LogWarning("[Realism] Could not locate the sun in the HDRI; leaving Sun rotation as it was.");

            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.94f, 0.84f);
            sun.intensity = 3.0f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;
            sun.shadowBias = 0.03f;
            sun.shadowNormalBias = 0.4f;
            sun.bounceIntensity = 1f;
            RenderSettings.sun = sun;
            EditorUtility.SetDirty(sun);
        }

        /// <summary>
        /// Reads a Radiance .hdr (RGBE, new-style RLE) and returns the direction of its brightest
        /// region, using the same lat-long mapping as Unity's Skybox/Panoramic shader.
        /// </summary>
        static bool TryFindSun(string file, out Vector3 dir)
        {
            dir = Vector3.up;
            using var fs = File.OpenRead(file);
            using var br = new BinaryReader(fs);

            string ReadLine()
            {
                var sb = new System.Text.StringBuilder();
                int b;
                while ((b = br.ReadByte()) != '\n') if (b != '\r') sb.Append((char)b);
                return sb.ToString();
            }

            while (ReadLine().Length > 0) { }                 // header ends at the first blank line
            var res = ReadLine().Split(' ');                  // "-Y <H> +X <W>"
            int H = int.Parse(res[1]), W = int.Parse(res[3]);

            var scale = new float[256];
            for (int e = 1; e < 256; e++) scale[e] = (float)System.Math.Pow(2.0, e - 136);

            var lum = new float[W * H];
            var scan = new byte[W * 4];
            float max = 0f;
            for (int y = 0; y < H; y++)
            {
                int b0 = br.ReadByte(), b1 = br.ReadByte(), hi = br.ReadByte(), lo = br.ReadByte();
                if (b0 != 2 || b1 != 2 || (hi & 0x80) != 0 || ((hi << 8) | lo) != W) return false;
                for (int ch = 0; ch < 4; ch++)
                {
                    int x = 0;
                    while (x < W)
                    {
                        int c = br.ReadByte();
                        if (c > 128) { int n = c - 128; byte v = br.ReadByte(); for (int i = 0; i < n; i++) scan[(x++) * 4 + ch] = v; }
                        else { for (int i = 0; i < c; i++) scan[(x++) * 4 + ch] = br.ReadByte(); }
                    }
                }
                for (int x = 0; x < W; x++)
                {
                    float s = scale[scan[x * 4 + 3]];
                    float l = (0.2126f * scan[x * 4] + 0.7152f * scan[x * 4 + 1] + 0.0722f * scan[x * 4 + 2]) * s;
                    lum[y * W + x] = l;
                    if (l > max) max = l;
                }
            }
            if (max <= 0f) return false;

            Vector3 acc = Vector3.zero;
            float cut = max * 0.5f;
            for (int y = 0; y < H; y++)
            {
                float lat = (y + 0.5f) / H * Mathf.PI;
                float sl = Mathf.Sin(lat), cl = Mathf.Cos(lat);
                for (int x = 0; x < W; x++)
                {
                    float l = lum[y * W + x];
                    if (l < cut) continue;
                    float u = (x + 0.5f) / W;
                    float lon = (0.5f - u) * 2f * Mathf.PI;
                    acc += new Vector3(sl * Mathf.Cos(lon), cl, sl * Mathf.Sin(lon)) * l;
                }
            }
            if (acc.sqrMagnitude < 1e-6f) return false;
            dir = acc.normalized;
            return true;
        }

        // ------------------------------------------------------------ materials

        static void UpgradeMaterials()
        {
            // Ground: replace the soft, repeating pack textures with real photographed surfaces.
            // Tiling is kept from the existing materials, which were tuned to the mesh UVs.
            foreach (var n in new[] { "N_ac_road_900x75", "N_road" })        ApplySet(n, "asphalt_02", null, 1f, 1f);

            // The photographed meadow is dry and straw-coloured; the tint pulls it to a healthy
            // suburban lawn without flattening the blade detail.
            var lawn = new Color(0.50f, 0.78f, 0.30f);
            foreach (var n in new[] { "N_ac_grass_900x9000", "N_grass" })    ApplySet(n, "grass_ground", lawn, 1f, 1f);

            // Brick and fencing: real photographed surfaces instead of the pack's soft ones.
            // Absolute tiling (not a multiplier), so re-running never compounds it. Foundation
            // bricks were reading as oversized, so each course is now roughly a third of the height.
            foreach (var n in new[] { "N_brick", "N_brick0", "N_brick1", "N_brick2" })
                ApplySet(n, "brick_4", new Color(0.95f, 0.85f, 0.80f), 1f, 1f, new Vector2(3.5f, 3.5f));

            // Interiors and walkways. Existing tiling is kept: it was tuned to each mesh's UVs.
            ApplySet("N_hardwood", "herringbone_parquet", Color.white, 1f, 1f);
            ApplySet("N_marble", "square_tiles_02", Color.white, 1f, 1f);
            ApplySet("N_carpet", "dirty_carpet", new Color(0.85f, 0.82f, 0.78f), 1f, 1f);
            ApplySet("N_wall", "beige_wall_001", new Color(0.97f, 0.93f, 0.86f), 1f, 1f);
            foreach (var n in new[] { "N_ac_walk_900x15", "N_walk" })
                ApplySet(n, "concrete_pavement", Color.white, 1f, 1f);
            foreach (var n in new[] { "N_fence", "N_fence0", "N_fence1", "N_fence2", "N_fence3" })
                ApplySet(n, "weathered_plank_siding", new Color(0.95f, 0.86f, 0.74f), 1f, 1f);

            // Roofs were close to featureless black. Real tile has a lit face, shadowed joints and
            // a sheen at grazing angles - all of which come from the normal and roughness maps.
            foreach (var n in new[] { "N_shingle", "N_shingle0", "N_shingle1", "N_shingle2", "N_shedroof" })
                ApplySet(n, "roof_slates_03", new Color(0.40f, 0.40f, 0.44f), 1f, 1f);

            // Everything else from the house pack: wire up the ambient-occlusion map it already
            // ships with but never used. That is what makes creases, eaves and brick joints read.
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { MatDir }))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                // Unlit and other non-Lit materials have no occlusion slot; skip them quietly.
                if (m == null || !m.HasProperty("_BaseMap") || !m.HasProperty("_OcclusionMap")) continue;
                HookPackOcclusion(m);
            }
        }

        static void ApplySet(string matName, string id, Color? tint, float bump, float smoothMul, Vector2? tiling = null)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{matName}.mat");
            if (m == null) { Debug.LogWarning($"[Realism] Material not found: {matName}"); return; }

            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{id}/{id}_Diffuse_2k.jpg");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{id}/{id}_nor_gl_2k.jpg");
            var mask   = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{id}/{id}_Mask.png");
            if (albedo == null || normal == null) { Debug.LogWarning($"[Realism] Texture set {id} not imported yet"); return; }

            var scale = tiling ?? m.GetTextureScale("_BaseMap");   // keep the UV tuning unless told otherwise
            m.SetTexture("_BaseMap", albedo);
            m.SetTextureScale("_BaseMap", scale);
            m.SetTextureOffset("_BaseMap", Vector2.zero);
            m.SetColor("_BaseColor", tint ?? Color.white);

            m.SetTexture("_BumpMap", normal);
            m.SetTextureScale("_BumpMap", scale);
            m.SetFloat("_BumpScale", bump);
            m.EnableKeyword("_NORMALMAP");

            if (mask != null)
            {
                m.SetTexture("_MetallicGlossMap", mask);
                m.SetTextureScale("_MetallicGlossMap", scale);
                m.SetFloat("_Metallic", 0f);
                m.SetFloat("_Smoothness", smoothMul);          // multiplies the map's alpha
                m.SetFloat("_SmoothnessTextureChannel", 0f);   // smoothness from metallic alpha
                m.EnableKeyword("_METALLICSPECGLOSSMAP");

                m.SetTexture("_OcclusionMap", mask);
                m.SetTextureScale("_OcclusionMap", scale);
                m.SetFloat("_OcclusionStrength", 1f);
                m.EnableKeyword("_OCCLUSIONMAP");
            }
            EditorUtility.SetDirty(m);
        }

        static void HookPackOcclusion(Material m)
        {
            if (m.GetTexture("_OcclusionMap") != null) return;
            var baseTex = m.GetTexture("_BaseMap");
            if (baseTex == null) return;
            string p = AssetDatabase.GetAssetPath(baseTex);
            if (string.IsNullOrEmpty(p)) return;

            string dir = Path.GetDirectoryName(p).Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(p).Replace("-albedo", "").Replace("_albedo", "");
            string aoPath = $"{dir}/{stem}-ao.png";
            var ao = AssetDatabase.LoadAssetAtPath<Texture2D>(aoPath);
            if (ao == null) return;

            // The pack's AO is imported as sRGB colour; occlusion is data.
            var ti = AssetImporter.GetAtPath(aoPath) as TextureImporter;
            if (ti != null && ti.sRGBTexture) { ti.sRGBTexture = false; ti.SaveAndReimport(); }

            m.SetTexture("_OcclusionMap", ao);
            m.SetTextureScale("_OcclusionMap", m.GetTextureScale("_BaseMap"));
            m.SetFloat("_OcclusionStrength", 1f);
            m.EnableKeyword("_OCCLUSIONMAP");
            EditorUtility.SetDirty(m);
        }

        // ------------------------------------------------------------ renderer

        static void TuneRenderer()
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(UrpAsset);
            if (asset == null) throw new FileNotFoundException(UrpAsset);
            var so = new SerializedObject(asset);
            SetInt(so, "m_MainLightShadowmapResolution", 4096);
            SetInt(so, "m_ShadowDistance", 90);
            SetInt(so, "m_MSAA", 4);
            SetInt(so, "m_AdditionalLightsShadowmapResolution", 4096);   // many shadowed lamps at night
            SetInt(so, "m_ColorGradingMode", 1);          // HDR grading: keeps highlights from clipping before ACES
            SetInt(so, "m_SoftShadowQuality", 3);         // High
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        static void SetInt(SerializedObject so, string name, int value)
        {
            var p = so.FindProperty(name);
            if (p == null) { Debug.LogWarning($"[Realism] URP property {name} not found - skipped."); return; }
            if (p.propertyType == SerializedPropertyType.Float) p.floatValue = value; else p.intValue = value;
        }

        /// <summary>
        /// Screen-space AO is what grounds objects: dark contact creases under eaves, hedges and
        /// steps. The renderer feature already exists; this just makes it do more of its job.
        /// Property names differ between URP versions, so unknown ones are logged, not guessed.
        /// </summary>
        static void TuneSsao()
        {
            const string rendererPath = "Assets/Settings/PC_Renderer.asset";
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(rendererPath))
            {
                if (o == null || o.GetType().Name != "ScreenSpaceAmbientOcclusion") continue;
                var so = new SerializedObject(o);
                var settings = so.FindProperty("m_Settings");
                if (settings == null) { Debug.LogWarning("[Realism] SSAO has no m_Settings."); return; }

                var names = new System.Text.StringBuilder();
                var it = settings.Copy();
                var end = settings.GetEndProperty();
                it.NextVisible(true);
                do
                {
                    if (SerializedProperty.EqualContents(it, end)) break;
                    names.Append(it.name).Append(' ');
                    switch (it.name)
                    {
                        case "Intensity":              it.floatValue = 1.1f; break;
                        case "Radius":                 it.floatValue = 0.35f; break;
                        case "DirectLightingStrength": it.floatValue = 0.2f; break;
                        case "Downsample":             it.boolValue = false; break;
                    }
                } while (it.NextVisible(false));
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(o);
                Debug.Log("[Realism] SSAO settings found: " + names);
                return;
            }
            Debug.LogWarning("[Realism] No ScreenSpaceAmbientOcclusion feature on " + rendererPath);
        }

        /// <summary>Points the Scene view straight at the HDRI's sun, to check sky and Sun light agree.</summary>
        [MenuItem("Horror House/Look At Sun", priority = 71)]
        public static void LookAtSun()
        {
            var sun = GameObject.Find("Sun");
            var sv = SceneView.lastActiveSceneView;
            if (sun == null || sv == null) { Debug.LogWarning("[Realism] Need a Sun and an open Scene view."); return; }
            Vector3 toSun = -sun.transform.forward;
            // Turn on the spot: LookAt places the camera BEHIND the pivot, so aim the pivot at a
            // point along the sun direction from where the camera already is. Otherwise the
            // camera ends up below the ground looking up through it.
            Vector3 cam = sv.camera.transform.position;
            float d = Mathf.Max(1f, sv.cameraDistance);
            sv.LookAt(cam + toSun * d, Quaternion.LookRotation(toSun), sv.size, false, true);
            sv.Repaint();
        }

        // ------------------------------------------------------------ post

        static void TunePost()
        {
            var vol = Object.FindAnyObjectByType<Volume>();
            if (vol == null || vol.sharedProfile == null) throw new System.Exception("No Global Volume profile in the scene");
            var p = vol.sharedProfile;

            // The profile was saved with empty component slots (its components only ever existed in
            // memory), so any code reading it threw. Drop the dead slots, and when a component is
            // added, store it INSIDE the asset so it survives a save and a reload.
            p.components.RemoveAll(x => x == null);
            T Get<T>() where T : VolumeComponent
            {
                if (p.TryGet<T>(out var existing)) return existing;
                var made = p.Add<T>(true);
                made.name = typeof(T).Name;
                AssetDatabase.AddObjectToAsset(made, p);
                return made;
            }

            Get<Tonemapping>().mode.Override(TonemappingMode.ACES);

            var bloom = Get<Bloom>();
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(0.18f);
            bloom.scatter.Override(0.65f);
            bloom.highQualityFiltering.Override(true);

            var vig = Get<Vignette>();
            vig.intensity.Override(0.12f);
            vig.smoothness.Override(0.45f);

            var ca = Get<ColorAdjustments>();
            ca.postExposure.Override(0f);
            ca.contrast.Override(8f);
            ca.saturation.Override(6f);

            // Leftover VHS-era artefacts: real cameras do not smear colour at the edges.
            Get<ChromaticAberration>().intensity.Override(0f);
            Get<LensDistortion>().intensity.Override(0f);
            Get<FilmGrain>().intensity.Override(0.0f);
            Get<MotionBlur>().intensity.Override(0f);

            EditorUtility.SetDirty(p);
        }
    }
}
