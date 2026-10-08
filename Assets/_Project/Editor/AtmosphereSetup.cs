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

        [MenuItem("Horror House/Stage 5 - Night Lighting and VHS Look", priority = 61)]
        public static void Setup()
        {
            EnsureFolder();

            // Each step is isolated: a failure in one should not silently abandon the rest, and
            // the log should name which step broke rather than leaving a half-applied look.
            Step("moonlight", BuildMoonlight);
            Step("ambient and fog", BuildAmbientAndFog);

            VolumeProfile profile = null;
            Step("volume profile", () => profile = BuildVolumeProfile());
            if (profile != null) Step("global volume", () => BuildGlobalVolume(profile));

            Step("camera post-processing", EnablePostOnCamera);
            Step("VHS overlay", AttachVhsOverlay);

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

        // ------------------------------------------------------------ lighting

        static void BuildMoonlight()
        {
            var existing = GameObject.Find("Moonlight");
            var go = existing != null ? existing : new GameObject("Moonlight");
            if (existing == null) Undo.RegisterCreatedObjectUndo(go, "Create Moonlight");

            // High and off to one side, so walls catch a rim and the ground stays dim.
            go.transform.rotation = Quaternion.Euler(38f, 205f, 0f);

            var light = go.GetComponent<Light>() ?? go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.60f, 0.72f, 1.00f);   // moonlight is cold
            light.intensity = 0.16f;                        // weak, but never zero
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
            RenderSettings.ambientSkyColor     = new Color(0.075f, 0.095f, 0.150f);
            RenderSettings.ambientEquatorColor = new Color(0.045f, 0.055f, 0.085f);
            RenderSettings.ambientGroundColor  = new Color(0.020f, 0.022f, 0.030f);
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
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) == null)
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
            const int W = 1024, H = 512;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var px = new Color[W * H];

            // Overcast, so no stars and no hard moon disc - just a vague brightening where the
            // moon sits behind the cloud. Must match the Moonlight rotation set above.
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
            ca.postExposure.overrideState = true; ca.postExposure.value = 0.20f;
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
            var vol = go.GetComponent<Volume>() ?? go.AddComponent<Volume>();
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
