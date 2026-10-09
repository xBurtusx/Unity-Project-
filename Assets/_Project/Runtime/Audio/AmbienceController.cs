using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// The sound of the street itself: wind by day, crickets and a faint electrical static at
    /// night. Levels follow <see cref="DayNightController.night"/>, so the crossfade happens with
    /// the sky rather than on a separate timer.
    ///
    /// When the player is under a roof the outdoor layers are muffled and quieter, the way a
    /// closed house shuts out most of the night. The static is left alone: it is meant to be
    /// heard from inside.
    /// </summary>
    public class AmbienceController : MonoBehaviour
    {
        [Header("Clips")]
        public AudioClip crickets;
        public AudioClip staticLoop;
        public AudioClip wind;

        [Header("Levels")]
        [Range(0f, 1f)] public float cricketVolume = 0.38f;
        [Range(0f, 1f)] public float staticVolume = 0.11f;
        [Range(0f, 1f)] public float windVolume = 0.14f;

        [Tooltip("Below 1 slows the chirping and lowers its pitch together: softer and more distant.")]
        [Range(0.5f, 1.2f)] public float cricketPitch = 0.78f;

        [Header("Sealed house")]
        [Tooltip("The inside of the player's house. While you are in it with the exterior doors closed the wind is silent.")]
        public Bounds houseBounds;
        public Door[] exteriorDoors;
        [Range(0f, 1f)] public float openDoorOutdoorGain = 0.45f;

        [Header("Indoors")]
        [Range(0f, 1f)] public float indoorOutdoorGain = 0.4f;
        public float indoorCutoff = 1400f;
        public float muffleSpeed = 2.5f;

        /// <summary>0 = outdoors, 1 = under a roof. Other outdoor sounds (passing cars) read this to muffle themselves.</summary>
        public static float Indoors { get; private set; }

        AudioSource cricketSrc, staticSrc, windSrc;
        AudioLowPassFilter cricketLp, windLp;
        Transform listener;
        float indoors;                       // 0 outside .. 1 under a roof, smoothed
        float sealedIn;                      // 0 outside .. 1 inside with the doors shut, smoothed
        float nextProbe;
        bool probeResult;

        void Awake()
        {
            cricketSrc = MakeLoop("Crickets", crickets, out cricketLp);
            cricketSrc.pitch = cricketPitch;
            staticSrc  = MakeLoop("Static", staticLoop, out _);
            windSrc    = MakeLoop("Wind", wind, out windLp);
        }

        AudioSource MakeLoop(string name, AudioClip clip, out AudioLowPassFilter lp)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true;
            s.spatialBlend = 0f;           // 2D: the street is everywhere, not a point
            s.playOnAwake = false;
            s.volume = 0f;
            lp = go.AddComponent<AudioLowPassFilter>();
            lp.cutoffFrequency = 22000f;
            if (clip != null)
            {
                s.Play();
                s.time = Random.value * clip.length;     // not every run starts at the same chirp
            }
            return s;
        }

        void Update()
        {
            float n = DayNightController.Instance != null ? DayNightController.Instance.night : 0f;
            float nightW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 1f, n));

            if (listener == null)
            {
                var l = FindAnyObjectByType<AudioListener>();
                if (l != null) listener = l.transform;
            }
            if (listener != null && Time.time >= nextProbe)
            {
                nextProbe = Time.time + 0.25f;
                // Anything solid within 8 m overhead counts as a roof.
                probeResult = Physics.Raycast(listener.position, Vector3.up, 8f, ~0, QueryTriggerInteraction.Ignore);
            }
            indoors = Mathf.MoveTowards(indoors, probeResult ? 1f : 0f, Time.deltaTime * muffleSpeed * 0.4f);

            // Inside the house footprint: shut away from the wind only if the exterior doors are closed.
            bool inHouse = listener != null && houseBounds.size.sqrMagnitude > 0.01f && houseBounds.Contains(listener.position);
            bool anyOpen = false;
            if (exteriorDoors != null)
                foreach (var d in exteriorDoors) if (d != null && d.isOpen) anyOpen = true;
            float sealTarget = !inHouse ? 0f : anyOpen ? 1f - openDoorOutdoorGain : 1f;
            sealedIn = Mathf.MoveTowards(sealedIn, sealTarget, Time.deltaTime * 0.9f);   // ~1 s to fade as the door shuts

            float shut = Mathf.Max(indoors, sealedIn);
            Indoors = shut;

            float outside = Mathf.Lerp(1f, indoorOutdoorGain, shut);
            float cutoff = Mathf.Lerp(22000f, indoorCutoff, shut);

            // A slow swell so the chorus is never perfectly steady.
            float swell = 0.85f + 0.15f * Mathf.Sin(Time.time * 0.13f) * Mathf.Sin(Time.time * 0.047f + 1f);

            cricketSrc.volume = cricketVolume * nightW * swell * outside;
            // Wind is outdoors only: dulled under the porch roof, gone once you are shut inside.
            windSrc.volume    = windVolume * Mathf.Lerp(1f, 0.7f, indoors) * (1f - sealedIn);
            staticSrc.volume  = staticVolume * nightW;
            cricketLp.cutoffFrequency = cutoff;
            windLp.cutoffFrequency = Mathf.Lerp(22000f, 2200f, indoors);
        }
    }
}
