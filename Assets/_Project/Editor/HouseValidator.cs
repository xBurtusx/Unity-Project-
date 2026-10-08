using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using HorrorHouse;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Checks the house against the rules a real building has to obey, BEFORE anyone judges it
    /// by eye. A model can look completely convincing in the viewport and still have a bedroom
    /// you can't reach, a door wider than its wall, or two rooms occupying the same space.
    /// </summary>
    public static class HouseValidator
    {
        const string JsonPath = "Assets/_Project/Data/floorplan.json";

        [MenuItem("Horror House/Stage 1 - Validate House", priority = 1)]
        public static void Validate()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(JsonPath);
            if (asset == null) { Debug.LogError($"[Validator] Missing {JsonPath}."); return; }

            var plan = JsonUtility.FromJson<HouseBuilder.Plan>(asset.text);
            var errors = new List<string>();
            var warnings = new List<string>();
            var notes = new List<string>();

            CheckOverlaps(plan, errors);
            CheckDoorsFit(plan, errors, warnings);
            CheckRoomNamesResolve(plan, errors);
            CheckReachability(plan, errors);
            CheckRoomSizes(plan, warnings);
            CheckRoofs(plan, errors, warnings);
            CheckOpeningsDoNotCollide(plan, errors, warnings);
            CheckSceneGeometry(errors, warnings);
            Summarise(plan, notes);

            var sb = new StringBuilder();
            sb.AppendLine("================ HOUSE VALIDATION ================");
            foreach (var n in notes) sb.AppendLine("  " + n);
            sb.AppendLine();

            if (errors.Count == 0 && warnings.Count == 0)
                sb.AppendLine("PASS - no problems found.");
            else
            {
                if (errors.Count > 0)
                {
                    sb.AppendLine($"ERRORS ({errors.Count}) - these must be fixed:");
                    foreach (var e in errors) sb.AppendLine("  [X] " + e);
                    sb.AppendLine();
                }
                if (warnings.Count > 0)
                {
                    sb.AppendLine($"WARNINGS ({warnings.Count}) - check these are intentional:");
                    foreach (var w in warnings) sb.AppendLine("  [!] " + w);
                }
            }
            sb.AppendLine("==================================================");

            // Also written to disk so the report can be read without the Editor open - useful
            // for tooling, and for checking a build that ran unattended.
            try { System.IO.File.WriteAllText("Temp/validation_report.txt", sb.ToString()); }
            catch { /* a failed convenience write must never fail the validation */ }

            if (errors.Count > 0) Debug.LogError(sb.ToString());
            else if (warnings.Count > 0) Debug.LogWarning(sb.ToString());
            else Debug.Log(sb.ToString());
        }

        // ------------------------------------------------------------ geometry

        static void CheckOverlaps(HouseBuilder.Plan plan, List<string> errors)
        {
            var rooms = plan.rooms;
            for (int i = 0; i < rooms.Length; i++)
                for (int j = i + 1; j < rooms.Length; j++)
                {
                    var a = rooms[i]; var b = rooms[j];
                    float ox = Mathf.Min(a.x + a.w, b.x + b.w) - Mathf.Max(a.x, b.x);
                    float oz = Mathf.Min(a.z + a.d, b.z + b.d) - Mathf.Max(a.z, b.z);
                    if (ox > 0.05f && oz > 0.05f)
                        errors.Add($"Rooms '{a.name}' and '{b.name}' overlap by " +
                                   $"{ox:0.##} x {oz:0.##} ft. Two rooms cannot occupy the same space.");
                }
        }

        static void CheckDoorsFit(HouseBuilder.Plan plan, List<string> errors, List<string> warnings)
        {
            var byName = plan.rooms.ToDictionary(r => r.name);

            foreach (var d in plan.doors)
            {
                if (d.width < 1.8f && d.kind != "overhead")
                    warnings.Add($"Door '{d.id}' is only {d.width * 12:0} inches wide - uncomfortably narrow.");

                if (d.roomB == "EXTERIOR" || !byName.ContainsKey(d.roomA) || !byName.ContainsKey(d.roomB))
                    continue;

                // The shared wall between two rooms can be no longer than their shared extent.
                var a = byName[d.roomA]; var b = byName[d.roomB];
                float sharedX = Mathf.Min(a.x + a.w, b.x + b.w) - Mathf.Max(a.x, b.x);
                float sharedZ = Mathf.Min(a.z + a.d, b.z + b.d) - Mathf.Max(a.z, b.z);
                float shared = Mathf.Max(sharedX, sharedZ);

                if (shared <= 0.05f)
                    errors.Add($"Door '{d.id}' connects '{d.roomA}' to '{d.roomB}', but those rooms " +
                               $"do not touch. There is no wall to put a door in.");
                else if (d.offset + d.width > shared + 0.05f)
                    errors.Add($"Door '{d.id}' needs {d.offset + d.width:0.##} ft of wall but '{d.roomA}'/" +
                               $"'{d.roomB}' only share {shared:0.##} ft. Reduce its offset or width.");
            }

            foreach (var w in plan.windows)
            {
                if (!byName.ContainsKey(w.room))
                    errors.Add($"Window '{w.id}' refers to room '{w.room}', which does not exist.");
                else
                {
                    var r = byName[w.room];
                    float wallLen = (w.wall == "N" || w.wall == "S") ? r.w : r.d;
                    if (w.offset + w.width > wallLen + 0.05f)
                        errors.Add($"Window '{w.id}' needs {w.offset + w.width:0.##} ft but the " +
                                   $"{w.wall} wall of '{w.room}' is only {wallLen:0.##} ft long.");
                }
                float sill = w.sill > 0.01f ? w.sill : plan.construction.windowSillHeight;
                if (sill + 0.5f >= plan.construction.windowHeadHeight)
                    errors.Add($"Window '{w.id}' has its sill at or above its head height.");
            }
        }

        static void CheckRoomNamesResolve(HouseBuilder.Plan plan, List<string> errors)
        {
            var names = new HashSet<string>(plan.rooms.Select(r => r.name));
            foreach (var d in plan.doors)
            {
                if (d.roomA != "EXTERIOR" && !names.Contains(d.roomA))
                    errors.Add($"Door '{d.id}' refers to unknown room '{d.roomA}'.");
                if (d.roomB != "EXTERIOR" && !names.Contains(d.roomB))
                    errors.Add($"Door '{d.id}' refers to unknown room '{d.roomB}'.");
            }
            var dupes = plan.rooms.GroupBy(r => r.name).Where(g => g.Count() > 1);
            foreach (var g in dupes) errors.Add($"Room name '{g.Key}' is used {g.Count()} times.");
        }

        // ------------------------------------------------------------ circulation

        /// <summary>
        /// Walks the house as a player would: start outside, come through the front door, and
        /// see which rooms can actually be reached through doors and open thresholds.
        /// </summary>
        static void CheckReachability(HouseBuilder.Plan plan, List<string> errors)
        {
            var graph = new Dictionary<string, HashSet<string>>();
            void Link(string a, string b)
            {
                if (!graph.TryGetValue(a, out var sa)) graph[a] = sa = new HashSet<string>();
                if (!graph.TryGetValue(b, out var sb)) graph[b] = sb = new HashSet<string>();
                sa.Add(b); sb.Add(a);
            }

            foreach (var d in plan.doors) Link(d.roomA, d.roomB);

            // Open thresholds carry traffic too, even though they have no door.
            Link("GreatRoom", "Dining");      Link("GreatRoom", "Kitchen");
            Link("Dining", "Kitchen");        Link("GreatRoom", "Hall");
            Link("GreatRoom", "HallToEntry"); Link("HallToEntry", "Kitchen");
            Link("HallToEntry", "Entry");     Link("Entry", "BasementStair");
            Link("Entry", "MudHall");         Link("Mudroom", "Laundry");
            Link("Bath2", "Bath2Vestibule");  Link("Hall", "Bath2Vestibule");

            // Porches sit outside, so they are reached by stepping out of the house.
            Link("EXTERIOR", "FrontPorch"); Link("EXTERIOR", "RearPorch");

            var seen = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue("EXTERIOR"); seen.Add("EXTERIOR");
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!graph.TryGetValue(cur, out var nbrs)) continue;
                foreach (var n in nbrs) if (seen.Add(n)) queue.Enqueue(n);
            }

            foreach (var r in plan.rooms)
                if (!seen.Contains(r.name))
                    errors.Add($"'{r.name}' cannot be reached from the front door. " +
                               $"It needs a door, or an open threshold registered in HouseBuilder.OpenPairs.");
        }

        static void CheckRoomSizes(HouseBuilder.Plan plan, List<string> warnings)
        {
            foreach (var r in plan.rooms)
            {
                if (r.w < 2f || r.d < 2f)
                    warnings.Add($"'{r.name}' is {r.w:0.##} x {r.d:0.##} ft - too small to stand in.");
                if (r.type == "bedroom" && r.w * r.d < 70f)
                    warnings.Add($"'{r.name}' is only {r.w * r.d:0} sq ft, small for a bedroom.");
                if (r.assumed)
                    warnings.Add($"'{r.name}' uses dimensions INFERRED from the plan, not printed on it. " +
                                 $"Worth confirming against the real drawing.");
            }
        }

        // ------------------------------------------------------------ openings sharing a wall

        /// <summary>
        /// Two openings on the same wall must not overlap, and should not touch either - a door
        /// and a window with no masonry between them leaves no jamb to hang either one from.
        ///
        /// The builder does not complain about this: it punches both holes, draws both frames,
        /// and the result is a window sill running straight through a door leaf. It only shows
        /// up by standing in front of that one wall, which is a poor way to find it.
        /// </summary>
        static void CheckOpeningsDoNotCollide(HouseBuilder.Plan plan, List<string> errors, List<string> warnings)
        {
            // Group by the wall each opening lands in, the same way the builder resolves them.
            var byWall = new Dictionary<string, List<(string id, float a, float b)>>();

            void Add(string wallKey, string id, float offset, float width)
            {
                if (!byWall.TryGetValue(wallKey, out var list))
                    byWall[wallKey] = list = new List<(string, float, float)>();
                list.Add((id, offset, offset + width));
            }

            foreach (var d in plan.doors)
            {
                string key = d.roomB == "EXTERIOR"
                    ? $"{d.roomA}|EXTERIOR|{d.wall}"
                    : string.CompareOrdinal(d.roomA, d.roomB) <= 0
                        ? $"{d.roomA}|{d.roomB}" : $"{d.roomB}|{d.roomA}";
                Add(key, d.id, d.offset, d.width);
            }

            foreach (var w in plan.windows)
                Add($"{w.room}|EXTERIOR|{w.wall}", w.id, w.offset, w.width);

            foreach (var kv in byWall)
            {
                var sorted = kv.Value.OrderBy(o => o.a).ToList();
                for (int i = 0; i < sorted.Count - 1; i++)
                {
                    var cur = sorted[i];
                    var next = sorted[i + 1];
                    float gap = next.a - cur.b;

                    if (gap < -0.01f)
                        errors.Add($"'{cur.id}' and '{next.id}' are in the same wall ({kv.Key}) and " +
                                   $"OVERLAP by {-gap:0.##} ft. They are being cut into the same hole - " +
                                   $"you will see one's frame passing through the other. " +
                                   $"'{cur.id}' spans {cur.a:0.##}-{cur.b:0.##} ft, " +
                                   $"'{next.id}' spans {next.a:0.##}-{next.b:0.##} ft.");
                    else if (gap < 0.33f)
                        warnings.Add($"'{cur.id}' and '{next.id}' are only {gap:0.##} ft apart in the " +
                                     $"same wall ({kv.Key}). That leaves almost no jamb between them.");
                }
            }
        }

        // ------------------------------------------------------------ roofs

        /// <summary>
        /// Where a secondary gable crosses the main one, its ridge has to stop at exactly the
        /// point where the main roof's slope has risen to meet it. Stop short and the ridge
        /// bursts out through the main roof trailing an open end; run long and it pokes out the
        /// far side. Both read instantly as broken, and neither is visible from inside the house,
        /// which is why this is worth checking by arithmetic rather than by eye.
        /// </summary>
        static void CheckRoofs(HouseBuilder.Plan plan, List<string> errors, List<string> warnings)
        {
            if (plan.roofs == null || plan.roofs.Length == 0)
            {
                warnings.Add("The house has no roof defined.");
                return;
            }

            // The biggest roof is the main one; everything else ties into it.
            var main = plan.roofs.OrderByDescending(r => r.w * r.d).First();

            foreach (var r in plan.roofs)
            {
                float ceiling = plan.construction.defaultCeilingHeight;
                if (Mathf.Abs(r.@base - ceiling) > 0.01f)
                    warnings.Add($"Roof '{r.id}' sits at {r.@base:0.##} ft but the ceilings are " +
                                 $"{ceiling:0.##} ft. The roof would not land on the wall plate.");

                if (r.pitch <= 0.5f)
                    errors.Add($"Roof '{r.id}' has a pitch of {r.pitch:0.##}:12, which will not shed water.");

                if (r == main) continue;

                if (r.RidgeHeight >= main.RidgeHeight - 0.01f)
                    warnings.Add($"Roof '{r.id}' reaches {r.RidgeHeight:0.##} ft, at or above the main " +
                                 $"ridge at {main.RidgeHeight:0.##} ft. A wing that matches or beats the " +
                                 $"main mass makes the house read as a pile of equal boxes.");

                // How far from the main ridge the main roof plane has dropped to this ridge's height.
                float drop = main.RidgeHeight - r.RidgeHeight;
                if (drop <= 0f || main.Slope <= 0f) continue;
                float runFromRidge = drop / main.Slope;

                // The axis the main roof varies along: across its span, perpendicular to its ridge.
                bool mainVariesAlongZ = main.RidgeAlongX;
                float mainMid = mainVariesAlongZ ? main.z + main.d * 0.5f : main.x + main.w * 0.5f;

                // This wing projects away from the main ridge; find the end that points back at it.
                float wingNear = mainVariesAlongZ ? r.z : r.x;
                float wingFar  = mainVariesAlongZ ? r.z + r.d : r.x + r.w;
                bool projectsLow = Mathf.Abs(wingNear - mainMid) > Mathf.Abs(wingFar - mainMid);
                float tieEnd = projectsLow ? wingFar : wingNear;
                float required = projectsLow ? mainMid - runFromRidge : mainMid + runFromRidge;

                float err = tieEnd - required;
                if (Mathf.Abs(err) > 0.25f)
                {
                    string axis = mainVariesAlongZ ? "z" : "x";
                    string fix = projectsLow
                        ? $"set its {(mainVariesAlongZ ? "d" : "w")} to {Mathf.Abs(required - wingNear):0.###}"
                        : $"start it at {axis}={required:0.###}";
                    errors.Add($"Roof '{r.id}' meets the main roof at {axis}={tieEnd:0.###} but its ridge " +
                               $"({r.RidgeHeight:0.##} ft) only reaches the main slope at {axis}={required:0.###} " +
                               $"- out by {Mathf.Abs(err):0.##} ft. " +
                               (err > 0 ? "It will poke out through the far side of the main roof. "
                                        : "It will burst out through the main roof with an open end in mid-air. ") +
                               $"To fix: {fix}.");
                }
            }
        }

        // ------------------------------------------------------------ scene

        static void CheckSceneGeometry(List<string> errors, List<string> warnings)
        {
            var root = GameObject.Find("HOUSE_SHELL (generated - do not edit by hand)");
            if (root == null)
            {
                warnings.Add("No generated house in the open scene - data was checked, geometry was not. " +
                             "Run 'Build House Shell' first.");
                return;
            }

            int noCollider = root.GetComponentsInChildren<MeshRenderer>()
                .Count(m => m.GetComponent<Collider>() == null
                         && m.transform.parent != null
                         && m.transform.parent.name.StartsWith("Wall_"));
            if (noCollider > 0)
                errors.Add($"{noCollider} wall pieces have no collider - the player would walk through them.");

            var doors = root.GetComponentsInChildren<DoorMeta>();
            foreach (var d in doors)
            {
                var leaf = d.transform.childCount > 0 ? d.transform.GetChild(0) : null;
                if (leaf == null) { errors.Add($"Door '{d.doorId}' has no leaf under its pivot."); continue; }

                // A hinge at the centre of the leaf is the classic broken-door signature.
                if (leaf.localPosition.magnitude < 0.05f)
                    errors.Add($"Door '{d.doorId}' has its pivot at the centre of the leaf. " +
                               $"The pivot must sit at the hinge edge or the door will rotate about its middle.");
                if (leaf.GetComponent<Collider>() == null)
                    errors.Add($"Door '{d.doorId}' leaf has no collider.");
            }

            if (doors.Length == 0) warnings.Add("No doors found in the generated house.");
        }

        static void Summarise(HouseBuilder.Plan plan, List<string> notes)
        {
            var inside = plan.rooms.Where(r => !r.exterior && r.type != "garage").ToArray();
            float condArea = inside.Sum(r => r.w * r.d);
            float garage = plan.rooms.Where(r => r.type == "garage").Sum(r => r.w * r.d);

            float minX = plan.rooms.Min(r => r.x), maxX = plan.rooms.Max(r => r.x + r.w);
            float minZ = plan.rooms.Min(r => r.z), maxZ = plan.rooms.Max(r => r.z + r.d);

            notes.Add($"Rooms: {plan.rooms.Length}   Doors: {plan.doors.Length}   Windows: {plan.windows.Length}");
            notes.Add($"Conditioned floor area: {condArea:0} sq ft   Garage: {garage:0} sq ft");
            notes.Add($"Overall extents: {maxX - minX:0.#} ft east-west x {maxZ - minZ:0.#} ft north-south");
            notes.Add($"In metres: {(maxX - minX) * 0.3048f:0.#} m x {(maxZ - minZ) * 0.3048f:0.#} m");
        }
    }
}
