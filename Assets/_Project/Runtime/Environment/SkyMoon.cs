using UnityEngine;
using UnityEngine.Rendering;

namespace HorrorHouse
{
    /// <summary>
    /// Keeps a photographic moon fixed in the sky, exactly where the moonlight comes from.
    ///
    /// The moon is a quad placed along the light's reverse direction and re-centred on every camera
    /// each frame (via the render pipeline's per-camera callback), so it has no parallax: you can
    /// walk anywhere and it stays put on the horizon, like the real thing, and the Scene view,
    /// the Game view and any photo camera all see it. It fades with the moonlight, so it rises and
    /// sets with night.
    ///
    /// Real size is half a degree; the default is 3.2 degrees, which reads as "the moon" on a
    /// monitor instead of a small speck.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public class SkyMoon : MonoBehaviour
    {
        public Light moonLight;
        public DayNightController dayNight;
        [Tooltip("Metres from the camera. Beyond the world, inside the camera's far plane.")]
        public float distance = 300f;
        [Tooltip("Apparent width of the disc in degrees. The real moon is about 0.5.")]
        public float angularDegrees = 3.2f;
        public float intensity = 1.7f;
        [Tooltip("Must match the shader's Disc Radius: the disc fills this fraction of the quad.")]
        public float discFraction = 0.6f;

        Renderer rend;
        MaterialPropertyBlock block;

        void OnEnable()
        {
            rend = GetComponent<Renderer>();
            RenderPipelineManager.beginCameraRendering += OnBegin;
        }

        void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnBegin;

        void OnBegin(ScriptableRenderContext context, Camera cam)
        {
            if (moonLight == null || rend == null) return;

            Vector3 toMoon = -moonLight.transform.forward;
            transform.position = cam.transform.position + toMoon * distance;
            transform.rotation = Quaternion.LookRotation(toMoon);
            float width = 2f * distance * Mathf.Tan(angularDegrees * 0.5f * Mathf.Deg2Rad) / Mathf.Max(0.1f, discFraction);
            transform.localScale = new Vector3(width, width, 1f);

            // Visible exactly as much as the moonlight is.
            float vis = dayNight != null && dayNight.moonIntensity > 0.0001f
                ? Mathf.Clamp01(moonLight.intensity / dayNight.moonIntensity)
                : (moonLight.enabled ? 1f : 0f);
            if (!moonLight.enabled) vis = 0f;

            rend.enabled = vis > 0.01f;
            if (block == null) block = new MaterialPropertyBlock();
            rend.GetPropertyBlock(block);
            block.SetFloat("_Intensity", intensity * vis);
            rend.SetPropertyBlock(block);
        }
    }
}
