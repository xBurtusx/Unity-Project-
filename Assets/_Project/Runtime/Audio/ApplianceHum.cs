using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// A looping appliance sound that is only audible when you are near it: a fridge, a freezer,
    /// a humming transformer.
    ///
    /// The falloff is linear and short, so you notice it as you walk into the kitchen and lose it
    /// again two rooms away - the way a real fridge behaves. A compressor also does not run
    /// forever: with <see cref="cycles"/> on, the hum fades out for a few seconds every minute or
    /// two and comes back, which is itself a detail people unconsciously expect.
    /// </summary>
    public class ApplianceHum : MonoBehaviour
    {
        public AudioClip hum;
        [Range(0f, 1f)] public float volume = 0.55f;
        public float minDistance = 0.7f;
        public float maxDistance = 7f;

        [Header("Compressor cycle")]
        public bool cycles = true;
        public Vector2 runSeconds = new Vector2(55f, 110f);
        public Vector2 restSeconds = new Vector2(10f, 22f);
        public float fadeSeconds = 2f;

        AudioSource src;
        bool running = true;
        float switchAt;

        void Awake()
        {
            src = gameObject.AddComponent<AudioSource>();
            src.clip = hum;
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = minDistance;
            src.maxDistance = maxDistance;
            src.dopplerLevel = 0f;
            src.volume = volume;
            if (hum != null) { src.Play(); src.time = Random.value * hum.length; }
            switchAt = Time.time + Random.Range(runSeconds.x, runSeconds.y);
        }

        void Update()
        {
            if (cycles && Time.time >= switchAt)
            {
                running = !running;
                var range = running ? runSeconds : restSeconds;
                switchAt = Time.time + Random.Range(range.x, range.y);
            }
            float target = running ? volume : 0f;
            src.volume = Mathf.MoveTowards(src.volume, target, volume * Time.deltaTime / Mathf.Max(0.1f, fadeSeconds));
        }
    }
}
