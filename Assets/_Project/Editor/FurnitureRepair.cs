using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Restores furniture materials from an earlier copy of the scene.
    ///
    /// The retexturing tool replaces a piece's original (scene-embedded) materials with new
    /// assets. If one of those assets is later deleted, the slot goes empty and renders magenta,
    /// and the original colour is gone with it. This opens a reference copy of the scene
    /// (Assets/Scenes/_OldNeighbourhood.unity, extracted from git), matches renderers by their
    /// hierarchy path, and puts the original materials back into any slot that is empty or points
    /// at a generated Fn_ material, ready for FurnitureTexturing to retexture cleanly.
    ///
    /// The reference scene is opened additively, read, closed and deleted; nothing in it is saved.
    /// </summary>
    public static class FurnitureRepair
    {
        const string RefScene = "Assets/Scenes/_OldNeighbourhood.unity";

        [MenuItem("Horror House/Repair Furniture Materials", priority = 77)]
        public static void Run()
        {
            if (!File.Exists(RefScene))
            {
                Debug.LogWarning($"[FurnitureRepair] {RefScene} not found; nothing to restore from.");
                return;
            }

            AssetDatabase.ImportAsset(RefScene);
            var current = EditorSceneManager.GetActiveScene();
            var old = EditorSceneManager.OpenScene(RefScene, OpenSceneMode.Additive);
            if (EditorSceneManager.GetActiveScene() != current) EditorSceneManager.SetActiveScene(current);

            var reference = new Dictionary<string, Material[]>();
            foreach (var root in old.GetRootGameObjects())
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                    reference[Key(mr.transform)] = mr.sharedMaterials;

            int restored = 0, missing = 0;
            foreach (var root in current.GetRootGameObjects())
            {
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats = mr.sharedMaterials;
                    bool needs = false;
                    foreach (var m in mats) if (m == null || m.name.StartsWith("Fn_")) needs = true;
                    if (!needs) continue;

                    if (!reference.TryGetValue(Key(mr.transform), out var originals) || originals.Length != mats.Length)
                    {
                        missing++;
                        Debug.LogWarning($"[FurnitureRepair] No reference for {Key(mr.transform)}");
                        continue;
                    }
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null && !mats[i].name.StartsWith("Fn_")) continue;
                        if (originals[i] == null) continue;
                        // A standalone copy: the original lives in the other scene and must not be referenced from this one.
                        var copy = new Material(originals[i]) { name = originals[i].name };
                        mats[i] = copy;
                    }
                    mr.sharedMaterials = mats;
                    EditorUtility.SetDirty(mr);
                    restored++;
                }
            }

            EditorSceneManager.CloseScene(old, true);
            AssetDatabase.DeleteAsset(RefScene);
            EditorSceneManager.MarkSceneDirty(current);
            Debug.Log($"[FurnitureRepair] Restored materials on {restored} renderers ({missing} had no match).");
        }

        /// <summary>Hierarchy path with sibling indices, so two sibling objects with the same name stay distinct.</summary>
        static string Key(Transform t)
        {
            var s = t.parent == null ? t.name : $"{t.name}#{t.GetSiblingIndex()}";
            while (t.parent != null)
            {
                t = t.parent;
                s = (t.parent == null ? t.name : $"{t.name}#{t.GetSiblingIndex()}") + "/" + s;
            }
            return s;
        }
    }
}
