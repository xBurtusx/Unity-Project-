using System.IO;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Adds a lawn of real grass blades next to the flat ground box HouseBuilder makes.
    ///
    /// The box stays: it is the collider the player walks on and the dark soil seen between
    /// blades. This only adds the ProceduralGrass component beside it and tells it where NOT to
    /// grow - under the house, porches, driveway, path, street and pavement - from floorplan.json,
    /// so the lawn follows the plan rather than a second hand-kept set of numbers.
    /// </summary>
    public static class GrassSetup
    {
        const string PlanPath = "Assets/_Project/Data/floorplan.json";
        const float F = 0.3048f;
        const float Margin = 0.6f;   // feet of bare ground around buildings and paving

        [MenuItem("Horror House/Add Real Grass", priority = 62)]
        public static void Setup()
        {
            var plan = JsonUtility.FromJson<HouseBuilder.Plan>(File.ReadAllText(PlanPath));
            if (plan?.site == null) { Debug.LogError("[Grass] floorplan.json has no site block."); return; }

            var ground = GameObject.Find("Grass");
            if (ground == null)
            {
                Debug.LogError("[Grass] No 'Grass' ground object in the scene. Run Stage 1 - Build House first.");
                return;
            }
            var parent = ground.transform.parent != null ? ground.transform.parent : ground.transform;

            var old = parent.Find("Lawn");
            if (old != null) Object.DestroyImmediate(old.gameObject);

            var go = new GameObject("Lawn");
            Undo.RegisterCreatedObjectUndo(go, "Add Lawn");
            go.transform.SetParent(parent, false);

            var lawn = go.AddComponent<ProceduralGrass>();
            var s = plan.site;

            // The street and pavement strips along the front are paving, not lawn.
            float frontZ = s.lotZ;
            foreach (var p in plan.site.paving)
                if (p.name == "Sidewalk") frontZ = p.z + p.d;

            lawn.min = new Vector2(s.lotX * F, frontZ * F);
            lawn.max = new Vector2((s.lotX + s.lotW) * F, (s.lotZ + s.lotD) * F);
            lawn.groundY = ground.transform.localPosition.y
                           + 0.5f * 1.0f * F;   // top face of the 1 ft ground box

            lawn.exclusions.Clear();
            foreach (var r in plan.rooms)
                lawn.exclusions.Add(KeepOut(r.x, r.z, r.w, r.d));
            foreach (var p in plan.site.paving)
                lawn.exclusions.Add(KeepOut(p.x, p.z, p.w, p.d));

            lawn.Rebuild();
            EditorUtility.SetDirty(go);
            Debug.Log($"[Grass] Lawn added: {lawn.exclusions.Count} keep-out rectangles, " +
                      $"{lawn.bladesPerSquareMetre:0} blades/m2. Tune on the 'Lawn' object.");
        }

        static Vector4 KeepOut(float x, float z, float w, float d) =>
            new Vector4((x - Margin) * F, (z - Margin) * F, (x + w + Margin) * F, (z + d + Margin) * F);
    }
}
