using UnityEngine;
using UnityEngine.UI;

namespace HorrorHouse
{
    /// <summary>
    /// The parts of the VHS look that a colour grade cannot do: scanlines, head-switching noise
    /// at the bottom of the frame, and the occasional tracking wobble.
    ///
    /// It is built as a screen-space Canvas rather than a post-process shader on purpose - it
    /// costs almost nothing, survives render pipeline changes, and every part of it can be turned
    /// down to zero independently, which matters because this effect is very easy to overdo.
    ///
    /// The brief is explicit that the player must still be able to see the house. Defaults here
    /// are deliberately restrained: the tape artefacts should be noticed only when you look for
    /// them, except during a tracking glitch, which is rare and brief by design.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT [ExecuteAlways]. A Screen Space - Overlay canvas is drawn in the Scene
    /// view as an enormous rectangle floating in world space, so running this in edit mode hangs
    /// a slab of scanlines and tape noise in the sky above the house - alarming, and easy to
    /// mistake for broken geometry. Previewing it is not worth that; press Play to see it.
    /// </remarks>
    public class VhsOverlay : MonoBehaviour
    {
        [Header("Master")]
        [Tooltip("Scales everything below. 0 disables the overlay entirely.")]
        [Range(0f, 1f)] public float intensity = 1f;

        [Header("Scanlines")]
        [Range(0f, 1f)] public float scanlineStrength = 0.22f;
        [Tooltip("Scanline pairs down the screen. Real VHS is around 240 visible lines.")]
        public int scanlineCount = 240;
        [Tooltip("How fast the line pattern creeps. Slow is unsettling; fast is nauseating.")]
        public float scanlineDrift = 0.04f;

        [Header("Tape noise")]
        [Range(0f, 1f)] public float noiseStrength = 0.07f;
        [Tooltip("Height of the dirty band at the bottom, where the video head switches.")]
        [Range(0f, 0.3f)] public float headSwitchBand = 0.045f;

        [Header("Tracking glitches")]
        [Tooltip("Average seconds between tracking wobbles. Rare is scarier than constant.")]
        public float glitchInterval = 26f;
        [Range(0f, 1f)] public float glitchStrength = 0.5f;

        Canvas canvas;
        RawImage scanlines, noise, headSwitch;
        Texture2D scanTex, noiseTex;
        float nextGlitch, glitchUntil;

        void OnEnable()
        {
            Build();
            ScheduleGlitch();
        }

        void OnDisable()
        {
            if (canvas != null) DestroySafe(canvas.gameObject);
            DestroySafe(scanTex);
            DestroySafe(noiseTex);
        }

        static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        void Build()
        {
            if (canvas != null) return;

            var go = new GameObject("VhsCanvas") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);

            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;            // on top of any gameplay UI
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            go.AddComponent<GraphicRaycaster>().enabled = false;   // never eat clicks

            scanTex = BuildScanlineTexture();
            noiseTex = BuildNoiseTexture();

            scanlines  = Layer("Scanlines", scanTex);
            noise      = Layer("TapeNoise", noiseTex);
            headSwitch = Layer("HeadSwitch", noiseTex);
        }

        RawImage Layer(string name, Texture tex)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(canvas.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            var img = go.AddComponent<RawImage>();
            img.texture = tex;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Two pixels tall: one dark line, one clear. Tiled to the scanline count.</summary>
        static Texture2D BuildScanlineTexture()
        {
            var t = new Texture2D(1, 2, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            t.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));
            t.SetPixel(0, 1, new Color(0f, 0f, 0f, 0f));
            t.Apply();
            return t;
        }

        static Texture2D BuildNoiseTexture()
        {
            const int n = 256;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };

            var px = new Color32[n * n];
            var rng = new System.Random(20261008);
            for (int i = 0; i < px.Length; i++)
            {
                byte v = (byte)rng.Next(110, 256);
                // Mostly transparent: this is sparse sparkle, not a grey film over the picture.
                byte a = (byte)(rng.NextDouble() < 0.18 ? rng.Next(40, 190) : 0);
                px[i] = new Color32(v, v, v, a);
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        void ScheduleGlitch() =>
            nextGlitch = Time.unscaledTime + glitchInterval * Random.Range(0.55f, 1.6f);

        void Update()
        {
            if (canvas == null) Build();
            if (canvas == null) return;

            float master = Mathf.Clamp01(intensity);
            canvas.enabled = master > 0.001f;
            if (!canvas.enabled) return;

            float t = Time.unscaledTime;

            bool glitching = t < glitchUntil;
            if (!glitching && t >= nextGlitch)
            {
                glitching = true;
                glitchUntil = t + Random.Range(0.10f, 0.45f);
                ScheduleGlitch();
            }
            float glitch = glitching ? glitchStrength * master : 0f;

            // Scanlines: tiled to a fixed line count, creeping slowly up the frame.
            float lines = Mathf.Max(1, scanlineCount);
            scanlines.color = new Color(0f, 0f, 0f, scanlineStrength * master);
            scanlines.uvRect = new Rect(0f, t * scanlineDrift, 1f, lines);

            // Tape sparkle: re-randomised each frame so it never reads as a static pattern.
            noise.color = new Color(1f, 1f, 1f, (noiseStrength + glitch * 0.25f) * master);
            noise.uvRect = new Rect(Random.value, Random.value, 6f, 4f);

            // Head-switching band: the torn strip along the very bottom of a tape frame.
            float band = headSwitchBand * (1f + glitch * 2.5f);
            var rt = headSwitch.rectTransform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, Mathf.Clamp01(band));
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            headSwitch.color = new Color(1f, 1f, 1f, (0.28f + glitch * 0.4f) * master);
            headSwitch.uvRect = new Rect(Random.value, Random.value, 3f, 0.5f);

            // During a glitch the whole picture slips sideways, the way tracking error looks.
            float slip = glitching ? (Random.value - 0.5f) * 0.02f * glitchStrength : 0f;
            var canvasRt = canvas.GetComponent<RectTransform>();
            if (canvasRt != null)
                canvasRt.anchoredPosition = new Vector2(slip * Screen.width, 0f);
        }
    }
}
