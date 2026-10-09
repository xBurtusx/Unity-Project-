using System;
using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// Footsteps that match what you are standing on.
    ///
    /// Reads <see cref="FirstPersonController.DistanceTravelled"/> (the controller already tracks
    /// it for exactly this purpose) and plays a step every stride, so the sound stays locked to
    /// the head-bob instead of running on its own timer. The surface is found by casting down and
    /// reading the collider's name and its parents' names, e.g. "FloorCollider_bathroom floor" is
    /// tile, "Road" is asphalt, "Ground" is grass.
    /// </summary>
    [RequireComponent(typeof(FirstPersonController))]
    public class FootstepAudio : MonoBehaviour
    {
        [Serializable]
        public class SurfaceSet
        {
            public string surface;
            public AudioClip[] clips;
            [Tooltip("Lets two surfaces share recordings yet still sound different: tile a little " +
                     "brighter, asphalt a little heavier.")]
            public float pitchScale = 1f;
        }

        public SurfaceSet[] surfaces;

        [Header("Stride (metres per step)")]
        public float walkStride = 0.78f;
        public float runStride = 1.05f;
        public float crouchStride = 0.58f;

        [Header("Level")]
        [Range(0f, 1f)] public float walkVolume = 0.5f;
        [Range(0f, 1f)] public float runVolume = 0.8f;
        [Range(0f, 1f)] public float crouchVolume = 0.25f;

        FirstPersonController fpc;
        AudioSource src;
        float nextAt;
        int lastIndex = -1;
        string lastSurface = "concrete";

        public string CurrentSurface => lastSurface;

        void Awake()
        {
            fpc = GetComponent<FirstPersonController>();
            src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;               // your own feet are not positional
        }

        void Start() => nextAt = fpc.DistanceTravelled + walkStride;

        float lastDistance, speed;

        void Update()
        {
            float d = fpc.DistanceTravelled;
            if (Time.deltaTime > 0f)
                speed = Mathf.Lerp(speed, Mathf.Max(0f, d - lastDistance) / Time.deltaTime, 0.25f);
            lastDistance = d;

            if (d < nextAt - 5f) nextAt = d + walkStride;      // teleported or reset
            if (d < nextAt || !fpc.IsMoving) return;

            bool crouch = fpc.IsCrouching;
            bool run = !crouch && speed > (fpc.walkSpeed + fpc.runSpeed) * 0.5f;
            float stride = crouch ? crouchStride : run ? runStride : walkStride;
            nextAt = d + stride;

            Play(crouch ? crouchVolume : run ? runVolume : walkVolume);
        }

        void Play(float volume)
        {
            lastSurface = DetectSurface();
            AudioClip[] clips = null;
            float pitchScale = 1f;
            foreach (var s in surfaces)
                if (s.surface == lastSurface) { clips = s.clips; pitchScale = s.pitchScale > 0f ? s.pitchScale : 1f; break; }
            if (clips == null || clips.Length == 0) return;

            int i = UnityEngine.Random.Range(0, clips.Length);
            if (clips.Length > 1 && i == lastIndex) i = (i + 1) % clips.Length;   // never the same step twice
            lastIndex = i;

            src.pitch = pitchScale * UnityEngine.Random.Range(0.95f, 1.05f);
            src.PlayOneShot(clips[i], volume * UnityEngine.Random.Range(0.9f, 1f));
        }

        string DetectSurface()
        {
            if (!Physics.Raycast(transform.position + Vector3.up * 0.3f, Vector3.down, out var hit, 2.2f, ~0, QueryTriggerInteraction.Ignore))
                return lastSurface;
            return Classify(hit.collider);
        }

        /// <summary>Public so other systems (and tests) can ask "what is this made of?".</summary>
        public static string Classify(Collider c)
        {
            string name = c.name.ToLowerInvariant();
            var p = c.transform.parent;
            if (p != null) name += " " + p.name.ToLowerInvariant();
            var r = c.GetComponent<Renderer>();
            if (r != null && r.sharedMaterial != null) name += " " + r.sharedMaterial.name.ToLowerInvariant();

            if (Has(name, "bathroom", "tile", "marble", "kitchen")) return "tile";
            if (Has(name, "bedroom", "carpet", "rug")) return "carpet";
            if (Has(name, "porch", "deck", "step", "stair", "hardwood", "oak", "wood", "living", "hall", "floor")) return "wood";
            if (Has(name, "road", "asphalt", "street")) return "asphalt";
            if (Has(name, "sidewalk", "walk", "path", "paving", "drive", "concrete", "pave")) return "concrete";
            if (Has(name, "ground", "grass", "lawn", "soil", "dirt", "yard", "lot", "terrain")) return "grass";
            return "concrete";
        }

        static bool Has(string s, params string[] keys)
        {
            foreach (var k in keys) if (s.Contains(k)) return true;
            return false;
        }
    }
}
