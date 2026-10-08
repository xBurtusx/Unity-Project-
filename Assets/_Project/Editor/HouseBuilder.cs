using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using HorrorHouse;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Builds the main-floor architectural shell from Assets/_Project/Data/floorplan.json.
    ///
    /// Method: the plan is rasterised onto a fine grid of room-occupancy cells. A wall exists
    /// wherever two adjacent cells belong to different rooms (or to a room and the outside).
    /// Collinear runs are then merged into wall segments.
    ///
    /// This matters: it means walls CANNOT fail to meet, cannot float, and cannot leave gaps,
    /// because every wall is derived from an actual boundary between two occupied areas rather
    /// than placed by hand. Partial adjacency (e.g. the Great Room meeting both the Dining room
    /// and the Kitchen along one line) is handled automatically.
    ///
    /// Re-running is non-destructive to your work: everything lives under a single root object
    /// that is deleted and rebuilt, so edit the JSON and rebuild rather than moving walls by hand.
    /// </summary>
    public static class HouseBuilder
    {
        const string JsonPath = "Assets/_Project/Data/floorplan.json";
        const string MatDir   = "Assets/_Project/Materials";
        const string RootName = "HOUSE_SHELL (generated - do not edit by hand)";

        const float F    = 0.3048f;  // feet -> metres
        const float Cell = 0.25f;    // rasteriser cell size, in feet
        const int   EMPTY = -1;

        // Room pairs that are deliberately open to each other: no wall is built between them.
        static readonly HashSet<string> OpenPairs = new HashSet<string>
        {
            Pair("GreatRoom",   "Dining"),
            Pair("GreatRoom",   "Kitchen"),
            Pair("Dining",      "Kitchen"),
            Pair("GreatRoom",   "Hall"),
            Pair("GreatRoom",   "HallToEntry"),
            Pair("HallToEntry", "Kitchen"),
            Pair("HallToEntry", "Entry"),
            Pair("Entry",       "BasementStair"),
            Pair("Entry",       "MudHall"),
            Pair("Mudroom",     "Laundry"),
            Pair("Bath2",       "Bath2Vestibule"),
            Pair("Hall",        "Bath2Vestibule"),
        };

        static string Pair(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        // ---------------------------------------------------------------- menu

        [MenuItem("Horror House/Stage 1 - Build House Shell", priority = 0)]
        public static void Build()
        {
            var plan = LoadPlan();
            if (plan == null) return;

            var old = GameObject.Find(RootName);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build House Shell");

            var mats = Materials.Load();
            var grid = new OccupancyGrid(plan);

            var floors   = new GameObject("Floors").Tr(root);
            var ceilings = new GameObject("Ceilings").Tr(root);
            var walls    = new GameObject("Walls").Tr(root);
            var doors    = new GameObject("Doors").Tr(root);
            var windows  = new GameObject("Windows").Tr(root);
            var exterior = new GameObject("ExteriorStructures").Tr(root);

            BuildSlabs(plan, floors, ceilings, mats);

            var segments = grid.ExtractSegments(plan);
            var openings = ResolveOpenings(plan, segments);

            foreach (var seg in segments)
                BuildWall(seg, openings.Where(o => o.Segment == seg).ToList(), plan, walls, mats);

            foreach (var o in openings)
            {
                if (o.IsDoor) BuildDoor(o, plan, doors, mats);
                else          BuildWindow(o, plan, windows, mats);
            }

            BuildPorchColumns(plan, exterior, mats);
            BuildBasementStair(plan, exterior, mats);

            var roofs = new GameObject("Roofs").Tr(root);
            BuildRoofs(plan, roofs, mats);

            var site = new GameObject("Site").Tr(root);
            BuildSite(plan, site, mats);

            Selection.activeGameObject = root;
            Debug.Log($"[HouseBuilder] Built {plan.rooms.Length} rooms, {segments.Count} wall segments, " +
                      $"{openings.Count(o => o.IsDoor)} doors, {openings.Count(o => !o.IsDoor)} windows, " +
                      $"{plan.roofs?.Length ?? 0} roofs.\n" +
                      "Run 'Horror House/Stage 1 - Validate House' next.");
        }

        // ---------------------------------------------------------------- slabs

        static void BuildSlabs(Plan plan, GameObject floorParent, GameObject ceilParent, Materials mats)
        {
            float t = plan.construction.floorSlabThickness;

            foreach (var r in plan.rooms)
            {
                var size = new Vector3(r.w * F, t * F, r.d * F);
                var centre = new Vector3((r.x + r.w * 0.5f) * F, -t * F * 0.5f, (r.z + r.d * 0.5f) * F);

                var floor = MeshFactory.Box($"Floor_{r.name}", size, mats.FloorFor(r.type));
                floor.transform.SetParent(floorParent.transform, false);
                floor.transform.localPosition = centre;
                floor.AddComponent<BoxCollider>();

                if (r.exterior) continue;   // porches are open to the sky

                // The ceiling hangs DOWN from the stated height rather than sitting on top of
                // it. Build it upward instead and, near the eaves where the roof plane is only
                // a few inches above the wall plate, the slab pushes straight out through the
                // roof - which shows up as a pale stripe running parallel to the eave.
                const float ceilThickness = 0.15f;
                var ceilSize = new Vector3(r.w * F, ceilThickness * F, r.d * F);
                var ceil = MeshFactory.Box($"Ceiling_{r.name}", ceilSize, mats.Ceiling);
                ceil.transform.SetParent(ceilParent.transform, false);
                ceil.transform.localPosition =
                    new Vector3(centre.x, (r.Ceiling(plan) - ceilThickness * 0.5f) * F, centre.z);
                ceil.AddComponent<BoxCollider>();
            }
        }

        // ---------------------------------------------------------------- walls

        static void BuildWall(Segment seg, List<Opening> segOpenings, Plan plan,
                              GameObject parent, Materials mats)
        {
            float thick  = seg.IsExterior ? plan.construction.exteriorWallThickness
                                          : plan.construction.interiorWallThickness;
            float height = seg.Height(plan);

            var go = new GameObject($"Wall_{seg.DescribeName()}");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = Vector3.zero;

            var mat = seg.IsExterior ? mats.ExteriorWall : mats.InteriorWall;

            // Sort openings along the wall and emit the solid pieces between/around them.
            var ordered = segOpenings.OrderBy(o => o.Offset).ToList();
            float cursor = 0f;

            foreach (var o in ordered)
            {
                float a = o.Offset, b = o.Offset + o.Width;
                if (a > cursor + 0.01f) Pier(go, seg, cursor, a - cursor, 0f, height, thick, mat);

                if (o.SillHeight > 0.01f)                       // wall under a window
                    Pier(go, seg, a, o.Width, 0f, o.SillHeight, thick, mat);
                if (o.HeadHeight < height - 0.01f)              // header above the opening
                    Pier(go, seg, a, o.Width, o.HeadHeight, height - o.HeadHeight, thick, mat);

                cursor = b;
            }

            if (cursor < seg.Length - 0.01f)
                Pier(go, seg, cursor, seg.Length - cursor, 0f, height, thick, mat);

            if (go.transform.childCount == 0) UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>One solid rectangle of wall, with collision.</summary>
        static void Pier(GameObject parent, Segment seg, float along, float length,
                         float bottom, float height, float thick, Material mat)
        {
            if (length <= 0.01f || height <= 0.01f) return;

            var size = seg.Horizontal
                ? new Vector3(length * F, height * F, thick * F)
                : new Vector3(thick * F, height * F, length * F);

            var p = seg.PointAt(along + length * 0.5f);
            var piece = MeshFactory.Box("Piece", size, mat);
            piece.transform.SetParent(parent.transform, false);
            piece.transform.localPosition = new Vector3(p.x * F, (bottom + height * 0.5f) * F, p.y * F);
            piece.AddComponent<BoxCollider>();
            piece.isStatic = true;
        }

        // ---------------------------------------------------------------- openings

        static List<Opening> ResolveOpenings(Plan plan, List<Segment> segments)
        {
            var result = new List<Opening>();
            var c = plan.construction;

            foreach (var d in plan.doors)
            {
                var seg = FindSegment(segments, d.roomA, d.roomB, d.wall);
                if (seg == null)
                {
                    Debug.LogWarning($"[HouseBuilder] Door '{d.id}': no wall found between " +
                                     $"'{d.roomA}' and '{d.roomB}'{(string.IsNullOrEmpty(d.wall) ? "" : $" on the {d.wall} side")}. Skipped.");
                    continue;
                }
                float h = d.kind == "overhead" ? 7.5f : c.doorHeight;
                if (d.offset + d.width > seg.Length)
                {
                    Debug.LogWarning($"[HouseBuilder] Door '{d.id}' ({d.width:0.##} ft at offset {d.offset:0.##}) " +
                                     $"does not fit its {seg.Length:0.##} ft wall. Clamped.");
                }
                float off = Mathf.Clamp(d.offset, 0.25f, Mathf.Max(0.25f, seg.Length - d.width - 0.25f));
                result.Add(new Opening
                {
                    Id = d.id, Segment = seg, Offset = off, Width = d.width,
                    SillHeight = 0f, HeadHeight = h, IsDoor = true, Door = d
                });
            }

            foreach (var w in plan.windows)
            {
                var seg = FindSegment(segments, w.room, "EXTERIOR", w.wall);
                if (seg == null)
                {
                    Debug.LogWarning($"[HouseBuilder] Window '{w.id}': no exterior wall found on the " +
                                     $"{w.wall} side of '{w.room}'. Skipped.");
                    continue;
                }
                float sill = w.sill > 0.01f ? w.sill : c.windowSillHeight;
                float off = Mathf.Clamp(w.offset, 0.25f, Mathf.Max(0.25f, seg.Length - w.width - 0.25f));
                result.Add(new Opening
                {
                    Id = w.id, Segment = seg, Offset = off, Width = w.width,
                    SillHeight = sill, HeadHeight = c.windowHeadHeight, IsDoor = false, Window = w
                });
            }

            return result;
        }

        /// <summary>
        /// Finds the wall between two rooms. When several segments qualify (a room can have
        /// more than one exterior wall) the caller disambiguates with a compass side; failing
        /// that the longest segment wins.
        /// </summary>
        static Segment FindSegment(List<Segment> segments, string roomA, string roomB, string side)
        {
            var candidates = segments.Where(s => s.Matches(roomA, roomB));
            if (!string.IsNullOrEmpty(side))
                candidates = candidates.Where(s => s.SideOf(roomA) == side.ToUpperInvariant());
            return candidates.OrderByDescending(s => s.Length).FirstOrDefault();
        }

        // ---------------------------------------------------------------- doors

        static void BuildDoor(Opening o, Plan plan, GameObject parent, Materials mats)
        {
            var seg = o.Segment;
            float thick = seg.IsExterior ? plan.construction.exteriorWallThickness
                                         : plan.construction.interiorWallThickness;

            var frame = new GameObject($"DoorFrame_{o.Id}");
            frame.transform.SetParent(parent.transform, false);

            // Casing: a lining around the opening so the wall never shows a raw cut edge.
            BuildCasing(frame, seg, o, thick, 0f, o.HeadHeight, mats.Trim);

            bool doubleLeaf = o.Door.swing == "double";
            int leaves = doubleLeaf ? 2 : 1;
            float leafW = o.Width / leaves;

            for (int i = 0; i < leaves; i++)
            {
                // The hinge is at one END of the opening - never its centre. This is the
                // single most common mistake with game doors and it reads as instantly fake.
                bool hingeAtLow = doubleLeaf ? (i == 0) : !o.Door.swing.EndsWith("right");
                float hingeAlong = o.Offset + (doubleLeaf ? i * leafW : (hingeAtLow ? 0f : o.Width));
                if (doubleLeaf && i == 1) hingeAlong = o.Offset + o.Width;
                if (doubleLeaf) hingeAtLow = (i == 0);

                var hinge = seg.PointAt(hingeAlong);
                var pivot = new GameObject($"DoorPivot_{o.Id}" + (doubleLeaf ? $"_{i}" : ""));
                pivot.transform.SetParent(frame.transform, false);
                pivot.transform.localPosition = new Vector3(hinge.x * F, 0f, hinge.y * F);

                float dirSign = hingeAtLow ? 1f : -1f;
                var along = seg.Direction * dirSign;

                var leaf = MeshFactory.Box($"Door_{o.Id}" + (doubleLeaf ? $"_{i}" : ""),
                    seg.Horizontal ? new Vector3(leafW * F, o.HeadHeight * F, 0.145f * F)
                                   : new Vector3(0.145f * F, o.HeadHeight * F, leafW * F),
                    mats.DoorFor(o.Door.kind));
                leaf.transform.SetParent(pivot.transform, false);

                // Offset the leaf by half its own thickness towards the side it opens into, so
                // its rear face lies ON the hinge axis. Centre the leaf on the wall instead and
                // its back corner scrapes through the jamb for the whole of the swing - small,
                // but it is a solid object passing through another solid object.
                const float leafThickness = 0.145f;
                float openSign = hingeAtLow ? 1f : -1f;
                var outward = seg.Horizontal
                    ? new Vector3(0f, 0f, -openSign)     // +angle about Y swings +X towards -Z
                    : new Vector3(openSign, 0f, 0f);

                leaf.transform.localPosition =
                    new Vector3(along.x * leafW * 0.5f * F, o.HeadHeight * 0.5f * F, along.y * leafW * 0.5f * F)
                    + outward * (leafThickness * 0.5f * F);

                leaf.AddComponent<BoxCollider>();

                // Handle, on the free edge, at a believable height.
                if (o.Door.kind != "overhead" && o.Door.kind != "bifold")
                {
                    var handle = MeshFactory.Box("Handle", new Vector3(0.35f, 0.12f, 0.12f) * F, mats.Metal);
                    handle.transform.SetParent(leaf.transform, false);
                    handle.transform.localPosition = new Vector3(
                        seg.Horizontal ? along.x * (leafW * 0.5f - 0.25f) * F : 0.12f * F,
                        (3.0f - o.HeadHeight * 0.5f) * F,
                        seg.Horizontal ? 0.12f * F : along.y * (leafW * 0.5f - 0.25f) * F);
                }

                var meta = pivot.AddComponent<DoorMeta>();
                meta.doorId    = o.Id;
                meta.kind      = o.Door.kind;
                meta.swing     = o.Door.swing;
                meta.soundSet  = o.Door.sound;
                meta.roomA     = o.Door.roomA;
                meta.roomB     = o.Door.roomB;
                meta.openAngle = hingeAtLow ? 95f : -95f;

                var door = pivot.AddComponent<Door>();
                door.doorId = o.Id;
                door.soundSet = o.Door.sound;

                // Not every door swings. A barn door slides along the wall and a garage door
                // rolls up overhead, and making either of them pivot would look absurd.
                // A bifold folds flat against its own opening - it does not swing out into the
                // room. Swinging a 6 ft bifold 95 degrees sweeps it straight through the wall.
                door.motion = o.Door.kind == "overhead" ? Door.Motion.Roll
                            : o.Door.swing is "slide" or "bifold" ? Door.Motion.Slide
                            : Door.Motion.Swing;

                door.openAngle = hingeAtLow ? 95f : -95f;
                door.openDistance = o.Door.kind == "overhead"
                    ? o.HeadHeight * F * 0.82f      // rolls up, stopping under the lintel
                    : leafW * F * 0.95f;            // slides aside by its own width

                // Weight read through speed: a front door is heavier and slower than a closet.
                bool heavy = o.Door.kind is "entry" or "french" or "fire" or "overhead";
                door.openTime  = o.Door.kind == "overhead" ? 3.2f : (heavy ? 1.05f : 0.75f);
                door.closeTime = o.Door.kind == "overhead" ? 3.4f : (heavy ? 1.25f : 0.85f);

                // A glazed front door onto the porch is the player's way out - never start locked.
                door.locked = false;
            }
        }

        static void BuildCasing(GameObject parent, Segment seg, Opening o,
                                float thick, float bottom, float top, Material mat)
        {
            float jamb = 0.12f;
            void Slab(float along, float len, float y, float h, bool vertical)
            {
                var size = seg.Horizontal
                    ? new Vector3(len * F, h * F, (thick + 0.08f) * F)
                    : new Vector3((thick + 0.08f) * F, h * F, len * F);
                var p = seg.PointAt(along + len * 0.5f);
                var g = MeshFactory.Box("Casing", size, mat);
                g.transform.SetParent(parent.transform, false);
                g.transform.localPosition = new Vector3(p.x * F, (y + h * 0.5f) * F, p.y * F);
            }
            Slab(o.Offset, jamb, bottom, top - bottom, true);
            Slab(o.Offset + o.Width - jamb, jamb, bottom, top - bottom, true);
            Slab(o.Offset, o.Width, top - jamb, jamb, false);
        }

        // ---------------------------------------------------------------- windows

        static void BuildWindow(Opening o, Plan plan, GameObject parent, Materials mats)
        {
            var seg = o.Segment;
            float thick = plan.construction.exteriorWallThickness;
            float h = o.HeadHeight - o.SillHeight;

            var go = new GameObject($"Window_{o.Id}");
            go.transform.SetParent(parent.transform, false);

            BuildCasing(go, seg, o, thick, o.SillHeight, o.HeadHeight, mats.Trim);

            // Sill, projecting slightly into the room - reads as real joinery from inside.
            var sillSize = seg.Horizontal
                ? new Vector3((o.Width + 0.3f) * F, 0.12f * F, (thick + 0.35f) * F)
                : new Vector3((thick + 0.35f) * F, 0.12f * F, (o.Width + 0.3f) * F);
            var sp = seg.PointAt(o.Offset + o.Width * 0.5f);
            var sill = MeshFactory.Box("Sill", sillSize, mats.Trim);
            sill.transform.SetParent(go.transform, false);
            sill.transform.localPosition = new Vector3(sp.x * F, (o.SillHeight - 0.06f) * F, sp.y * F);
            sill.AddComponent<BoxCollider>();

            // Glass: real thickness, so it catches light and reflections correctly.
            var glassSize = seg.Horizontal
                ? new Vector3((o.Width - 0.2f) * F, (h - 0.2f) * F, 0.06f * F)
                : new Vector3(0.06f * F, (h - 0.2f) * F, (o.Width - 0.2f) * F);
            var glass = MeshFactory.Box("Glass", glassSize, mats.Glass);
            glass.transform.SetParent(go.transform, false);
            glass.transform.localPosition =
                new Vector3(sp.x * F, (o.SillHeight + h * 0.5f) * F, sp.y * F);
            glass.AddComponent<BoxCollider>();

            var meta = go.AddComponent<WindowMeta>();
            meta.windowId = o.Id;
            meta.room     = o.Window.room;
            meta.operable = o.Window.operable;
        }

        // ---------------------------------------------------------------- exterior bits

        static void BuildPorchColumns(Plan plan, GameObject parent, Materials mats)
        {
            var porch = plan.rooms.FirstOrDefault(r => r.name == "FrontPorch");
            if (porch == null) return;

            var group = new GameObject("FrontPorchColumns").Tr(parent);
            int count = 4;
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0.5f : i / (float)(count - 1);
                float x = Mathf.Lerp(porch.x + 0.75f, porch.x + porch.w - 0.75f, t);
                var col = MeshFactory.Box($"Column_{i}",
                    new Vector3(0.83f, porch.ceiling, 0.83f) * F, mats.Trim);
                col.transform.SetParent(group.transform, false);
                col.transform.localPosition =
                    new Vector3(x * F, porch.ceiling * 0.5f * F, (porch.z + 0.75f) * F);
                col.AddComponent<BoxCollider>();
            }

            // The porch is covered by a real gable in BuildRoofs, not a flat slab.
        }

        // ---------------------------------------------------------------- roofs

        /// <summary>
        /// Gable roofs built from the same dimension data as the walls, so the roof cannot
        /// drift out of register with the building underneath it.
        ///
        /// The roof plane passes through the top of the wall at 'base' and continues outward
        /// and DOWNWARD past it by the overhang - which is how a real eave works, and why the
        /// soffit sits below the wall plate rather than level with it.
        /// </summary>
        static void BuildRoofs(Plan plan, GameObject parent, Materials mats)
        {
            if (plan.roofs == null) return;

            const float deckThickness = 0.5f;   // rafters plus sheathing plus shingles, in feet

            foreach (var r in plan.roofs)
            {
                var go = new GameObject($"Roof_{r.id}").Tr(parent);

                float angle = Mathf.Atan(r.Slope) * Mathf.Rad2Deg;
                float slopeLen = r.EaveRun * Mathf.Sqrt(1f + r.Slope * r.Slope);
                float eaveDrop = r.EaveRun * r.Slope;          // drop from ridge to eave edge
                float midY = r.RidgeHeight - eaveDrop * 0.5f;  // halfway down the slope

                // Rafters sit ON the wall plate, so lift the deck until its underside - not its
                // centreline - lies on the roof plane. Measured perpendicular to the slope, then
                // converted back to vertical, which is why the pitch appears in the sum.
                midY += deckThickness * 0.5f * Mathf.Sqrt(1f + r.Slope * r.Slope) + 0.02f;
                float xMid = r.x + r.w * 0.5f, zMid = r.z + r.d * 0.5f;

                if (r.RidgeAlongX)
                {
                    // Ridge east-west: one slope faces north, the other south.
                    var size = new Vector3((r.w + 2f * r.overhang) * F, deckThickness * F, slopeLen * F);
                    MakeSlope(go, "Slope_N", size, mats.Shingle,
                        new Vector3(xMid, midY, zMid + r.EaveRun * 0.5f), new Vector3(angle, 0, 0));
                    MakeSlope(go, "Slope_S", size, mats.Shingle,
                        new Vector3(xMid, midY, zMid - r.EaveRun * 0.5f), new Vector3(-angle, 0, 0));
                }
                else
                {
                    // Ridge north-south: slopes face east and west.
                    var size = new Vector3(slopeLen * F, deckThickness * F, (r.d + 2f * r.overhang) * F);
                    MakeSlope(go, "Slope_E", size, mats.Shingle,
                        new Vector3(xMid + r.EaveRun * 0.5f, midY, zMid), new Vector3(0, 0, -angle));
                    MakeSlope(go, "Slope_W", size, mats.Shingle,
                        new Vector3(xMid - r.EaveRun * 0.5f, midY, zMid), new Vector3(0, 0, angle));
                }

                bool low  = r.gableEnds == "both" || r.gableEnds == "low";
                bool high = r.gableEnds == "both" || r.gableEnds == "high";
                if (low)  BuildGableEnd(go, r, false, plan, mats);
                if (high) BuildGableEnd(go, r, true,  plan, mats);
            }
        }

        static void MakeSlope(GameObject parent, string name, Vector3 size, Material mat,
                              Vector3 posFeet, Vector3 euler)
        {
            var g = MeshFactory.Box(name, size, mat);
            g.transform.SetParent(parent.transform, false);
            g.transform.localPosition = new Vector3(posFeet.x * F, posFeet.y * F, posFeet.z * F);
            g.transform.localRotation = Quaternion.Euler(euler);
            g.AddComponent<BoxCollider>();
            g.isStatic = true;
        }

        /// <summary>
        /// The triangular wall closing off one end of a gable. Spans the full width of the
        /// building at the wall line and comes to a point at the ridge.
        /// </summary>
        static void BuildGableEnd(GameObject parent, Roof r, bool highEnd, Plan plan, Materials mats)
        {
            float half = r.HalfSpan;
            float rise = half * r.Slope;
            float t = plan.construction.exteriorWallThickness;

            var mesh = MeshFactory.TriangularPrism(half * F, rise * F, t * F);
            var g = new GameObject($"GableEnd_{(highEnd ? "High" : "Low")}");
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            g.AddComponent<MeshRenderer>().sharedMaterial = mats.ExteriorWall;
            g.AddComponent<MeshCollider>().sharedMesh = mesh;
            g.transform.SetParent(parent.transform, false);
            g.isStatic = true;

            if (r.RidgeAlongX)
            {
                // Gable walls close the east and west ends; the triangle spans north-south.
                float xEnd = highEnd ? r.x + r.w : r.x;
                g.transform.localPosition = new Vector3(xEnd * F, r.@base * F, (r.z + r.d * 0.5f) * F);
                g.transform.localRotation = Quaternion.Euler(0, -90f, 0);
            }
            else
            {
                // Gable walls close the north and south ends; the triangle spans east-west.
                float zEnd = highEnd ? r.z + r.d : r.z;
                g.transform.localPosition = new Vector3((r.x + r.w * 0.5f) * F, r.@base * F, zEnd * F);
                g.transform.localRotation = Quaternion.identity;
            }
        }

        // ---------------------------------------------------------------- site

        static void BuildSite(Plan plan, GameObject parent, Materials mats)
        {
            var s = plan.site;
            if (s == null) return;

            // Grass sits just below the finished floor so paving can be laid on top of it
            // without the two surfaces fighting for the same pixels.
            var grass = MeshFactory.Box("Grass",
                new Vector3(s.lotW, 1.0f, s.lotD) * F, mats.Grass);
            grass.transform.SetParent(parent.transform, false);
            grass.transform.localPosition = new Vector3(
                (s.lotX + s.lotW * 0.5f) * F, -0.55f * F, (s.lotZ + s.lotD * 0.5f) * F);
            grass.AddComponent<BoxCollider>();
            grass.isStatic = true;

            if (s.paving != null)
            {
                var pav = new GameObject("Paving").Tr(parent);
                foreach (var p in s.paving)
                {
                    var mat = p.material == "asphalt" ? mats.Asphalt : mats.Paving;
                    var g = MeshFactory.Box(p.name, new Vector3(p.w, 0.5f, p.d) * F, mat);
                    g.transform.SetParent(pav.transform, false);
                    g.transform.localPosition = new Vector3(
                        (p.x + p.w * 0.5f) * F, -0.28f * F, (p.z + p.d * 0.5f) * F);
                    g.AddComponent<BoxCollider>();
                    g.isStatic = true;
                }
            }

            if (s.fence != null)
            {
                var fenceRoot = new GameObject("Fence").Tr(parent);
                foreach (var run in s.fence) BuildFenceRun(fenceRoot, run, s, mats);
            }
        }

        /// <summary>
        /// A board fence along a polyline: posts at a regular spacing plus a solid panel
        /// between each pair, standing slightly proud of the posts the way real boards do.
        /// </summary>
        static void BuildFenceRun(GameObject parent, FenceRun run, Site s, Materials mats)
        {
            if (run.points == null || run.points.Length < 2) return;

            float h = s.fenceHeight;
            float spacing = Mathf.Max(2f, s.postSpacing);

            for (int i = 0; i < run.points.Length - 1; i++)
            {
                var a = new Vector2(run.points[i].x, run.points[i].z);
                var b = new Vector2(run.points[i + 1].x, run.points[i + 1].z);
                float len = Vector2.Distance(a, b);
                if (len < 0.1f) continue;

                var dir = (b - a) / len;
                float yaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
                var mid = (a + b) * 0.5f;

                var panel = MeshFactory.Box($"Panel_{i}",
                    new Vector3(0.17f, h - 0.5f, len) * F, mats.FenceWood);
                panel.transform.SetParent(parent.transform, false);
                panel.transform.localPosition = new Vector3(mid.x * F, (h - 0.5f) * 0.5f * F, mid.y * F);
                panel.transform.localRotation = Quaternion.Euler(0, yaw, 0);
                panel.AddComponent<BoxCollider>();
                panel.isStatic = true;

                int posts = Mathf.Max(1, Mathf.RoundToInt(len / spacing));
                for (int p = 0; p <= posts; p++)
                {
                    // Skip the shared post where one leg of the polyline meets the next.
                    if (p == posts && i < run.points.Length - 2) continue;

                    var at = Vector2.Lerp(a, b, p / (float)posts);
                    var post = MeshFactory.Box($"Post_{i}_{p}",
                        new Vector3(0.33f, h, 0.33f) * F, mats.FenceWood);
                    post.transform.SetParent(parent.transform, false);
                    post.transform.localPosition = new Vector3(at.x * F, h * 0.5f * F, at.y * F);
                    post.transform.localRotation = Quaternion.Euler(0, yaw, 0);
                    post.AddComponent<BoxCollider>();
                    post.isStatic = true;
                }
            }
        }

        static void BuildBasementStair(Plan plan, GameObject parent, Materials mats)
        {
            var r = plan.rooms.FirstOrDefault(x => x.name == "BasementStair");
            if (r == null) return;

            // 14 risers at 7.5" descending east, matching the plan's DOWN arrow.
            const int risers = 14;
            float rise = 0.625f;   // feet
            float run  = r.w / risers;

            var group = new GameObject("BasementStair").Tr(parent);
            for (int i = 0; i < risers; i++)
            {
                float top = -rise * i;
                var step = MeshFactory.Box($"Step_{i:00}",
                    new Vector3(run, rise, r.d) * F, mats.FloorFor("stair"));
                step.transform.SetParent(group.transform, false);
                step.transform.localPosition = new Vector3(
                    (r.x + run * (i + 0.5f)) * F, (top - rise * 0.5f) * F, (r.z + r.d * 0.5f) * F);
                step.AddComponent<BoxCollider>();
            }

            // The stair opening means this room must NOT get a floor slab over it.
            var slab = GameObject.Find($"Floor_{r.name}");
            if (slab != null) UnityEngine.Object.DestroyImmediate(slab);
        }

        // ---------------------------------------------------------------- json

        static Plan LoadPlan()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(JsonPath);
            if (asset == null)
            {
                Debug.LogError($"[HouseBuilder] Could not find {JsonPath}.");
                return null;
            }
            try
            {
                var plan = JsonUtility.FromJson<Plan>(asset.text);
                if (plan?.rooms == null || plan.rooms.Length == 0)
                {
                    Debug.LogError("[HouseBuilder] floorplan.json parsed but contains no rooms.");
                    return null;
                }
                return plan;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HouseBuilder] floorplan.json is not valid JSON: {e.Message}");
                return null;
            }
        }

        // ================================================================ types

        [Serializable] public class Plan
        {
            public Construction construction;
            public Room[]   rooms;
            public DoorDef[] doors;
            public WindowDef[] windows;
            public Roof[]   roofs;
            public Site     site;
        }

        [Serializable] public class Roof
        {
            public string id, ridgeAxis, gableEnds, note;
            public float x, z, w, d, pitch, overhang, @base;

            /// <summary>Ridge runs east-west, so the slopes face north and south.</summary>
            public bool RidgeAlongX => ridgeAxis != "Z";
            /// <summary>Half the span the roof has to cross, measured from the ridge to the wall.</summary>
            public float HalfSpan => (RidgeAlongX ? d : w) * 0.5f;
            /// <summary>Rise per foot of run, e.g. 6:12 = 0.5.</summary>
            public float Slope => pitch / 12f;
            public float RidgeHeight => @base + HalfSpan * Slope;
            /// <summary>Horizontal distance from the ridge out to the edge of the overhang.</summary>
            public float EaveRun => HalfSpan + overhang;
        }

        [Serializable] public class Site
        {
            public float lotX, lotZ, lotW, lotD, fenceHeight, postSpacing;
            public FenceRun[] fence;
            public Paving[] paving;
        }

        [Serializable] public class FenceRun { public FencePoint[] points; }
        [Serializable] public class FencePoint { public float x, z; }
        [Serializable] public class Paving { public string name, material, note; public float x, z, w, d; }

        [Serializable] public class Construction
        {
            public float exteriorWallThickness = 0.54f;
            public float interiorWallThickness = 0.375f;
            public float floorSlabThickness    = 0.75f;
            public float defaultCeilingHeight  = 10f;
            public float doorHeight            = 6.75f;
            public float windowSillHeight      = 2.5f;
            public float windowHeadHeight      = 7.5f;
        }

        [Serializable] public class Room
        {
            public string name, type, note, printed;
            public float x, z, w, d, ceiling;
            public bool exterior, assumed;
            public float Ceiling(Plan p) => ceiling > 0.01f ? ceiling : p.construction.defaultCeilingHeight;
        }

        [Serializable] public class DoorDef
        {
            public string id, roomA, roomB, kind, swing, sound, wall, note;
            public float offset, width;
        }

        [Serializable] public class WindowDef
        {
            public string id, room, wall, note;
            public float offset, width, sill;
            public bool operable;
        }

        public class Opening
        {
            public string Id;
            public Segment Segment;
            public float Offset, Width, SillHeight, HeadHeight;
            public bool IsDoor;
            public DoorDef Door;
            public WindowDef Window;
        }

        /// <summary>A run of wall between two areas. Horizontal = runs east-west.</summary>
        public class Segment
        {
            public bool Horizontal;
            public float Fixed;        // the z of an E-W wall, or the x of a N-S wall
            public float Start, End;   // along the wall's own axis
            public string RoomLow, RoomHigh;   // low/high side across the wall
            public float CeilingLow, CeilingHigh;

            public float Length => End - Start;
            public bool IsExterior => RoomLow == null || RoomHigh == null;
            public float Height(Plan p) =>
                Mathf.Max(CeilingLow, CeilingHigh) > 0.01f
                    ? Mathf.Max(CeilingLow, CeilingHigh)
                    : p.construction.defaultCeilingHeight;

            public Vector2 Direction => Horizontal ? Vector2.right : Vector2.up;

            /// <summary>Plan-space point at a distance along the wall from its start.</summary>
            public Vector2 PointAt(float along) =>
                Horizontal ? new Vector2(Start + along, Fixed) : new Vector2(Fixed, Start + along);

            public bool Matches(string a, string b)
            {
                string lo = RoomLow ?? "EXTERIOR", hi = RoomHigh ?? "EXTERIOR";
                return (lo == a && hi == b) || (lo == b && hi == a);
            }

            /// <summary>Which face of the given room this wall is: N, S, E or W.</summary>
            public string SideOf(string room)
            {
                bool roomIsLow = (RoomLow ?? "EXTERIOR") == room;
                if (Horizontal) return roomIsLow ? "N" : "S";
                return roomIsLow ? "E" : "W";
            }

            public string DescribeName() =>
                $"{(RoomLow ?? "OUT")}_{(RoomHigh ?? "OUT")}_{(Horizontal ? "EW" : "NS")}_{Fixed:0.##}_{Start:0.##}";
        }

        // ================================================================ rasteriser

        class OccupancyGrid
        {
            readonly int[,] cells;
            readonly int nx, nz;
            readonly float x0, z0;
            readonly List<Room> indexed = new List<Room>();

            public OccupancyGrid(Plan plan)
            {
                var solid = plan.rooms.Where(r => !r.exterior).ToList();
                indexed.AddRange(solid);

                x0 = solid.Min(r => r.x) - 2f;
                z0 = solid.Min(r => r.z) - 2f;
                float x1 = solid.Max(r => r.x + r.w) + 2f;
                float z1 = solid.Max(r => r.z + r.d) + 2f;

                nx = Mathf.CeilToInt((x1 - x0) / Cell);
                nz = Mathf.CeilToInt((z1 - z0) / Cell);
                cells = new int[nx, nz];
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < nz; j++)
                        cells[i, j] = EMPTY;

                for (int k = 0; k < solid.Count; k++)
                {
                    var r = solid[k];
                    int i0 = Mathf.RoundToInt((r.x - x0) / Cell);
                    int j0 = Mathf.RoundToInt((r.z - z0) / Cell);
                    int i1 = Mathf.RoundToInt((r.x + r.w - x0) / Cell);
                    int j1 = Mathf.RoundToInt((r.z + r.d - z0) / Cell);

                    for (int i = i0; i < i1; i++)
                        for (int j = j0; j < j1; j++)
                        {
                            if (i < 0 || j < 0 || i >= nx || j >= nz) continue;
                            if (cells[i, j] != EMPTY && cells[i, j] != k)
                                Debug.LogWarning($"[HouseBuilder] Rooms '{indexed[cells[i, j]].name}' and " +
                                                 $"'{r.name}' overlap. Later room wins - fix floorplan.json.");
                            cells[i, j] = k;
                        }
                }
            }

            int At(int i, int j) => (i < 0 || j < 0 || i >= nx || j >= nz) ? EMPTY : cells[i, j];
            string Name(int idx) => idx == EMPTY ? null : indexed[idx].name;
            float Ceil(int idx, Plan p) => idx == EMPTY ? 0f : indexed[idx].Ceiling(p);

            /// <summary>
            /// Walks every cell boundary and merges runs where the two sides differ.
            /// </summary>
            public List<Segment> ExtractSegments(Plan plan)
            {
                var segs = new List<Segment>();

                // North-south walls: boundaries at constant x.
                for (int i = 0; i <= nx; i++)
                {
                    int runLow = EMPTY, runHigh = EMPTY, runStart = -1;
                    for (int j = 0; j <= nz; j++)
                    {
                        int lo = j < nz ? At(i - 1, j) : EMPTY;
                        int hi = j < nz ? At(i, j)     : EMPTY;
                        bool wall = j < nz && lo != hi && !IsOpen(lo, hi);

                        if (wall && lo == runLow && hi == runHigh && runStart >= 0) continue;

                        if (runStart >= 0)
                            segs.Add(Make(false, x0 + i * Cell, z0 + runStart * Cell, z0 + j * Cell,
                                          runLow, runHigh, plan));

                        runStart = wall ? j : -1;
                        runLow = wall ? lo : EMPTY;
                        runHigh = wall ? hi : EMPTY;
                        if (!wall) { runLow = runHigh = EMPTY; }
                    }
                }

                // East-west walls: boundaries at constant z.
                for (int j = 0; j <= nz; j++)
                {
                    int runLow = EMPTY, runHigh = EMPTY, runStart = -1;
                    for (int i = 0; i <= nx; i++)
                    {
                        int lo = i < nx ? At(i, j - 1) : EMPTY;
                        int hi = i < nx ? At(i, j)     : EMPTY;
                        bool wall = i < nx && lo != hi && !IsOpen(lo, hi);

                        if (wall && lo == runLow && hi == runHigh && runStart >= 0) continue;

                        if (runStart >= 0)
                            segs.Add(Make(true, z0 + j * Cell, x0 + runStart * Cell, x0 + i * Cell,
                                          runLow, runHigh, plan));

                        runStart = wall ? i : -1;
                        runLow = wall ? lo : EMPTY;
                        runHigh = wall ? hi : EMPTY;
                        if (!wall) { runLow = runHigh = EMPTY; }
                    }
                }

                return segs.Where(s => s.Length > 0.3f).ToList();
            }

            bool IsOpen(int a, int b)
            {
                if (a == EMPTY || b == EMPTY) return false;
                return OpenPairs.Contains(Pair(indexed[a].name, indexed[b].name));
            }

            Segment Make(bool horizontal, float fixedCoord, float start, float end,
                         int lo, int hi, Plan plan) => new Segment
            {
                Horizontal = horizontal,
                Fixed = fixedCoord,
                Start = start,
                End = end,
                RoomLow = Name(lo),
                RoomHigh = Name(hi),
                CeilingLow = Ceil(lo, plan),
                CeilingHigh = Ceil(hi, plan),
            };
        }

        // ================================================================ materials

        class Materials
        {
            public Material InteriorWall, ExteriorWall, Ceiling, Trim, Glass, Metal;
            public Material Shingle, Grass, Asphalt, Paving, FenceWood;
            Material floorWood, floorTile, floorConcrete, floorCarpet, doorPainted, doorGlazed;

            public Material FloorFor(string type) => type switch
            {
                "bathroom" or "laundry" => floorTile,
                "garage" or "porch"     => floorConcrete,
                "bedroom" or "closet"   => floorCarpet,
                _                       => floorWood,
            };

            public Material DoorFor(string kind) => kind switch
            {
                "french" or "glazed" => doorGlazed,
                _                    => doorPainted,
            };

            public static Materials Load()
            {
                System.IO.Directory.CreateDirectory(MatDir);
                var m = new Materials
                {
                    InteriorWall  = Get("M_Drywall",        new Color(0.86f, 0.85f, 0.82f), 0.92f),
                    ExteriorWall  = Get("M_Siding",         new Color(0.62f, 0.62f, 0.60f), 0.85f),
                    Ceiling       = Get("M_Ceiling",        new Color(0.92f, 0.92f, 0.90f), 0.95f),
                    Trim          = Get("M_Trim",           new Color(0.94f, 0.94f, 0.93f), 0.55f),
                    Metal         = Get("M_Metal",          new Color(0.60f, 0.60f, 0.62f), 0.25f, 1f),
                    Glass         = Get("M_Glass",          new Color(0.80f, 0.86f, 0.88f, 0.18f), 0.05f, 0f, true),
                    Shingle       = Get("M_Roof_Shingle",   new Color(0.26f, 0.25f, 0.24f), 0.88f),
                    Grass         = Get("M_Grass",          new Color(0.28f, 0.36f, 0.19f), 0.95f),
                    Asphalt       = Get("M_Asphalt",        new Color(0.22f, 0.22f, 0.23f), 0.88f),
                    Paving        = Get("M_Paving",         new Color(0.55f, 0.54f, 0.52f), 0.82f),
                    FenceWood     = Get("M_Fence_Wood",     new Color(0.46f, 0.37f, 0.27f), 0.90f),
                    floorWood     = Get("M_Floor_Wood",     new Color(0.42f, 0.30f, 0.20f), 0.45f),
                    floorTile     = Get("M_Floor_Tile",     new Color(0.72f, 0.70f, 0.68f), 0.30f),
                    floorConcrete = Get("M_Floor_Concrete", new Color(0.48f, 0.48f, 0.47f), 0.80f),
                    floorCarpet   = Get("M_Floor_Carpet",   new Color(0.40f, 0.37f, 0.34f), 0.98f),
                    doorPainted   = Get("M_Door_Painted",   new Color(0.90f, 0.89f, 0.87f), 0.50f),
                    doorGlazed    = Get("M_Door_Glazed",    new Color(0.85f, 0.88f, 0.88f, 0.45f), 0.20f, 0f, true),
                };
                AssetDatabase.SaveAssets();
                return m;
            }

            static Material Get(string name, Color col, float smoothInv,
                                float metallic = 0f, bool transparent = false)
            {
                string path = $"{MatDir}/{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null) return mat;

                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                mat.SetColor("_BaseColor", col);
                mat.SetFloat("_Smoothness", 1f - smoothInv);
                mat.SetFloat("_Metallic", metallic);

                if (transparent)
                {
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 0f);
                    mat.SetFloat("_ZWrite", 0f);
                    mat.renderQueue = 3000;
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                }
                AssetDatabase.CreateAsset(mat, path);
                return mat;
            }
        }

        // ================================================================ meshes

        static class MeshFactory
        {
            static readonly Dictionary<Vector3, Mesh> cache = new Dictionary<Vector3, Mesh>();

            public static GameObject Box(string name, Vector3 size, Material mat)
            {
                var go = new GameObject(name);
                go.AddComponent<MeshFilter>().sharedMesh = BoxMesh(size);
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                return go;
            }

            /// <summary>
            /// A solid triangular prism: an isosceles triangle of half-width <paramref name="half"/>
            /// and height <paramref name="rise"/> standing on its base, extruded by
            /// <paramref name="thickness"/>. Used for the triangular wall at a gable end.
            /// Local space: X across the base, Y up from the base, Z through the thickness.
            /// </summary>
            public static Mesh TriangularPrism(float half, float rise, float thickness)
            {
                float t = thickness * 0.5f;
                var a = new Vector3(-half, 0, 0);
                var b = new Vector3( half, 0, 0);
                var c = new Vector3(    0, rise, 0);

                var verts = new List<Vector3>();
                var tris = new List<int>();

                void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
                {
                    int i = verts.Count;
                    verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                    tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
                }

                Vector3 fz = new Vector3(0, 0, -t), bz = new Vector3(0, 0, t);

                // Front and back faces, wound so each is visible from its own side.
                int k = verts.Count;
                verts.Add(a + fz); verts.Add(b + fz); verts.Add(c + fz);
                tris.AddRange(new[] { k, k + 2, k + 1 });
                k = verts.Count;
                verts.Add(a + bz); verts.Add(b + bz); verts.Add(c + bz);
                tris.AddRange(new[] { k, k + 1, k + 2 });

                Quad(a + fz, b + fz, b + bz, a + bz);   // base
                Quad(b + fz, c + fz, c + bz, b + bz);   // right rake
                Quad(c + fz, a + fz, a + bz, c + bz);   // left rake

                var mesh = new Mesh { name = $"Gable_{half:0.##}x{rise:0.##}" };
                mesh.SetVertices(verts);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }

            /// <summary>
            /// A unit cube scaled into the mesh itself rather than via transform.localScale,
            /// so lightmap UVs and physics stay sane and nothing inherits a skewed scale.
            /// UVs are in world units so textures tile at a consistent real-world size.
            /// </summary>
            static Mesh BoxMesh(Vector3 size)
            {
                var key = new Vector3(Mathf.Round(size.x * 1000f), Mathf.Round(size.y * 1000f),
                                      Mathf.Round(size.z * 1000f));
                if (cache.TryGetValue(key, out var cached) && cached != null) return cached;

                Vector3 h = size * 0.5f;
                var verts = new List<Vector3>();
                var norms = new List<Vector3>();
                var uvs   = new List<Vector2>();
                var tris  = new List<int>();

                void Face(Vector3 n, Vector3 u, Vector3 v, float uLen, float vLen)
                {
                    int b = verts.Count;
                    Vector3 c = Vector3.Scale(n, h);
                    verts.Add(c - Vector3.Scale(u, h) - Vector3.Scale(v, h));
                    verts.Add(c + Vector3.Scale(u, h) - Vector3.Scale(v, h));
                    verts.Add(c + Vector3.Scale(u, h) + Vector3.Scale(v, h));
                    verts.Add(c - Vector3.Scale(u, h) + Vector3.Scale(v, h));
                    for (int i = 0; i < 4; i++) norms.Add(n);
                    uvs.Add(new Vector2(0, 0));       uvs.Add(new Vector2(uLen, 0));
                    uvs.Add(new Vector2(uLen, vLen)); uvs.Add(new Vector2(0, vLen));
                    tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                }

                Face(Vector3.forward, Vector3.right,   Vector3.up,      size.x, size.y);
                Face(Vector3.back,    Vector3.left,    Vector3.up,      size.x, size.y);
                Face(Vector3.right,   Vector3.back,    Vector3.up,      size.z, size.y);
                Face(Vector3.left,    Vector3.forward, Vector3.up,      size.z, size.y);
                Face(Vector3.up,      Vector3.right,   Vector3.forward, size.x, size.z);
                Face(Vector3.down,    Vector3.right,   Vector3.back,    size.x, size.z);

                var mesh = new Mesh { name = $"Box_{size.x:0.##}x{size.y:0.##}x{size.z:0.##}" };
                mesh.SetVertices(verts);
                mesh.SetNormals(norms);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();

                cache[key] = mesh;
                return mesh;
            }
        }

        static GameObject Tr(this GameObject go, GameObject parent)
        {
            go.transform.SetParent(parent.transform, false);
            return go;
        }
    }
}
