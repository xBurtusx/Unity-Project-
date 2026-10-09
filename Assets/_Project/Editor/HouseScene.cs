using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Builds the whole game scene from the free "1 Story Home" pack: one house with working doors,
    /// lights and switches (the player's), copied along a street with fences, hedges and lamps.
    ///
    /// The pack ships a house shell with holes cut for windows and doors but no frames or glass, so
    /// the openings are FOUND by ray-casting the shell (a hole is a place a ray passes straight
    /// through the siding), and a door or window is fitted into each one. Nothing is hand-placed,
    /// which is why it survives a change of scale or a different house model.
    /// </summary>
    public static partial class HouseScene
    {
        const string Dir = "Assets/NeighbourhoodHouse";
        const string MatDir = Dir + "/Materials";
        const string ScenePath = "Assets/Scenes/Neighbourhood.unity";
        const bool FlipHouses = true;           // user request: every house turned 180 degrees from the first layout
        const float HouseScale = 0.78f;         // pack doors are 2.6 m tall; this brings them to ~2 m

        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
        static StringBuilder log;
        static int doorStyle;
        static readonly List<Vector3> roomShots = new List<Vector3>();

        struct Opening
        {
            public Vector3 outerCentre;   // centre of the hole, on the outer face of the wall
            public Vector3 normal;        // points out of the house
            public float width, height, bottom;
            public bool isDoor;
        }

        // ============================================================ entry point

        [MenuItem("Horror House/Build New Neighbourhood Scene", priority = 5)]
        public static void Build()
        {
            // A new scene cannot be created while the game is running, so stop it first and carry on
            // once the Editor is back in edit mode.
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.playModeStateChanged += WhenStopped;
                EditorApplication.isPlaying = false;
                return;
            }
            log = new StringBuilder();
            try { BuildInternal(); }
            catch (System.Exception e) { log.AppendLine("FAILED: " + e); Debug.LogError("[HouseScene] " + e); }
            File.WriteAllText("Temp/housescene_report.txt", log.ToString());
        }

        static void WhenStopped(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= WhenStopped;
            EditorApplication.delayCall += Build;
        }

        static void BuildInternal()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            mats.Clear(); roomShots.Clear(); keepClear.Clear();

            log.AppendLine("lightmap UVs reimported: " + HouseLighting.EnsureLightmapUVs("Assets/NeighbourhoodHouse/Meshes/House_Small.fbx"));
            log.AppendLine("SSAO: " + HouseLighting.EnsureAmbientOcclusion());
            ConfigureTextures();
            MakeMaterials();

            // Template shell: built once at the origin, measured, then copied for every neighbour.
            var template = BuildShell("Template", 0, interactive: false, lit: false);
            var shellBounds = Bounds(template);
            float floorTop = FloorTop(template);
            float minZ = shellBounds.min.z;

            var steps = template.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Contains("front steps"));
            float walkX = steps != null ? steps.bounds.center.x : shellBounds.center.x;
            float stepsBottom = steps != null ? steps.bounds.min.y : shellBounds.min.y;
            Object.DestroyImmediate(template);

            log.AppendLine($"shell bounds {shellBounds.min} .. {shellBounds.max}  floorTop={floorTop:F2}  walkX={walkX:F2}");

            const float lotW = 24f, houseFrontZ = 9.0f, roadHalf = 3.75f, walk = 1.5f;
            float houseDepth = shellBounds.size.z;
            float rearZ = houseFrontZ + houseDepth + 5f;
            float yOff = 0.02f - stepsBottom;            // bottom of the stairs rests on the ground; the pack intends the porch ~1 m up

            var root = new GameObject("World");
            BuildGround(root.transform, roadHalf, walk);

            // ---- houses, both sides of the street
            var houses = new GameObject("Houses"); houses.transform.SetParent(root.transform, false);
            var exclusions = new List<Vector4>();
            GameObject playerHouse = null;
            for (int side = 0; side < 2; side++)
            for (int i = -2; i <= 2; i++)
            {
                bool north = side == 0;
                bool isPlayer = north && i == 0;
                float cx = i * lotW;
                string name = isPlayer ? "PlayerHouse" : $"House_{(north ? "N" : "S")}{i + 2}";

                var h = BuildShell(name, (i + 2 + side * 2) % 5, interactive: isPlayer, lit: isPlayer, neighbourWindows: !isPlayer);
                h.transform.SetParent(houses.transform, true);
                // Shell is built facing -Z at the origin. North houses face the road already; south
                // houses are turned half a circle. Either way, centre the porch steps on the lot
                // and put their front edge on the plot line.
                h.transform.rotation = Quaternion.Euler(0f, (north ? 0f : 180f) + (FlipHouses ? 180f : 0f), 0f);
                var hb = Bounds(h);
                var st = h.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Contains("front steps"));
                float stepsX = st != null ? st.bounds.center.x : hb.center.x;
                float dz = north ? houseFrontZ - hb.min.z : -houseFrontZ - hb.max.z;
                h.transform.position += new Vector3(cx - stepsX, yOff, dz);
                // Grass keeps out of the house's true footprint (foundation, walls, porch, steps), not its
                // roof: the overhang is a metre or more wider, and using it left a bare band round the walls.
                var fp = new Bounds(h.transform.position, Vector3.zero); bool fpInit = false;
                Bounds stepsFp = default; bool stepsInit = false;
                foreach (var rr in h.GetComponentsInChildren<Renderer>())
                {
                    string rn = rr.name.ToLower().Trim();
                    if (rn.Contains("steps")) { if (!stepsInit) { stepsFp = rr.bounds; stepsInit = true; } else stepsFp.Encapsulate(rr.bounds); continue; }
                    bool part = rn.StartsWith("foundation") || rn == "porch" || rn == "walls";
                    if (!part) continue;
                    if (!fpInit) { fp = rr.bounds; fpInit = true; } else fp.Encapsulate(rr.bounds);
                }
                if (!fpInit) fp = Bounds(h);
                exclusions.Add(new Vector4(fp.min.x - 0.12f, fp.min.z - 0.12f, fp.max.x + 0.12f, fp.max.z + 0.12f));
                if (stepsInit) exclusions.Add(new Vector4(stepsFp.min.x - 0.1f, stepsFp.min.z - 0.1f, stepsFp.max.x + 0.1f, stepsFp.max.z + 0.1f));
                float backZ = north ? fp.max.z : -fp.min.z;
                int lotIdx = side * 5 + i + 2;
                if (isPlayer) playerHouse = h;

                var st3 = h.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Contains("front steps"));
                float treadFront = st3 != null ? (north ? st3.bounds.min.z : -st3.bounds.max.z) : houseFrontZ;
                BuildLot(root.transform, cx, north, lotW, treadFront + 0.15f, rearZ, backZ, lotIdx, roadHalf + walk);
            }

            BuildStreetLamps(root.transform, roadHalf, walk);

            // ---- lawn
            AddLawn(root.transform, true, exclusions, roadHalf + walk, rearZ, lotW);
            AddLawn(root.transform, false, exclusions, roadHalf + walk, rearZ, lotW);

            // ---- player, sky, night grade
            var spawn = new Vector3(0f, 0.2f, roadHalf + walk * 0.5f);
            PlayerSetup.CreateAt(spawn, 0f);
            AtmosphereSetup.Setup();

            Snapshot("Temp/scene_street.png", new Vector3(-3f, 1.7f, -2f), Quaternion.Euler(8, 18, 0), 70f);
            Snapshot("Temp/scene_front.png", spawn + Vector3.up * 1.6f, Quaternion.Euler(0, 0, 0), 70f);
            Snapshot("Temp/scene_aerial.png", new Vector3(-30f, 38f, -34f), Quaternion.Euler(38, 38, 0), 60f);

            if (playerHouse != null)
            {
                // Porch steps from three angles, to check how they meet the porch, the walkway and the ground.
                var stepsR = playerHouse.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Contains("front steps"));
                var hb2 = Bounds(playerHouse);
                if (stepsR != null)
                {
                    var c = stepsR.bounds.center;
                    var outward = new Vector3(c.x - hb2.center.x, 0, c.z - hb2.center.z).normalized;
                    var side = Vector3.Cross(Vector3.up, outward);
                    log.AppendLine($"steps bounds {stepsR.bounds.min}..{stepsR.bounds.max}; outward {outward}");
                    SnapshotLookAt("Temp/scene_steps_a.png", c + outward * 7f + Vector3.up * 1.7f, c, 60f);
                    SnapshotLookAt("Temp/scene_steps_b.png", c + side * 6f + outward * 2f + Vector3.up * 1.2f, c, 60f);
                    SnapshotLookAt("Temp/scene_steps_c.png", c + outward * 4f + Vector3.up * 4.5f, c, 65f);
                }
                SnapshotLookAt("Temp/scene_house_front.png", new Vector3(hb2.center.x, 1.7f, hb2.min.z - 14f), hb2.center, 60f);
                SnapshotLookAt("Temp/scene_house_back.png", new Vector3(hb2.center.x, 1.7f, hb2.max.z + 14f), hb2.center, 60f);
                var fd = playerHouse.GetComponentsInChildren<Door>().FirstOrDefault(d => d.doorId == "D_Front");
                if (fd != null)
                {
                    var db = Bounds(fd.transform.parent.gameObject);
                    var outward = new Vector3(db.center.x - hb2.center.x, 0, db.center.z - hb2.center.z).normalized;
                    var tgt = db.center;
                    SnapshotLookAt("Temp/scene_door_a.png", tgt + outward * 2.4f, tgt, 55f);
                    SnapshotLookAt("Temp/scene_door_b.png", tgt + outward * 1.6f + Vector3.Cross(Vector3.up, outward) * 1.1f - Vector3.up * 0.3f, tgt - Vector3.up * 0.2f, 55f);
                    log.AppendLine($"front door bounds {db.min}..{db.max} height {db.size.y:F2}");
                    // Swing the front door open and photograph the arc, to check the leaf clears its frame.
                    fd.SetOpenImmediate(true);
                    Physics.SyncTransforms();
                    SnapshotLookAt("Temp/scene_dooropen_top.png", db.center + Vector3.up * 3.4f - outward * 0.6f, db.center - outward * 0.5f, 50f);
                    SnapshotLookAt("Temp/scene_dooropen_inside.png", db.center - outward * 2.2f + Vector3.up * 0.2f, db.center, 60f);
                    fd.SetOpenImmediate(false);
                }
            }

            if (playerHouse != null)
            {
                // Interior check: switch every lamp on for a moment and photograph each room.
                var lamps = playerHouse.GetComponentsInChildren<Light>(true);
                var was = lamps.Select(l => l.enabled).ToArray();
                foreach (var l in lamps) l.enabled = true;
                for (int k = 0; k < roomShots.Count; k++)
                {
                    var p = playerHouse.transform.TransformPoint(roomShots[k]);
                    float yaw0 = playerHouse.transform.eulerAngles.y;
                    foreach (int ya in new[] { 45, 135, 225, 315 })
                        Snapshot($"Temp/scene_room{k}_{ya}.png", p, Quaternion.Euler(14, ya + yaw0, 0), 85f);
                }
                for (int k = 0; k < lamps.Length; k++) lamps[k].enabled = was[k];
            }


            if (playerHouse != null)
            {
                // Walk test: drop a ray at the porch, the front door and each room, and log the height of
                // whatever it lands on. If the porch were not solid these would read at ground level.
                Physics.SyncTransforms();
                void Probe(string label, Vector3 p)
                {
                    bool hit = Physics.Raycast(new Vector3(p.x, p.y + 3f, p.z), Vector3.down, out var h, 10f);
                    log.AppendLine($"  floor probe {label}: {(hit ? $"y={h.point.y:F2} on {h.collider.name}" : "NOTHING")}");
                }
                var porchR = playerHouse.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Trim() == "porch");
                if (porchR != null) Probe("porch centre", porchR.bounds.center);
                if (porchR != null) Probe("porch near steps", new Vector3(porchR.bounds.center.x, porchR.bounds.max.y, porchR.bounds.min.z + 0.4f));
                for (int k = 0; k < roomShots.Count; k++) Probe("room " + k, playerHouse.transform.TransformPoint(roomShots[k]) - Vector3.up * 1.5f);
                // Stair walk test: heights along the steps from the bottom to the porch edge.
                var stR = playerHouse.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Contains("front steps"));
                if (stR != null)
                {
                    var sbn = stR.bounds;
                    var line = new StringBuilder("  stair heights (bottom -> porch): ");
                    bool roadIsMinZ = true;
                    for (int i = 0; i <= 10; i++)
                    {
                        float z = roadIsMinZ ? Mathf.Lerp(sbn.min.z + 0.05f, sbn.max.z - 0.05f, i / 10f) : sbn.center.z;
                        bool hit = Physics.Raycast(new Vector3(sbn.center.x, sbn.max.y + 3f, z), Vector3.down, out var hh, 12f);
                        line.Append(hit ? hh.point.y.ToString("F2") : "none").Append(' ');
                    }
                    log.AppendLine(line.ToString());
                    log.AppendLine($"  steps bottom y={sbn.min.y:F2}, porch deck y={(porchR != null ? porchR.bounds.max.y : 0):F2}");
                }
            }
            if (playerHouse != null)
            {
                HouseLighting.RestrictGI(root, playerHouse);
                HouseLighting.AddProbes(playerHouse.transform, Bounds(playerHouse));
            }

            // ---- save, make it the build scene, retire the old one
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            if (File.Exists("Assets/Scenes/SampleScene.unity"))
                AssetDatabase.DeleteAsset("Assets/Scenes/SampleScene.unity");
            AssetDatabase.SaveAssets();
            log.AppendLine("OK scene saved to " + ScenePath);
            Debug.Log("[HouseScene] Neighbourhood scene built and saved. Report: Temp/housescene_report.txt");
        }

        // ============================================================ the house shell

        static GameObject BuildShell(string name, int style, bool interactive, bool lit, bool neighbourWindows = false)
        {
            var root = new GameObject(name);
            doorStyle = style % 5;
            var shell = Object.Instantiate(Load("House_Small"));
            shell.name = "Shell";
            shell.transform.SetParent(root.transform, false);

            foreach (var r in shell.GetComponentsInChildren<Renderer>())
            {
                r.sharedMaterial = HouseMaterial(r.name.ToLower(), style);
                string n = r.name.ToLower().Trim();
                bool solid = n == "walls" || n == "siding" || n.StartsWith("foundation") ||
                             n.Contains("front steps");
                if (solid) r.gameObject.AddComponent<MeshCollider>();
                // Floors and the porch deck are zero-thickness planes in the pack. A mesh collider on
                // a single-sided plane only holds from one side, and a CharacterController sinks
                // through it - so each gets a real 30 cm slab collider instead.
                if (n.EndsWith("floor") || n == "porch")
                {
                    var b = r.bounds;
                    var slab = new GameObject("FloorCollider_" + r.name);
                    slab.transform.SetParent(shell.transform, false);
                    slab.transform.position = new Vector3(b.center.x, b.max.y - 0.15f, b.center.z);
                    var bc = slab.AddComponent<BoxCollider>();
                    bc.size = new Vector3(b.size.x, 0.3f, b.size.z);
                }
            }


            var stepsR0 = shell.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Contains("front steps"));
            if (stepsR0 != null)
                foreach (var r in shell.GetComponentsInChildren<Renderer>())
                    if (r.name.ToLower().Contains("rails") && r.GetComponent<MeshFilter>() != null)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        float dz0 = Mathf.Sign(stepsR0.bounds.center.z - Bounds(shell).center.z);
                        mf.sharedMesh = StairlessRails(mf.sharedMesh, r.transform, stepsR0.bounds, dz0);
                    }
            Physics.SyncTransforms();
            var openings = FindOpenings(shell);
            log.AppendLine($"{name}: {openings.Count} openings ({openings.Count(o => o.isDoor)} doors)");

            var stepsRend = shell.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower().Contains("front steps"));
            var sb0 = Bounds(shell);
            var stepsDir = stepsRend != null
                ? new Vector3(stepsRend.bounds.center.x - sb0.center.x, 0, stepsRend.bounds.center.z - sb0.center.z).normalized
                : Vector3.back;
            var rng = new System.Random(1000 + style * 31 + name.GetHashCode() % 97);
            int n1 = 0, n2 = 0;
            Opening? front = null;
            foreach (var o in openings)
            {
                if (o.isDoor)
                {
                    bool isFront = Vector3.Dot(o.normal, stepsDir) > 0.5f;
                    PlaceDoor(shell.transform, o, interactive, interactive ? $"D_{(isFront ? "Front" : "Door" + n1)}" : $"d{n1}");
                    if (isFront && front == null) front = o;
                    n1++;
                }
                else
                {
                    bool glow = neighbourWindows && rng.NextDouble() < 0.3;
                    PlaceWindow(shell.transform, o, glow);
                    n2++;
                }
            }
            log.AppendLine($"  placed {n1} doors, {n2} windows");

            shell.transform.localScale = Vector3.one * HouseScale;
            Physics.SyncTransforms();

            if (lit) BuildInterior(root.transform, shell, front);
            foreach (var t in root.GetComponentsInChildren<Transform>()) t.gameObject.isStatic = !interactive;
            return root;
        }

        static Mesh stairlessRails;

        /// <summary>
        /// The pack welds the stair handrails into the porch-rail mesh, and their upper ends run
        /// straight through the porch railing and corner post. They cannot be separated by part, so
        /// every triangle that lies out beyond the porch edge, within the width of the steps, is cut.
        /// The steps themselves are a different mesh and stay.
        /// </summary>
        static Mesh StairlessRails(Mesh src, Transform tf, Bounds steps, float dirZ)
        {
            const string path = Dir + "/Meshes/PorchRails_NoStairRails.asset";
            if (stairlessRails != null) return stairlessRails;
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) return stairlessRails = existing;

            var v = src.vertices;
            var keep = new List<int>();
            float zCut = dirZ > 0 ? steps.min.z + 0.3f : steps.max.z - 0.3f;
            float xHalf = steps.extents.x + 0.4f;
            var tris = src.triangles;
            for (int t = 0; t < tris.Length; t += 3)
            {
                var c = tf.TransformPoint((v[tris[t]] + v[tris[t + 1]] + v[tris[t + 2]]) / 3f);
                bool stair = (dirZ > 0 ? c.z > zCut : c.z < zCut) && Mathf.Abs(c.x - steps.center.x) < xHalf;
                if (stair) continue;
                keep.Add(tris[t]); keep.Add(tris[t + 1]); keep.Add(tris[t + 2]);
            }
            var m = new Mesh { name = "PorchRails_NoStairRails", indexFormat = src.indexFormat };
            m.vertices = v; m.normals = src.normals; m.uv = src.uv; m.tangents = src.tangents;
            m.SetTriangles(keep, 0);
            m.RecalculateBounds();
            log.AppendLine($"  stair rails removed: {tris.Length / 3 - keep.Count / 3} of {tris.Length / 3} triangles");
            AssetDatabase.CreateAsset(m, path);
            return stairlessRails = m;
        }

        static Material HouseMaterial(string n, int style)
        {
            if (n.Contains("glass")) return Mat("glass");
            if (n.Contains("gable window")) return Mat("white");
            if (n.Contains("roof gables")) return Mat("siding" + style);
            if (n.Contains("roof") || n.Contains("shingle")) return Mat("shingle" + style % 3);
            if (n.Contains("gutter") || n.Contains("rail")) return Mat("white");
            if (n.StartsWith("foundation")) return Mat("brick" + style % 3);
            if (n.Contains("steps") || n == "porch") return Mat("oak");
            if (n.StartsWith("bathroom") || n.StartsWith("kitchen")) return Mat("marble");
            if (n.StartsWith("living")) return Mat("hardwood");
            if (n.Contains("floor")) return Mat("carpet");
            if (n.Contains("ceiling")) return Mat("wall");
            if (n == "siding") return Mat("siding" + style);
            return Mat("wall");
        }

        // ============================================================ opening detection

        static List<Opening> FindOpenings(GameObject shell)
        {
            var result = new List<Opening>();
            var sidingR = shell.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower() == "siding");
            if (sidingR == null) { log.AppendLine("  no siding renderer"); return result; }
            var col = sidingR.GetComponent<MeshCollider>();
            var b = sidingR.bounds;

            const float step = 0.1f;
            float y0 = b.min.y + 0.05f, y1 = b.max.y - 0.05f;
            int ny = Mathf.Max(2, Mathf.FloorToInt((y1 - y0) / step));

            // side: outward normal, tangent range
            var sides = new[]
            {
                new { n = new Vector3(0, 0, -1), isX = true  },   // south face, tangent along X
                new { n = new Vector3(0, 0,  1), isX = true  },   // north
                new { n = new Vector3(-1, 0, 0), isX = false },   // west, tangent along Z
                new { n = new Vector3( 1, 0, 0), isX = false },   // east
            };

            foreach (var s in sides)
            {
                float u0 = s.isX ? b.min.x : b.min.z, u1 = s.isX ? b.max.x : b.max.z;
                int nu = Mathf.Max(2, Mathf.FloorToInt((u1 - u0) / step));
                var dist = new float[nu, ny];
                float reach = (s.isX ? b.size.z : b.size.x) + 3f;

                for (int iu = 0; iu < nu; iu++)
                for (int iv = 0; iv < ny; iv++)
                {
                    float u = u0 + (iu + 0.5f) * step, y = y0 + iv * step;
                    var origin = s.isX
                        ? new Vector3(u, y, s.n.z < 0 ? b.min.z - 1.5f : b.max.z + 1.5f)
                        : new Vector3(s.n.x < 0 ? b.min.x - 1.5f : b.max.x + 1.5f, y, u);
                    var ray = new Ray(origin, -s.n);
                    dist[iu, iv] = col.Raycast(ray, out var hit, reach) ? hit.distance : float.PositiveInfinity;
                }

                // Wall plane for each column = the nearest hit anywhere in that column.
                var plane = new float[nu];
                for (int iu = 0; iu < nu; iu++)
                {
                    float m = float.PositiveInfinity;
                    for (int iv = 0; iv < ny; iv++) m = Mathf.Min(m, dist[iu, iv]);
                    plane[iu] = m;
                }


                // A doorway often has NO siding above it either, so its columns register no wall at
                // all. Treat a narrow run of wall-less columns between two wall columns as a gap in
                // the wall, using its neighbours as the plane.
                for (int iu = 0; iu < nu; iu++)
                {
                    if (!float.IsInfinity(plane[iu])) continue;
                    int a = iu;
                    while (a < nu && float.IsInfinity(plane[a])) a++;
                    if (iu > 0 && a < nu && (a - iu) * step <= 3.5f)
                    {
                        float fill = (plane[iu - 1] + plane[a]) * 0.5f;
                        for (int k = iu; k < a; k++) plane[k] = fill;
                    }
                    iu = a;
                }
                var open = new bool[nu, ny];
                for (int iu = 0; iu < nu; iu++)
                    if (!float.IsInfinity(plane[iu]))
                        for (int iv = 0; iv < ny; iv++)
                            open[iu, iv] = dist[iu, iv] > plane[iu] + 0.5f;

                int openCount = 0, noPlane = 0;
                for (int iu = 0; iu < nu; iu++) { if (float.IsInfinity(plane[iu])) noPlane++; for (int iv = 0; iv < ny; iv++) if (open[iu, iv]) openCount++; }
                log.AppendLine($"  side {s.n}: grid {nu}x{ny}, open cells {openCount}, columns without wall {noPlane}, plane range {plane.Where(p => !float.IsInfinity(p)).DefaultIfEmpty(0).Min():F2}..{plane.Where(p => !float.IsInfinity(p)).DefaultIfEmpty(0).Max():F2}");

                // Connected components -> rectangles.
                var seen = new bool[nu, ny];
                for (int iu = 0; iu < nu; iu++)
                for (int iv = 0; iv < ny; iv++)
                {
                    if (!open[iu, iv] || seen[iu, iv]) continue;
                    int minU = iu, maxU = iu, minV = iv, maxV = iv;
                    var q = new Queue<Vector2Int>(); q.Enqueue(new Vector2Int(iu, iv)); seen[iu, iv] = true;
                    var planes = new List<float>();
                    while (q.Count > 0)
                    {
                        var c = q.Dequeue();
                        minU = Mathf.Min(minU, c.x); maxU = Mathf.Max(maxU, c.x);
                        minV = Mathf.Min(minV, c.y); maxV = Mathf.Max(maxV, c.y);
                        planes.Add(plane[c.x]);
                        foreach (var d in new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) })
                        {
                            int a = c.x + d.x, bb = c.y + d.y;
                            if (a < 0 || bb < 0 || a >= nu || bb >= ny || seen[a, bb] || !open[a, bb]) continue;
                            seen[a, bb] = true; q.Enqueue(new Vector2Int(a, bb));
                        }
                    }
                    float w = (maxU - minU + 1) * step, h = (maxV - minV + 1) * step;
                    if (w < 0.5f || h < 0.5f || w * h < 0.35f) { log.AppendLine($"  rejected comp w={w:F2} h={h:F2}"); continue; }

                    planes.Sort();
                    float pd = planes[planes.Count / 2];
                    float uc = u0 + (minU + maxU + 1) * 0.5f * step;
                    float bottom = y0 + minV * step;
                    float yc = bottom + h * 0.5f - 0.0f;
                    var originC = s.isX
                        ? new Vector3(uc, yc, s.n.z < 0 ? b.min.z - 1.5f : b.max.z + 1.5f)
                        : new Vector3(s.n.x < 0 ? b.min.x - 1.5f : b.max.x + 1.5f, yc, uc);
                    var outer = originC - s.n * pd;

                    bool isDoor = bottom - b.min.y < 0.3f && h > 1.8f;
                    result.Add(new Opening { outerCentre = outer, normal = s.n, width = w, height = h, bottom = bottom, isDoor = isDoor });
                    log.AppendLine($"  opening n={s.n} w={w:F2} h={h:F2} bottom={bottom - b.min.y:F2} door={isDoor} at {outer}");
                }
            }
            return result;
        }

        // ============================================================ doors and windows

        static void PlaceWindow(Transform parent, Opening o, bool glow)
        {
            bool wide = o.width > 3.4f;
            var inst = Object.Instantiate(Load(wide ? "Window3panel" : (o.height > o.width * 1.1f ? "Window001" : "Window002")));
            inst.name = "Window";
            inst.transform.SetParent(parent, false);
            inst.transform.rotation = Quaternion.LookRotation(o.normal, Vector3.up);
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
                r.sharedMaterial = r.name.ToLower().Contains("glass") ? Mat(glow ? "glassLit" : "glass") : Mat("white");

            var pb = Bounds(inst);
            // Fit the opening exactly: rotation was applied, so width lies along the tangent.
            float sw = Mathf.Abs(o.normal.z) > 0.5f ? pb.size.x : pb.size.z;
            float sx = (o.width + 0.06f) / Mathf.Max(0.01f, sw);
            float sy = (o.height + 0.06f) / Mathf.Max(0.01f, pb.size.y);
            inst.transform.localScale = new Vector3(sx, sy, Mathf.Min(sx, sy));
            pb = Bounds(inst);
            var target = o.outerCentre - o.normal * 0.12f;
            inst.transform.position += target - pb.center;

            foreach (var r in inst.GetComponentsInChildren<Renderer>())
                if (r.name.ToLower().Contains("glass")) r.gameObject.AddComponent<BoxCollider>();
        }

        static void PlaceDoor(Transform parent, Opening o, bool interactive, string id)
        {
            var inst = Object.Instantiate(Load("Door001"));
            inst.name = "Door_" + id;
            inst.transform.SetParent(parent, false);
            inst.transform.rotation = Quaternion.LookRotation(o.normal, Vector3.up);

            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            {
                string n = r.name.ToLower().Trim();
                r.sharedMaterial = n.Contains("knob") || n.Contains("hinge") ? Mat("brass") : n.StartsWith("frame") ? Mat("door" + doorStyle) : Mat("door" + doorStyle);
            }

            var pb = Bounds(inst);
            float across = Mathf.Abs(o.normal.z) > 0.5f ? pb.size.x : pb.size.z;
            // Measure the real opening: probe up the doorway for the first height where the wall
            // resumes (the lintel). The siding has no lintel at all, so the opening cannot be
            // read from it - and sizing the door from it left a gap above the frame.
            float topY = o.bottom + Mathf.Min(o.height, 3.0f);
            for (float y = o.bottom + 1.0f; y < o.bottom + o.height; y += 0.03f)
            {
                var origin = o.outerCentre + o.normal * 1.2f; origin.y = y;
                if (Physics.Raycast(origin, -o.normal, out _, 2.4f)) { topY = y; break; }
            }
            float oh = topY - o.bottom;
            log.AppendLine($"  door opening: hole {o.height:F2} m, lintel found at {oh:F2} m above the sill");
            // Horizontal scale is uniform so the leaf can swing without shearing; vertical fits the opening.
            float sH = Mathf.Clamp((o.width + 0.04f) / across, 0.8f, 1.4f);
            float sV = Mathf.Clamp((oh + 0.02f) / pb.size.y, 0.8f, 1.5f);
            inst.transform.localScale = new Vector3(sH, sV, sH);
            pb = Bounds(inst);
            var target = new Vector3(o.outerCentre.x, o.bottom, o.outerCentre.z) - o.normal * 0.12f;
            inst.transform.position += new Vector3(target.x - pb.center.x, target.y - pb.min.y, target.z - pb.center.z);

            // Leaf parts swing on a pivot at the hinge; the frame stays put.
            var leaf = new List<Renderer>();
            Renderer hingeR = null;
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            {
                string n = r.name.ToLower().Trim();
                if (n.StartsWith("frame")) continue;
                leaf.Add(r);
                if (n.StartsWith("hinge") && n != "hinges" && hingeR == null) hingeR = r;
            }
            var hingeCentre = hingeR != null ? hingeR.bounds.center : pb.center;
            var pivot = new GameObject("Pivot");
            pivot.transform.SetParent(inst.transform, true);
            // Hinge at the interior corner of the leaf, not mid-thickness: the door opens inward, so the leaf then
            // swings away from the jamb instead of its back corner cutting into the frame.
            var leafBox = FBounds(leaf[0].transform.parent.gameObject == null ? leaf[0].gameObject : leaf.First(r => r.name.ToLower() == "door").gameObject);
            float leafThick = Mathf.Abs(o.normal.z) > 0.5f ? leafBox.size.z : leafBox.size.x;
            pivot.transform.position = new Vector3(hingeCentre.x, pb.min.y, hingeCentre.z) - o.normal * (leafThick * 0.5f);
            pivot.transform.rotation = inst.transform.rotation;
            foreach (var r in leaf) r.transform.SetParent(pivot.transform, true);

            // Fill the frame exactly. The leaf is modelled with clearance all round, which reads as a gap of
            // light down the hinge side and across the top. The opening is MEASURED, not guessed: temporary
            // colliders go on the frame, rays are fired from the middle of the leaf to the inner faces, and
            // the leaf is stretched about its hinge and slid until it meets them.
            var lining = inst.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower() == "frame");
            var leafBody = leaf.FirstOrDefault(r => r.name.ToLower() == "door");
            if (lining != null && leafBody != null)
            {
                bool alongX = Mathf.Abs(o.normal.z) > 0.5f;
                var lb = FBounds(leafBody.gameObject);    // the panel itself: the knob and hinges stick out past it and must not count
                var c = lb.center;
                var tang = alongX ? Vector3.right : Vector3.forward;
                float cc = alongX ? c.x : c.z;
                // The frame mesh is single-sided, so rays from inside miss its inner faces. Its vertices do not lie:
                // the nearest ones to the leaf on each side ARE the inner faces of the opening.
                float innerLeft = float.NegativeInfinity, innerRight = float.PositiveInfinity, innerTop = float.PositiveInfinity;
                var pts = new List<Vector3>();
                foreach (var fr in inst.GetComponentsInChildren<MeshFilter>().Where(m => m.name.ToLower().StartsWith("frame")))
                {
                    if (fr.sharedMesh == null) continue;
                    var mtx = fr.transform.localToWorldMatrix;
                    foreach (var v in fr.sharedMesh.vertices)
                    {
                        var w = mtx.MultiplyPoint3x4(v);
                        float t = alongX ? w.x : w.z;
                        // The frame is a low-poly box: vertices exist only at the corners of each jamb, never at mid-height.
                        {
                            if (t < cc - 0.1f) innerLeft = Mathf.Max(innerLeft, t);
                            if (t > cc + 0.1f) innerRight = Mathf.Min(innerRight, t);
                        }
                        pts.Add(w);
                    }
                }
                foreach (var w in pts)
                {
                    float t = alongX ? w.x : w.z;
                    if (t >= innerLeft - 0.03f && t <= innerRight + 0.03f && w.y > c.y + 0.3f) innerTop = Mathf.Min(innerTop, w.y);   // underside of the head
                }
                foreach (var fr in inst.GetComponentsInChildren<MeshFilter>()) log.AppendLine($"    door part {fr.name}: size {FBounds(fr.gameObject).size:F3} centre {FBounds(fr.gameObject).center:F3}");
                float dl = cc - innerLeft, dr = innerRight - cc, du = innerTop - c.y;
                log.AppendLine($"  door frame vertices: left {dl:F2} right {dr:F2} up {du:F2} from the leaf centre");
                // The visible strip beside the leaf is the reveal of the hole in the WALL, which is wider than the frame's
                // inner edge. Measure that hole directly: rays from the leaf centre hit the wall's side faces.
                float wl = float.MaxValue, wr = float.MaxValue;
                foreach (var h in Physics.RaycastAll(c, -tang, 3f)) if (h.collider is MeshCollider) wl = Mathf.Min(wl, h.distance);
                foreach (var h in Physics.RaycastAll(c, tang, 3f)) if (h.collider is MeshCollider) wr = Mathf.Min(wr, h.distance);
                bool wallMeasured = wl < 3f && wr < 3f && wl > 0.2f && wr > 0.2f;
                // (measured for the log only: sizing the leaf to the wall hole made it overlap the casing. Flush placement hides the reveal.)
                log.AppendLine($"  door wall hole: {(wallMeasured ? $"left {wl:F3} right {wr:F3}" : "not measured")}");

                if (dl < 3f && dr < 3f && du < 3f && dl > 0.2f && dr > 0.2f)
                {
                    float centreCoord = alongX ? c.x : c.z;
                    float openLeft = centreCoord - dl, openW = dl + dr, openTop = c.y + du;
                    float leafW = alongX ? lb.size.x : lb.size.z;
                    // The frame mesh has no vertices on its true inner faces, so the measured width reads about 8 cm short;
                    // that figure comes from comparing the render, and is why the leaf is widened by it here.
                    float frameBias = 0.075f;   // widen towards the latch side: the frame reads that much narrower than the leaf needs
                    float rx = (openW + frameBias) / Mathf.Max(0.01f, leafW);
                    float ry = (openTop - lb.min.y + 0.004f) / Mathf.Max(0.01f, lb.size.y);
                    pivot.transform.localScale = new Vector3(rx, ry, 1f);
                    Physics.SyncTransforms();
                    var lb2 = FBounds(leafBody.gameObject);
                    float shift = (openLeft - 0.01f) - (alongX ? lb2.min.x : lb2.min.z);   // hinge edge stays tight to its jamb
                    pivot.transform.position += alongX ? new Vector3(shift, 0, 0) : new Vector3(0, 0, shift);
                    // Bring the leaf forward to sit nearly flush with the casing. Set back by the thickness of the wall it showed the
                    // side of the wall hole as a pale strip down one edge.
                    pivot.transform.position += o.normal * 0.12f;
                    log.AppendLine($"  door leaf fitted to measured opening {openW:F3} x {openTop - lb.min.y:F3}: x{rx:F2} y{ry:F2}, shifted {shift:F3}");
                }
                else log.AppendLine("  door leaf: could not measure the opening, left as modelled");
            }

            var body = leaf.FirstOrDefault(r => r.name.ToLower() == "door");
            if (body != null) body.gameObject.AddComponent<BoxCollider>();

            if (!interactive) return;

            // Hinge on the lower tangent side opens inward; on the other side the angle flips.
            var tangent = Vector3.Cross(Vector3.up, o.normal);
            float side = Vector3.Dot(pb.center - hingeCentre, tangent);
            var d = pivot.AddComponent<Door>();
            d.doorId = id;
            d.motion = Door.Motion.Swing;
            d.openAngle = side > 0 ? 92f : -92f;
            d.openTime = 0.9f; d.closeTime = 1.0f;
        }

        // ============================================================ interior: lights and switches

        static void BuildInterior(Transform root, GameObject shell, Opening? front)
        {
            var interior = new GameObject("Interior"); interior.transform.SetParent(root, false);
            float floorTop = FloorTop(shell.transform.parent.gameObject);
            var sidingR = shell.GetComponentsInChildren<Renderer>().First(r => r.name.ToLower() == "siding");
            float ceilingY = floorTop + sidingR.bounds.size.y - 0.12f;

            var rooms = shell.GetComponentsInChildren<Renderer>()
                .Where(r => r.name.ToLower().EndsWith("floor") && !r.name.ToLower().Contains("porch")).ToList();

            foreach (var r in rooms)
            {
                var b = r.bounds;
                roomShots.Add(new Vector3(b.center.x, floorTop + 1.5f, b.center.z));
                HouseLighting.AddRoomProbe(interior.transform, r.name.Replace(" floor", "").Replace(" ", ""), b, floorTop);
                string id = r.name.Replace(" floor", "").Replace(" ", "");
                string group = id;
                int count = b.size.x * b.size.z > 50f ? 2 : 1;
                bool alongX = b.size.x >= b.size.z;
                for (int i = 0; i < count; i++)
                {
                    float f = count == 1 ? 0.5f : (i == 0 ? 0.28f : 0.72f);
                    var p = new Vector3(
                        alongX ? Mathf.Lerp(b.min.x, b.max.x, f) : b.center.x, ceilingY,
                        alongX ? b.center.z : Mathf.Lerp(b.min.z, b.max.z, f));
                    MakeCeilingLight(interior.transform, $"L_{id}_{i + 1}", group, id, p, 2700f, count == 1 ? 1.6f : 1.2f, Mathf.Max(b.size.x, b.size.z) * 0.75f);
                }
                var sw = FindSwitchSpot(b, floorTop + 1.25f);
                if (sw.HasValue) MakeSwitch(interior.transform, $"SW_{id}", group, id, sw.Value.pos, sw.Value.inward);
                else log.AppendLine($"  no doorway found for room {id}; switch not placed");
            }

            if (front.HasValue)
            {
                var o = front.Value;
                var tangent = Vector3.Cross(Vector3.up, o.normal).normalized;
                var lampPos = o.outerCentre + tangent * (o.width * 0.5f + 0.5f) + Vector3.up * (o.bottom - o.outerCentre.y + 2.1f + 0f);
                lampPos.y = floorTop + 2.1f;
                lampPos += o.normal * 0.02f;
                MakePorchLight(interior.transform, "L_FrontPorch", "Porch", lampPos, o.normal);

                // Matching switch just inside, on the same side of the door.
                var inside = o.outerCentre - o.normal * 0.45f + tangent * (o.width * 0.5f + 0.4f);
                inside.y = floorTop + 1.25f;
                MakeSwitch(interior.transform, "SW_Porch", "Porch", "Porch", inside, -o.normal);
            }

            // Furniture last, so it can keep clear of the switches placed above.
            FurnishHouse(interior.transform, rooms);
        }

        struct Spot { public Vector3 pos; public Vector3 inward; }

        /// <summary>
        /// Finds the doorway out of a room and returns a point on the wall beside it, where a switch
        /// belongs. The room is probed with short rays from just inside its edge: a ray that hits
        /// nothing has found a gap, and the switch goes on solid wall a hand's width past it.
        /// </summary>
        static Spot? FindSwitchSpot(Bounds room, float y)
        {
            var dirs = new[] { Vector3.back, Vector3.forward, Vector3.left, Vector3.right };
            Spot? best = null; float bestScore = float.MaxValue;
            foreach (var d in dirs)
            {
                bool alongX = Mathf.Abs(d.z) > 0.5f;
                float u0 = alongX ? room.min.x + 0.4f : room.min.z + 0.4f;
                float u1 = alongX ? room.max.x - 0.4f : room.max.z - 0.4f;
                float edge = alongX ? (d.z > 0 ? room.max.z : room.min.z) : (d.x > 0 ? room.max.x : room.min.x);

                const float step = 0.1f;
                var solid = new List<bool>(); var pts = new List<Vector3>(); var hits = new List<Vector3>();
                for (float u = u0; u <= u1; u += step)
                {
                    var inner = alongX ? new Vector3(u, y, edge - d.z * 0.5f) : new Vector3(edge - d.x * 0.5f, y, u);
                    bool hit = Physics.Raycast(inner, d, out var h, 1.2f);
                    solid.Add(hit); pts.Add(inner); hits.Add(hit ? h.point : inner + d);
                }
                // longest open run
                int runStart = -1, bestStart = -1, bestLen = 0;
                for (int i = 0; i <= solid.Count; i++)
                {
                    bool open = i < solid.Count && !solid[i];
                    if (open && runStart < 0) runStart = i;
                    if (!open && runStart >= 0)
                    {
                        int len = i - runStart;
                        if (len > bestLen) { bestLen = len; bestStart = runStart; }
                        runStart = -1;
                    }
                }
                float runLen = bestLen * step;
                float score = Mathf.Abs(runLen - 1.0f);
                if (runLen < 0.7f || runLen > 12f || score >= bestScore) continue;

                // solid wall past the gap's end (prefer the far end), at least 0.5 m of it
                int after = bestStart + bestLen + 4;
                int before = bestStart - 5;
                int pick = after < solid.Count && solid[after] ? after : (before >= 0 && solid[before] ? before : -1);
                if (pick < 0) continue;
                bestScore = score;
                best = new Spot { pos = hits[pick] - d * 0.015f, inward = -d };
            }
            return best;
        }

        static void MakeCeilingLight(Transform parent, string id, string group, string room, Vector3 pos, float kelvin, float intensity, float range)
        {
            var go = new GameObject("Fixture_" + id);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;

            var dome = Object.Instantiate(Load("LightCover_Dome_002"));
            dome.transform.SetParent(go.transform, false);
            dome.transform.localPosition = Vector3.zero;
            dome.transform.localScale = Vector3.one * 0.9f;
            foreach (var r in dome.GetComponentsInChildren<Renderer>()) r.sharedMaterial = Mat("shade");

            var lg = new GameObject("Light"); lg.transform.SetParent(go.transform, false);
            lg.transform.localPosition = new Vector3(0, -0.15f, 0);
            lg.transform.localRotation = Quaternion.Euler(90, 0, 0);
            var l = lg.AddComponent<Light>();
            l.type = LightType.Spot; l.spotAngle = 140f; l.innerSpotAngle = 70f;
            l.useColorTemperature = true; l.colorTemperature = kelvin; l.color = Color.white;
            l.intensity = intensity; l.range = range;
            l.shadows = LightShadows.Soft; l.shadowStrength = 0.9f; l.shadowBias = 0.03f; l.shadowNormalBias = 0.25f;
            l.enabled = false;

            var f = go.AddComponent<LightFixture>();
            f.fixtureId = id; f.group = group; f.room = room; f.fixtureType = "ceiling"; f.isOn = false;
        }

        static void MakePorchLight(Transform parent, string id, string group, Vector3 pos, Vector3 outward)
        {
            var go = new GameObject("Fixture_" + id);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(outward, Vector3.up);

            var body = Object.Instantiate(Load("PorchLight"));
            body.transform.SetParent(go.transform, false);
            body.transform.localScale = Vector3.one * 0.8f;
            foreach (var r in body.GetComponentsInChildren<Renderer>())
                r.sharedMaterial = r.name.ToLower().Contains("glass") ? Mat("shade") : Mat("castiron");

            var lg = new GameObject("Light"); lg.transform.SetParent(go.transform, false);
            lg.transform.localPosition = new Vector3(0, -0.1f, 0.25f);
            lg.transform.localRotation = Quaternion.Euler(70, 0, 0);
            var l = lg.AddComponent<Light>();
            l.type = LightType.Spot; l.spotAngle = 130f; l.innerSpotAngle = 60f;
            l.useColorTemperature = true; l.colorTemperature = 2400f; l.color = Color.white;
            l.intensity = 2.2f; l.range = 11f;
            l.shadows = LightShadows.Soft; l.shadowStrength = 0.9f; l.shadowBias = 0.03f; l.shadowNormalBias = 0.25f;

            var f = go.AddComponent<LightFixture>();
            f.fixtureId = id; f.group = group; f.room = "FrontPorch"; f.fixtureType = "porch"; f.isOn = true;
        }

        static void MakeSwitch(Transform parent, string id, string group, string room, Vector3 pos, Vector3 inward)
        {
            var go = new GameObject("Switch_" + id);
            go.transform.SetParent(parent, false);
            go.transform.position = pos + inward * 0.02f;
            keepClear.Add(pos);
            go.transform.rotation = Quaternion.FromToRotation(Vector3.right, inward);

            var vis = Object.Instantiate(Load("Fixture_LightSwitch_001"));
            vis.transform.SetParent(go.transform, false);
            vis.transform.localScale = Vector3.one * 0.5f;
            foreach (var r in vis.GetComponentsInChildren<Renderer>()) r.sharedMaterial = Mat("switchWhite");

            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(0.30f, 0.45f, 0.35f);
            box.center = new Vector3(0.1f, 0f, 0f);

            var sw = go.AddComponent<LightSwitch>();
            sw.switchId = id; sw.room = room; sw.controls = group;
        }

        // ============================================================ street, lots, lamps

        static void BuildGround(Transform parent, float roadHalf, float walk)
        {
            const float len = 900f;
            Slab(parent, "Ground", new Vector3(0, -0.5f, 0), new Vector3(len, 1f, len), AcTiled("ac_grass", "AC_Grass004", "Grass004", new Color(0.82f, 0.86f, 0.78f), 0.12f, len, len, 2.2f));
            Slab(parent, "Road", new Vector3(0, -0.06f, 0), new Vector3(len, 0.3f, roadHalf * 2f), AcTiled("ac_road", "AC_Asphalt033", "Asphalt033", new Color(0.85f, 0.85f, 0.87f), 0.2f, len, roadHalf * 2f, 3f));
            foreach (int s in new[] { -1, 1 })
                Slab(parent, s < 0 ? "Sidewalk_S" : "Sidewalk_N", new Vector3(0, 0.0f, s * (roadHalf + walk * 0.5f)),
                     new Vector3(len, 0.26f, walk), AcTiled("ac_walk", "AC_PavingStones150", "PavingStones150", new Color(0.9f, 0.9f, 0.9f), 0.2f, len, walk, 1.6f));

            var line = FlatMat("roadline", new Color(0.55f, 0.48f, 0.18f), 0.2f);
            var dashes = new GameObject("CentreLine"); dashes.transform.SetParent(parent, false);
            for (float x = -len * 0.5f + 3f; x < len * 0.5f; x += 9f)
            {
                var d = GameObject.CreatePrimitive(PrimitiveType.Cube);
                d.transform.SetParent(dashes.transform, false);
                d.transform.position = new Vector3(x, 0.1f, 0);
                d.transform.localScale = new Vector3(4f, 0.02f, 0.15f);
                d.GetComponent<Renderer>().sharedMaterial = line;
                Object.DestroyImmediate(d.GetComponent<Collider>());
            }
        }

        static void BuildLot(Transform parent, float cx, bool north, float lotW, float frontZ, float rearZ, float backZ, int idx, float sidewalkEdge)
        {
            float sgn = north ? 1f : -1f;
            var lot = new GameObject($"Lot_{(north ? "N" : "S")}_{cx:0}"); lot.transform.SetParent(parent, false);

            // Each lot picks its own fence stain, so the street does not look stamped out.
            string fenceKey = "fence" + (idx * 7 % 4);
            Fence(lot.transform, new Vector3(cx - lotW * 0.5f, 0, sgn * rearZ), new Vector3(cx + lotW * 0.5f, 0, sgn * rearZ), fenceKey);
            Fence(lot.transform, new Vector3(cx - lotW * 0.5f, 0, sgn * (frontZ + 6f)), new Vector3(cx - lotW * 0.5f, 0, sgn * rearZ), fenceKey);
            Fence(lot.transform, new Vector3(cx + lotW * 0.5f, 0, sgn * (frontZ + 6f)), new Vector3(cx + lotW * 0.5f, 0, sgn * rearZ), fenceKey);

            // Front boundary: tall hedge, trimmed-low hedge, or open lawn, depending on the lot.
            float hz = sgn * (sidewalkEdge + 0.9f);
            int frontStyle = 0;   // every lot has the same hedge
            if (frontStyle != 3)
            {
                const float hh = 1.6f;   // one height for every hedge
                Hedge(lot.transform, cx - lotW * 0.5f + 0.5f, cx - 1.0f, hz, hh);
                Hedge(lot.transform, cx + 1.0f, cx + lotW * 0.5f - 0.5f, hz, hh);
            }

            // Walkway from the porch steps to the pavement.
            var w = GameObject.CreatePrimitive(PrimitiveType.Cube);
            w.name = "Walkway"; w.transform.SetParent(lot.transform, false);
            float z0 = sidewalkEdge, z1 = frontZ;
            w.transform.position = new Vector3(cx, 0.04f, sgn * (z0 + z1) * 0.5f);
            w.transform.localScale = new Vector3(1.4f, 0.14f, z1 - z0);
            w.GetComponent<Renderer>().sharedMaterial = AcTiled("ac_path", "AC_PavingStones150", "PavingStones150", new Color(0.9f, 0.9f, 0.9f), 0.2f, 1.4f, z1 - z0, 1.6f);

            Backyard(lot.transform, cx, sgn, backZ, rearZ, idx);
            DirtPatches(lot.transform, cx, sgn, frontZ, backZ, rearZ, idx);
        }

        static void Fence(Transform parent, Vector3 a, Vector3 b, string matKey = "fence0")
        {
            var wood = Mat(matKey);
            float len = Vector3.Distance(a, b);
            int segs = Mathf.Max(1, Mathf.RoundToInt(len / 2.4f));
            var dir = (b - a).normalized;
            float segLen = len / segs;
            for (int i = 0; i < segs; i++)
            {
                var mid = a + dir * (segLen * (i + 0.5f));
                var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                panel.transform.SetParent(parent, false);
                panel.transform.position = mid + Vector3.up * 1.0f;
                panel.transform.rotation = Quaternion.LookRotation(dir);
                panel.transform.localScale = new Vector3(0.05f, 2.0f, segLen);
                panel.GetComponent<Renderer>().sharedMaterial = wood;
                var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                post.transform.SetParent(parent, false);
                post.transform.position = a + dir * (segLen * i) + Vector3.up * 1.1f;
                post.transform.localScale = new Vector3(0.12f, 2.2f, 0.12f);
                post.GetComponent<Renderer>().sharedMaterial = wood;
            }
            var last = GameObject.CreatePrimitive(PrimitiveType.Cube);
            last.transform.SetParent(parent, false);
            last.transform.position = b + Vector3.up * 1.1f;
            last.transform.localScale = new Vector3(0.12f, 2.2f, 0.12f);
            last.GetComponent<Renderer>().sharedMaterial = wood;
        }

        /// <summary>A clipped hedge: a flat-topped, straight-sided block of leaf texture, not a row of balls.</summary>
        static void Hedge(Transform parent, float x0, float x1, float z, float height = 1.45f)
        {
            float len = x1 - x0;
            if (len < 0.3f) return;
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "Hedge";
            block.transform.SetParent(parent, false);
            block.transform.position = new Vector3((x0 + x1) * 0.5f, height * 0.5f, z);
            block.transform.localScale = new Vector3(len, height, 0.8f);
            block.GetComponent<Renderer>().sharedMaterial = HedgeMat(len, height);
            block.isStatic = true;
        }

        static Texture2D hedgeTex;

        static Material HedgeMat(float len, float height)
        {
            string key = $"hedge_{Mathf.RoundToInt(len * 10)}_{Mathf.RoundToInt(height * 100)}";
            if (mats.TryGetValue(key, out var cached)) return cached;
            if (hedgeTex == null)
            {
                const string texPath = MatDir + "/T_Hedge.png";
                const int S = 256;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
                var rng = new System.Random(77);
                var px = new Color[S * S];
                for (int i = 0; i < px.Length; i++) px[i] = new Color(0.05f, 0.10f, 0.04f, 1f);   // dark gaps between leaves
                for (int n = 0; n < 5200; n++)
                {
                    int cx = rng.Next(S), cy = rng.Next(S);
                    float r = 2.2f + (float)rng.NextDouble() * 2.6f, ang = (float)rng.NextDouble() * Mathf.PI;
                    float shade = (float)rng.NextDouble();
                    var c = Color.Lerp(new Color(0.07f, 0.17f, 0.05f), new Color(0.24f, 0.40f, 0.12f), shade * shade);
                    for (int dy = -5; dy <= 5; dy++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        float u = dx * Mathf.Cos(ang) + dy * Mathf.Sin(ang), w = -dx * Mathf.Sin(ang) + dy * Mathf.Cos(ang);
                        if ((u * u) / (r * r) + (w * w) / (r * r * 0.28f) > 1f) continue;   // narrow leaf shape
                        int xx = (cx + dx + S) % S, yy = (cy + dy + S) % S;
                        px[yy * S + xx] = c;
                    }
                }
                tex.SetPixels(px); tex.Apply();
                File.WriteAllBytes(texPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
                var imp = (TextureImporter)AssetImporter.GetAtPath(texPath);
                imp.wrapMode = TextureWrapMode.Repeat; imp.mipmapEnabled = true;
                imp.SaveAndReimport();
                hedgeTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            }
            var m = NewMat(key, Lit);
            m.SetTexture("_BaseMap", hedgeTex);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.05f);
            m.mainTextureScale = new Vector2(len / 0.7f, height / 0.67f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void BuildStreetLamps(Transform parent, float roadHalf, float walk)
        {
            var lamps = new GameObject("StreetLamps"); lamps.transform.SetParent(parent, false);
            var xs = new[] { -60f, -24f, 12f, 48f, 84f };
            for (int i = 0; i < xs.Length; i++)
            {
                BuildLamp(lamps.transform, $"Lamp_N{i}", xs[i], roadHalf + walk + 0.3f, -1, Mathf.Abs(xs[i] - 6f) < 50f);
                BuildLamp(lamps.transform, $"Lamp_S{i}", xs[i] - 18f, -(roadHalf + walk + 0.3f), +1, Mathf.Abs(xs[i] - 18f - 6f) < 50f);
            }
        }

        /// <param name="armDir">-1: arm reaches toward -Z (over the road from the north side); +1 toward +Z.</param>
        static void BuildLamp(Transform parent, string name, float x, float z, int armDir, bool shadows)
        {
            var lamp = new GameObject(name); lamp.transform.SetParent(parent, false);
            lamp.transform.position = new Vector3(x, 0, z);
            var metal = Mat("castiron");
            const float poleH = 5.2f, armLen = 2.2f;

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.transform.SetParent(lamp.transform, false);
            pole.transform.localPosition = new Vector3(0, poleH * 0.5f, 0);
            pole.transform.localScale = new Vector3(0.14f, poleH * 0.5f, 0.14f);
            pole.GetComponent<Renderer>().sharedMaterial = metal;

            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.transform.SetParent(lamp.transform, false);
            arm.transform.localPosition = new Vector3(0, poleH, armDir * armLen * 0.5f);
            arm.transform.localScale = new Vector3(0.09f, 0.09f, armLen);
            arm.GetComponent<Renderer>().sharedMaterial = metal;
            Object.DestroyImmediate(arm.GetComponent<Collider>());

            var headPos = new Vector3(0, poleH - 0.08f, armDir * armLen);
            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.transform.SetParent(lamp.transform, false);
            head.transform.localPosition = headPos;
            head.transform.localScale = new Vector3(0.4f, 0.12f, 0.75f);
            head.GetComponent<Renderer>().sharedMaterial = AtmosphereSetup.Day ? Mat("shade") : Mat("streetGlow");
            Object.DestroyImmediate(head.GetComponent<Collider>());

            var lg = new GameObject("Light"); lg.transform.SetParent(lamp.transform, false);
            lg.transform.localPosition = headPos + Vector3.down * 0.1f;
            lg.transform.localRotation = Quaternion.Euler(90, 0, 0);
            var l = lg.AddComponent<Light>();
            l.type = LightType.Spot; l.spotAngle = 100f; l.innerSpotAngle = 40f;
            l.useColorTemperature = true; l.colorTemperature = 2300f; l.color = Color.white;
            l.intensity = 14f; l.range = 16f;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            l.enabled = !AtmosphereSetup.Day;   // street lamps are off in daylight
            l.shadowStrength = 0.9f; l.shadowBias = 0.04f; l.shadowNormalBias = 0.3f;

            // Faint visible cone of light in the damp air.
            var cone = new GameObject("Cone"); cone.transform.SetParent(lg.transform, false);
            cone.AddComponent<MeshFilter>().sharedMesh = ConeMesh(poleH - 0.3f, 3.4f);
            var mr = cone.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Mat("cone");
            mr.enabled = !AtmosphereSetup.Day;
            mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        static Mesh ConeMesh(float height, float baseRadius)
        {
            const int seg = 24;
            var v = new List<Vector3> { Vector3.zero };
            var uv = new List<Vector2> { new Vector2(0.5f, 1f) };
            var t = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                v.Add(new Vector3(Mathf.Cos(a) * baseRadius, Mathf.Sin(a) * baseRadius, height));
                uv.Add(new Vector2(i / (float)seg, 0f));
            }
            for (int i = 1; i <= seg; i++) { t.AddRange(new[] { 0, i, i + 1 }); t.AddRange(new[] { 0, i + 1, i }); }
            var m = new Mesh { name = "LampCone" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateBounds();
            return m;
        }

        static void AddLawn(Transform parent, bool north, List<Vector4> houseRects, float inner, float rearZ, float lotW)
        {
            var go = new GameObject(north ? "Lawn_North" : "Lawn_South");
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var lawn = go.AddComponent<ProceduralGrass>();
            float sgn = north ? 1f : -1f;
            lawn.min = new Vector2(-lotW * 2.5f, north ? inner + 0.2f : -rearZ);
            lawn.max = new Vector2(lotW * 2.5f, north ? rearZ : -(inner + 0.2f));
            lawn.groundY = 0f;
            lawn.bladesPerSquareMetre = 95f;
            lawn.bladeWidth = 0.05f;
            lawn.heightRange = new Vector2(0.16f, 0.38f);
            lawn.chunkSize = 10f;
            foreach (var r in houseRects) lawn.exclusions.Add(r);
            for (int i = -2; i <= 2; i++)   // walkways
                lawn.exclusions.Add(new Vector4(i * lotW - 0.9f, -50f, i * lotW + 0.9f, 50f));
            go.SetActive(true);   // builds the blades once, with the settings above
        }

        // ============================================================ materials and helpers

        static void ConfigureTextures()
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var f in Directory.GetFiles(Dir + "/Textures", "*.*", SearchOption.AllDirectories))
                {
                    if (Path.GetExtension(f).ToLower() != ".png") continue;
                    string p = f.Replace('\\', '/');
                    var imp = AssetImporter.GetAtPath(p) as TextureImporter;
                    if (imp == null) continue;
                    string n = Path.GetFileNameWithoutExtension(p).ToLower();
                    bool changed = false;
                    bool normal = n.Contains("normal");
                    var wantType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    if (imp.textureType != wantType) { imp.textureType = wantType; changed = true; }
                    bool linear = n.EndsWith("-ao") || n.EndsWith("_ao") || n.Contains("height") || n.Contains("metallic") ||
                                  n.Contains("smooth") || n.Contains("rough");
                    if (!normal && imp.sRGBTexture == linear) { imp.sRGBTexture = !linear; changed = true; }
                    if (imp.maxTextureSize > 1024) { imp.maxTextureSize = 1024; changed = true; }
                    if (changed) imp.SaveAndReimport();
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh();
        }

        static Texture2D Tex(string folder, string file)
        {
            string d = $"{Dir}/Textures/{folder}";
            if (!Directory.Exists(d)) return null;
            foreach (var f in Directory.GetFiles(d))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (Path.GetExtension(f).ToLower() == ".png" && string.Equals(name, file, System.StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(f.Replace('\\', '/'));
            }
            return null;
        }

        static void MakeMaterials()
        {
            Directory.CreateDirectory(MatDir);
            var sidingTints = new[]
            {
                new Color(0.80f, 0.82f, 0.85f), new Color(0.78f, 0.72f, 0.60f), new Color(0.62f, 0.70f, 0.66f),
                new Color(0.84f, 0.82f, 0.76f), new Color(0.60f, 0.64f, 0.74f),
            };
            for (int i = 0; i < sidingTints.Length; i++)
                Pbr("siding" + i, "AluminiumSiding", "AluminiumSiding", "AluminiumSiding-normal", "AluminiumSiding-ao", sidingTints[i], 0.25f);
            Pbr("shingle", "RoofShingles", "RoofShingles", "RoofShingles-normal", "RoofShingles-ao", new Color(0.55f, 0.55f, 0.58f), 0.15f);
            Pbr("brick", "Brick", "Brick", "Brick-normal", "Brick-ao", new Color(0.78f, 0.70f, 0.66f), 0.1f);
            Pbr("carpet", "Carpet", "Carpet", "Carpet-normal", "Carpet-ao", new Color(0.8f, 0.78f, 0.74f), 0.05f);
            Pbr("hardwood", "HardwoodFloor-Oak", "HardwoodFloorOak", "HardwoodFloorOak-normal", "HardwoodFloorOak-ao", Color.white, 0.4f);
            Pbr("marble", "MarbleTile", "MarbleTile", "MarbleTile-normal", "MarbleTile-ao", Color.white, 0.6f);
            Pbr("oak", "Oak", "Oak", "Oak-normal", "Oak-ao", new Color(0.85f, 0.8f, 0.75f), 0.3f);
            Pbr("white", "PaintedMetal", "PaintedMetal-white", "PaintedMetal-normal", "PaintedMetal-ao", new Color(0.9f, 0.9f, 0.9f), 0.35f);
            var doorCols = new[] { new Color(0.10f, 0.22f, 0.15f), new Color(0.82f, 0.82f, 0.78f), new Color(0.30f, 0.38f, 0.45f), new Color(0.30f, 0.10f, 0.09f), new Color(0.08f, 0.08f, 0.09f) };
            for (int di = 0; di < doorCols.Length; di++) Pbr("door" + di, "PaintedMetal", "PaintedMetal-white", "PaintedMetal-normal", "PaintedMetal-ao", doorCols[di], 0.3f);
            var shingleCols = new[] { new Color(0.55f, 0.55f, 0.58f), new Color(0.46f, 0.38f, 0.32f), new Color(0.33f, 0.35f, 0.37f) };
            for (int si = 0; si < shingleCols.Length; si++) Pbr("shingle" + si, "RoofShingles", "RoofShingles", "RoofShingles-normal", "RoofShingles-ao", shingleCols[si], 0.15f);
            var brickCols = new[] { new Color(0.78f, 0.70f, 0.66f), new Color(0.66f, 0.58f, 0.5f), new Color(0.70f, 0.70f, 0.72f) };
            for (int bi = 0; bi < brickCols.Length; bi++) Pbr("brick" + bi, "Brick", "Brick", "Brick-normal", "Brick-ao", brickCols[bi], 0.1f);
            Pbr("wall", "PaintedWall-Emerald", "PaintedWall-White", "PaintedWall-normal", null, new Color(0.93f, 0.89f, 0.82f), 0.1f);
            Pbr("brass", "Brass", "Brass_001_albedo", "Brass_001_normal", null, Color.white, 0.7f);
            Pbr("castiron", "CastIron", "CastIron", "CastIron-normal", "CastIron-ao", new Color(0.35f, 0.35f, 0.37f), 0.4f);

            Glass("glass", new Color(0.06f, 0.09f, 0.12f, 0.35f), Color.black);
            Glass("glassLit", new Color(0.5f, 0.35f, 0.18f, 0.6f), new Color(1.0f, 0.68f, 0.35f) * 1.6f);
            Emissive("shade", new Color(0.85f, 0.8f, 0.7f), Color.black);
            Emissive("streetGlow", new Color(0.4f, 0.28f, 0.14f), new Color(1.0f, 0.66f, 0.30f) * 3f);
            Plain("switchWhite", new Color(0.88f, 0.88f, 0.86f), 0.4f);
            Plain("hedge", new Color(0.16f, 0.30f, 0.12f), 0.05f);
            var woodSrc = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Materials/M_Fence_Wood.mat");
            var fenceTints = new[] { new Color(0.78f, 0.62f, 0.44f), new Color(0.34f, 0.26f, 0.2f), new Color(0.62f, 0.6f, 0.57f), new Color(0.86f, 0.86f, 0.84f) };   // cedar, dark stain, weathered grey, white paint
            for (int fi = 0; fi < fenceTints.Length; fi++)
            {
                var fm = Copy("fence" + fi, woodSrc);
                fm.SetColor("_BaseColor", fenceTints[fi]); fm.color = fenceTints[fi];
                fm.mainTextureScale = new Vector2(1f, 1.2f);
            }
            var fence = mats["fence0"];
            fence.mainTextureScale = new Vector2(1f, 1.2f);
            Cone();
        }

        static Material NewMat(string key, Shader shader)
        {
            string path = $"{MatDir}/N_{key}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            else m.shader = shader;
            mats[key] = m;
            return m;
        }

        static Shader Lit => Shader.Find("Universal Render Pipeline/Lit");

        static void Pbr(string key, string folder, string albedo, string normal, string ao, Color tint, float smooth)
        {
            var m = NewMat(key, Lit);
            m.SetTexture("_BaseMap", Tex(folder, albedo));
            m.SetColor("_BaseColor", tint);
            m.SetFloat("_Smoothness", smooth);
            var nt = normal != null ? Tex(folder, normal) : null;
            if (nt != null) { m.SetTexture("_BumpMap", nt); m.EnableKeyword("_NORMALMAP"); m.SetFloat("_BumpScale", 0.8f); }
            var at = ao != null ? Tex(folder, ao) : null;
            if (at != null) { m.SetTexture("_OcclusionMap", at); m.EnableKeyword("_OCCLUSIONMAP"); m.SetFloat("_OcclusionStrength", 0.8f); }
            EditorUtility.SetDirty(m);
        }

        static void Plain(string key, Color c, float smooth)
        {
            var m = NewMat(key, Lit);
            m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
        }

        static Material FlatMat(string key, Color c, float smooth) { Plain(key, c, smooth); return mats[key]; }

        static void Emissive(string key, Color baseC, Color emission)
        {
            var m = NewMat(key, Lit);
            m.SetColor("_BaseColor", baseC); m.SetFloat("_Smoothness", 0.3f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", emission);
            EditorUtility.SetDirty(m);
        }

        static void Glass(string key, Color c, Color emission)
        {
            var m = NewMat(key, Lit);
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", 0.95f);
            if (emission != Color.black)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                m.SetColor("_EmissionColor", emission);
            }
            EditorUtility.SetDirty(m);
        }

        static void Cone()
        {
            var m = NewMat("cone", Shader.Find("Universal Render Pipeline/Unlit"));
            const int size = 16;
            string texPath = MatDir + "/T_LampCone.png";
            var tex = new Texture2D(1, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            {
                float f = y / (float)(size - 1);
                tex.SetPixel(0, y, new Color(1f, 0.72f, 0.40f, Mathf.Pow(f, 2.2f) * 0.2f));
            }
            tex.Apply();
            File.WriteAllBytes(texPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(texPath);
            imp.wrapMode = TextureWrapMode.Clamp; imp.alphaIsTransparency = true; imp.mipmapEnabled = false;
            imp.SaveAndReimport();
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 2f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.One);
            m.SetFloat("_ZWrite", 0f); m.SetFloat("_Cull", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(m);
        }

        static Material Copy(string key, Material src)
        {
            string path = $"{MatDir}/N_{key}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(src); AssetDatabase.CreateAsset(m, path); }
            else m.CopyPropertiesFromMaterial(src);
            mats[key] = m;
            return m;
        }

        /// <summary>
        /// A material from a downloaded ambientCG set (colour, normal and occlusion maps), tiled so one
        /// repeat covers roughly <paramref name="metresPerTile"/> metres of a surface of the given size.
        /// Slabs are cubes whose UVs span each whole face once, so the tiling has to be set per size.
        /// </summary>
        static Material AcTiled(string key, string folder, string name, Color tint, float smooth, float wMetres, float dMetres, float metresPerTile)
        {
            string k = $"{key}_{Mathf.RoundToInt(wMetres)}x{Mathf.RoundToInt(dMetres * 10)}";
            Pbr(k, folder, name + "-albedo", name + "-normal", name + "-ao", tint, smooth);
            var m = mats[k];
            m.mainTextureScale = new Vector2(wMetres / metresPerTile, dMetres / metresPerTile);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material Tiled(string key, string source, float tilesX, float tilesY)
        {
            var m = Copy(key, AssetDatabase.LoadAssetAtPath<Material>($"Assets/_Project/Materials/{source}.mat"));
            m.mainTextureScale = new Vector2(tilesX, tilesY);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material Mat(string key) => mats.TryGetValue(key, out var m) ? m : mats["white"];

        static GameObject Load(string fbx) =>
            AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/Meshes/{fbx}.fbx");

        static void Slab(Transform parent, string name, Vector3 centre, Vector3 size, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.transform.SetParent(parent, false);
            go.transform.position = centre; go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
            go.isStatic = true;
        }

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static Bounds Bounds(GameObject unused, List<Renderer> rs)
        {
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static float FloorTop(GameObject root)
        {
            var f = root.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name.ToLower() == "living room floor");
            return f != null ? f.bounds.max.y : Bounds(root).min.y;
        }

        static void SnapshotLookAt(string path, Vector3 pos, Vector3 target, float fov) =>
            Snapshot(path, pos, Quaternion.LookRotation((target - pos).normalized, Vector3.up), fov);

        static void Snapshot(string path, Vector3 pos, Quaternion rot, float fov)
        {
            var go = new GameObject("snapCam");
            var cam = go.AddComponent<Camera>();
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.fieldOfView = fov; cam.nearClipPlane = 0.05f; cam.farClipPlane = 500f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            RenderTexture.active = null; cam.targetTexture = null;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(tex); Object.DestroyImmediate(go);
        }
    }
}
