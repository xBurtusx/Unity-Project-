using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Dumps the live lighting state (works in Play mode too) when Temp/playdiag appears.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayDiag
    {
        const string Marker = "Temp/playdiag";
        static double next;

        static PlayDiag() { EditorApplication.update += Poll; }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1.0;
            if (!File.Exists(Marker)) return;
            File.Delete(Marker);
            try { Dump(); } catch (System.Exception e) { File.WriteAllText("Temp/playdiag.txt", e.ToString()); }
        }

        static string Sh(Vector3 p)
        {
            LightProbes.GetInterpolatedProbe(p, null, out SphericalHarmonicsL2 sh);
            var dirs = new[] { Vector3.up, Vector3.forward, Vector3.back };
            var cols = new Color[dirs.Length];
            sh.Evaluate(dirs, cols);
            return string.Join(" ", cols.Select(c => $"({c.r:F3},{c.g:F3},{c.b:F3})"));
        }

        static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"playing={Application.isPlaying} t={System.DateTime.Now:HH:mm:ss}");
            sb.AppendLine($"ambientMode={RenderSettings.ambientMode} sky={RenderSettings.ambientSkyColor} eq={RenderSettings.ambientEquatorColor} int={RenderSettings.ambientIntensity}");
            sb.AppendLine($"ambientProbe(up,fwd,back)={EvalAmbient()}");
            sb.AppendLine($"fog={RenderSettings.fog} {RenderSettings.fogMode} col={RenderSettings.fogColor} dens={RenderSettings.fogDensity}");
            sb.AppendLine($"lightmaps={LightmapSettings.lightmaps.Length} probes={(LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : -1)} mode={LightmapSettings.lightmapsMode}");
            sb.AppendLine($"skybox={(RenderSettings.skybox ? RenderSettings.skybox.name : "null")} sun={(RenderSettings.sun ? RenderSettings.sun.name : "null")}");
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(l => l.enabled && l.type == LightType.Directional))
                sb.AppendLine($"DIR {l.name} int={l.intensity} rot={l.transform.eulerAngles} shadows={l.shadows} str={l.shadowStrength} bake={l.lightmapBakeType}");
            sb.AppendLine("enabled lights: " + Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Count(l => l.enabled && l.gameObject.activeInHierarchy));

            var house = GameObject.Find("PlayerHouse");
            if (house != null)
            {
                foreach (var r in house.GetComponentsInChildren<Renderer>().Where(r =>
                         r.sharedMaterials.Any(m => m != null && (m.name.Contains("siding") || m.name.Contains("shingle") || m.name.Contains("brick") || m.name == "N_white"))).Take(14))
                {
                    var m = r.sharedMaterials.FirstOrDefault(x => x != null);
                    sb.AppendLine($"  {r.name} en={r.enabled} vis={r.isVisible} lmi={r.lightmapIndex} probe={r.lightProbeUsage} anchor={(r.probeAnchor ? r.probeAnchor.name : "-")} mats={string.Join("/", r.sharedMaterials.Select(x => x ? x.name : "null"))}");
                    sb.AppendLine($"     SH@{r.bounds.center}: {Sh(r.bounds.center)}");
                    if (m != null) sb.AppendLine($"     kw={string.Join(",", m.shaderKeywords)} col={(m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : "-")} map={(m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") ? m.GetTexture("_BaseMap").name : "-")}");
                }
            }
            File.WriteAllText("Temp/playdiag.txt", sb.ToString());
        }

        static string EvalAmbient()
        {
            var dirs = new[] { Vector3.up, Vector3.forward, Vector3.back };
            var cols = new Color[3];
            RenderSettings.ambientProbe.Evaluate(dirs, cols);
            return string.Join(" ", cols.Select(c => $"({c.r:F3},{c.g:F3},{c.b:F3})"));
        }
    }
}
