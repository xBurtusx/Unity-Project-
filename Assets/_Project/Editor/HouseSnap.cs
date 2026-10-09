using System.IO;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>Renders reference pictures of House_Small.fbx to Temp/ so its openings can be inspected without opening the Editor window.</summary>
    public static class HouseSnap
    {
        static readonly string[] Roofy = { "roof", "shingles", "gutters", "ceiling" };

        public static void Run()
        {
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/NeighbourhoodHouse/Meshes/House_Small.fbx");
            var house = Object.Instantiate(prefab);
            var lightGo = new GameObject("snapLight");
            var l = lightGo.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.6f;
            lightGo.transform.rotation = Quaternion.Euler(55, 30, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white * 0.3f;

            var bounds = new Bounds(house.transform.position, Vector3.zero);
            foreach (var r in house.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);

            // Top-down with roof and ceiling hidden: shows wall layout and openings.
            foreach (var r in house.GetComponentsInChildren<Renderer>())
                foreach (var k in Roofy) if (r.name.ToLower().Contains(k)) r.enabled = false;
            Shoot("Temp/snap_top.png", bounds, new Vector3(90, 0, 0), true, 1400);

            // Front / back / sides, everything visible.
            foreach (var r in house.GetComponentsInChildren<Renderer>()) r.enabled = true;
            Shoot("Temp/snap_a.png", bounds, new Vector3(18, 0, 0), false, 1400);
            Shoot("Temp/snap_b.png", bounds, new Vector3(18, 180, 0), false, 1400);
            Shoot("Temp/snap_c.png", bounds, new Vector3(18, 90, 0), false, 1400);
            Shoot("Temp/snap_e.png", bounds, new Vector3(50, 20, 0), false, 1400);
            foreach (var r in house.GetComponentsInChildren<Renderer>()) foreach (var k in Roofy) if (r.name.ToLower().Contains(k)) r.enabled = false;
            Shoot("Temp/snap_f.png", bounds, new Vector3(55, 200, 0), false, 1400);
            Shoot("Temp/snap_d.png", bounds, new Vector3(18, 270, 0), false, 1400);

            Object.DestroyImmediate(house); Object.DestroyImmediate(lightGo);
            Debug.Log($"[HouseSnap] done. bounds centre {bounds.center} size {bounds.size}");
        }

        static void Shoot(string path, Bounds b, Vector3 euler, bool ortho, int px)
        {
            var go = new GameObject("snapCam");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
            cam.orthographic = ortho; cam.nearClipPlane = 0.1f; cam.farClipPlane = 500f;
            float dist = b.size.magnitude * (ortho ? 1f : 1.15f);
            go.transform.rotation = Quaternion.Euler(euler);
            go.transform.position = b.center - go.transform.forward * dist;
            if (ortho) cam.orthographicSize = Mathf.Max(b.size.x, b.size.z) * 0.55f; else cam.fieldOfView = 45f;
            var rt = new RenderTexture(px, px, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(px, px, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, px, px), 0, 0); tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            RenderTexture.active = null; cam.targetTexture = null;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(tex); Object.DestroyImmediate(go);
        }
    }
}
