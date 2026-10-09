using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>Writes the size, hierarchy and material slots of every imported house FBX to Temp/house_report.txt.</summary>
    public static class HouseInspect
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets/NeighbourhoodHouse/Meshes" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var go = Object.Instantiate(prefab);
                var rs = go.GetComponentsInChildren<Renderer>();
                Bounds b = default; bool first = true;
                foreach (var r in rs) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
                sb.AppendLine($"== {path}  size={b.size:F3}  centre={b.center:F3}  renderers={rs.Length}");
                foreach (var r in rs)
                {
                    var names = new StringBuilder();
                    foreach (var m in r.sharedMaterials) names.Append(m != null ? m.name : "null").Append(", ");
                    sb.AppendLine($"   {r.name}  size={r.bounds.size:F2}  mats=[{names}]");
                }
                Object.DestroyImmediate(go);
            }
            File.WriteAllText("Temp/house_report.txt", sb.ToString());
            Debug.Log("[HouseInspect] wrote Temp/house_report.txt");
        }
    }
}
