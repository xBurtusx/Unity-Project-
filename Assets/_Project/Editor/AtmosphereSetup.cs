using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using HorrorHouse;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Night lighting and the VHS grade.
    ///
    /// The single most important decision here is that night is NOT black. A black screen is not
    /// frightening, it is just unreadable, and players turn up their monitor until the mood is
    /// gone. Real night has a dim blue key from the sky and enough bounce to make shapes legible.
    /// So the moonlight is weak but present, the ambient is a cool gradient rather than flat, and
    /// the grade lifts the blacks slightly - which also happens to be exactly what VHS tape does,
    /// since tape could never hold a true black.
    /// </summary>
    public static class AtmosphereSetup
    {
        const string SettingsDir = "Assets/_Project/Settings";

        /// <summary>true = bright daytime scene, false = the original night look.</summary>
        public static bool Day = true;

        [MenuItem("Horror House/Stage 5 - Night Lighting and VHS Look", priority = 61)]
        public static void Setup()
        {
            EnsureFolder();

            // Each step is isolated: a failure in one should not silently abandon the rest, and
            // the log should name which step broke rather than leaving a half-applied look.
            if (Day) Step("daylight", BuildDaylight);
            else {
            Step("moonlight", BuildMoonlight);
            Step("ambient and fog", BuildAmbientAndFog);
            }

            VolumeProfile profile = null;
            Step("volume profile", () => profile = BuildVolumeProfile());
            if (profile != null) Step("global volume", () => BuildGlobalVolume(profile));

            Step("camera post-processing", EnablePostOnCamera);
            if (!Day) Step("VHS overlay", AttachVhsOverlay);

            AssetDatabase.SaveAssets();
            Debug.Log("[Atmosphere] Night lighting and VHS grade applied.\n" +
                      "Tune everything on the 'Global Volume' object and the VhsOverlay component. " +
                      "If it looks too dark, raise Moonlight intensity before touching anything else.");
        }

        static void Step(string name, System.Action action)
        {
            try { action(); }
            catch (System.Exception e)
            {
                Debug.LogError($"[Atmosphere] Step '{name}' failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Creates the folder through the AssetDatabase, not just on disk. Directory.CreateDirectory
        /// makes the folder but leaves the AssetDatabase unaware of it, and CreateAsset into a
        /// folder it does not know about throws - which is exactly how this failed the first time.
        /// </summary>
        static void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(SettingsDir)) return;
            if (!AssetDatabase.IsValidFolder("Assets/_Project"))
                AssetDatabase.CreateFolder("Assets", "_Project");
            AssetDatabase.CreateFolder("Assets/_Project", "Settings");
            AssetDatabase.Refresh();
        }

        // ------------------------------------------------------------ daytime

        /// <summary>Bright, slightly hazy afternoon: warm sun, blue sky, soft sky-coloured ambient.</summary>
        static void BuildDaylight()
        {
            // The night scene's moon may exist from an earlier run; two directional lights would fight.
            var moon = GameObject.Find("Moonlight");
            if (moon != null) Object.DestroyImmediate(moon);

            var go = GameObject.Find("Sun");
            if (go == null) { go = new GameObject("Sun"); Undo.RegisterCreatedObjectUndo(go, "Create Sun"); }
            go.transform.rotation = Quaternion.Euler(46f, -38f, 0f);
            if (!go.TryGetComponent(out Light sun)) sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.95f, 0.86f);
            sun.intensity = 1.6f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.92f;
            sun.shadowBias = 0.03f;
            sun.shadowNormalBias = 0.4f;
            RenderSettings.sun = sun;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor     = new Color(0.52f, 0.64f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.48f, 0.53f, 0.58f);
            RenderSettings.ambientGroundColor  = new Color(0.26f, 0.25f, 0.23f);
            RenderSettings.ambientIntensity = 1f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.72f, 0.80f, 0.90f);
            RenderSettings.fogDensity = 0.0035f;
            RenderSettings.reflectionIntensity = 0.8f;

            var shader = Shader.Find("Skybox/Procedural");
            if (shader != null)
            {
                string path = $"{SettingsDir}/M_DaySky.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
                mat.SetFloat("_SunSize", 0.045f);
                mat.SetFloat("_SunSizeConvergence", 5f);
                mat.SetFloat("_AtmosphereThickness", 1.05f);
                mat.SetColor("_SkyTint", new Color(0.5f, 0.5f, 0.5f));
                mat.SetColor("_GroundColor", new Color(0.55f, 0.58f, 0.62f));
                mat.SetFloat("_Exposure", 1.15f);
                EditorUtility.SetDirty(mat);
                RenderSettings.skybox = mat;
            }
            DynamicGI.UpdateEnvironment();
        }

        /// <summary>Neutral, clean grade for daytime: no crushed blacks, no tape tint.</summary>
        static void ApplyDayGrade(VolumeProfile p)
        {
            if (p.TryGet(out WhiteBalance wb)) { wb.temperature.value = 4f; wb.tint.value = 0f; }
            if (p.TryGet(out ColorAdjustments ca)) { ca.postExposure.value = 0.0f; ca.contrast.value = 6f; ca.saturation.value = 6f; }
            if (p.TryGet(out LiftGammaGain lgg)) { lgg.lift.value = new Vector4(1f, 1f, 1f, 0f); lgg.gain.value = new Vector4(1f, 1f, 1f, 0f); }
            if (p.TryGet(out FilmGrain fg)) fg.intensity.value = 0.08f;
            if (p.TryGet(out Vignette v)) { v.intensity.value = 0.16f; v.color.value = Color.black; }
            if (p.TryGet(out ChromaticAberration c)) c.intensity.value = 0.03f;
            if (p.TryGet(out LensDistortion l)) l.intensity.value = 0f;
            if (p.TryGet(out Bloom b)) { b.threshold.value = 1.0f; b.intensity.value = 0.25f; }
            if (p.TryGet(out MotionBlur m)) m.intensity.value = 0f;
        }

        // ------------------------------------------------------------ lighting

        static void BuildMoonlight()
        {
            var existing = GameObject.Find("Moonlight");
            var go = existing != null ? existing : new GameObject("Moonlight");
            if (existing == null) Undo.RegisterCreatedObjectUndo(go, "Create Moonlight");

            // High and off to one side, so walls catch a rim and the ground stays dim.
            go.transform.rotation = Quaternion.Euler(38f, 205f, 0f);

            if (!go.TryGetComponent(out Light light)) light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.60f, 0.72f, 1.00f);   // moonlight is cold
            light.intensity = 0.55f;                        // weak, but never zero
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.82f;                   // never fully black shadows
            light.shadowBias = 0.04f;
            light.shadowNormalBias = 0.3f;

            // Retire the template's sun so there are not two directional lights fighting.
            foreach (var other in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
                if (other != light && other.type == LightType.Directional) other.enabled = false;
        }

        static void BuildAmbientAndFog()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor     = new Color(0.17f, 0.21f, 0.32f);
            RenderSettings.ambientEquatorColor = new Color(0.12f, 0.14f, 0.21f);
            RenderSettings.ambientGroundColor  = new Color(0.06f, 0.065f, 0.09f);
            RenderSettings.ambientIntensity = 1f;

            // Fog does the heavy lifting outdoors: it hides the edge of the world, separates
            // the neighbours from the house, and makes distance feel uncertain.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.055f, 0.065f, 0.095f);
            RenderSettings.fogDensity = 0.012f;

            RenderSettings.reflectionIntensity = 0.35f;
            RenderSettings.subtractiveShadowColor = new Color(0.05f, 0.06f, 0.09f);

            var sky = BuildSkyboxMaterial();
            if (sky != null) RenderSettings.skybox = sky;
            DynamicGI.UpdateEnvironment();
        }

        /// <summary>
        /// A hand-rolled overcast night sky, as a latitude-longitude panorama.
        ///
        /// Unity's Skybox/Procedural shader is built for daylight. Stopped down far enough to read
        /// as night, its gradient runs out of 8-bit precision and shatters - worst around the sun
        /// direction, where the gradient is steepest, which shows up as a large fuzzy block of
        /// noise hanging in the sky. No amount of exposure tweaking fixes that; the precision is
        /// simply gone.
        ///
        /// Generating the sky instead means the gradient can carry its own dither (sub-one-LSB
        /// noise added per pixel), which breaks up banding before it can form.
        /// </summary>
        static Material BuildSkyboxMaterial()
        {
            string texPath = $"{SettingsDir}/T_NightSky.png";
            // Always regenerated: it is deterministic and cheap, and it lets the stars be retuned.
            GenerateSkyTexture(texPath);

            var shader = Shader.Find("Skybox/Panoramic");
            if (shader == null)
            {
                Debug.LogWarning("[Atmosphere] Skybox/Panoramic not found; leaving the sky alone.");
                return null;
            }

            string path = $"{SettingsDir}/M_NightSky.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            // An earlier run may have left this on Skybox/Procedural, whose properties are
            // completely different - setting panoramic values on it would silently do nothing.
            else if (mat.shader != shader) mat.shader = shader;

            mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            mat.SetFloat("_Mapping", 1f);      // latitude-longitude
            mat.SetFloat("_ImageType", 0f);    // full 360
            mat.SetFloat("_Exposure", 1f);     // brightness lives in the texture
            mat.SetFloat("_Rotation", 0f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void GenerateSkyTexture(string path)
        {
            const int W = 2048, H = 1024;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var px = new Color[W * H];

            // Thin high cloud: a soft moon behind it and a sparse scatter of stars where it has
            // thinned. Deliberately NOT a clear sky - a few dozen visible stars and no Milky Way.
            // Must match the Moonlight rotation set above.
            var moonDir = -(Quaternion.Euler(38f, 205f, 0f) * Vector3.forward);

            var zenith  = new Color(0.030f, 0.040f, 0.072f);
            var horizon = new Color(0.105f, 0.120f, 0.165f);
            var ground  = new Color(0.016f, 0.018f, 0.024f);

            var rng = new System.Random(8102026);

            for (int y = 0; y < H; y++)
            {
                float v = y / (float)(H - 1);
                float lat = (v - 0.5f) * Mathf.PI;

                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)(W - 1);
                    float lon = u * Mathf.PI * 2f;

                    Color c;
                    if (v < 0.5f)
                    {
                        // Below the horizon: fades to near-black, mostly hidden by terrain anyway.
                        float t = Mathf.SmoothStep(0f, 1f, v / 0.5f);
                        c = Color.Lerp(ground, horizon, t);
                    }
                    else
                    {
                        // Above: horizon haze falling off quickly into a dark zenith.
                        float t = Mathf.Pow((v - 0.5f) / 0.5f, 0.55f);
                        c = Color.Lerp(horizon, zenith, t);
                    }

                    // Diffuse moon glow through cloud. Broad and weak - a hard disc would read
                    // as a clear night and undercut the gloom.
                    var dir = new Vector3(Mathf.Cos(lat) * Mathf.Sin(lon),
                                          Mathf.Sin(lat),
                                          Mathf.Cos(lat) * Mathf.Cos(lon));
                    float d = Mathf.Max(0f, Vector3.Dot(dir, moonDir));
                    float glow = Mathf.Pow(d, 14f) * 0.16f + Mathf.Pow(d, 3f) * 0.045f;
                    c += new Color(glow * 0.80f, glow * 0.88f, glow * 1.00f);

                    // Sub-LSB dither: breaks the gradient up before it can band. Kept to roughly
                    // one level, because mipmapping will average it away at distance and a
                    // stronger value reads as grain rather than as a smooth sky.
                    float n = ((float)rng.NextDouble() - 0.5f) * (1.1f / 255f);
                    px[y * W + x] = new Color(c.r + n, c.g + n, c.b + n, 1f);
                }
            }

            AddMoonDisc(px, W, H, moonDir);
            AddStars(px, W, H, rng);

            tex.SetPixels(px);
            tex.Apply();
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = TextureImporterType.Default;
            imp.sRGBTexture = true;
            imp.wrapModeU = TextureWrapMode.Repeat;    // seamless all the way round
            imp.wrapModeV = TextureWrapMode.Clamp;     // no wrap from zenith to nadir

            // Mipmaps are ESSENTIAL here, not optional. Without them the sky is sampled at
            // grazing angles with no filtering, and the dither baked into the gradient aliases
            // into shimmering blocks that look like broken geometry hanging in the sky.
            imp.mipmapEnabled = true;
            imp.filterMode = FilterMode.Trilinear;
            imp.anisoLevel = 4;
            imp.maxTextureSize = 2048;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.SaveAndReimport();
        }

        /// <summary>A soft, veiled moon: bright enough to find, not a clean white disc.</summary>
        static void AddMoonDisc(Color[] px, int W, int H, Vector3 moonDir)
        {
            for (int y = 0; y < H; y++)
            {
                float lat = (y / (float)(H - 1) - 0.5f) * Mathf.PI;
                for (int x = 0; x < W; x++)
                {
                    float lon = x / (float)(W - 1) * Mathf.PI * 2f;
                    var dir = new Vector3(Mathf.Cos(lat) * Mathf.Sin(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Cos(lon));
                    float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(dir, moonDir), -1f, 1f)) * Mathf.Rad2Deg;
                    if (ang > 4f) continue;
                    float disc = 1f - Mathf.SmoothStep(0.9f, 1.5f, ang);     // ~1 degree radius, soft edge
                    float halo = Mathf.Exp(-ang * ang / 3.0f) * 0.22f;       // veil of cloud around it
                    float v = disc * 0.62f + halo;
                    var c = px[y * W + x];
                    px[y * W + x] = new Color(c.r + v * 0.85f, c.g + v * 0.92f, c.b + v, 1f);
                }
            }
        }

        /// <summary>
        /// Sparse stars. Most are barely there; a handful are clear. They fade out toward the
        /// horizon where haze would swallow them, and a few are masked by "cloud" noise so the
        /// field is uneven rather than a uniform sprinkle.
        /// </summary>
        static void AddStars(Color[] px, int W, int H, System.Random rng)
        {
            const int count = 520;
            for (int i = 0; i < count; i++)
            {
                // Uniform on the upper hemisphere only, above ~12 degrees elevation.
                float lat = Mathf.Asin(Mathf.Lerp(0.21f, 1f, (float)rng.NextDouble()));
                float y01 = 0.5f + lat / Mathf.PI;     // the sky texture maps latitude linearly to v
                int py = Mathf.Clamp(Mathf.RoundToInt(y01 * (H - 1)), 2, H - 3);
                int pxX = rng.Next(W);

                // Equirectangular rows are squeezed near the poles, so widen the star there
                // to keep it round on screen.
                float widen = Mathf.Clamp(1f / Mathf.Max(0.25f, Mathf.Cos(lat)), 1f, 3f);

                // Power-law brightness: lots of faint, very few bright.
                float b = Mathf.Pow((float)rng.NextDouble(), 3.2f);
                float level = Mathf.Lerp(0.20f, 0.95f, b);
                level *= Mathf.SmoothStep(0.0f, 1f, (y01 - 0.55f) / 0.25f);   // haze near horizon
                if (level < 0.04f) continue;

                float tint = (float)rng.NextDouble();
                var col = Color.Lerp(new Color(0.80f, 0.88f, 1.00f), new Color(1.00f, 0.93f, 0.82f), tint * tint);

                int rx = Mathf.CeilToInt(1.6f * widen), ry = 2;
                for (int dy = -ry; dy <= ry; dy++)
                for (int dx = -rx; dx <= rx; dx++)
                {
                    float d2 = (dx / widen) * (dx / widen) + dy * dy;
                    float a = Mathf.Exp(-d2 / 0.75f) * level;
                    int xx = (pxX + dx + W) % W;
                    int idx = (py + dy) * W + xx;
                    var c = px[idx];
                    px[idx] = new Color(c.r + col.r * a, c.g + col.g * a, c.b + col.b * a, 1f);
                }
            }
        }

        // ------------------------------------------------------------ post processing

        static VolumeProfile BuildVolumeProfile()
        {
            string path = $"{SettingsDir}/VP_VhsNight.asset";
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }

            T Get<T>() where T : VolumeComponent =>
                profile.TryGet<T>(out var c) ? c : profile.Add<T>(true);

            var tone = Get<Tonemapping>();
            tone.mode.overrideState = true;
            tone.mode.value = TonemappingMode.ACES;

            // Cold, slightly green-grey: consumer tape never got white balance right indoors.
            var wb = Get<WhiteBalance>();
            wb.temperature.overrideState = true; wb.temperature.value = -22f;
            wb.tint.overrideState = true;        wb.tint.value = 6f;

            var ca = Get<ColorAdjustments>();
            // The sky texture is already authored dark, so this no longer has to pull the whole
            // image down - it just gives the night a slight lift so shapes stay readable.
            ca.postExposure.overrideState = true; ca.postExposure.value = 0.75f;
            ca.contrast.overrideState = true;     ca.contrast.value = 8f;
            ca.saturation.overrideState = true;   ca.saturation.value = -26f;

            // Lifted blacks - the signature of tape, and it keeps night readable.
            var lgg = Get<LiftGammaGain>();
            lgg.lift.overrideState = true;
            lgg.lift.value = new Vector4(1.02f, 1.03f, 1.09f, 0.035f);
            lgg.gain.overrideState = true;
            lgg.gain.value = new Vector4(0.99f, 1.00f, 1.02f, -0.01f);

            var grain = Get<FilmGrain>();
            grain.type.overrideState = true;      grain.type.value = FilmGrainLookup.Medium1;
            grain.intensity.overrideState = true; grain.intensity.value = 0.42f;
            grain.response.overrideState = true;  grain.response.value = 0.75f;

            var vig = Get<Vignette>();
            vig.intensity.overrideState = true; vig.intensity.value = 0.34f;
            vig.smoothness.overrideState = true; vig.smoothness.value = 0.55f;
            vig.color.overrideState = true; vig.color.value = new Color(0.02f, 0.02f, 0.04f);

            var chrom = Get<ChromaticAberration>();
            chrom.intensity.overrideState = true; chrom.intensity.value = 0.18f;

            var lens = Get<LensDistortion>();
            lens.intensity.overrideState = true; lens.intensity.value = -0.06f;
            lens.scale.overrideState = true;     lens.scale.value = 1.0f;

            var bloom = Get<Bloom>();
            bloom.threshold.overrideState = true; bloom.threshold.value = 0.85f;
            bloom.intensity.overrideState = true; bloom.intensity.value = 0.55f;
            bloom.scatter.overrideState = true;   bloom.scatter.value = 0.72f;

            var mb = Get<MotionBlur>();
            mb.intensity.overrideState = true; mb.intensity.value = 0.12f;   // subtle, per the brief

            if (Day) ApplyDayGrade(profile);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        static void BuildGlobalVolume(VolumeProfile profile)
        {
            var go = GameObject.Find("Global Volume");
            if (go == null)
            {
                go = new GameObject("Global Volume");
                Undo.RegisterCreatedObjectUndo(go, "Create Global Volume");
            }
            if (!go.TryGetComponent(out Volume vol)) vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;
            vol.weight = 1f;
            vol.sharedProfile = profile;
        }

        static void EnablePostOnCamera()
        {
            var player = GameObject.Find("Player");
            var cam = player != null ? player.GetComponentInChildren<Camera>() : Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[Atmosphere] No camera found - run 'Stage 3 - Create Player' first. " +
                                 "The grade is set up but nothing is rendering it.");
                return;
            }

            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.Medium;
            data.dithering = true;
            cam.allowHDR = true;
        }

        static void AttachVhsOverlay()
        {
            var player = GameObject.Find("Player");
            if (player == null) return;
            if (player.GetComponentInChildren<VhsOverlay>() != null) return;

            var go = new GameObject("VhsOverlay");
            go.transform.SetParent(player.transform, false);
            go.AddComponent<VhsOverlay>();
        }
    }
}
