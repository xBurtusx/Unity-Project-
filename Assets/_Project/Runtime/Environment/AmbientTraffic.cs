using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HorrorHouse
{
    /// <summary>
    /// Life on the street: now and then a car passes, now and then someone walks along the
    /// sidewalk. Rare on purpose - the point is that the world is not frozen, not that it is busy.
    ///
    /// Vehicles and people are built from primitives at spawn time, so there are no model assets
    /// to manage and swapping in real models later means changing only the two Build methods.
    /// Pedestrians are deliberately faceless: a smooth featureless head, dark clothing, and no
    /// detail that would read as someone looking at you.
    ///
    /// Paths come from the road and sidewalk renderers' bounds, so this follows the street if it
    /// is moved or resized. Debug keys in play mode: C spawns a car, P spawns a pedestrian.
    /// </summary>
    public class AmbientTraffic : MonoBehaviour
    {
        [Header("Street")]
        public Renderer road;
        public Renderer sidewalkNorth;
        public Renderer sidewalkSouth;
        [Tooltip("New Zealand drives on the left.")]
        public bool leftHandTraffic = true;

        [Header("Cars")]
        public Vector2 carInterval = new Vector2(35f, 80f);
        [Tooltip("Matches the recordings, which were made at 50 km/h (13.9 m/s).")]
        public Vector2 carSpeed = new Vector2(12.5f, 14.5f);
        [Tooltip("Real recorded pass-bys. One is chosen per car and timed to peak as the car draws level with you.")]
        public AudioClip[] carPassClips;
        [Tooltip("Fallback if no pass-by clips are assigned: a looping engine on the car.")]
        public AudioClip carLoop;
        public float firstCarDelay = 12f;

        [Header("Pedestrians")]
        public Vector2 walkerInterval = new Vector2(60f, 120f);
        public Vector2 walkerSpeed = new Vector2(1.25f, 1.55f);
        public AudioClip[] walkerSteps;
        public float firstWalkerDelay = 25f;

        [Header("Visible stretch")]
        [Tooltip("The road is 900 m long. Cars only run this far either side of its centre, so a pass " +
                 "takes seconds rather than minutes.")]
        public float carHalfLength = 150f;
        public float walkerHalfLength = 70f;

        public bool debugKeys = true;

        float nextCar, nextWalker;

        static readonly Color[] CarColours =
        {
            new Color(0.55f, 0.07f, 0.08f), new Color(0.07f, 0.12f, 0.28f), new Color(0.72f, 0.73f, 0.75f),
            new Color(0.12f, 0.13f, 0.14f), new Color(0.88f, 0.88f, 0.86f), new Color(0.10f, 0.28f, 0.20f),
        };
        static readonly Color[] Clothing =
        {
            new Color(0.10f, 0.11f, 0.14f), new Color(0.22f, 0.12f, 0.10f), new Color(0.14f, 0.18f, 0.16f),
            new Color(0.25f, 0.25f, 0.28f), new Color(0.08f, 0.08f, 0.08f),
        };

        void Start()
        {
            if (road == null) { var g = GameObject.Find("Road"); if (g != null) road = g.GetComponentInChildren<Renderer>(); }
            if (sidewalkNorth == null) { var g = GameObject.Find("Sidewalk_N"); if (g != null) sidewalkNorth = g.GetComponentInChildren<Renderer>(); }
            if (sidewalkSouth == null) { var g = GameObject.Find("Sidewalk_S"); if (g != null) sidewalkSouth = g.GetComponentInChildren<Renderer>(); }
            nextCar = Time.time + firstCarDelay;
            nextWalker = Time.time + firstWalkerDelay;
        }

        void Update()
        {
            if (Time.time >= nextCar)
            {
                SpawnCar();
                nextCar = Time.time + Random.Range(carInterval.x, carInterval.y);
            }
            if (Time.time >= nextWalker)
            {
                SpawnWalker();
                nextWalker = Time.time + Random.Range(walkerInterval.x, walkerInterval.y);
            }

            if (debugKeys)
            {
#if ENABLE_INPUT_SYSTEM
                var k = Keyboard.current;
                if (k != null && k.cKey.wasPressedThisFrame) SpawnCar();
                if (k != null && k.pKey.wasPressedThisFrame) SpawnWalker();
#else
                if (Input.GetKeyDown(KeyCode.C)) SpawnCar();
                if (Input.GetKeyDown(KeyCode.P)) SpawnWalker();
#endif
            }
        }

        // ------------------------------------------------------------ paths

        /// <summary>A straight path along the long axis of <paramref name="r"/>, offset sideways.</summary>
        static bool MakePath(Renderer r, bool forward, float sideOffset, bool leftHand, float margin, float maxHalf,
                             out Vector3 start, out Vector3 end)
        {
            start = end = Vector3.zero;
            if (r == null) return false;
            var b = r.bounds;
            bool alongX = b.size.x >= b.size.z;
            Vector3 axis = alongX ? Vector3.right : Vector3.forward;
            float half = Mathf.Min((alongX ? b.size.x : b.size.z) * 0.5f, maxHalf) + margin;

            Vector3 centre = b.center;
            centre.y = b.max.y;
            Vector3 dir = forward ? axis : -axis;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            Vector3 offset = (leftHand ? -right : right) * sideOffset;

            start = centre - dir * half + offset;
            end = centre + dir * half + offset;
            return true;
        }

        // ------------------------------------------------------------ cars

        void SpawnCar()
        {
            if (road == null) return;
            var b = road.bounds;
            float lane = Mathf.Min(b.size.x, b.size.z) * 0.25f;
            if (!MakePath(road, Random.value < 0.5f, lane, leftHandTraffic, 28f, carHalfLength, out var a, out var z)) return;

            var car = BuildCar(CarColours[Random.Range(0, CarColours.Length)], out var wheels, out var lights, out var headMats);
            car.transform.SetParent(transform, false);
            car.transform.position = a;
            car.transform.rotation = Quaternion.LookRotation((z - a).normalized);

            var v = car.AddComponent<TrafficVehicle>();
            v.start = a; v.end = z;
            v.speed = Random.Range(carSpeed.x, carSpeed.y);
            v.wheels = wheels;
            v.headlights = lights;
            v.lampMaterials = headMats;

            var src = car.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.volume = 0.85f;
            v.passSource = src;
            v.passClip = carPassClips != null && carPassClips.Length > 0
                ? carPassClips[Random.Range(0, carPassClips.Length)] : null;

            if (v.passClip != null)
            {
                // A recorded pass-by already contains the approach, the Doppler shift and the
                // fade, so it plays flat (2D) and is panned to follow the car instead.
                src.spatialBlend = 0f;
                car.AddComponent<AudioLowPassFilter>().cutoffFrequency = 22000f;
            }
            else if (carLoop != null)
            {
                src.clip = carLoop;
                src.loop = true;
                src.spatialBlend = 1f;
                src.dopplerLevel = 1f;
                src.minDistance = 6f;
                src.maxDistance = 75f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.Play();
            }
        }

        static GameObject BuildCar(Color paintColour, out Transform[] wheels, out Light[] headLights, out List<Material> lampMats)
        {
            var root = new GameObject("Car");
            var paint = Mat(paintColour, 0.85f, 0.55f);
            var glass = Mat(new Color(0.03f, 0.035f, 0.04f), 0.95f, 0f);
            var plastic = Mat(new Color(0.04f, 0.04f, 0.045f), 0.25f, 0f);
            var tyre = Mat(new Color(0.02f, 0.02f, 0.02f), 0.15f, 0f);

            Part(root, PrimitiveType.Cube, new Vector3(0, 0.55f, 0), new Vector3(1.80f, 0.55f, 4.30f), paint);          // body
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.07f, -0.15f), new Vector3(1.58f, 0.50f, 2.30f), glass);     // cabin glass
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.34f, -0.15f), new Vector3(1.60f, 0.05f, 2.14f), paint);     // roof
            Part(root, PrimitiveType.Cube, new Vector3(0, 0.36f, 2.18f), new Vector3(1.76f, 0.22f, 0.14f), plastic);    // bumpers
            Part(root, PrimitiveType.Cube, new Vector3(0, 0.36f, -2.18f), new Vector3(1.76f, 0.22f, 0.14f), plastic);

            wheels = new Transform[4];
            int w = 0;
            foreach (float x in new[] { -0.88f, 0.88f })
            foreach (float z in new[] { -1.35f, 1.35f })
            {
                var pivot = new GameObject("Wheel").transform;
                pivot.SetParent(root.transform, false);
                pivot.localPosition = new Vector3(x, 0.33f, z);
                var rim = Part(pivot.gameObject, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.66f, 0.11f, 0.66f), tyre);
                rim.transform.localRotation = Quaternion.Euler(0, 0, 90);
                wheels[w++] = pivot;
            }

            lampMats = new List<Material>();
            var headMat = Mat(new Color(0.9f, 0.9f, 0.85f), 0.9f, 0f);
            var tailMat = Mat(new Color(0.5f, 0.02f, 0.02f), 0.8f, 0f);
            lampMats.Add(headMat); lampMats.Add(tailMat);

            var lights = new List<Light>();
            foreach (float x in new[] { -0.6f, 0.6f })
            {
                Part(root, PrimitiveType.Cube, new Vector3(x, 0.66f, 2.17f), new Vector3(0.30f, 0.12f, 0.06f), headMat);
                Part(root, PrimitiveType.Cube, new Vector3(x * 1.05f, 0.68f, -2.17f), new Vector3(0.30f, 0.10f, 0.06f), tailMat);

                var go = new GameObject("Headlight");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(x, 0.66f, 2.2f);
                go.transform.localRotation = Quaternion.Euler(4f, 0, 0);
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot;
                l.color = new Color(1f, 0.95f, 0.82f);
                l.intensity = 22f;
                l.range = 24f;
                l.spotAngle = 58f;
                l.innerSpotAngle = 24f;
                l.shadows = LightShadows.None;
                l.enabled = false;
                lights.Add(l);
            }
            headLights = lights.ToArray();
            return root;
        }

        // ------------------------------------------------------------ pedestrians

        void SpawnWalker()
        {
            Renderer side = Random.value < 0.5f ? sidewalkNorth : sidewalkSouth;
            if (side == null) side = sidewalkNorth != null ? sidewalkNorth : sidewalkSouth;
            if (side == null) return;
            float jitter = Random.Range(-0.2f, 0.2f);
            if (!MakePath(side, Random.value < 0.5f, jitter, false, 6f, walkerHalfLength, out var a, out var z)) return;

            var p = BuildPerson(Clothing[Random.Range(0, Clothing.Length)], out var legs, out var arms, out var body);
            p.transform.SetParent(transform, false);
            p.transform.position = a;
            p.transform.rotation = Quaternion.LookRotation((z - a).normalized);

            var walker = p.AddComponent<TrafficPedestrian>();
            walker.start = a; walker.end = z;
            walker.speed = Random.Range(walkerSpeed.x, walkerSpeed.y);
            walker.legs = legs; walker.arms = arms; walker.body = body;
            walker.steps = walkerSteps;

            var src = p.AddComponent<AudioSource>();
            src.spatialBlend = 1f;
            src.minDistance = 2f;
            src.maxDistance = 24f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.dopplerLevel = 0f;
            walker.source = src;
        }

        static GameObject BuildPerson(Color clothes, out Transform[] legs, out Transform[] arms, out Transform body)
        {
            var root = new GameObject("Pedestrian");
            var cloth = Mat(clothes, 0.2f, 0f);
            var trousers = Mat(clothes * 0.7f, 0.2f, 0f);
            var head = Mat(new Color(0.46f, 0.43f, 0.41f), 0.25f, 0f);      // featureless, matte

            body = new GameObject("Body").transform;
            body.SetParent(root.transform, false);

            Part(body.gameObject, PrimitiveType.Capsule, new Vector3(0, 1.17f, 0), new Vector3(0.40f, 0.30f, 0.24f), cloth);   // torso
            Part(body.gameObject, PrimitiveType.Sphere, new Vector3(0, 1.64f, 0), new Vector3(0.24f, 0.26f, 0.24f), head);      // head: no face

            legs = new Transform[2];
            arms = new Transform[2];
            for (int i = 0; i < 2; i++)
            {
                float s = i == 0 ? -1f : 1f;
                var hip = new GameObject("Leg").transform;
                hip.SetParent(body, false);
                hip.localPosition = new Vector3(0.10f * s, 0.88f, 0);
                Part(hip.gameObject, PrimitiveType.Capsule, new Vector3(0, -0.44f, 0), new Vector3(0.14f, 0.44f, 0.14f), trousers);
                legs[i] = hip;

                var sh = new GameObject("Arm").transform;
                sh.SetParent(body, false);
                sh.localPosition = new Vector3(0.25f * s, 1.40f, 0);
                Part(sh.gameObject, PrimitiveType.Capsule, new Vector3(0, -0.30f, 0), new Vector3(0.10f, 0.31f, 0.10f), cloth);
                arms[i] = sh;
            }
            return root;
        }

        // ------------------------------------------------------------ helpers

        static GameObject Part(GameObject parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material m)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());           // decoration only: never blocks the player
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }

        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();

        static Material Mat(Color c, float smooth, float metal)
        {
            string key = $"{c.r:F2},{c.g:F2},{c.b:F2},{smooth:F2},{metal:F2}";
            if (cache.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
            cache[key] = m;
            return m;
        }
    }

    /// <summary>Drives one car along its path, spinning the wheels and handling headlights.</summary>
    public class TrafficVehicle : MonoBehaviour
    {
        public Vector3 start, end;
        public float speed = 10f;
        public Transform[] wheels;
        public Light[] headlights;
        public List<Material> lampMaterials;
        public AudioClip passClip;
        public AudioSource passSource;

        float travelled;
        float length;
        Transform listener;
        AudioLowPassFilter lowPass;

        static readonly Dictionary<AudioClip, float> peakCache = new Dictionary<AudioClip, float>();

        void Start()
        {
            length = Vector3.Distance(start, end);
            lowPass = GetComponent<AudioLowPassFilter>();
            var l = FindAnyObjectByType<AudioListener>();
            listener = l != null ? l.transform : null;
            PlanAudio();
        }

        /// <summary>
        /// Starts the recording so its loudest moment lands exactly when the car is closest to the
        /// listener: if the clip's peak is 6 s in and the car will be level with you in 11 s, the
        /// clip starts in 5 s. If the car is already past that point the clip starts part-way in.
        /// </summary>
        void PlanAudio()
        {
            if (passClip == null || passSource == null) return;

            Vector3 dir = (end - start).normalized;
            float along = listener != null ? Mathf.Clamp(Vector3.Dot(listener.position - start, dir), 0f, length) : length * 0.5f;
            float closestIn = along / Mathf.Max(0.1f, speed);
            float offset = closestIn - PeakTime(passClip);

            passSource.clip = passClip;
            passSource.loop = false;
            if (offset >= 0f) passSource.PlayDelayed(offset);
            else
            {
                passSource.time = Mathf.Min(-offset, passClip.length - 0.1f);
                passSource.Play();
            }
        }

        /// <summary>Time of the loudest stretch of a clip, cached. Falls back to the middle if unreadable.</summary>
        static float PeakTime(AudioClip clip)
        {
            if (peakCache.TryGetValue(clip, out float cached)) return cached;
            float peak = clip.length * 0.5f;
            var data = new float[clip.samples * clip.channels];
            if (clip.GetData(data, 0))
            {
                int win = Mathf.Max(1, (int)(0.4f * clip.frequency));
                int hop = Mathf.Max(1, (int)(0.1f * clip.frequency));
                float best = -1f;
                for (int s = 0; s + win < clip.samples; s += hop)
                {
                    float e = 0f;
                    for (int i = s; i < s + win; i += 4)
                        for (int c = 0; c < clip.channels; c++) { float v = data[i * clip.channels + c]; e += v * v; }
                    if (e > best) { best = e; peak = (s + win * 0.5f) / clip.frequency; }
                }
            }
            peakCache[clip] = peak;
            return peak;
        }

        void Update()
        {
            float step = speed * Time.deltaTime;
            travelled += step;
            transform.position = Vector3.Lerp(start, end, Mathf.Clamp01(travelled / Mathf.Max(0.01f, length)));
            foreach (var w in wheels)
                if (w != null) w.Rotate(Vector3.right, step / 0.33f * Mathf.Rad2Deg, Space.Self);

            float n = DayNightController.Instance != null ? DayNightController.Instance.night : 0f;
            bool on = n > 0.35f;
            foreach (var l in headlights) if (l != null) l.enabled = on;
            if (lampMaterials != null && lampMaterials.Count >= 2)
            {
                lampMaterials[0].EnableKeyword("_EMISSION");
                lampMaterials[1].EnableKeyword("_EMISSION");
                lampMaterials[0].SetColor("_EmissionColor", on ? new Color(1f, 0.95f, 0.8f) * 6f : Color.black);
                lampMaterials[1].SetColor("_EmissionColor", on ? new Color(1f, 0.05f, 0.03f) * 3f : new Color(0.25f, 0.01f, 0.01f));
            }

            if (passClip != null && passSource != null && listener != null)
            {
                // Follow the car across the stereo field, and muffle it if you are indoors.
                float side = Vector3.Dot(transform.position - listener.position, listener.right);
                passSource.panStereo = Mathf.Clamp(side / 22f, -1f, 1f);
                float indoors = AmbienceController.Indoors;
                passSource.volume = Mathf.Lerp(0.85f, 0.4f, indoors);
                if (lowPass != null) lowPass.cutoffFrequency = Mathf.Lerp(22000f, 1300f, indoors);
            }

            if (travelled >= length) Destroy(gameObject);
        }
    }

    /// <summary>Walks one pedestrian along a path with a simple swinging gait and matching footsteps.</summary>
    public class TrafficPedestrian : MonoBehaviour
    {
        public Vector3 start, end;
        public float speed = 1.4f;
        public Transform[] legs, arms;
        public Transform body;
        public AudioClip[] steps;
        public AudioSource source;

        float travelled, phase, length;
        int lastSign = 1;

        void Start() => length = Vector3.Distance(start, end);

        void Update()
        {
            float dt = Time.deltaTime;
            travelled += speed * dt;
            transform.position = Vector3.Lerp(start, end, Mathf.Clamp01(travelled / Mathf.Max(0.01f, length)));

            phase += dt * speed * 4.2f;                          // about two steps per second at a stroll
            float s = Mathf.Sin(phase);
            legs[0].localRotation = Quaternion.Euler(s * 30f, 0, 0);
            legs[1].localRotation = Quaternion.Euler(-s * 30f, 0, 0);
            arms[0].localRotation = Quaternion.Euler(-s * 22f, 0, 0);
            arms[1].localRotation = Quaternion.Euler(s * 22f, 0, 0);
            body.localPosition = new Vector3(0, Mathf.Abs(Mathf.Cos(phase)) * 0.025f, 0);

            int sign = s >= 0 ? 1 : -1;
            if (sign != lastSign)
            {
                lastSign = sign;
                if (source != null && steps != null && steps.Length > 0)
                {
                    source.pitch = Random.Range(0.92f, 1.06f);
                    source.PlayOneShot(steps[Random.Range(0, steps.Length)], 0.35f);
                }
            }

            if (travelled >= length) Destroy(gameObject);
        }
    }
}
