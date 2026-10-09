using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>Compares how the player's house and a neighbour are lit (lightmap index, shader, flags).</summary>
    public static class MainHouseDiag
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("lightmaps in settings: " + LightmapSettings.lightmaps.Length);
            foreach (var rootName in new[] { "PlayerHouse", "House_N1" })
            {
                var root = GameObject.Find(rootName);
                if (root == null) { sb.AppendLine(rootName + ": not found"); continue; }
                var rs = root.GetComponentsInChildren<Renderer>(true);
                sb.AppendLine($"== {rootName}: {rs.Length} renderers");
                foreach (var g in rs.GroupBy(r => r.lightmapIndex)) sb.AppendLine($"  lightmapIndex {g.Key}: {g.Count()}");
                sb.AppendLine("  static: " + string.Join(",", rs.GroupBy(r => (int)GameObjectUtility.GetStaticEditorFlags(r.gameObject)).Select(g => g.Key + "x" + g.Count())));
                foreach (var g in rs.SelectMany(r => r.sharedMaterials).Where(m => m != null).GroupBy(m => m.name + " [" + m.shader.name + "]").Take(25))
                    sb.AppendLine("  mat " + g.Key + " x" + g.Count());
                foreach (var r in rs.Take(8))
                    sb.AppendLine($"  e.g. {r.name} idx={r.lightmapIndex} probe={r.lightProbeUsage} gi={GameObjectUtility.GetStaticEditorFlags(r.gameObject)}");
            }
            System.IO.File.WriteAllText("Temp/mainhouse_diag.txt", sb.ToString());
            Debug.Log("[MainHouseDiag] written");
        }
    }
}
