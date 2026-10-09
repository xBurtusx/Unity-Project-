using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// One light in the house, with an identity and a group.
    ///
    /// Horror events need to talk about lights in the abstract - "kill everything downstairs",
    /// "turn the porch light back on" - and that is only possible if fixtures register themselves
    /// somewhere findable. Hence the static registry: it is populated automatically as fixtures
    /// wake, so no event script ever has to hold a serialized list that goes stale.
    /// </summary>
    public class LightFixture : MonoBehaviour
    {
        [Header("Identity")]
        public string fixtureId;
        [Tooltip("Switches act on a group, not a fixture, so several lights can gang together.")]
        public string group;
        public string room;
        [Tooltip("ceiling, pendant, wall, lamp, strip, porch - what kind of fitting this is.")]
        public string fixtureType = "ceiling";

        [Header("State")]
        public bool isOn;

        [Tooltip("Brightness of the lit shade or bulb. Above ~2 it blooms, which is what makes a lamp look lit.")]
        public float glowIntensity = 3.5f;

        [Header("Fault behaviour")]
        [Tooltip("Chance per second that this light flickers on its own. Keep at 0 for most " +
                 "fixtures - a house where everything flickers reads as a funfair, not a home.")]
        [Range(0f, 0.2f)] public float spontaneousFlickerChance;

        Light lamp;
        Renderer glowRenderer;
        MaterialPropertyBlock block;
        float baseIntensity;
        Coroutine running;

        static readonly Dictionary<string, List<LightFixture>> byGroup =
            new Dictionary<string, List<LightFixture>>();
        static readonly List<LightFixture> all = new List<LightFixture>();

        public static IReadOnlyList<LightFixture> All => all;

        void Awake()
        {
            lamp = GetComponentInChildren<Light>(true);
            glowRenderer = GetComponentInChildren<Renderer>(true);
            block = new MaterialPropertyBlock();
            if (lamp != null) baseIntensity = lamp.intensity;
        }

        void OnEnable()
        {
            all.Add(this);
            if (!string.IsNullOrEmpty(group))
            {
                if (!byGroup.TryGetValue(group, out var list))
                    byGroup[group] = list = new List<LightFixture>();
                list.Add(this);
            }
            Apply();
        }

        void OnDisable()
        {
            all.Remove(this);
            if (!string.IsNullOrEmpty(group) && byGroup.TryGetValue(group, out var list))
                list.Remove(this);
        }

        void Update()
        {
            if (!isOn || spontaneousFlickerChance <= 0f) return;
            if (Random.value < spontaneousFlickerChance * Time.deltaTime) Flicker();
        }

        // ------------------------------------------------------------ state

        public void Set(bool on)
        {
            if (isOn == on) return;
            isOn = on;
            Apply();
        }

        public void Toggle() => Set(!isOn);

        /// <summary>Re-reads the Light and glow renderer and applies state; lets edit-mode tools preview a lit or unlit fixture.</summary>
        public void RefreshInEditor() { Awake(); Apply(); }

        void Apply()
        {
            if (lamp != null)
            {
                lamp.enabled = isOn;
                lamp.intensity = baseIntensity;
            }

            // The shade glows when lit. Done through a property block so every fixture can share
            // one material rather than instantiating a copy per light.
            if (glowRenderer != null && block != null)
            {
                glowRenderer.GetPropertyBlock(block);
                var c = lamp != null ? lamp.color : Color.white;
                block.SetColor("_EmissionColor", isOn ? c * glowIntensity : Color.black);
                glowRenderer.SetPropertyBlock(block);
            }
        }

        /// <summary>A brief stutter, as a failing bulb or a disturbed supply would give.</summary>
        public void Flicker(float duration = 0.45f)
        {
            if (!gameObject.activeInHierarchy || lamp == null) return;
            if (running != null) StopCoroutine(running);
            running = StartCoroutine(FlickerRoutine(duration));
        }

        IEnumerator FlickerRoutine(float duration)
        {
            float end = Time.time + duration;
            bool wasOn = isOn;

            while (Time.time < end)
            {
                lamp.enabled = Random.value > 0.45f;
                // Irregular intervals: an even stutter reads as an effect, not a fault.
                yield return new WaitForSeconds(Random.Range(0.02f, 0.11f));
            }

            isOn = wasOn;
            Apply();
            running = null;
        }

        // ------------------------------------------------------------ group control

        public static void SetGroup(string group, bool on)
        {
            if (byGroup.TryGetValue(group, out var list))
                foreach (var f in list) f.Set(on);
        }

        public static void ToggleGroup(string group)
        {
            if (!byGroup.TryGetValue(group, out var list) || list.Count == 0) return;
            // Gang behaviour: if any light in the group is on, the switch turns them all off.
            bool anyOn = list.Exists(f => f.isOn);
            foreach (var f in list) f.Set(!anyOn);
        }

        public static void FlickerGroup(string group, float duration = 0.45f)
        {
            if (byGroup.TryGetValue(group, out var list))
                foreach (var f in list) f.Flicker(duration);
        }

        /// <summary>Every light in the house out at once - a power cut.</summary>
        public static void AllOff()
        {
            foreach (var f in all) f.Set(false);
        }

        public static LightFixture Find(string fixtureId) =>
            all.Find(f => f.fixtureId == fixtureId);
    }
}
