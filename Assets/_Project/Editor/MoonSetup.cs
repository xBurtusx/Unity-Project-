using System.IO;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Gives the night a real moon.
    ///
    ///  * Builds a material from NASA's full-moon photograph and a quad that SkyMoon keeps in the sky.
    ///  * Aims the moonlight so the moon sits just above the roofline as seen from the street, low
    ///    enough to throw long shadows.
    ///  * Removes the painted moon from the night sky texture. That texture carried a dark disc with
    ///    a halo; with a real moon in the sky it would be a second, fake one.
    /// </summary>
    public static class MoonSetup
    {
        const string PhotoPath = "Assets/_Project/Textures/Moon/moon_full_nasa.jpg";
        const string MatPath   = "Assets/_Project/Materials/M_Moon.mat";
        const string SkyPath   = "Assets/_Project/Settings/T_NightSky.png";

        /// <summary>Where the moon sits: 24 degrees up, 35 degrees to the right of straight ahead from the street.</summary>
        public static readonly Quaternion MoonLightRotation = Quaternion.Euler(28f, 75f, 0f);

        public static void Build(DayNightController dn)
        {
            ConfigurePhoto();

            var shader = Shader.Find("HorrorHouse/MoonDisc");
            if (shader == null) throw new System.Exception("Shader HorrorHouse/MoonDisc not found (did it compile?)");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, MatPath); }
            else if (mat.shader != shader) mat.shader = shader;
            mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(PhotoPath));
            mat.SetColor("_Tint", new Color(0.92f, 0.95f, 1f, 1f));
            mat.SetFloat("_DiscRadius", 0.3f);
            mat.SetFloat("_Halo", 0.35f);
            EditorUtility.SetDirty(mat);

            var moonLight = dn.moon;
            if (moonLight != null) moonLight.transform.rotation = MoonLightRotation;

            var go = GameObject.Find("Moon");
            if (go == null)
            {
                go = new GameObject("Moon");
                Undo.RegisterCreatedObjectUndo(go, "Create Moon");
                go.transform.SetParent(dn.transform, false);
            }
            if (!go.TryGetComponent(out MeshFilter mf)) mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            if (!go.TryGetComponent(out MeshRenderer mr)) mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            if (!go.TryGetComponent(out SkyMoon sky)) sky = go.AddComponent<SkyMoon>();
            sky.moonLight = moonLight;
            sky.dayNight = dn;
            sky.distance = 300f;
            sky.angularDegrees = 3.2f;
            sky.intensity = 1.7f;
            sky.discFraction = 0.6f;
            EditorUtility.SetDirty(sky);

            CleanSkyTexture();
        }

        static void ConfigurePhoto()
        {
            var ti = AssetImporter.GetAtPath(PhotoPath) as TextureImporter;
            if (ti == null) throw new FileNotFoundException(PhotoPath + " is not imported");
            if (ti.maxTextureSize != 2048 || ti.wrapMode != TextureWrapMode.Clamp || !ti.mipmapEnabled)
            {
                ti.sRGBTexture = true;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.anisoLevel = 4;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
        }

        /// <summary>
        /// The sky is a 360-degree panorama and mostly smooth, so the painted moon (and the glow
        /// that wraps round the edge) is replaced with sky copied from a clean strip further along
        /// the same rows, feathered at the edges. Idempotent: re-running changes nothing visible.
        /// </summary>
        static void CleanSkyTexture()
        {
            string full = Path.Combine(Application.dataPath, "..", SkyPath);
            if (!File.Exists(full)) return;

            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            t.LoadImage(File.ReadAllBytes(full));
            int w = t.width, h = t.height;
            var px = t.GetPixels32();

            // The painted moon: ~7% across, ~29% down from the top of the panorama.
            float cx = w * 0.0695f, cy = h - 1 - h * 0.289f;
            float radius = w * 0.15f;
            int shift = (int)(w * 0.27f);

            var src = (Color32[])px.Clone();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = Mathf.Abs(x - cx);
                    dx = Mathf.Min(dx, w - dx);                      // wrap around the seam
                    float d = Mathf.Sqrt(dx * dx + (y - cy) * (y - cy));
                    if (d > radius) continue;

                    float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(radius, radius * 0.7f, d));
                    var from = src[y * w + (x + shift) % w];
                    var to = src[y * w + x];
                    px[y * w + x] = Color32.Lerp(to, from, k);
                }
            }
            t.SetPixels32(px);
            File.WriteAllBytes(full, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(SkyPath);
        }
    }
}
