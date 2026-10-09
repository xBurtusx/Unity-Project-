using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Furnishes the player's house from Kenney's CC0 Furniture Kit.
    ///
    /// Nothing is dropped blindly. Every piece is measured, stood against a wall that a ray has
    /// confirmed is really there at both ends of the piece (so nothing sits across a doorway), and then
    /// test-fitted: it must lie inside its room, must not overlap a wall or anything already placed, and
    /// must keep clear of the light switches. A piece that fails any test is removed and logged, which
    /// is better than a sofa half inside a wall.
    /// </summary>
    public static partial class HouseScene
    {
        const string FurnDir = "Assets/Furniture/Models";
        const float FurnScale = 0.20f;  // the kit imports roughly six times life size; this brings a bed to ~1.6 m, a fridge to ~0.9 m
        const float FrontSign = -1f;    // the kit's models face -Z, so every piece is turned half a circle from the direction it should face

        static readonly Dictionary<string, Vector3> furnSize = new Dictionary<string, Vector3>();
        static readonly List<Vector3> keepClear = new List<Vector3>();     // switches and other things that must stay reachable
        static int furnPlaced, furnRejected;

        static GameObject FurnPrefab(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{FurnDir}/{name}.fbx");

        /// <summary>The kit is not modelled to one scale; these pieces come out oversized next to the rest.</summary>
        static float ModelScale(string model)
        {
            switch (model)
            {
                case "toilet": return 0.5f;
                case "trashcan": return 0.45f;
                default: return 1f;
            }
        }
        static bool loose;

        /// <summary>
        /// Bounds of a piece from its actual mesh geometry. Renderer.bounds proved unreliable for a few of
        /// the kit's models (the toilet and the bin reported different sizes depending on when they were
        /// measured), so every corner of every mesh is transformed into world space instead.
        /// </summary>
        static Bounds FBounds(GameObject go)
        {
            bool any = false; var b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r != null && !r.enabled) continue;
                var mb = mf.sharedMesh.bounds;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var w = m.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; } else b.Encapsulate(w);
                }
            }
            return b;
        }

        static Vector3 FurnSize(string name)
        {
            if (furnSize.TryGetValue(name, out var s)) return s;
            var prefab = FurnPrefab(name);
            if (prefab == null) return furnSize[name] = new Vector3(1, 1, 1);
            var g = Object.Instantiate(prefab);
            g.transform.localScale = Vector3.one * FurnScale * ModelScale(name);   // measure exactly the way a placed piece is built
            var b = FBounds(g);
            Object.DestroyImmediate(g);
            return furnSize[name] = b.size;
        }


        static readonly Dictionary<string, Material> furnMats = new Dictionary<string, Material>();

        /// <summary>
        /// The kit is painted in bright toy colours (pink fabric, peach wood). Re-tint each material to a
        /// natural equivalent: pink and red fabric becomes slate, beige or sage, picked per piece so a
        /// room's sofa and rug do not all match; peach wood becomes a darker stained wood. Whites, greys,
        /// metals and glass are left exactly as they are.
        /// </summary>
        static void Recolour(GameObject g, string model)
        {
            var fabrics = new[] { new Color(0.20f, 0.23f, 0.27f), new Color(0.55f, 0.50f, 0.42f), new Color(0.30f, 0.36f, 0.31f), new Color(0.26f, 0.22f, 0.20f) };
            int pick = Mathf.Abs(model.GetHashCode()) % fabrics.Length;
            foreach (var r in g.GetComponentsInChildren<Renderer>())
            {
                var src = r.sharedMaterials;
                var dst = new Material[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    var m = src[i];
                    if (m == null) { dst[i] = m; continue; }
                    Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
                    Color.RGBToHSV(c, out float h, out float s, out float v);
                    Color? target = null;
                    if ((h < 0.04f || h > 0.88f) && s > 0.22f) target = fabrics[pick];                       // pink / red fabric
                    else if (h >= 0.04f && h <= 0.13f && s > 0.18f) target = new Color(c.r * 0.55f, c.g * 0.48f, c.b * 0.42f);   // peach wood -> stained wood
                    else if (h >= 0.13f && h <= 0.2f && s > 0.3f) target = new Color(0.45f, 0.40f, 0.30f);    // yellow -> muted tan
                    if (target == null) { dst[i] = m; continue; }
                    string key = $"{m.name}_{(Color32)target.Value}_{pick}";
                    if (!furnMats.TryGetValue(key, out var nm))
                    {
                        nm = new Material(m) { name = "furn_" + key };
                        nm.SetColor("_BaseColor", target.Value); nm.color = target.Value;
                        furnMats[key] = nm;
                    }
                    dst[i] = nm;
                }
                r.sharedMaterials = dst;
            }
        }
        // ============================================================ placement

        /// <summary>Stands a model on the floor at a point, facing a direction, then test-fits it. Null if it does not fit.</summary>
        static GameObject PlaceFurn(Transform parent, string model, Vector3 floorCentre, Vector3 face, Bounds room,
                                    float raise = 0f, bool stacked = false)
        {
            var prefab = FurnPrefab(model);
            if (prefab == null) { log.AppendLine($"  furniture: missing model {model}"); return null; }

            var g = Object.Instantiate(prefab);
            g.name = model;
            g.transform.SetParent(parent, false);
            g.transform.localScale = Vector3.one * FurnScale * ModelScale(model);
            Recolour(g, model);
            face.y = 0f;
            g.transform.rotation = Quaternion.LookRotation(face.normalized * FrontSign, Vector3.up);

            var b = FBounds(g);
            g.transform.position += new Vector3(floorCentre.x - b.center.x, floorCentre.y + raise - b.min.y, floorCentre.z - b.center.z);
            b = FBounds(g);

            bool inside = b.min.x >= room.min.x - 0.1f && b.max.x <= room.max.x + 0.1f &&
                          b.min.z >= room.min.z - 0.1f && b.max.z <= room.max.z + 0.1f;

            bool nearSwitch = false;
            foreach (var k in keepClear)
                if (k.x > b.min.x - 0.3f && k.x < b.max.x + 0.3f && k.z > b.min.z - 0.3f && k.z < b.max.z + 0.3f) nearSwitch = true;

            bool hits = false; string hitNames = "";
            if (!stacked)
            {
                var half = b.extents;
                var centre = b.center + Vector3.up * 0.05f;
                var hitCols = Physics.OverlapBox(centre, new Vector3(half.x * 0.9f, Mathf.Max(0.02f, half.y - 0.08f), half.z * 0.9f));
                hits = hitCols.Length > 0;
                if (hits) hitNames = string.Join(",", hitCols.Select(c => c.name + "/" + c.transform.root.name).Distinct());
            }

            if (!inside || hits || nearSwitch)
            {
                if (model == "toilet" && furnRejected < 400) log.AppendLine($"  DEBUG toilet rejected ({(!inside ? "outside" : hits ? "overlap " + hitNames : "switch")}) size {b.size:F2} centre {b.center:F2} room {room.min:F2}..{room.max:F2}");
                if (!quiet) log.AppendLine($"  furniture: rejected {model} ({(!inside ? "outside room" : hits ? "overlaps something" : "covers a switch")}) hit [{hitNames}] item {b.min:F2}..{b.max:F2} room {room.min:F2}..{room.max:F2} target {floorCentre:F2}");
                Object.DestroyImmediate(g);
                furnRejected++; Why(!inside ? "outside room" : hits ? "overlap " + hitNames : "switch");
                return null;
            }

            // A collider so the player cannot walk through it. World-aligned box, child of the item.
            var c = new GameObject("Collider");
            c.transform.SetParent(g.transform, true);
            c.transform.rotation = Quaternion.identity;
            c.transform.position = b.center;
            var box = c.AddComponent<BoxCollider>();
            box.size = b.size;   // parented with world scale kept, so this is already in world units
            Physics.SyncTransforms();
            furnPlaced++;
            return g;
        }

        /// <summary>
        /// Finds a point on the wall of a room: the surface a ray finds when fired out from just inside.
        /// Side 0 = south (z min), 1 = north, 2 = west (x min), 3 = east. <paramref name="along"/> is metres from the corner.
        /// </summary>
        static bool Slot(Bounds room, int side, float along, out Vector3 point, out Vector3 inward)
        {
            inward = side == 0 ? Vector3.forward : side == 1 ? Vector3.back : side == 2 ? Vector3.right : Vector3.left;
            bool alongX = side < 2;
            float edge = side == 0 ? room.min.z : side == 1 ? room.max.z : side == 2 ? room.min.x : room.max.x;
            float u = (alongX ? room.min.x : room.min.z) + along;
            float y = room.max.y + 1.0f;
            var from = (alongX ? new Vector3(u, y, edge) : new Vector3(edge, y, u)) + inward * 0.6f;
            point = default;
            foreach (var h in Physics.RaycastAll(from, -inward, 1.5f).OrderBy(h => h.distance))
            {
                if (!(h.collider is MeshCollider)) continue;       // walls only, not furniture already placed
                point = new Vector3(h.point.x, room.max.y, h.point.z);
                return true;
            }
            return false;
        }

        static float WallLength(Bounds room, int side) => side < 2 ? room.size.x : room.size.z;

        /// <summary>How much of a side is real wall (as opposed to a doorway or opening), 0..1.</summary>
        static float SolidFraction(Bounds room, int side)
        {
            int n = 12, solid = 0;
            float len = WallLength(room, side);
            for (int i = 0; i < n; i++)
                if (Slot(room, side, Mathf.Lerp(0.3f, len - 0.3f, i / (float)(n - 1)), out _, out _)) solid++;
            return solid / (float)n;
        }

        /// <summary>The side with the most usable wall, optionally excluding some sides.</summary>
        static int BestSide(Bounds room, params int[] exclude)
        {
            int best = -1; float bestScore = -1f;
            for (int s = 0; s < 4; s++)
            {
                if (exclude.Contains(s)) continue;
                float score = SolidFraction(room, s) * WallLength(room, s);
                if (score > bestScore) { bestScore = score; best = s; }
            }
            return best;
        }

        /// <summary>Stands a piece with its back to a wall, centred <paramref name="along"/> metres from the corner. Both ends must be real wall.</summary>
        static GameObject OnWall(Transform parent, string model, Bounds room, int side, float along, float raise = 0f)
        {
            var size = FurnSize(model);
            float w = size.x;
            if (!Slot(room, side, along, out var p, out var inward)) { Why("no wall"); return null; }
            if (!loose && !Slot(room, side, along - w * 0.5f + 0.05f, out _, out _)) { Why("gap start"); return null; }
            if (!loose && !Slot(room, side, along + w * 0.5f - 0.05f, out _, out _)) { Why("gap end"); return null; }
            var centre = p + inward * (size.z * 0.5f + 0.05f);
            return PlaceFurn(parent, model, centre, inward, room, raise);
        }

        /// <summary>Pieces placed one after another along a wall, each starting where the last ended.</summary>
        static List<GameObject> Row(Transform parent, string[] models, Bounds room, int side, float startAlong)
        {
            var result = new List<GameObject>();
            float cursor = startAlong;
            foreach (var m in models)
            {
                float w = FurnSize(m).x;
                result.Add(OnWall(parent, m, room, side, cursor + w * 0.5f));
                cursor += w + 0.01f;
            }
            return result;
        }

        static GameObject OnTop(Transform parent, string model, GameObject under, Bounds room)
        {
            if (under == null) return null;
            var b = FBounds(under);
            var face = under.transform.forward * FrontSign;
            return PlaceFurn(parent, model, new Vector3(b.center.x, room.max.y, b.center.z), face, room, b.max.y - room.max.y, true);
        }

        static GameObject Hang(Transform parent, string model, GameObject under, Bounds room, float height)
        {
            if (under == null) return null;
            var b = FBounds(under);
            return PlaceFurn(parent, model, new Vector3(b.center.x, room.max.y, b.center.z), under.transform.forward * FrontSign, room, height, true);
        }

        static Vector3 Inward(int side) => side == 0 ? Vector3.forward : side == 1 ? Vector3.back : side == 2 ? Vector3.right : Vector3.left;

        // ============================================================ rooms

        static bool quiet;
        static readonly Dictionary<string, int> reasons = new Dictionary<string, int>();
        static void Why(string r) { reasons.TryGetValue(r, out var n); reasons[r] = n + 1; }

        /// <summary>
        /// Tries a piece against a wall, starting at the middle and working outwards, and keeps the first
        /// spot that passes every test. Pieces that belong centred on a wall (a sofa, a bed) use this.
        /// </summary>
        static GameObject Centred(Transform p, string model, Bounds room, int[] sides, float centreFraction = 0.5f)
        {
            quiet = true;
            try
            {
                foreach (int side in sides)
                {
                    float len = WallLength(room, side), half = FurnSize(model).x * 0.5f;
                    float c = len * centreFraction;
                    for (float d = 0f; d < len * 0.5f; d += 0.15f)
                        foreach (float a in new[] { c + d, c - d })
                        {
                            if (a < half + 0.15f || a > len - half - 0.15f) continue;
                            var g = OnWall(p, model, room, side, a);
                            if (g != null) return g;
                        }
                }
            }
            finally { quiet = false; }
            log.AppendLine($"  furniture: could not place {model} [{string.Join(", ", reasons.Select(kv => kv.Key + " x" + kv.Value))}]"); reasons.Clear();
            return null;
        }


        /// <summary>Like Centred, but lets a piece stand across a window or a small gap; it still has to pass the fit tests.</summary>
        static GameObject LooseCentred(Transform p, string model, Bounds room, int[] sides, float fraction = 0.5f)
        {
            loose = true;
            try { return Centred(p, model, room, sides, fraction); }
            finally { loose = false; }
        }
        /// <summary>Places the next piece of a run, first fit from the cursor onward, trying each wall in turn.</summary>
        static GameObject Next(Transform p, string model, Bounds room, int[] sides, float[] cursors)
        {
            quiet = true;
            try
            {
                float w = FurnSize(model).x;
                for (int si = 0; si < sides.Length; si++)
                {
                    float len = WallLength(room, sides[si]);
                    for (float a = cursors[si] + w * 0.5f; a < len - w * 0.5f; a += 0.1f)
                    {
                        var g = OnWall(p, model, room, sides[si], a);
                        if (g == null) continue;
                        cursors[si] = a + w * 0.5f + 0.02f;
                        return g;
                    }
                }
            }
            finally { quiet = false; }
            log.AppendLine($"  furniture: could not place {model} [{string.Join(", ", reasons.Select(kv => kv.Key + " x" + kv.Value))}]"); reasons.Clear();
            return null;
        }

        static int[] SidesByWall(Bounds room, params int[] exclude)
        {
            return Enumerable.Range(0, 4).Where(s => !exclude.Contains(s))
                .OrderByDescending(s => SolidFraction(room, s) * WallLength(room, s)).ToArray();
        }

        static void FurnishHouse(Transform interior, IEnumerable<Renderer> floors)
        {
            furnPlaced = furnRejected = 0;
            foreach (var m in new[] { "toilet", "bathroomSink", "bathtub", "washer", "dryer", "loungeSofaLong", "bedDouble", "bedSingle", "kitchenCabinet", "tableCoffee", "cabinetTelevision", "trashcan", "bathroomCabinet", "desk" })
                log.AppendLine($"  size {m}: {FurnSize(m):F2}");
            var root = new GameObject("Furniture"); root.transform.SetParent(interior, false);
            foreach (var f in floors)
            {
                string key = f.name.ToLower().Replace(" floor", "").Trim();
                var room = f.bounds;
                var g = new GameObject(key); g.transform.SetParent(root.transform, false);
                switch (key)
                {
                    case "living room": Living(g.transform, room); break;
                    case "kitchen": Kitchen(g.transform, room); break;
                    case "bathroom": Bathroom(g.transform, room); break;
                    case "bedroom": Bedroom(g.transform, room, false); break;
                    case "master bedroom": Bedroom(g.transform, room, true); break;
                }
            }
            log.AppendLine($"furniture: {furnPlaced} placed, {furnRejected} rejected");
        }

        static int SideOf(GameObject g, Bounds room)
        {
            // Which wall a placed piece is against, from the way it faces.
            var f = g.transform.forward * FrontSign;
            return Mathf.Abs(f.z) > Mathf.Abs(f.x) ? (f.z > 0 ? 0 : 1) : (f.x > 0 ? 2 : 3);
        }

        static void Living(Transform p, Bounds room)
        {
            var sides = SidesByWall(room);
            var sofa = Centred(p, "loungeSofaLong", room, sides);
            if (sofa != null)
            {
                int side = SideOf(sofa, room);
                var sb = FBounds(sofa); var inward = Inward(side);
                float sw = side < 2 ? sb.size.x : sb.size.z, sd = side < 2 ? sb.size.z : sb.size.x;
                var tangent = Vector3.Cross(Vector3.up, inward);
                var left = PlaceFurn(p, "sideTable", new Vector3(sb.center.x, room.max.y, sb.center.z) - tangent * (sw * 0.5f + FurnSize("sideTable").x * 0.5f + 0.12f) - inward * (sd * 0.5f - 0.3f), inward, room);
                var right = PlaceFurn(p, "sideTable", new Vector3(sb.center.x, room.max.y, sb.center.z) + tangent * (sw * 0.5f + FurnSize("sideTable").x * 0.5f + 0.12f) - inward * (sd * 0.5f - 0.3f), inward, room);
                OnTop(p, "lampRoundTable", left, room);
                OnTop(p, "lampRoundTable", right, room);
                var rugCentre = new Vector3(sb.center.x, room.max.y, sb.center.z) + inward * (sd * 0.5f + 1.5f);
                PlaceFurn(p, "rugRectangle", rugCentre, inward, room, 0f, true);
                PlaceFurn(p, "tableCoffee", rugCentre, inward, room, 0.02f, true);
                PlaceFurn(p, "loungeChair", rugCentre + tangent * 2.3f, -tangent, room);
                // The television goes on the wall the sofa faces.
                var tv = Centred(p, "cabinetTelevision", room, new[] { side ^ 1 }) ?? Centred(p, "cabinetTelevision", room, SidesByWall(room, side)) ?? LooseCentred(p, "cabinetTelevision", room, new[] { side ^ 1 }) ?? LooseCentred(p, "cabinetTelevision", room, sides, 0.12f);
                OnTop(p, "televisionModern", tv, room);
            }
            else
            {
                var tv = Centred(p, "cabinetTelevision", room, sides);
                OnTop(p, "televisionModern", tv, room);
            }
            var cur = new float[4];
            Next(p, "pottedPlant", room, sides, cur);
            Centred(p, "bookcaseClosedWide", room, sides, 0.3f);
        }

        static void Kitchen(Transform p, Bounds room)
        {
            var sides = SidesByWall(room);
            var cur = new float[sides.Length];
            var row = new List<GameObject>();
            foreach (var m in new[] { "kitchenFridgeLarge", "kitchenCabinet", "kitchenStove", "kitchenCabinet", "kitchenSink", "kitchenCabinet", "kitchenCabinet" })
                row.Add(Next(p, m, room, sides, cur));
            for (int i = 0; i < row.Count; i++)
            {
                if (row[i] == null) continue;
                if (i == 2) Hang(p, "hoodLarge", row[i], room, 1.55f);
                else if (i >= 1) Hang(p, "kitchenCabinetUpper", row[i], room, 1.45f);
            }
            OnTop(p, "kitchenMicrowave", row.Count > 3 ? row[3] : null, room);
            OnTop(p, "kitchenCoffeeMachine", row.Count > 5 ? row[5] : null, room);

            // Dining table with four chairs, out in the middle of the room.
            var tc = new Vector3(room.center.x, room.max.y, room.center.z);
            var tbl = PlaceFurn(p, "table", tc, Vector3.forward, room);
            if (tbl != null)
            {
                var tb = FBounds(tbl);
                PlaceFurn(p, "chair", new Vector3(tb.min.x - 0.3f, room.max.y, tb.center.z), Vector3.right, room);
                PlaceFurn(p, "chair", new Vector3(tb.max.x + 0.3f, room.max.y, tb.center.z), Vector3.left, room);
                PlaceFurn(p, "chair", new Vector3(tb.center.x, room.max.y, tb.min.z - 0.3f), Vector3.forward, room);
                PlaceFurn(p, "chair", new Vector3(tb.center.x, room.max.y, tb.max.z + 0.3f), Vector3.back, room);
            }
        }

        static void Bathroom(Transform p, Bounds room)
        {
            var sides = SidesByWall(room);
            int main = sides[0];
            var cur = new float[4];
            var one = new[] { main };
            var toilet = Next(p, "toilet", room, one, new float[] { 0.4f }) ?? Centred(p, "toilet", room, SidesByWall(room), 0.15f);
            var sinkCur = new float[] { toilet != null ? 0.4f + FurnSize("toilet").x + 0.4f : 1.2f };
            var sink = Next(p, "bathroomSink", room, one, sinkCur);
            Hang(p, "bathroomMirror", sink, room, 1.25f);
            Centred(p, "bathtub", room, one, 0.85f);

            // The house has no laundry room, so the washer and dryer stand along the other long wall.
            int opp = main ^ 1;
            var wc = new float[] { 0.5f };
            Next(p, "washer", room, new[] { opp }, wc);
            Next(p, "dryer", room, new[] { opp }, wc);
            Next(p, "trashcan", room, new[] { opp }, wc);
            Next(p, "bathroomCabinet", room, new[] { opp }, wc);
        }

        static void Bedroom(Transform p, Bounds room, bool master)
        {
            var sides = SidesByWall(room);
            var bed = Centred(p, master ? "bedDouble" : "bedSingle", room, sides);
            int bedSide = -1;
            if (bed != null)
            {
                bedSide = SideOf(bed, room);
                var bb = FBounds(bed); var inward = Inward(bedSide);
                float bw = bedSide < 2 ? bb.size.x : bb.size.z, bd = bedSide < 2 ? bb.size.z : bb.size.x;
                var tangent = Vector3.Cross(Vector3.up, inward);
                var c = new Vector3(bb.center.x, room.max.y, bb.center.z);
                var left = PlaceFurn(p, "cabinetBedDrawer", c - tangent * (bw * 0.5f + FurnSize("cabinetBedDrawer").x * 0.5f + 0.1f) - inward * (bd * 0.5f - 0.25f), inward, room);
                var right = PlaceFurn(p, "cabinetBedDrawer", c + tangent * (bw * 0.5f + FurnSize("cabinetBedDrawer").x * 0.5f + 0.1f) - inward * (bd * 0.5f - 0.25f), inward, room);
                OnTop(p, "lampRoundTable", left, room);
                OnTop(p, "lampSquareTable", right, room);
                PlaceFurn(p, "rugRectangle", c + inward * (bd * 0.5f + 0.8f), inward, room, 0f, true);
            }
            var others = SidesByWall(room, bedSide >= 0 ? new[] { bedSide } : new int[0]);
            Centred(p, master ? "bookcaseClosedDoors" : "bookcaseClosedWide", room, others, 0.5f);
            var desk = Centred(p, "desk", room, others, 0.3f);
            if (desk != null)
            {
                int ds = SideOf(desk, room);
                var db = FBounds(desk); var inward = Inward(ds);
                float dd = ds < 2 ? db.size.z : db.size.x;
                PlaceFurn(p, "chairDesk", new Vector3(db.center.x, room.max.y, db.center.z) + inward * (dd * 0.5f + 0.35f), -inward, room);
                OnTop(p, "computerScreen", desk, room);
            }
            if (master) Next(p, "coatRackStanding", room, others, new float[others.Length]);
        }
    }
}
