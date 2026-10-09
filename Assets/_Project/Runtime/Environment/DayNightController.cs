using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HorrorHouse
{
    /// <summary>
    /// Day and night for the whole street, driven by one number: <see cref="night"/>, 0 = midday,
    /// 1 = deep night.
    ///
    /// Everything that changes with the time of day reads that value, so nothing has to be told
    /// separately: the sun and moon, the sky, the ambient light, the fog, the post-processing,
    /// the exterior lights (porch, street) and the ambience (crickets, static) all follow it.
    /// Cars read it to decide whether to switch their headlights on.
    ///
    /// Press N in play mode to flip between day and night. From code: <see cref="SetNight"/>.
    /// In the Editor, Horror House > Set Day / Set Night previews either look without playing.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class DayNightController : MonoBehaviour
    {
        public static DayNightController Instance { get; private set; }

        [Header("Scene references")]
        public Light sun;
        public Light moon;
        public Material daySky;
        public Material nightSky;

        [Header("State")]
        [Range(0f, 1f)] public float night;
        [Tooltip("Where the scene starts when you press Play.")]
        public bool startAtNight;
        public float transitionSeconds = 5f;
        public bool allowKeyToggle = true;

        [Header("Daylight indoors")]
        [Tooltip("Soft ceiling lights standing in for skylight through the windows. Full strength by " +
                 "day, fading out as the sun sets - so interiors read as daylit, then go properly dark.")]
        public Light[] daylightFills;
        public float fillIntensity = 12f;

        [Header("Lights that switch on at night")]
        [Tooltip("Light groups (see LightFixture.group) that come on at dusk and go off at dawn.")]
        public string[] nightOnGroups = { "Porch", "Street", "livingroom", "kitchen" };

        [Header("Day look")]
        public float daySkyExposure = 1f;
        public float daySunIntensity = 3f;
        public Color dayFog = new Color(0.66f, 0.74f, 0.84f);
        public float dayFogDensity = 0.0016f;
        public float dayReflection = 1f;
        public float dayBloom = 0.18f;

        [Header("Night look")]
        [Tooltip("The generated night sky is dark by design; this lifts it to a readable deep blue.")]
        public float nightSkyExposure = 2.4f;
        public float moonIntensity = 0.65f;
        public Color nightFog = new Color(0.035f, 0.045f, 0.075f);
        public float nightFogDensity = 0.0075f;
        public float nightReflection = 0.45f;
        public float nightBloom = 0.85f;
        [Tooltip("Post-exposure at night (stops). Negative is darker.")]
        public float nightExposure = -0.35f;
        public float dayVignette = 0.12f;
        public float nightVignette = 0.34f;
        public Color nightAmbientSky = new Color(0.13f, 0.17f, 0.27f);
        public Color nightAmbientEquator = new Color(0.10f, 0.12f, 0.19f);
        public Color nightAmbientGround = new Color(0.05f, 0.055f, 0.08f);

        [Tooltip("At night the baked (daytime) lighting is replaced by this flat level, on lightmapped " +
                 "surfaces AND light probes alike, so walls, hedges, railings and road all sit in the " +
                 "same dim shadow. Real lights (street, porch, moon) add on top. Raise to see more.")]
        public Color nightBakedLight = new Color(0.10f, 0.12f, 0.20f);
        [Tooltip("The same, but for light probes inside the house, so rooms are darker and rely on their lamps.")]
        public Color nightIndoorLight = new Color(0.045f, 0.055f, 0.085f);
        public Bounds indoorBounds;

        static LightmapData[] dayMaps;
        static LightProbes dayProbes, nightProbes;
        static Texture2D standInColor, standInDir;

        static bool MapsAreNight()
        {
            var m = LightmapSettings.lightmaps;
            return standInColor != null && m != null && m.Length > 0 && m[0].lightmapColor == standInColor;
        }
        static bool ProbesAreNight() => nightProbes != null && LightmapSettings.lightProbes == nightProbes;

        /// <summary>
        /// Baked lightmaps AND baked light probes both hold DAYTIME light. Left alone at night they
        /// keep surfaces sunlit; removed, lightmapped walls go pitch black while probe-lit props
        /// still glow. So at night both are swapped for the same flat dim level, and the originals
        /// are put back for the day. It checks what is actually showing each time rather than
        /// trusting remembered state, because Unity reloads the scene's lighting on its own (entering
        /// and leaving Play, reopening the scene). Nothing here touches an asset.
        /// </summary>
        public static void SetBakedLight(bool on, Color level)
        {
            // Whatever is showing that is not ours is the real (daytime) bake: remember it.
            if (!MapsAreNight() && LightmapSettings.lightmaps != null && LightmapSettings.lightmaps.Length > 0)
                dayMaps = LightmapSettings.lightmaps;
            if (!ProbesAreNight() && LightmapSettings.lightProbes != null && LightmapSettings.lightProbes.count > 0)
                dayProbes = LightmapSettings.lightProbes;

            if (on)
            {
                if (MapsAreNight() && dayMaps != null) LightmapSettings.lightmaps = dayMaps;
                if (ProbesAreNight() && dayProbes != null) LightmapSettings.lightProbes = dayProbes;
                return;
            }

            if (standInColor == null)
            {
                standInColor = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true) { hideFlags = HideFlags.HideAndDontSave };
                standInDir = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                standInDir.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
                standInDir.Apply();
            }
            standInColor.SetPixel(0, 0, new Color(level.r, level.g, level.b, 1f));
            standInColor.Apply();

            if (!MapsAreNight() && dayMaps != null)
            {
                var stand = new LightmapData[dayMaps.Length];
                for (int i = 0; i < stand.Length; i++) stand[i] = new LightmapData { lightmapColor = standInColor, lightmapDir = standInDir };
                LightmapSettings.lightmaps = stand;
            }
            if (!ProbesAreNight() && dayProbes != null)
            {
                if (nightProbes == null || nightProbes.count != dayProbes.count)
                {
                    nightProbes = Object.Instantiate(dayProbes);
                    nightProbes.hideFlags = HideFlags.HideAndDontSave;
                }
                var sh = new UnityEngine.Rendering.SphericalHarmonicsL2[dayProbes.count];
                var inst = Instance;
                bool split = inst != null && inst.indoorBounds.size.sqrMagnitude > 0.01f;
                var pos = dayProbes.positions;
                for (int i = 0; i < sh.Length; i++)
                {
                    sh[i].Clear();
                    bool inside = split && inst.indoorBounds.Contains(pos[i]);
                    sh[i].AddAmbientLight(inside ? inst.nightIndoorLight : level);
                }
                nightProbes.bakedProbes = sh;
                LightmapSettings.lightProbes = nightProbes;
            }
        }

        public static void SetBakedLight(bool on) =>
            SetBakedLight(on, Instance != null ? Instance.nightBakedLight : new Color(0.10f, 0.12f, 0.20f));

        float target;
        bool lightsAreOn;
        bool warnedProfile;
        Material dayInstance, nightInstance;
        VolumeProfile profile;

        public bool IsNight => night > 0.5f;

        // ------------------------------------------------------------ lifecycle

        void Awake()
        {
            Instance = this;
            if (Application.isPlaying)
            {
                if (daySky != null) dayInstance = new Material(daySky);
                if (nightSky != null) nightInstance = new Material(nightSky);
                try
                {
                    var vol = FindAnyObjectByType<Volume>();
                    if (vol != null) profile = vol.profile;    // an instance: runtime edits never touch the asset
                }
                catch (System.Exception e)
                {
                    profile = null;
                    Debug.LogWarning("[DayNight] Could not instance the volume profile (damaged asset): " + e.Message);
                }
            }
        }

        void Start()
        {
            if (!Application.isPlaying) return;
            night = target = startAtNight ? 1f : 0f;
            Apply(night, true);
        }

        // In the Editor the scene reloads with its daytime baked lighting; put the night back.
        void OnEnable()
        {
            Instance = this;
            if (!Application.isPlaying && night > 0.5f) SetBakedLight(false, nightBakedLight);
        }

        void OnDisable() => SetBakedLight(true, nightBakedLight);

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            // Cheap when nothing changed; catches Unity reloading the day bake behind our back.
            SetBakedLight(night <= 0.5f, nightBakedLight);
            if (!Application.isPlaying) return;

            if (allowKeyToggle && TogglePressed()) Toggle();

            if (!Mathf.Approximately(night, target))
            {
                night = Mathf.MoveTowards(night, target, Time.deltaTime / Mathf.Max(0.1f, transitionSeconds));
                Apply(night, true);
            }
        }

        static bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.nKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.N);
#endif
        }

        // ------------------------------------------------------------ control

        public void Toggle() => SetNight(target < 0.5f);

        public void SetNight(bool value, bool instant = false)
        {
            target = value ? 1f : 0f;
            if (instant || !Application.isPlaying)
            {
                night = target;
                Apply(night, Application.isPlaying);
            }
        }

        // ------------------------------------------------------------ the look

        /// <summary>
        /// 0 below <paramref name="a"/>, 1 above <paramref name="b"/>, smooth between. (Not
        /// Mathf.SmoothStep, which interpolates BETWEEN its first two arguments rather than
        /// treating them as edges - using it that way left the sun at 45% at midnight.)
        /// </summary>
        static float Edge(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

        /// <param name="n">0 day .. 1 night.</param>
        /// <param name="runtime">At runtime sky exposure is crossfaded on instance materials; in the
        /// Editor only the two endpoint states are applied, so no asset is left half-faded.</param>
        public void Apply(float n, bool runtime)
        {
            float duskSun  = 1f - Edge(0f, 0.55f, n);   // sun fades out over the first half
            float duskMoon = Edge(0.35f, 1f, n);        // moon fades in over the second
            bool nightHalf = n > 0.5f;

            if (sun != null)
            {
                sun.intensity = daySunIntensity * duskSun;
                sun.enabled = duskSun > 0.01f;
            }
            if (moon != null)
            {
                moon.intensity = moonIntensity * duskMoon;
                moon.enabled = duskMoon > 0.01f;
                moon.shadows = LightShadows.Soft;
                if (sun == null || !sun.enabled) RenderSettings.sun = moon;
                else RenderSettings.sun = sun;
            }

            if (daylightFills != null)
                foreach (var f in daylightFills)
                {
                    if (f == null) continue;
                    f.intensity = fillIntensity * duskSun;
                    f.enabled = duskSun > 0.01f;
                }

            // Sky: crossfade through dark by dipping each sky's exposure around the swap.
            // Made lazily: with fast Enter Play Mode (no reload) Awake does not run again before Play.
            if (runtime && dayInstance == null && daySky != null) dayInstance = new Material(daySky);
            if (runtime && nightInstance == null && nightSky != null) nightInstance = new Material(nightSky);
            var chosen = nightHalf ? (runtime ? nightInstance : nightSky) : (runtime ? dayInstance : daySky);
            if (chosen != null)
            {
                RenderSettings.skybox = chosen;
                if (chosen.HasProperty("_Exposure"))
                {
                    float full = nightHalf ? nightSkyExposure : daySkyExposure;
                    float edge = nightHalf ? Mathf.InverseLerp(0.5f, 1f, n) : 1f - Mathf.InverseLerp(0f, 0.5f, n);
                    // Runtime dips toward dark around the swap; the Editor only ever shows the endpoints.
                    chosen.SetFloat("_Exposure", runtime ? Mathf.Lerp(0.15f, full, edge) : full);
                }
            }

            SetBakedLight(!nightHalf, nightBakedLight);

            if (nightHalf)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = nightAmbientSky;
                RenderSettings.ambientEquatorColor = nightAmbientEquator;
                RenderSettings.ambientGroundColor = nightAmbientGround;
                RenderSettings.ambientIntensity = 1f;
            }
            else
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 1f;
            }

            float k = Mathf.SmoothStep(0f, 1f, n);
            RenderSettings.fogColor = Color.Lerp(dayFog, nightFog, k);
            RenderSettings.fogDensity = Mathf.Lerp(dayFogDensity, nightFogDensity, k);
            RenderSettings.reflectionIntensity = Mathf.Lerp(dayReflection, nightReflection, k);

            // Lights are switched once as the sun crosses the horizon, not every frame. This comes
            // BEFORE the post-processing touch below: a damaged volume profile must never be able
            // to stop the lamps coming on.
            if (nightHalf != lightsAreOn)
            {
                lightsAreOn = nightHalf;
                foreach (var g in nightOnGroups) LightFixture.SetGroup(g, nightHalf);
                if (!nightHalf) LightFixture.SetGroup("Street", false);
            }

            try
            {
                if (runtime && profile == null && FindAnyObjectByType<Volume>() is Volume rv) profile = rv.profile;   // instance, never the asset
                var p = profile != null ? profile : (FindAnyObjectByType<Volume>() is Volume v ? v.sharedProfile : null);
                if (p != null && p.TryGet(out Bloom bloom))
                    bloom.intensity.Override(Mathf.Lerp(dayBloom, nightBloom, k));
                if (p != null && p.TryGet(out ColorAdjustments ca))
                    ca.postExposure.Override(Mathf.Lerp(0f, nightExposure, k));
                if (p != null && p.TryGet(out Vignette vg))
                    vg.intensity.Override(Mathf.Lerp(dayVignette, nightVignette, k));
            }
            catch (System.Exception e)
            {
                if (!warnedProfile) { warnedProfile = true; Debug.LogWarning("[DayNight] Post-processing profile is damaged; skipping bloom. Run Horror House > Realism Pass to repair it. " + e.Message); }
            }

            if (!runtime || Mathf.Approximately(n, 0f) || Mathf.Approximately(n, 1f))
                DynamicGI.UpdateEnvironment();
        }
    }
}
