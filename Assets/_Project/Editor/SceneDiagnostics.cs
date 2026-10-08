using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Lists what is actually in the scene, by size and height, and writes it to a file.
    ///
    /// Written because something large and unexplained was hanging in the sky and no amount of
    /// looking at it from different angles identified it. Guessing at a shape in a viewport is a
    /// poor substitute for asking the scene what objects exist and how big they are.
    /// </summary>
    public static class SceneDiagnostics
    {
        [MenuItem("Horror House/Debug - Report scene contents", priority = 80)]
        public static void Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("================ SCENE DIAGNOSTICS ================");

            var renderers = Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.AppendLine($"Renderers in scene: {renderers.Length}");
            sb.AppendLine();

            // Anything floating well above the ridge, which tops out around 5.6 m.
            sb.AppendLine("--- renderers whose geometry sits above 8 m ---");
            foreach (var r in renderers
                         .Where(r => r.bounds.max.y > 8f)
                         .OrderByDescending(r => r.bounds.max.y)
                         .Take(25))
                sb.AppendLine(Describe(r));

            sb.AppendLine();
            sb.AppendLine("--- 15 physically largest renderers ---");
            foreach (var r in renderers
                         .OrderByDescending(r => r.bounds.size.magnitude)
                         .Take(15))
                sb.AppendLine(Describe(r));

            sb.AppendLine();
            sb.AppendLine("--- root objects ---");
            foreach (var go in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
                sb.AppendLine($"  {go.name}   active={go.activeSelf}   children={go.transform.childCount}");

            sb.AppendLine();
            sb.AppendLine($"--- skybox: {(RenderSettings.skybox != null ? RenderSettings.skybox.name + " / " + RenderSettings.skybox.shader.name : "none")}");
            sb.AppendLine($"--- canvases ---");
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"  {Path(c.transform)}   mode={c.renderMode}   enabled={c.enabled}");

            // ---- interaction wiring -------------------------------------------------
            sb.AppendLine();
            sb.AppendLine("--- interaction ---");

            var interactor = Object.FindFirstObjectByType<HorrorHouse.PlayerInteractor>();
            sb.AppendLine(interactor != null
                ? $"  PlayerInteractor present, reach {interactor.reach} m, eye={(interactor.eye != null ? "set" : "MISSING")}"
                : "  NO PlayerInteractor - nothing can be used. Re-run Stage 3 - Create Player.");

            var doors = Object.FindObjectsByType<HorrorHouse.Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var switches = Object.FindObjectsByType<HorrorHouse.LightSwitch>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.AppendLine($"  Doors with behaviour: {doors.Length}    Switches: {switches.Length}");

            int noCollider = 0, pivotAtCentre = 0;
            foreach (var d in doors)
            {
                // A usable door needs a collider on the leaf, or the ray passes straight through.
                if (d.GetComponentInChildren<Collider>() == null) { noCollider++; continue; }

                // And the pivot must sit at the hinge EDGE. If the leaf's local offset is nearly
                // zero the pivot is at its centre, and the door will rotate about its middle.
                var leaf = d.transform.childCount > 0 ? d.transform.GetChild(0) : null;
                if (leaf != null && leaf.localPosition.magnitude < 0.05f) pivotAtCentre++;
            }
            sb.AppendLine(noCollider == 0
                ? "  every door leaf has a collider"
                : $"  {noCollider} doors have NO collider - they cannot be aimed at");
            sb.AppendLine(pivotAtCentre == 0
                ? "  every pivot sits at the hinge edge, not the leaf centre"
                : $"  {pivotAtCentre} doors pivot about their middle - they will look fake");

            foreach (var d in doors.Take(6))
            {
                var leaf = d.transform.childCount > 0 ? d.transform.GetChild(0) : null;
                sb.AppendLine($"    {d.doorId,-18} motion={d.motion,-6} " +
                              $"angle={d.openAngle:0}deg dist={d.openDistance:0.00}m " +
                              $"hingeOffset={(leaf != null ? leaf.localPosition.magnitude : 0f):0.00}m");
            }

            int litSwitches = switches.Count(s => !string.IsNullOrEmpty(s.controls));
            sb.AppendLine($"  switches wired to a light group: {litSwitches} of {switches.Length}");

            sb.AppendLine("===================================================");

            try { System.IO.File.WriteAllText("Temp/scene_report.txt", sb.ToString()); } catch { }
            Debug.Log(sb.ToString());
        }

        static string Describe(Renderer r)
        {
            var b = r.bounds;
            return $"  {Path(r.transform),-60} size=({b.size.x:0.#},{b.size.y:0.#},{b.size.z:0.#})m " +
                   $"centre=({b.center.x:0.#},{b.center.y:0.#},{b.center.z:0.#}) topY={b.max.y:0.#} " +
                   $"mat={(r.sharedMaterial != null ? r.sharedMaterial.name : "none")}";
        }

        static string Path(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
