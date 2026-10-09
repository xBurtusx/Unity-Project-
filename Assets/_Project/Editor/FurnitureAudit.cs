using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Writes Temp/furniture_audit.txt: every renderer under the player house's furniture, with
    /// its mesh, UV availability, world scale, and each material's shader, colour and texture.
    /// Retexturing needs to know whether the meshes have usable UVs and what the materials are.
    /// </summary>
    public static class FurnitureAudit
    {
        [MenuItem("Horror House/Audit Furniture", priority = 74)]
        public static void Run()
        {
            var sb = new StringBuilder();
            var seen = new System.Collections.Generic.Dictionary<string, int>();
            int renderers = 0;

            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (!mr.transform.root.name.Contains("Neighbourhood") && mr.transform.root != mr.transform) { }
                string path = Path(mr.transform);
                if (!path.Contains("Furniture") && !path.Contains("Interior")) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                renderers++;
                var m = mf.sharedMesh;
                var mats = mr.sharedMaterials.Select(x => x == null ? "null" :
                    $"{x.name}|{x.shader.name}|col={(x.HasProperty("_BaseColor") ? x.GetColor("_BaseColor").ToString("F2") : "-")}|tex={(x.HasProperty("_BaseMap") && x.GetTexture("_BaseMap") != null ? x.GetTexture("_BaseMap").name : "none")}");
                string key = m.name;
                seen[key] = seen.TryGetValue(key, out int c) ? c + 1 : 1;
                if (seen[key] == 1)
                    sb.AppendLine($"MESH {m.name,-28} verts={m.vertexCount,5} uv0={(m.uv.Length > 0 ? "yes" : "NO ")} normals={(m.normals.Length > 0 ? "yes" : "no")} " +
                                  $"scale={mr.transform.lossyScale:F2} subMeshes={m.subMeshCount} mats=[{string.Join("; ", mats)}]  e.g. {path}");
            }
            sb.Insert(0, $"Furniture/interior renderers: {renderers}; distinct meshes: {seen.Count}\n");
            sb.AppendLine("\n=== instance counts ===");
            foreach (var kv in seen.OrderByDescending(k => k.Value)) sb.AppendLine($"{kv.Value,3}  {kv.Key}");

            Directory.CreateDirectory("Temp");
            File.WriteAllText("Temp/furniture_audit.txt", sb.ToString());
            Debug.Log("[FurnitureAudit] Wrote Temp/furniture_audit.txt");
        }

        static string Path(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
