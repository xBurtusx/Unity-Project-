using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Writes a plain-text survey of the open scene to Temp/scene_info.txt: every light, the road
    /// and sidewalk bounds, lamp posts, the fridge, and how many doors and switches have sounds.
    /// It exists so tools (and people) can check what is really in the scene instead of guessing.
    /// </summary>
    public static class SceneInfoDump
    {
        [MenuItem("Horror House/Dump Scene Info", priority = 73)]
        public static void Run()
        {
            var sb = new StringBuilder();
            string P(Transform t) { var s = t.name; while (t.parent != null) { t = t.parent; s = t.name + "/" + s; } return s; }

            sb.AppendLine("=== LIGHTS ===");
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None).OrderBy(x => x.name))
            {
                var fx = l.GetComponentInParent<LightFixture>();
                sb.AppendLine($"{l.type,-11} on={l.enabled,-5} I={l.intensity,5:F2} R={l.range,5:F1} " +
                              $"col=({l.color.r:F2},{l.color.g:F2},{l.color.b:F2}) shad={l.shadows,-4} bake={l.lightmapBakeType,-8} " +
                              $"fx={(fx != null ? fx.fixtureId + "/" + fx.group + "/on=" + fx.isOn : "-")}  {P(l.transform)}  pos={l.transform.position:F1}");
            }

            sb.AppendLine("\n=== ROAD / SIDEWALK / GROUND BOUNDS ===");
            foreach (var n in new[] { "Road", "Sidewalk_N", "Sidewalk_S", "CentreLine", "Ground" })
            {
                var go = GameObject.Find(n);
                if (go == null) { sb.AppendLine($"{n}: not found"); continue; }
                var rs = go.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) { sb.AppendLine($"{n}: no renderer, pos={go.transform.position:F1}"); continue; }
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                sb.AppendLine($"{n}: center={b.center:F2} size={b.size:F2} min={b.min:F2} max={b.max:F2} mat={rs[0].sharedMaterial?.name}");
            }

            sb.AppendLine("\n=== LAMP / STREET / FRIDGE / PLAYER OBJECTS ===");
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).OrderBy(x => x.name))
            {
                string nm = t.name.ToLowerInvariant();
                if (nm.Contains("street") || nm.Contains("lamppost") || nm.Contains("streetlight") || nm.Contains("fridge") || nm == "player" || nm.Contains("porchlight") || nm.Contains("mailbox"))
                    sb.AppendLine($"{P(t)}  pos={t.position:F2}");
            }

            sb.AppendLine("\n=== DOORS / SWITCHES ===");
            var doors = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
            sb.AppendLine($"Doors: {doors.Length}, with openClip: {doors.Count(d => d.openClip != null)}");
            var sw = Object.FindObjectsByType<LightSwitch>(FindObjectsSortMode.None);
            sb.AppendLine($"Switches: {sw.Length}, with clickOn: {sw.Count(s => s.clickOn != null)}");
            foreach (var s in sw) sb.AppendLine($"  switch {s.switchId} controls='{s.controls}' room='{s.room}'");

            sb.AppendLine("\n=== COLLIDER NAMES UNDER PLAYER HOUSE FLOORS / WORLD (for footstep surfaces) ===");
            foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).OrderBy(x => x.name))
            {
                string nm = c.name.ToLowerInvariant();
                if (nm.Contains("floor") || nm.Contains("ground") || nm.Contains("road") || nm.Contains("sidewalk") || nm.Contains("porch") || nm.Contains("step") || nm.Contains("path") || nm.Contains("yard"))
                    sb.AppendLine($"  {c.GetType().Name,-14} {P(c.transform)}");
            }

            sb.AppendLine("\n=== RENDER SETTINGS ===");
            sb.AppendLine($"ambientMode={RenderSettings.ambientMode} ambientIntensity={RenderSettings.ambientIntensity} fog={RenderSettings.fog} sky={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "none")} sun={(RenderSettings.sun != null ? RenderSettings.sun.name : "none")}");
            var vol = Object.FindAnyObjectByType<Volume>();
            sb.AppendLine($"Volume: {(vol != null && vol.sharedProfile != null ? vol.sharedProfile.name : "none")}");

            Directory.CreateDirectory("Temp");
            File.WriteAllText("Temp/scene_info.txt", sb.ToString());
            Debug.Log("[SceneInfo] Wrote Temp/scene_info.txt");
        }
    }
}
