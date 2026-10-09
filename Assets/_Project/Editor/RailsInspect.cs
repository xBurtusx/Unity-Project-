using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>Lists the loose parts of the "porch rails" mesh so the stair handrails can be told apart from the porch railing.</summary>
    public static class RailsInspect
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/NeighbourhoodHouse/Meshes/House_Small.fbx");
            var go = Object.Instantiate(prefab);
            var rails = go.GetComponentsInChildren<Renderer>().First(r => r.name.ToLower().Contains("rails"));
            var steps = go.GetComponentsInChildren<Renderer>().First(r => r.name.ToLower().Contains("front steps"));
            var porch = go.GetComponentsInChildren<Renderer>().First(r => r.name.ToLower().Trim() == "porch" || r.name.ToLower().StartsWith("porch") && !r.name.ToLower().Contains("rail") && !r.name.ToLower().Contains("step") && !r.name.ToLower().Contains("roof"));
            sb.AppendLine($"steps  {steps.bounds.min} .. {steps.bounds.max}");
            sb.AppendLine($"porch  {porch.bounds.min} .. {porch.bounds.max}");
            sb.AppendLine($"rails  {rails.bounds.min} .. {rails.bounds.max}");

            var mesh = rails.GetComponent<MeshFilter>().sharedMesh;
            var verts = mesh.vertices; var tris = mesh.triangles;
            var tf = rails.transform;
            // union-find over triangles that share a (quantised) vertex position
            var parent = Enumerable.Range(0, tris.Length / 3).ToArray();
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            var owner = new Dictionary<Vector3Int, int>();
            for (int t = 0; t < tris.Length / 3; t++)
                for (int k = 0; k < 3; k++)
                {
                    var p = verts[tris[t * 3 + k]];
                    var key = new Vector3Int(Mathf.RoundToInt(p.x * 200), Mathf.RoundToInt(p.y * 200), Mathf.RoundToInt(p.z * 200));
                    if (owner.TryGetValue(key, out var o)) parent[Find(t)] = Find(o); else owner[key] = t;
                }
            var groups = new Dictionary<int, Bounds>(); var counts = new Dictionary<int, int>();
            for (int t = 0; t < tris.Length / 3; t++)
            {
                int g = Find(t);
                for (int k = 0; k < 3; k++)
                {
                    var wp = tf.TransformPoint(verts[tris[t * 3 + k]]);
                    if (!groups.ContainsKey(g)) { groups[g] = new Bounds(wp, Vector3.zero); counts[g] = 0; }
                    var b = groups[g]; b.Encapsulate(wp); groups[g] = b;
                }
                counts[g]++;
            }
            sb.AppendLine($"{groups.Count} loose parts");
            foreach (var kv in groups.OrderBy(k => k.Value.min.z).ThenBy(k => k.Value.min.x))
                sb.AppendLine($"part tris={counts[kv.Key],5}  min=({kv.Value.min.x:F2},{kv.Value.min.y:F2},{kv.Value.min.z:F2})  max=({kv.Value.max.x:F2},{kv.Value.max.y:F2},{kv.Value.max.z:F2})");
            Object.DestroyImmediate(go);
            File.WriteAllText("Temp/rails_report.txt", sb.ToString());
        }
    }
}
