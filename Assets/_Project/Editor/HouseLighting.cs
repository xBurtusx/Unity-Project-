using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// What makes the scene look lit by a real sun rather than painted: contact shadows, per-room
    /// reflections, and baked bounce light.
    ///
    /// Direct light only ever shows where a ray lands. In a real room most of what you see is light that
    /// has bounced - off the wood floor, up into the ceiling, round the corner - so shadows are soft, corners
    /// are dark but not black, and a bright floor warms the wall beside it. A baked lightmap stores exactly
    /// that. Only the player's house is baked: the neighbours are never entered, and a lightmap for the
    /// whole street would be enormous.
    /// </summary>
    public static class HouseLighting
    {
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const string LightingSettingsPath = "Assets/Scenes/NeighbourhoodLighting.lighting";

        // ------------------------------------------------------------ ambient occlusion

        /// <summary>Adds screen-space ambient occlusion to the PC renderer: the dark creases where surfaces meet.</summary>
        public static string EnsureAmbientOcclusion()
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (data == null) return "renderer asset not found";

            var existing = data.rendererFeatures.OfType<ScreenSpaceAmbientOcclusion>().FirstOrDefault();
            if (existing == null)
            {
                existing = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                existing.name = "ScreenSpaceAmbientOcclusion";
                AssetDatabase.AddObjectToAsset(existing, data);
                data.rendererFeatures.Add(existing);
            }
            existing.SetActive(true);
            // The settings type is internal in this URP version, so they are written through the serialized fields.
            var so = new SerializedObject(existing);
            var st = so.FindProperty("m_Settings");
            if (st != null)
            {
                void SetF(string n, float v) { var p = st.FindPropertyRelative(n); if (p != null) p.floatValue = v; }
                SetF("Intensity", 0.8f); SetF("Radius", 0.28f); SetF("DirectLightingStrength", 0.1f); SetF("Falloff", 100f);
                var src = st.FindPropertyRelative("Source");
                if (src != null) src.enumValueIndex = 1;      // DepthNormals
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // The feature list is mirrored in a map that has to be rebuilt when features change.
            var validate = typeof(ScriptableRendererData).GetMethod("OnValidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            validate?.Invoke(data, null);
            EditorUtility.SetDirty(existing);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return "ambient occlusion on";
        }

        // ------------------------------------------------------------ reflection probes

        /// <summary>One box-projected reflection probe per room, so glass and glossy floors reflect that room, not the sky.</summary>
        public static void AddRoomProbe(Transform parent, string id, Bounds room, float floorY)
        {
            var go = new GameObject("ReflectionProbe_" + id);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(room.center.x, floorY + 1.4f, room.center.z);
            var rp = go.AddComponent<ReflectionProbe>();
            rp.mode = ReflectionProbeMode.Realtime;
            rp.refreshMode = ReflectionProbeRefreshMode.OnAwake;     // captured once when the game starts; cheap
            rp.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            rp.resolution = 128;
            rp.boxProjection = true;
            rp.center = Vector3.zero;
            rp.size = new Vector3(room.size.x + 0.6f, 3.2f, room.size.z + 0.6f);
            rp.blendDistance = 0.5f;
            rp.importance = 10;
            rp.intensity = 1f;
        }

        // ------------------------------------------------------------ baked bounce light

        /// <summary>Marks only the given renderers as contributing to the lightmap, and clears the flag everywhere else.</summary>
        public static void RestrictGI(GameObject world, GameObject house)
        {
            foreach (var r in world.GetComponentsInChildren<Renderer>(true))
            {
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags & ~StaticEditorFlags.ContributeGI);
            }
            foreach (var r in house.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.name.ToLower().Trim();
                // Architecture only: furniture, doors and light fittings move or switch, and take probe light instead.
                bool architecture = n == "walls" || n == "siding" || n.StartsWith("foundation") || n.EndsWith("floor") ||
                                    n.Contains("ceiling") || n == "porch" || n.Contains("roof") || n.Contains("gable") || n.Contains("shingle");
                if (!architecture) continue;
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags | StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic);
                if (r is MeshRenderer mr) mr.receiveGI = ReceiveGI.Lightmaps;
            }
        }

        /// <summary>Makes sure the house model generates lightmap UVs, which baking needs. Returns true if it had to reimport.</summary>
        public static bool EnsureLightmapUVs(string fbxPath)
        {
            var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (imp == null || imp.generateSecondaryUV) return false;
            imp.generateSecondaryUV = true;
            imp.SaveAndReimport();
            return true;
        }

        /// <summary>Light probes through the player's house, so the furniture and doors are lit by the bounce light too.</summary>
        public static void AddProbes(Transform parent, Bounds houseBounds)
        {
            var go = new GameObject("LightProbes");
            go.transform.SetParent(parent, false);
            var group = go.AddComponent<LightProbeGroup>();
            var pts = new System.Collections.Generic.List<Vector3>();
            for (float x = houseBounds.min.x + 0.5f; x < houseBounds.max.x; x += 2.2f)
            for (float z = houseBounds.min.z + 0.5f; z < houseBounds.max.z; z += 2.2f)
            for (float y = 0.6f; y < 3.0f; y += 1.2f)
                pts.Add(go.transform.InverseTransformPoint(new Vector3(x, houseBounds.min.y + 1.0f + y, z)));
            group.probePositions = pts.ToArray();
        }

        static LightingSettings MakeSettings()
        {
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingSettingsPath);
            if (ls == null)
            {
                ls = new LightingSettings();
                AssetDatabase.CreateAsset(ls, LightingSettingsPath);
            }
            ls.bakedGI = true;
            ls.realtimeGI = false;
            ls.autoGenerate = false;
            ls.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
            ls.mixedBakeMode = MixedLightingMode.IndirectOnly;     // direct light stays real-time and shadows stay crisp
            ls.directSampleCount = 32;
            ls.indirectSampleCount = 128;
            ls.environmentSampleCount = 128;
            ls.maxBounces = 4;
            ls.lightmapResolution = 12f;
            ls.lightmapMaxSize = 1024;
            ls.lightmapCompression = LightmapCompression.NormalQuality;
            ls.ao = false;
            ls.indirectScale = 2.8f;     // boosts all bounced light: the cheap way to a brighter, more evenly lit interior
            ls.albedoBoost = 1.6f;       // light bounces off pale surfaces more strongly, so rooms fill in
            EditorUtility.SetDirty(ls);
            return ls;
        }

        static bool baking;

        /// <summary>Bakes the bounce light for the current scene. Runs in the background; writes Temp/bake_report.txt when done.</summary>
        public static void Bake()
        {
            var sun = GameObject.Find("Sun");
            if (sun != null && sun.TryGetComponent(out Light l)) { l.lightmapBakeType = LightmapBakeType.Mixed; l.bounceIntensity = 6f; }
            RenderSettings.ambientIntensity = 2.3f;       // a brighter sky means brighter light spilling in through the windows
            EnsureAmbientOcclusion();

            Lightmapping.lightingSettings = MakeSettings();
            var scene = EditorSceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path)) { File.WriteAllText("Temp/bake_report.txt", "scene has no path; save it first"); return; }
            EditorSceneManager.SaveScene(scene);

            baking = true;
            var start = System.DateTime.Now;
            Lightmapping.bakeCompleted += Done;
            if (!Lightmapping.BakeAsync())
            {
                Lightmapping.bakeCompleted -= Done;
                File.WriteAllText("Temp/bake_report.txt", "BakeAsync refused to start");
                baking = false;
                return;
            }
            File.WriteAllText("Temp/bake_report.txt", "baking... started " + start.ToString("HH:mm:ss"));

            void Done()
            {
                Lightmapping.bakeCompleted -= Done;
                baking = false;
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
                File.WriteAllText("Temp/bake_report.txt",
                    $"DONE in {(System.DateTime.Now - start).TotalSeconds:F0}s; lightmaps: {LightmapSettings.lightmaps.Length}; probes baked: {LightmapSettings.lightProbes != null}");
            }
        }
    }
}
