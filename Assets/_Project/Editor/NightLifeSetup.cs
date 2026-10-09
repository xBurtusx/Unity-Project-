using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Wires day/night, lighting, sound, traffic and footsteps into the open scene.
    /// Idempotent: run it as often as you like, it finds and updates what it made before.
    ///
    ///  * Warm, bright fixtures: porch, interior ceilings and the street lamps (which gain a
    ///    LightFixture each, so the day/night system and light switches can drive them).
    ///  * Daylight fills so rooms are bright by day (the baker cannot pass light through glass).
    ///  * All sounds assigned: doors, switches, footsteps per surface, fridge, crickets, static,
    ///    wind, car pass-bys.
    ///  * Traffic and pedestrians.
    ///
    /// Run "Generate Audio" and "Realism Pass" first. Preview with Horror House > Set Day / Set Night.
    /// </summary>
    public static class NightLifeSetup
    {
        const string DaySkyPath   = "Assets/_Project/Settings/M_HdriSky.mat";
        const string NightSkyPath = "Assets/_Project/Settings/M_NightSky.mat";

        static readonly Color Warm      = new Color(1.00f, 0.84f, 0.64f);   // ~3000 K: warm, but not orange
        static readonly Color PorchWarm = new Color(1.00f, 0.72f, 0.42f);
        static readonly Color LampWarm  = new Color(1.00f, 0.74f, 0.44f);   // sodium-ish street lamp
        static readonly Color Daylight  = new Color(0.84f, 0.91f, 1.00f);

        static AudioClip Clip(string name) =>
            AssetDatabase.LoadAssetAtPath<AudioClip>($"{ProceduralAudio.Dir}/{name}.wav");

        /// <summary>A real recording from Audio/Recorded, or the synthesised stand-in if it is missing.</summary>
        static AudioClip Rec(string relativePath, string generatedFallback)
        {
            var c = AssetDatabase.LoadAssetAtPath<AudioClip>($"{RecordedAudioPrep.RecDir}/{relativePath}");
            if (c == null)
            {
                Debug.LogWarning($"[NightLife] Recording '{relativePath}' not found; using the generated '{generatedFallback}'.");
                c = Clip(generatedFallback);
            }
            return c;
        }

        static AudioClip[] Steps(string kind, string generatedSurface) =>
            Enumerable.Range(0, 5).Select(i => Rec($"Footsteps/footstep_{kind}_00{i}.ogg", $"step_{generatedSurface}_{i % ProceduralAudio.Variants}")).ToArray();

        [MenuItem("Horror House/Night Life Setup", priority = 80)]
        public static void Run()
        {
            DayNightController dn = null;
            Step("day/night rig", () => dn = BuildRig());
            if (dn != null) Step("moon", () => MoonSetup.Build(dn));
            Step("street lamps", BuildStreetLamps);
            Step("interior + porch lights", BuildFixtures);
            if (dn != null) Step("daylight fills", () => BuildDaylightFills(dn));
            Step("door + switch sounds", WireDoorsAndSwitches);
            Step("footsteps", WireFootsteps);
            Step("fridge", WireFridge);
            if (dn != null) Step("ambience + traffic", () => WireAmbienceAndTraffic(dn));
            if (dn != null) Step("preview day", () => Preview(false));

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[NightLife] Setup complete. Press Play, then N to flip day/night. C spawns a car, P a pedestrian.");
        }

        static void Step(string name, System.Action a)
        {
            try { a(); Debug.Log($"[NightLife] {name}: ok"); }
            catch (System.Exception e) { Debug.LogError($"[NightLife] '{name}' failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }

        // ------------------------------------------------------------ preview

        [MenuItem("Horror House/Set Day", priority = 81)]
        public static void SetDay() => Preview(false);

        [MenuItem("Horror House/Set Night", priority = 82)]
        public static void SetNight() => Preview(true);

        static void Preview(bool night)
        {
            var dn = Object.FindAnyObjectByType<DayNightController>();
            if (dn == null) { Debug.LogWarning("[NightLife] No DayNightController: run Night Life Setup first."); return; }
            dn.SetNight(night, true);

            // LightFixture only registers itself while playing, so switch the fixtures directly.
            foreach (var fx in Object.FindObjectsByType<LightFixture>(FindObjectsSortMode.None))
            {
                bool on = night && dn.nightOnGroups.Contains(fx.group);
                fx.isOn = on;
                var l = fx.GetComponentInChildren<Light>(true);
                if (l != null) l.enabled = on;
                fx.RefreshInEditor();
                EditorUtility.SetDirty(fx);
            }
            // MarkSceneDirty throws in Play mode, where there is nothing to save anyway.
            var scene = EditorSceneManager.GetActiveScene();
            if (!Application.isPlaying && scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }

        // ------------------------------------------------------------ rig

        static DayNightController BuildRig()
        {
            var rig = GetOrCreate("DayNight", null);

            var moonGo = GetOrCreate("Moonlight", null);
            moonGo.transform.rotation = MoonSetup.MoonLightRotation;
            var moon = GetOrAdd<Light>(moonGo);
            moon.type = LightType.Directional;
            moon.color = new Color(0.60f, 0.72f, 1.00f);
            moon.intensity = 0.07f;             // faint: the night is dark, the real moon is a sight not a lamp
            moon.shadows = LightShadows.Soft;
            moon.shadowStrength = 1f;           // deep, hard-edged shadows
            moon.shadowBias = 0.04f;
            moon.lightmapBakeType = LightmapBakeType.Realtime;
            moon.enabled = false;

            var dn = GetOrAdd<DayNightController>(rig);
            var sunGo = GameObject.Find("Sun");
            dn.sun = sunGo != null ? sunGo.GetComponent<Light>() : null;
            dn.moon = moon;
            dn.daySky = AssetDatabase.LoadAssetAtPath<Material>(DaySkyPath);
            dn.nightSky = AssetDatabase.LoadAssetAtPath<Material>(NightSkyPath);
            dn.fillIntensity = 34f;             // rooms should read as properly daylit, not merely visible

            // Night: dark and uneasy. Little moonlight, almost no ambient fill, thick dark fog, and
            // only the exterior lights on, so the house is a lit island and the rest is shadow.
            dn.moonIntensity = 0.14f;                       // a tiny trace of moonlight
            dn.nightAmbientSky     = new Color(0.03f, 0.04f, 0.075f);
            dn.nightAmbientEquator = new Color(0.022f, 0.03f, 0.055f);
            dn.nightAmbientGround  = new Color(0.004f, 0.005f, 0.009f);
            dn.nightFog = new Color(0.001f, 0.0015f, 0.003f);
            dn.nightFogDensity = 0.016f;
            dn.nightReflection = 0.02f;
            dn.nightSkyExposure = 1.3f;
            dn.nightBloom = 0.6f;
            dn.nightExposure = 0f;
            dn.nightBakedLight = new Color(0.17f, 0.21f, 0.33f);   // dim moonlit shadow outdoors
            dn.nightIndoorLight = new Color(0.045f, 0.055f, 0.085f); // rooms rely on their own lights
            dn.nightVignette = 0.34f;
            dn.nightOnGroups = new[] { "Porch", "Street" };
            if (dn.daySky == null) Debug.LogWarning("[NightLife] Day sky material missing: run Realism Pass first.");
            if (dn.nightSky == null) Debug.LogWarning("[NightLife] Night sky material missing: run Atmosphere setup once.");
            EditorUtility.SetDirty(dn);
            return dn;
        }

        // ------------------------------------------------------------ lights

        static void BuildStreetLamps()
        {
            var root = GameObject.Find("StreetLamps");
            if (root == null) throw new System.Exception("No StreetLamps object in the scene");
            // The faulty lamp: always the one furthest along the street, so it is the same every run.
            Transform faulty = null;
            foreach (Transform lamp in root.transform)
                if (faulty == null || lamp.position.x > faulty.position.x) faulty = lamp;

            foreach (Transform lamp in root.transform)
            {
                var l = lamp.GetComponentInChildren<Light>(true);
                if (l == null) continue;
                l.type = LightType.Spot;
                l.color = LampWarm;
                l.intensity = 34f;      // the street lamps are the main light of the night
                l.range = 30f;
                l.spotAngle = 118f;
                l.innerSpotAngle = 60f;
                l.lightmapBakeType = LightmapBakeType.Realtime;
                l.enabled = false;
                // Shadow maps are the expensive part: only the lamps near the house cast them.
                l.shadows = Mathf.Abs(lamp.position.x) < 25f ? LightShadows.Soft : LightShadows.None;

                var fx = GetOrAdd<LightFixture>(lamp.gameObject);
                fx.fixtureId = lamp.name;
                fx.group = "Street";
                fx.room = "Street";
                fx.fixtureType = "street";
                fx.isOn = false;
                fx.glowIntensity = 5f;
                fx.spontaneousFlickerChance = lamp == faulty ? 0.12f : 0f;
                EditorUtility.SetDirty(fx);
                EditorUtility.SetDirty(l);
            }
        }

        static void BuildFixtures()
        {
            foreach (var fx in Object.FindObjectsByType<LightFixture>(FindObjectsSortMode.None))
            {
                if (fx.group == "Street") continue;
                var l = fx.GetComponentInChildren<Light>(true);
                if (l == null) continue;
                bool porch = fx.group == "Porch";
                l.type = LightType.Spot;
                l.color = porch ? PorchWarm : Warm;
                l.intensity = porch ? 10f : 15f;
                l.range = porch ? 9f : 10f;
                // A bulb lights the walls too, not just the floor below it: a very wide cone gets
                // most of the way to that without the cost of six shadow maps per point light.
                l.spotAngle = porch ? 120f : 165f;
                l.innerSpotAngle = porch ? 60f : 90f;
                l.shadows = LightShadows.Soft;
                l.shadowStrength = 0.9f;
                l.lightmapBakeType = LightmapBakeType.Realtime;
                fx.glowIntensity = 3.5f;
                fx.isOn = false;
                l.enabled = false;
                EditorUtility.SetDirty(fx);
                EditorUtility.SetDirty(l);
            }
        }

        static void BuildDaylightFills(DayNightController dn)
        {
            var rig = GetOrCreate("DaylightFills", dn.transform);
            for (int i = rig.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(rig.transform.GetChild(i).gameObject);

            var lights = new System.Collections.Generic.List<Light>();
            foreach (var probe in Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None)
                         .Where(p => p.name.StartsWith("ReflectionProbe_")))
            {
                var b = probe.bounds;
                var go = new GameObject("DaylightFill_" + probe.name.Replace("ReflectionProbe_", ""));
                go.transform.SetParent(rig.transform, true);
                go.transform.position = new Vector3(b.center.x, b.max.y - 0.3f, b.center.z);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);       // straight down
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot;
                l.color = Daylight;
                l.intensity = dn.fillIntensity;
                l.spotAngle = 150f;
                l.innerSpotAngle = 100f;
                l.range = Mathf.Max(b.size.x, b.size.z) * 0.8f + b.size.y;
                l.shadows = LightShadows.None;
                l.lightmapBakeType = LightmapBakeType.Realtime;
                lights.Add(l);
            }
            dn.daylightFills = lights.ToArray();
            EditorUtility.SetDirty(dn);
            Debug.Log($"[NightLife] {lights.Count} daylight fills (one per room).");
        }

        // ------------------------------------------------------------ sound

        static void WireDoorsAndSwitches()
        {
            foreach (var d in Object.FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                d.openClip = Rec("Slices/door_open.wav", "door_open");
                d.closeClip = Rec("Slices/door_close.wav", "door_close");
                d.lockedClip = Rec("Slices/door_locked.wav", "door_locked");
                d.volume = 0.85f;
                EditorUtility.SetDirty(d);
            }
            foreach (var s in Object.FindObjectsByType<LightSwitch>(FindObjectsSortMode.None))
            {
                s.clickOn = Rec("Slices/switch_on.wav", "switch_on");
                s.clickOff = Rec("Slices/switch_off.wav", "switch_off");
                s.clickVolume = 0.8f;
                EditorUtility.SetDirty(s);
            }
        }

        static void WireFootsteps()
        {
            var player = GameObject.Find("Player");
            if (player == null) throw new System.Exception("No Player object");
            var fs = GetOrAdd<FootstepAudio>(player);
            // Real recordings exist for concrete, grass, wood and carpet. Tile and asphalt reuse the
            // concrete steps, pitched up (brighter, harder) and down (heavier) so they still differ.
            fs.surfaces = new[]
            {
                new FootstepAudio.SurfaceSet { surface = "concrete", clips = Steps("concrete", "concrete"), pitchScale = 1.00f },
                new FootstepAudio.SurfaceSet { surface = "asphalt",  clips = Steps("concrete", "asphalt"),  pitchScale = 0.86f },
                new FootstepAudio.SurfaceSet { surface = "grass",    clips = Steps("grass", "grass"),       pitchScale = 1.00f },
                new FootstepAudio.SurfaceSet { surface = "wood",     clips = Steps("wood", "wood"),         pitchScale = 1.00f },
                new FootstepAudio.SurfaceSet { surface = "tile",     clips = Steps("concrete", "tile"),     pitchScale = 1.14f },
                new FootstepAudio.SurfaceSet { surface = "carpet",   clips = Steps("carpet", "carpet"),     pitchScale = 1.00f },
            };
            EditorUtility.SetDirty(fs);
        }

        static void WireFridge()
        {
            var fridge = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "kitchenFridgeLarge");
            if (fridge == null) throw new System.Exception("kitchenFridgeLarge not found");
            var hum = GetOrAdd<ApplianceHum>(fridge.gameObject);
            hum.hum = Rec("fridge_1157.mp3", "fridge_hum");
            hum.volume = 0.5f;
            hum.minDistance = 0.7f;
            hum.maxDistance = 7f;
            EditorUtility.SetDirty(hum);
        }

        static void WireAmbienceAndTraffic(DayNightController dn)
        {
            var amb = GetOrAdd<AmbienceController>(GetOrCreate("Ambience", dn.transform));
            amb.crickets = Rec("crickets_night_1880.mp3", "cricket_loop");
            amb.staticLoop = Rec("pink_noise_0432.mp3", "night_static");
            amb.wind = Rec("wind_0595.mp3", "wind_loop");
            amb.cricketVolume = 0.17f;           // soft and distant
            amb.cricketPitch = 0.76f;            // slower chirping, lower and gentler
            amb.staticVolume = 0.04f;            // the recorded pink noise is much louder than the old synthesised hiss
            amb.windVolume = 0.26f;              // a soft, steady blow, outdoors only

            // The sealed interior: the floors of the player's house (porch excluded, it is outdoors).
            var house = GameObject.Find("PlayerHouse");
            if (house != null)
            {
                bool any = false;
                var b = new Bounds();
                foreach (var c in house.GetComponentsInChildren<Collider>())
                {
                    if (!c.name.StartsWith("FloorCollider") || c.name.ToLowerInvariant().Contains("porch")) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
                if (any)
                {
                    b.Expand(new Vector3(-0.3f, 0f, -0.3f));          // just inside the walls
                    b.Encapsulate(new Vector3(b.center.x, b.min.y + 4f, b.center.z));   // up to the ceiling
                    amb.houseBounds = b;
                    var dnc = Object.FindFirstObjectByType<DayNightController>();
                    if (dnc != null) { dnc.indoorBounds = b; EditorUtility.SetDirty(dnc); }
                    Debug.Log($"[NightLife] Sealed-house bounds: centre {b.center:F1}, size {b.size:F1}");
                }
            }
            amb.exteriorDoors = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
            EditorUtility.SetDirty(amb);

            var traffic = GetOrAdd<AmbientTraffic>(GetOrCreate("Traffic", dn.transform));
            traffic.carLoop = Clip("car_pass_loop");      // fallback only
            traffic.carPassClips = new[]
            {
                Rec("Cars/car_pass_a_2996.mp3", "car_pass_loop"),
                Rec("Cars/car_pass_b_2987.mp3", "car_pass_loop"),
                Rec("Cars/car_pass_c_2988.mp3", "car_pass_loop"),
            };
            traffic.walkerSteps = Steps("concrete", "concrete");
            var r = GameObject.Find("Road"); if (r != null) traffic.road = r.GetComponentInChildren<Renderer>();
            var n = GameObject.Find("Sidewalk_N"); if (n != null) traffic.sidewalkNorth = n.GetComponentInChildren<Renderer>();
            var s = GameObject.Find("Sidewalk_S"); if (s != null) traffic.sidewalkSouth = s.GetComponentInChildren<Renderer>();
            EditorUtility.SetDirty(traffic);
        }

        // ------------------------------------------------------------ helpers

        static GameObject GetOrCreate(string name, Transform parent)
        {
            var existing = GameObject.Find(name);
            if (existing != null) return existing;
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component =>
            go.TryGetComponent(out T c) ? c : go.AddComponent<T>();
    }
}
