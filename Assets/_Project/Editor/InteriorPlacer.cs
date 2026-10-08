using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using HorrorHouse;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Places furniture, light fixtures and switches from props.json and lights.json, positioned
    /// relative to the rooms in floorplan.json.
    ///
    /// Models are taken from Assets/_Project/Props/Incoming - drop anything in, in any structure.
    /// Matching is by filename keyword, and the result is written to asset_map.json so a wrong
    /// guess can be corrected once rather than fought with repeatedly.
    ///
    /// Scale is corrected automatically: every model is measured and compared against the
    /// real-world size declared in props.json, then scaled to match. This is worth doing because
    /// asset packs disagree wildly about units - the same sofa may arrive 2.4 units long or 240 -
    /// and a mis-scaled prop is the single fastest way to destroy the sense of being in a real room.
    /// What cannot be guessed is which way a model faces, so yaw corrections live in asset_map.json
    /// for you to adjust.
    /// </summary>
    public static class InteriorPlacer
    {
        const string PlanPath   = "Assets/_Project/Data/floorplan.json";
        const string PropsPath  = "Assets/_Project/Data/props.json";
        const string LightsPath = "Assets/_Project/Data/lights.json";
        const string MapPath    = "Assets/_Project/Data/asset_map.json";
        const string IncomingDir = "Assets/_Project/Props/Incoming";
        const string MatDir     = "Assets/_Project/Materials";
        const string RootName   = "INTERIOR (generated - do not edit by hand)";

        const float F = 0.3048f;
        const float CounterHeight = 3.0f;   // feet, standard worktop

        // ---------------------------------------------------------------- menu

        [MenuItem("Horror House/Stage 6 - Place Furniture and Lights", priority = 70)]
        public static void Place()
        {
            var plan = Load<HouseBuilder.Plan>(PlanPath);
            var props = Load<PropFile>(PropsPath);
            var lights = Load<LightFile>(LightsPath);
            if (plan == null || props == null || lights == null) return;

            var rooms = plan.rooms.ToDictionary(r => r.name);

            // Lights and switches are fixtures, not fillers, so they are placed either way.
            var library = props.placeProps ? AssetLibrary.Build(props.props) : null;

            var old = GameObject.Find(RootName);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Place Interior");

            var propsRoot = Child(root, "Props");
            var lightsRoot = Child(root, "Lights");
            var switchRoot = Child(root, "Switches");

            int placed = 0, placeholders = 0, skipped = 0;

            foreach (var p in props.placeProps ? props.props : new PropDef[0])
            {
                // props.json carries documentation objects between groups of entries; they have
                // no id and should pass through silently rather than being reported as errors.
                if (string.IsNullOrEmpty(p.id) || string.IsNullOrEmpty(p.room)) continue;

                if (!rooms.TryGetValue(p.room, out var room))
                {
                    Debug.LogWarning($"[Interior] Prop '{p.id}' names room '{p.room}', which does not exist.");
                    skipped++;
                    continue;
                }
                bool real = PlaceProp(p, room, plan, propsRoot, library);
                if (real) placed++; else placeholders++;
            }

            foreach (var f in lights.fixtures)
            {
                if (!rooms.TryGetValue(f.room, out var room)) { skipped++; continue; }
                PlaceFixture(f, room, plan, lightsRoot);
            }

            foreach (var s in lights.switches)
            {
                if (!rooms.TryGetValue(s.room, out var room)) { skipped++; continue; }
                PlaceSwitch(s, room, switchRoot);
            }

            library?.Save();
            Selection.activeGameObject = root;

            if (!props.placeProps)
            {
                Debug.Log($"[Interior] Props are OFF (placeProps is false in props.json). " +
                          $"Placed {lights.fixtures.Length} light fixtures and {lights.switches.Length} " +
                          $"switches only. The furniture layout is still there - set placeProps to " +
                          $"true to bring it all back.");
                return;
            }

            Debug.Log($"[Interior] {placed} props from your models, {placeholders} placeholders, " +
                      $"{lights.fixtures.Length} light fixtures, {lights.switches.Length} switches." +
                      (placeholders > 0
                          ? $"\nPlaceholders are correctly sized grey boxes. Drop matching models into " +
                            $"{IncomingDir} and run this again - or point them manually in asset_map.json."
                          : ""));
        }

        // ---------------------------------------------------------------- props

        static bool PlaceProp(PropDef p, HouseBuilder.Room room, HouseBuilder.Plan plan,
                              GameObject parent, AssetLibrary library)
        {
            var entry = library.Resolve(p.asset);
            var size = p.SizeFeet;

            GameObject go = null;
            bool real = false;

            if (entry?.Prefab != null)
            {
                go = (GameObject)PrefabUtility.InstantiatePrefab(entry.Prefab, parent.transform);
                if (go != null) real = true;
            }

            if (go == null)
            {
                go = Placeholder(p.id, size);
                go.transform.SetParent(parent.transform, false);
            }

            go.name = p.id + (real ? "" : " (placeholder)");

            // Order matters. Scale first, because the measurement used to sit the object on the
            // floor is only meaningful once it is the right size; then place it; then drop it onto
            // the floor. Sitting it down before positioning would simply be overwritten.
            if (real) NormaliseScale(go, size, entry);

            var (pos, yaw) = Resolve(p, room, plan, size);
            go.transform.localPosition = pos * F;
            go.transform.localRotation = Quaternion.Euler(0f, yaw + p.yaw + (entry?.yawCorrection ?? 0f), 0f);

            if (real && !IsHung(p)) SitOnSurface(go, pos.y * F);

            EnsureCollider(go);
            return real;
        }

        /// <summary>
        /// Works out where a prop stands and which way it faces.
        /// Wall-backed props are inset by half their own depth so they touch the wall rather than
        /// embedding in it, and are turned to face into the room.
        /// </summary>
        static (Vector3 pos, float yaw) Resolve(PropDef p, HouseBuilder.Room room,
                                                HouseBuilder.Plan plan, Vector3 size)
        {
            float ceiling = room.ceiling > 0.01f ? room.ceiling : plan.construction.defaultCeilingHeight;
            float x, z, y = 0f, yaw = 0f;
            float halfDepth = size.z * 0.5f + p.offsetFromWall;

            switch ((p.placement ?? "floor").ToLowerInvariant())
            {
                case "wall":
                    switch ((p.wall ?? "N").ToUpperInvariant())
                    {
                        case "N": x = room.x + p.along * room.w; z = room.z + room.d - halfDepth; yaw = 180f; break;
                        case "S": x = room.x + p.along * room.w; z = room.z + halfDepth;          yaw = 0f;   break;
                        case "E": z = room.z + p.along * room.d; x = room.x + room.w - halfDepth; yaw = -90f; break;
                        default:  z = room.z + p.along * room.d; x = room.x + halfDepth;          yaw = 90f;  break;
                    }
                    y = p.height;
                    break;

                case "ceiling":
                    x = room.x + p.u * room.w;
                    z = room.z + p.v * room.d;
                    // Hang from the ceiling: the top of the object meets it.
                    y = p.height > 0.01f ? p.height : ceiling - size.y;
                    break;

                case "counter":
                    x = room.x + p.u * room.w;
                    z = room.z + p.v * room.d;
                    y = p.height > 0.01f ? p.height : CounterHeight;
                    break;

                default:   // floor
                    x = room.x + p.u * room.w;
                    z = room.z + p.v * room.d;
                    y = p.height;
                    break;
            }

            return (new Vector3(x, y, z), yaw);
        }

        /// <summary>
        /// Scales an imported model to its declared real-world size and drops it so its base sits
        /// on the floor. Height is the more reliable axis to match on, because a model's facing is
        /// unknown and its width and depth may therefore be swapped.
        /// </summary>
        static void NormaliseScale(GameObject go, Vector3 sizeFeet, AssetEntry entry)
        {
            if (entry.scaleMultiplier > 0.0001f)
            {
                go.transform.localScale = Vector3.one * entry.scaleMultiplier;
                return;
            }

            var bounds = MeasureBounds(go);
            if (bounds.size.magnitude < 0.0001f) return;

            float wanted, measured;
            if (sizeFeet.y >= 0.5f && bounds.size.y > 0.001f)
            {
                wanted = sizeFeet.y * F;  measured = bounds.size.y;
            }
            else
            {
                // Flat things - rugs, mats - have no useful height, so match the footprint instead.
                wanted = Mathf.Max(sizeFeet.x, sizeFeet.z) * F;
                measured = Mathf.Max(bounds.size.x, bounds.size.z);
            }
            if (measured < 0.0001f) return;

            float scale = Mathf.Clamp(wanted / measured, 0.002f, 500f);
            go.transform.localScale = Vector3.one * scale;

            if (scale < 0.2f || scale > 5f)
                Debug.Log($"[Interior] '{go.name}' needed a {scale:0.###}x scale correction - its " +
                          $"source units differ sharply from metres. Recorded in asset_map.json.");

            entry.scaleMultiplier = scale;
        }

        /// <summary>
        /// Drops the object so the lowest point of its geometry rests at <paramref name="surfaceY"/>,
        /// whatever its pivot happens to be. Packs put pivots at the base, the centre, or somewhere
        /// arbitrary, and a chair floating two inches off the floor is instantly noticeable.
        /// </summary>
        static void SitOnSurface(GameObject go, float surfaceY)
        {
            var b = MeasureBounds(go);
            if (b.size.magnitude < 0.0001f) return;
            float lift = surfaceY - b.min.y;
            if (Mathf.Abs(lift) > 0.0005f)
                go.transform.position += new Vector3(0f, lift, 0f);
        }

        /// <summary>Ceiling-hung and wall-mounted things must not be dropped to the floor.</summary>
        static bool IsHung(PropDef p)
        {
            string mode = (p.placement ?? "floor").ToLowerInvariant();
            if (mode == "ceiling") return true;
            if (mode == "wall" && p.height > 0.01f) return true;   // e.g. a wall-mounted TV
            return false;
        }

        static Bounds MeasureBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        static GameObject Placeholder(string name, Vector3 sizeFeet)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            // Scale here is fine: a placeholder is a throwaway box, not shipped geometry.
            go.transform.localScale = new Vector3(sizeFeet.x, sizeFeet.y, sizeFeet.z) * F;

            // Unity primitives are centred on their pivot, so raise the box by half its height
            // to make the holder's origin sit at the object's base - matching how a real prop's
            // pivot is expected to behave.
            go.transform.localPosition = new Vector3(0f, sizeFeet.y * 0.5f * F, 0f);

            var holder = new GameObject(name);
            go.transform.SetParent(holder.transform, false);
            go.GetComponent<MeshRenderer>().sharedMaterial = PlaceholderMaterial();
            return holder;
        }

        static Material placeholderMat;
        static Material PlaceholderMaterial()
        {
            if (placeholderMat != null) return placeholderMat;
            string path = $"{MatDir}/M_Placeholder.mat";
            placeholderMat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (placeholderMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                placeholderMat = new Material(shader) { name = "M_Placeholder" };
                placeholderMat.SetColor("_BaseColor", new Color(0.40f, 0.42f, 0.46f));
                placeholderMat.SetFloat("_Smoothness", 0.1f);
                Directory.CreateDirectory(MatDir);
                AssetDatabase.CreateAsset(placeholderMat, path);
            }
            return placeholderMat;
        }

        static void EnsureCollider(GameObject go)
        {
            if (go.GetComponentInChildren<Collider>() != null) return;
            var b = MeasureBounds(go);
            if (b.size.magnitude < 0.0001f) return;

            var box = go.AddComponent<BoxCollider>();
            box.center = go.transform.InverseTransformPoint(b.center);
            box.size = new Vector3(
                b.size.x / Mathf.Max(go.transform.lossyScale.x, 0.0001f),
                b.size.y / Mathf.Max(go.transform.lossyScale.y, 0.0001f),
                b.size.z / Mathf.Max(go.transform.lossyScale.z, 0.0001f));
        }

        // ---------------------------------------------------------------- lights

        static void PlaceFixture(FixtureDef f, HouseBuilder.Room room, HouseBuilder.Plan plan, GameObject parent)
        {
            float ceiling = room.ceiling > 0.01f ? room.ceiling : plan.construction.defaultCeilingHeight;
            string type = (f.type ?? "ceiling").ToLowerInvariant();

            float x, z, y;
            if (!string.IsNullOrEmpty(f.wall))
            {
                switch (f.wall.ToUpperInvariant())
                {
                    case "N": x = room.x + f.along * room.w; z = room.z + room.d - 0.45f; break;
                    case "S": x = room.x + f.along * room.w; z = room.z + 0.45f;          break;
                    case "E": z = room.z + f.along * room.d; x = room.x + room.w - 0.45f; break;
                    default:  z = room.z + f.along * room.d; x = room.x + 0.45f;          break;
                }
            }
            else
            {
                x = room.x + f.u * room.w;
                z = room.z + f.v * room.d;
            }

            y = f.height > 0.01f ? f.height : type switch
            {
                "pendant" => ceiling - 3.6f,
                "strip"   => ceiling - 0.45f,
                "lamp"    => 3.4f,
                _         => ceiling - 0.55f,
            };

            var go = new GameObject($"Fixture_{f.id}");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = new Vector3(x, y, z) * F;

            // A visible body, so a lit room shows you where its light is coming from.
            if (type != "lamp")
            {
                var shade = GameObject.CreatePrimitive(PrimitiveType.Cube);
                shade.name = "Shade";
                shade.transform.SetParent(go.transform, false);
                var s = type == "strip" ? new Vector3(4.0f, 0.25f, 0.5f)
                      : type == "pendant" ? new Vector3(1.4f, 0.7f, 1.4f)
                      : type == "porch" ? new Vector3(0.6f, 1.0f, 0.6f)
                      : new Vector3(1.1f, 0.35f, 1.1f);
                shade.transform.localScale = s * F;
                shade.GetComponent<MeshRenderer>().sharedMaterial = ShadeMaterial();
                UnityEngine.Object.DestroyImmediate(shade.GetComponent<Collider>());
            }

            var lightGo = new GameObject("Light");
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, -0.35f * F, 0f);

            var light = lightGo.AddComponent<Light>();

            // A flush ceiling fitting genuinely throws light downward in a wide cone, so a spot
            // models it better than a point - and costs one shadow map instead of six. Pendants
            // and lamps are omnidirectional, so they stay points.
            bool isDirectional = type is "ceiling" or "strip" or "wall" or "porch";
            light.type = isDirectional ? LightType.Spot : LightType.Point;
            if (isDirectional)
            {
                light.spotAngle = type switch { "wall" => 110f, "porch" => 120f, _ => 130f };
                light.innerSpotAngle = light.spotAngle * 0.55f;
                lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // aim down
            }

            // Colour temperature rather than a hand-picked RGB: it is how bulbs are actually
            // specified, and it keeps the whole house consistent.
            light.useColorTemperature = true;
            light.colorTemperature = f.kelvin > 100f ? f.kelvin : 2700f;
            light.color = Color.white;

            light.intensity = f.intensity > 0.01f ? f.intensity : 1.4f;
            light.range = (f.range > 0.01f ? f.range : 14f) * F;
            light.bounceIntensity = 0.5f;

            // THE important one. A Unity light with shadows disabled passes straight through
            // walls, so a kitchen lamp quietly lights the bedroom next door and the whole house
            // reads as a single glowing box. Every interior fitting casts shadows - that is what
            // makes rooms feel separate, and what lets a lit window read as one room being awake.
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.88f;
            light.shadowBias = 0.03f;
            light.shadowNormalBias = 0.25f;
            light.shadowNearPlane = 0.08f;

            // Most lights start off, so the cost is only paid for rooms that are actually lit.
            light.renderMode = LightRenderMode.Auto;

            var fixture = go.AddComponent<LightFixture>();
            fixture.fixtureId = f.id;
            fixture.group = f.group;
            fixture.room = f.room;
            fixture.fixtureType = type;
            fixture.isOn = f.startsOn;
            light.enabled = f.startsOn;
        }

        static Material shadeMat;
        static Material ShadeMaterial()
        {
            if (shadeMat != null) return shadeMat;
            string path = $"{MatDir}/M_LightShade.mat";
            shadeMat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (shadeMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                shadeMat = new Material(shader) { name = "M_LightShade" };
                shadeMat.SetColor("_BaseColor", new Color(0.92f, 0.90f, 0.84f));
                shadeMat.SetFloat("_Smoothness", 0.3f);
                shadeMat.EnableKeyword("_EMISSION");
                shadeMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                AssetDatabase.CreateAsset(shadeMat, path);
            }
            return shadeMat;
        }

        static void PlaceSwitch(SwitchDef s, HouseBuilder.Room room, GameObject parent)
        {
            const float plateHeight = 4.0f;   // feet - standard switch height
            float x, z, yaw;

            switch ((s.wall ?? "N").ToUpperInvariant())
            {
                case "N": x = room.x + s.along * room.w; z = room.z + room.d - 0.1f; yaw = 180f; break;
                case "S": x = room.x + s.along * room.w; z = room.z + 0.1f;          yaw = 0f;   break;
                case "E": z = room.z + s.along * room.d; x = room.x + room.w - 0.1f; yaw = -90f; break;
                default:  z = room.z + s.along * room.d; x = room.x + 0.1f;          yaw = 90f;  break;
            }

            var go = new GameObject($"Switch_{s.id}");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = new Vector3(x, plateHeight, z) * F;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "Plate";
            plate.transform.SetParent(go.transform, false);
            plate.transform.localScale = new Vector3(0.23f, 0.37f, 0.04f) * F;
            plate.GetComponent<MeshRenderer>().sharedMaterial = ShadeMaterial();

            var rocker = new GameObject("Rocker");
            rocker.transform.SetParent(go.transform, false);
            var rockerMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rockerMesh.transform.SetParent(rocker.transform, false);
            rockerMesh.transform.localScale = new Vector3(0.14f, 0.20f, 0.05f) * F;
            rockerMesh.transform.localPosition = new Vector3(0f, 0f, -0.03f) * F;
            rockerMesh.GetComponent<MeshRenderer>().sharedMaterial = ShadeMaterial();
            UnityEngine.Object.DestroyImmediate(rockerMesh.GetComponent<Collider>());

            // A switch plate is about 3 inches across - far too small to aim at comfortably. This
            // invisible box gives it a hand-sized target without making the plate itself look odd.
            UnityEngine.Object.DestroyImmediate(plate.GetComponent<Collider>());
            var target = go.AddComponent<BoxCollider>();
            target.size = new Vector3(0.55f, 0.65f, 0.30f) * F;
            target.center = new Vector3(0f, 0f, -0.08f) * F;

            var sw = go.AddComponent<LightSwitch>();
            sw.switchId = s.id;
            sw.room = s.room;
            sw.controls = s.controls;
            sw.rocker = rocker.transform;
        }

        // ---------------------------------------------------------------- asset library

        /// <summary>
        /// Indexes whatever is sitting in the incoming folder and matches it to the semantic keys
        /// used by props.json. Choices are persisted so a manual correction is never overwritten.
        /// </summary>
        class AssetLibrary
        {
            readonly Dictionary<string, AssetEntry> entries = new Dictionary<string, AssetEntry>();

            /// <summary>Keywords that identify each slot. Order matters: longer, more specific first.</summary>
            static readonly Dictionary<string, string[]> Synonyms = new Dictionary<string, string[]>
            {
                ["sofa"] = new[] { "sofa", "couch", "settee" },
                ["armchair"] = new[] { "armchair", "arm_chair", "lounge_chair", "accent_chair" },
                ["coffee_table"] = new[] { "coffee_table", "coffeetable", "low_table" },
                ["rug"] = new[] { "rug", "carpet_piece", "area_rug" },
                ["tv_stand"] = new[] { "tv_stand", "tv_unit", "media_console", "sideboard" },
                ["television"] = new[] { "television", "tv_", "monitor", "screen" },
                ["floor_lamp"] = new[] { "floor_lamp", "standing_lamp", "standard_lamp" },
                ["table_lamp"] = new[] { "table_lamp", "bedside_lamp", "desk_lamp" },
                ["pendant_light"] = new[] { "pendant", "hanging_lamp", "chandelier" },
                ["bookshelf"] = new[] { "bookshelf", "bookcase", "display_shelves", "shelf_unit" },
                ["vintage_cabinet"] = new[] { "vintage_cabinet", "cabinet" },
                ["grandfather_clock"] = new[] { "grandfather_clock", "grandfather", "clock" },
                ["picture_frame"] = new[] { "picture_frame", "hanging_picture", "painting", "frame" },
                ["broom"] = new[] { "broom" },
                ["ladder"] = new[] { "ladder" },
                ["bin_bag"] = new[] { "trashbag", "bin_bag", "garbage_bag" },
                ["cutting_board"] = new[] { "cutting_board", "chopping_board" },
                ["wooden_spoon"] = new[] { "wooden_spoon", "spoon" },
                ["security_camera"] = new[] { "security_camera", "cctv", "camera" },
                ["street_lamp"] = new[] { "street_lamp", "streetlight", "lamp_post" },
                ["fir_tree"] = new[] { "fir_tree", "pine", "tree" },
                ["fern"] = new[] { "fern" },
                ["nettle"] = new[] { "nettle", "weed" },
                ["moss"] = new[] { "moss" },
                ["dining_table"] = new[] { "dining_table", "table_dining", "kitchen_table" },
                ["dining_chair"] = new[] { "dining_chair", "chair" },
                ["kitchen_island"] = new[] { "island", "kitchen_island" },
                ["counter_run"] = new[] { "counter", "worktop", "cabinet_run", "kitchen_cabinet" },
                ["kitchen_sink"] = new[] { "sink", "basin_kitchen" },
                ["dishwasher"] = new[] { "dishwasher", "dish_washer" },
                ["range_cooker"] = new[] { "range", "cooker", "stove", "oven" },
                ["range_hood"] = new[] { "hood", "extractor", "cooker_hood" },
                ["refrigerator"] = new[] { "refrigerator", "fridge", "freezer" },
                ["microwave"] = new[] { "microwave" },
                ["toaster"] = new[] { "toaster" },
                ["dish_rack"] = new[] { "dish_rack", "drainer" },
                ["rubbish_bin"] = new[] { "bin", "trash", "waste" },
                ["pantry_shelf"] = new[] { "pantry", "shelving", "shelf" },
                ["bed_king"] = new[] { "bed_king", "king_bed", "double_bed", "bed" },
                ["bed_double"] = new[] { "bed_double", "double_bed", "bed" },
                ["nightstand"] = new[] { "nightstand", "bedside", "night_stand" },
                ["dresser"] = new[] { "dresser", "chest_of_drawers", "drawers", "commode" },
                ["desk"] = new[] { "desk", "writing_table" },
                ["desk_chair"] = new[] { "office_chair", "desk_chair", "swivel" },
                ["bathtub"] = new[] { "bathtub", "bath_tub", "tub" },
                ["bathtub_shower"] = new[] { "bathtub", "tub", "bath" },
                ["vanity_double"] = new[] { "vanity_double", "double_vanity", "vanity" },
                ["vanity_single"] = new[] { "vanity", "washbasin", "basin", "sink_bathroom" },
                ["shower_enclosure"] = new[] { "shower" },
                ["toilet"] = new[] { "toilet", "wc_", "lavatory" },
                ["wardrobe_rail"] = new[] { "wardrobe", "closet_rail", "clothes_rail", "clothes" },
                ["washing_machine"] = new[] { "washing_machine", "washer" },
                ["tumble_dryer"] = new[] { "dryer", "tumble" },
                ["laundry_basket"] = new[] { "laundry_basket", "hamper", "basket" },
                ["lockers"] = new[] { "locker", "mudroom", "cubby" },
                ["bench"] = new[] { "bench" },
                ["shoes"] = new[] { "shoe", "boots", "trainers" },
                ["console_table"] = new[] { "console_table", "hall_table", "side_table" },
                ["mail_pile"] = new[] { "mail", "letters", "envelope", "paper_stack" },
                ["car"] = new[] { "car", "sedan", "suv", "vehicle" },
                ["garage_shelving"] = new[] { "garage_shelf", "shelving", "rack" },
                ["workbench"] = new[] { "workbench", "work_bench", "tool_bench" },
                ["wheelie_bin"] = new[] { "wheelie", "dustbin", "garbage_can" },
                ["bicycle"] = new[] { "bicycle", "bike" },
                ["cardboard_boxes"] = new[] { "cardboard", "box", "crate" },
                ["outdoor_chair"] = new[] { "garden_chair", "patio_chair", "outdoor_chair", "chair" },
                ["outdoor_table"] = new[] { "garden_table", "patio_table", "outdoor_table" },
                ["doormat"] = new[] { "doormat", "mat" },
            };

            public static AssetLibrary Build(PropDef[] props)
            {
                var lib = new AssetLibrary();
                lib.LoadExisting();

                var models = FindModels();
                if (models.Count == 0)
                    Debug.Log($"[Interior] No models found in {IncomingDir}. Everything will be " +
                              $"placed as a correctly-sized placeholder - which is still a complete, " +
                              $"walkable layout.");

                // props.json interleaves documentation objects between groups of entries, and
                // those deserialise with a null asset key. Feeding one to the matcher throws.
                foreach (var key in props.Select(p => p.asset)
                                         .Where(k => !string.IsNullOrEmpty(k))
                                         .Distinct())
                {
                    if (!lib.entries.TryGetValue(key, out var entry))
                        lib.entries[key] = entry = new AssetEntry { key = key };

                    // Never override a path someone set by hand.
                    if (!string.IsNullOrEmpty(entry.prefabPath) &&
                        AssetDatabase.LoadAssetAtPath<GameObject>(entry.prefabPath) != null)
                        continue;

                    var match = BestMatch(key, models);
                    if (match != null)
                    {
                        entry.prefabPath = match;
                        entry.matchedAutomatically = true;
                    }
                }
                return lib;
            }

            static List<string> FindModels()
            {
                if (!AssetDatabase.IsValidFolder(IncomingDir)) return new List<string>();
                return AssetDatabase.FindAssets("t:GameObject", new[] { IncomingDir })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
            }

            static string BestMatch(string key, List<string> models)
            {
                if (!Synonyms.TryGetValue(key, out var words)) words = new[] { key };

                string best = null;
                int bestScore = 0;
                foreach (var path in models)
                {
                    string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    var tokens = name.Split(new[] { '_', '-', ' ', '.' },
                                            StringSplitOptions.RemoveEmptyEntries);

                    for (int i = 0; i < words.Length; i++)
                    {
                        if (!Matches(name, tokens, words[i])) continue;
                        // Earlier synonyms are more specific, and longer matches beat shorter.
                        int score = (words.Length - i) * 100 + words[i].Length;
                        if (score > bestScore) { bestScore = score; best = path; }
                    }
                }
                return best;
            }

            /// <summary>
            /// Matches on whole words rather than raw substrings.
            ///
            /// A plain Contains() check is badly wrong here: "bin" appears inside "cabinet", so a
            /// vintage cabinet was being picked as the kitchen bin. Multi-word keys still match
            /// loosely, since filenames vary in how they join words, but a single word must line
            /// up with a whole token in the filename.
            /// </summary>
            static bool Matches(string name, string[] tokens, string word)
            {
                if (word.Contains('_')) return name.Contains(word);

                foreach (var t in tokens)
                {
                    if (t == word) return true;
                    // Allow simple plurals and trailing digits: "shelves"/"shelf", "sofa03".
                    if (t.Length > word.Length && t.StartsWith(word) &&
                        t.Substring(word.Length).All(c => char.IsDigit(c) || c == 's'))
                        return true;
                }
                return false;
            }

            public AssetEntry Resolve(string key)
            {
                if (!entries.TryGetValue(key, out var e)) return null;
                if (e.Prefab == null && !string.IsNullOrEmpty(e.prefabPath))
                    e.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(e.prefabPath);
                return e;
            }

            void LoadExisting()
            {
                if (!File.Exists(MapPath)) return;
                try
                {
                    var file = JsonUtility.FromJson<AssetMapFile>(File.ReadAllText(MapPath));
                    if (file?.assets == null) return;
                    foreach (var a in file.assets) entries[a.key] = a;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Interior] asset_map.json could not be read ({e.Message}); " +
                                     $"starting a fresh one.");
                }
            }

            public void Save()
            {
                var file = new AssetMapFile { assets = entries.Values.OrderBy(a => a.key).ToArray() };
                File.WriteAllText(MapPath, JsonUtility.ToJson(file, true));
                AssetDatabase.ImportAsset(MapPath);
            }
        }

        // ---------------------------------------------------------------- plumbing

        static GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        static T Load<T>(string path) where T : class
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) { Debug.LogError($"[Interior] Missing {path}."); return null; }
            try { return JsonUtility.FromJson<T>(asset.text); }
            catch (Exception e) { Debug.LogError($"[Interior] {path} is not valid JSON: {e.Message}"); return null; }
        }

        // ---------------------------------------------------------------- data

        [Serializable] class PropFile { public bool placeProps = true; public PropDef[] props; }
        [Serializable] class LightFile { public FixtureDef[] fixtures; public SwitchDef[] switches; }
        [Serializable] class AssetMapFile { public AssetEntry[] assets; }

        [Serializable] public class PropDef
        {
            public string id, asset, room, placement, wall, note;
            public float along, u, v, offsetFromWall, yaw, height;
            public float[] sizeFeet;
            public Vector3 SizeFeet => sizeFeet != null && sizeFeet.Length >= 3
                ? new Vector3(sizeFeet[0], sizeFeet[2], sizeFeet[1])   // [w, d, h] -> (x, y, z)
                : new Vector3(2f, 2f, 2f);
        }

        [Serializable] public class FixtureDef
        {
            public string id, room, type, wall, group, note;
            public float u, v, along, height, kelvin, intensity, range;
            public bool startsOn;
        }

        [Serializable] public class SwitchDef
        {
            public string id, room, wall, controls;
            public float along;
        }

        [Serializable] public class AssetEntry
        {
            public string key;
            public string prefabPath;
            public bool matchedAutomatically;
            [Tooltip("Degrees added to this asset's rotation. Use when a model faces the wrong way.")]
            public float yawCorrection;
            [Tooltip("Leave 0 for automatic scaling from the declared real-world size.")]
            public float scaleMultiplier;

            [NonSerialized] public GameObject Prefab;
        }
    }
}
