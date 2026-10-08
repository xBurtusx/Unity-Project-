using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using HorrorHouse;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Inspects every switch, light, door and window in the built house and reports what is
    /// physically wrong with it.
    ///
    /// This is done by measurement rather than by eye. A switch floating in a doorway, a door
    /// whose leaf sweeps through a wall, a fixture buried in a ceiling slab - all of these are
    /// easy to walk straight past in a viewport and impossible to miss with a raycast. Looking at
    /// 24 rooms by hand would take an hour and still miss things; this takes a second and does not
    /// get bored.
    ///
    /// Reports only. It changes nothing.
    /// </summary>
    public static class InteriorAudit
    {
        const float F = 0.3048f;
        const string PlanPath = "Assets/_Project/Data/floorplan.json";

        class Issue
        {
            public string room, subject, severity, detail;
        }

        [MenuItem("Horror House/Debug - Audit interior", priority = 81)]
        public static void Run()
        {
            Physics.SyncTransforms();

            var plan = LoadPlan();
            var issues = new List<Issue>();
            var sb = new StringBuilder();

            sb.AppendLine("=================== INTERIOR AUDIT ===================");
            sb.AppendLine();

            AuditSwitches(plan, issues, sb);
            AuditLights(plan, issues, sb);
            AuditDoors(issues, sb);

            sb.AppendLine();
            sb.AppendLine("=================== SUMMARY ===================");
            foreach (var g in issues.GroupBy(i => i.severity).OrderBy(g => g.Key))
                sb.AppendLine($"  {g.Key}: {g.Count()}");
            sb.AppendLine();

            foreach (var g in issues.GroupBy(i => i.room).OrderBy(g => g.Key))
            {
                sb.AppendLine($"  [{g.Key}]");
                foreach (var i in g.OrderBy(i => i.severity))
                    sb.AppendLine($"     {i.severity,-8} {i.subject,-22} {i.detail}");
            }
            sb.AppendLine("===============================================");

            try { System.IO.File.WriteAllText("Temp/interior_audit.txt", sb.ToString()); } catch { }
            Debug.Log(sb.ToString());
        }

        // ------------------------------------------------------------ switches

        static void AuditSwitches(HouseBuilder.Plan plan, List<Issue> issues, StringBuilder sb)
        {
            sb.AppendLine("--- SWITCHES ---");
            var switches = Object.FindObjectsByType<LightSwitch>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(s => s.room).ThenBy(s => s.switchId).ToArray();

            var doors = Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).ToArray();

            foreach (var s in switches)
            {
                var p = s.transform.position;
                var intoRoom = s.transform.forward;

                // The wall must be BEHIND the switch. Start slightly in front and fire backwards:
                // if nothing is hit, the switch is hanging in a doorway or an open threshold.
                // The switch's own aim-assist collider has to be excluded, or every switch
                // reports itself as its own wall - which is exactly what the first run did.
                var own = new HashSet<Collider>(s.GetComponentsInChildren<Collider>());
                var start = p + intoRoom * 0.12f;

                Collider wall = null;
                foreach (var h in Physics.RaycastAll(start, -intoRoom, 0.45f)
                                         .OrderBy(h => h.distance))
                {
                    if (own.Contains(h.collider)) continue;
                    wall = h.collider;
                    break;
                }
                bool wallBehind = wall != null;

                bool blockedInFront = Physics.RaycastAll(p + intoRoom * 0.02f, intoRoom, 0.35f)
                                             .Any(h => !own.Contains(h.collider));

                string wallName = wallBehind ? (wall.transform.parent?.name ?? wall.name) : "none";

                string detail = $"at ({p.x:0.00},{p.y:0.00},{p.z:0.00})m  wall behind: {wallName}";
                sb.AppendLine($"  {s.switchId,-16} {s.room,-14} {detail}");

                if (!wallBehind)
                    issues.Add(new Issue {
                        room = s.room, subject = s.switchId, severity = "BROKEN",
                        detail = "no wall behind it - the switch is floating in an opening or a gap." });

                if (blockedInFront)
                    issues.Add(new Issue {
                        room = s.room, subject = s.switchId, severity = "BROKEN",
                        detail = "something solid is immediately in front - unreachable." });

                // A real switch sits by the door you walk through, on the latch side.
                float nearest = float.MaxValue;
                string nearestDoor = "none";
                foreach (var d in doors)
                {
                    float dist = Vector3.Distance(
                        new Vector3(p.x, 0f, p.z),
                        new Vector3(d.transform.position.x, 0f, d.transform.position.z));
                    if (dist < nearest) { nearest = dist; nearestDoor = d.doorId; }
                }

                if (nearest > 2.5f)
                    issues.Add(new Issue {
                        room = s.room, subject = s.switchId, severity = "WRONG",
                        detail = $"{nearest:0.0} m from the nearest door ({nearestDoor}). " +
                                 $"Switches belong beside the door you enter by, not mid-wall." });
            }
            sb.AppendLine();
        }

        // ------------------------------------------------------------ lights

        static void AuditLights(HouseBuilder.Plan plan, List<Issue> issues, StringBuilder sb)
        {
            sb.AppendLine("--- LIGHTS ---");
            var fixtures = Object.FindObjectsByType<LightFixture>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(f => f.room).ThenBy(f => f.fixtureId).ToArray();

            foreach (var group in fixtures.GroupBy(f => f.room).OrderBy(g => g.Key))
            {
                var list = group.ToList();
                sb.AppendLine($"  {group.Key,-16} {list.Count} fixture(s): " +
                              string.Join(", ", list.Select(f => f.fixtureId)));

                // Ordinary rooms have one ceiling fitting. More than that is a lighting rig.
                int ceilingish = list.Count(f => !f.fixtureId.Contains("Lamp"));
                if (ceilingish > 1 && group.Key != "Garage")
                    issues.Add(new Issue {
                        room = group.Key, subject = "lighting", severity = "EXCESS",
                        detail = $"{ceilingish} ceiling fittings. A domestic room has one. " +
                                 $"Each extra one also costs a real-time shadow map." });
            }

            sb.AppendLine();
            foreach (var f in fixtures)
            {
                var p = f.transform.position;
                // Buried in the ceiling slab, or sitting below it in open air?
                bool ceilingAbove = Physics.Raycast(p, Vector3.up, out var up, 1.2f);
                bool insideSolid = Physics.CheckSphere(p, 0.06f);

                // Pendants and wall lights are SUPPOSED to sit well below the ceiling - a pendant
                // hanging at ceiling level would be the fault, not the other way round.
                bool hangsByDesign = f.fixtureType is "pendant" or "wall" or "lamp" or "porch";

                if (insideSolid)
                    issues.Add(new Issue {
                        room = f.room, subject = f.fixtureId, severity = "BROKEN",
                        detail = "the fitting is inside solid geometry." });
                else if (!ceilingAbove && !hangsByDesign)
                    issues.Add(new Issue {
                        room = f.room, subject = f.fixtureId, severity = "WRONG",
                        detail = "no ceiling within 1.2 m above it - the fitting hangs in open air." });
                else if (ceilingAbove && !hangsByDesign && up.distance > 0.75f)
                    issues.Add(new Issue {
                        room = f.room, subject = f.fixtureId, severity = "WRONG",
                        detail = $"hangs {up.distance:0.00} m below the ceiling - too low for a flush fitting." });
            }
            sb.AppendLine();
        }

        // ------------------------------------------------------------ doors

        static void AuditDoors(List<Issue> issues, StringBuilder sb)
        {
            sb.AppendLine("--- DOORS ---");
            var doors = Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(d => d.doorId).ToArray();

            foreach (var d in doors)
            {
                var leaf = d.transform.childCount > 0 ? d.transform.GetChild(0) : null;
                if (leaf == null) continue;

                // Must use the leaf's own BOX, oriented with the leaf. Using the renderer's
                // axis-aligned bounds is wrong and spectacularly so: a thin door at 45 degrees
                // has an AABB nearly as large as the whole swept quarter-circle, which overlaps
                // the adjacent wall every single time. The first run reported 15 broken doors
                // purely because of that, and every one of them was fine.
                var box = leaf.GetComponent<BoxCollider>();
                if (box == null) continue;

                var ownColliders = new HashSet<Collider>(d.GetComponentsInChildren<Collider>());

                // Swing the leaf through its arc and look for anything it passes through. A door
                // that opens into a wall, a worktop or another door is the commonest fault in a
                // generated building, and it is invisible until someone tries to open it.
                var originalRot = d.transform.localRotation;
                var originalPos = d.transform.localPosition;

                var blockers = new HashSet<string>();
                for (int step = 1; step <= 6; step++)
                {
                    float f01 = step / 6f;
                    if (d.motion == Door.Motion.Swing)
                        d.transform.localRotation = originalRot * Quaternion.Euler(0f, d.openAngle * f01, 0f);
                    else if (d.motion == Door.Motion.Roll)
                        d.transform.localPosition = originalPos + Vector3.up * (d.openDistance * f01);
                    else
                        continue;   // sliding doors run along their own wall by design

                    Physics.SyncTransforms();

                    var centre = leaf.TransformPoint(box.center);
                    var half = Vector3.Scale(box.size * 0.5f, leaf.lossyScale) * 0.85f;

                    foreach (var hit in Physics.OverlapBox(centre, half, leaf.rotation))
                    {
                        if (ownColliders.Contains(hit)) continue;
                        var n = hit.transform.parent != null ? hit.transform.parent.name : hit.name;
                        if (n.StartsWith("Floor_") || n.StartsWith("Grass")) continue;
                        blockers.Add(n);
                    }
                }

                d.transform.localRotation = originalRot;
                d.transform.localPosition = originalPos;
                Physics.SyncTransforms();

                string state = blockers.Count == 0 ? "clear" : "BLOCKED by " + string.Join(", ", blockers.Take(4));
                sb.AppendLine($"  {d.doorId,-18} {d.motion,-6} {state}");

                if (blockers.Count > 0)
                    issues.Add(new Issue {
                        room = "doors", subject = d.doorId, severity = "BROKEN",
                        detail = $"the leaf sweeps through: {string.Join(", ", blockers.Take(5))}" });
            }
            sb.AppendLine();
        }

        /// <summary>
        /// Proves a door can actually be opened and reached, outside play mode.
        ///
        /// Two separate things can break "the door will not open", and they look identical from
        /// the player's side: the door's own movement is wrong, or the ray never finds it. This
        /// tests both - drive the door open and measure whether the leaf really moved, then fire
        /// a ray at it from head height the way the player's interactor does.
        /// </summary>
        [MenuItem("Horror House/Debug - Test doors", priority = 82)]
        public static void TestDoors()
        {
            Physics.SyncTransforms();
            var sb = new StringBuilder();
            sb.AppendLine("=================== DOOR TEST ===================");

            var doors = Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(d => d.doorId).ToArray();

            sb.AppendLine($"Doors found: {doors.Length}");
            sb.AppendLine();

            int movedOk = 0, reachableOk = 0;

            foreach (var d in doors)
            {
                var leaf = d.transform.childCount > 0 ? d.transform.GetChild(0) : null;
                if (leaf == null) { sb.AppendLine($"  {d.doorId,-18} NO LEAF"); continue; }

                d.CapturePose();
                var closedPos = leaf.position;

                d.SetOpenImmediate(true);
                Physics.SyncTransforms();
                float travel = Vector3.Distance(leaf.position, closedPos);

                d.SetOpenImmediate(false);
                Physics.SyncTransforms();

                // Now check the player could actually hit it: stand back from the leaf face and
                // fire at its centre, exactly as PlayerInteractor does.
                var box = leaf.GetComponent<BoxCollider>();
                string reach = "no collider";
                if (box != null)
                {
                    var centre = leaf.TransformPoint(box.center);
                    var approach = leaf.forward;   // the leaf's thin axis - its face normal
                    var eye = centre + approach * 1.1f;

                    bool found = false;
                    foreach (var h in Physics.RaycastAll(eye, -approach, 2.2f)
                                             .OrderBy(h => h.distance))
                    {
                        var it = h.collider.GetComponentInParent<IInteractable>();
                        if (it == null) continue;
                        found = it is Door hitDoor && hitDoor.doorId == d.doorId;
                        break;
                    }
                    reach = found ? "reachable" : "RAY MISSES - something else is in the way";
                    if (found) reachableOk++;
                }

                bool moves = travel > 0.05f;
                if (moves) movedOk++;

                sb.AppendLine($"  {d.doorId,-18} {d.motion,-6} " +
                              $"{(moves ? $"moves {travel:0.00} m" : "DOES NOT MOVE"),-22} {reach}" +
                              $"   locked={d.locked} flag='{d.requiredFlag}'");
            }

            sb.AppendLine();
            sb.AppendLine($"  {movedOk}/{doors.Length} move when opened");
            sb.AppendLine($"  {reachableOk}/{doors.Length} can be hit by the interaction ray");
            sb.AppendLine("=================================================");

            try { System.IO.File.WriteAllText("Temp/door_test.txt", sb.ToString()); } catch { }
            Debug.Log(sb.ToString());
        }

        static HouseBuilder.Plan LoadPlan()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(PlanPath);
            return asset != null ? JsonUtility.FromJson<HouseBuilder.Plan>(asset.text) : null;
        }
    }
}
