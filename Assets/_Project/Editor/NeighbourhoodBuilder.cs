using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Fills the world beyond the lot: a road behind the house, a row of neighbouring houses
    /// across it, a second row across the front street, and street lamps with real shadow-casting
    /// spot lights.
    ///
    /// The neighbours are deliberately simple and mostly dark. They exist so the player looks out
    /// of a window and sees a street that is somebody's neighbourhood, not the edge of the world;
    /// a few lit windows say "people live here" without drawing the eye. Fog hides the rest.
    ///
    /// Everything is generated under one root, "Neighbourhood", which is destroyed and rebuilt on
    /// each run, so this is safe to re-run after changing the numbers below.
    /// </summary>
    public static class NeighbourhoodBuilder
    {
        const float F = 0.3048f;
        const string RootName = "Neighbourhood";
        const string MatDir = "Assets/_Project/Materials";

        // Rear road (north of the lot, which ends at z = 64 ft).
        const float RearSidewalkZ = 64f, SidewalkW = 4f, RoadW = 22f;
        const float RearRoadZ = RearSidewalkZ + SidewalkW;                 // 68
        const float RearFarWalkZ = RearRoadZ + RoadW;                      // 90
        const float RearHouseFrontZ = RearFarWalkZ + SidewalkW + 14f;      // 108

        // Front street already exists at z = -48 .. -40; houses sit beyond it.
        const float FrontHouseFrontZ = -48f - 18f;                         // -66

        static readonly float[] HouseX = { -70f, -22f, 28f, 80f, 132f };
        static readonly Color[] Siding =
        {
            new Color(0.34f, 0.36f, 0.40f), new Color(0.40f, 0.36f, 0.30f),
            new Color(0.28f, 0.33f, 0.31f), new Color(0.42f, 0.41f, 0.38f),
            new Color(0.30f, 0.30f, 0.36f),
        };

        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        [MenuItem("Horror House/Add Street and Neighbourhood", priority = 63)]
        public static void Build()
        {
            var old = GameObject.Find(RootName);
            if (old != null) Object.DestroyImmediate(old);
            mats.Clear();

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Neighbourhood");

            BuildGround(root.transform);
            BuildRearRoad(root.transform);

            var rng = new System.Random(4172026);
            for (int i = 0; i < HouseX.Length; i++)
            {
                BuildHouse(root.transform, $"RearHouse_{i}", HouseX[i], RearHouseFrontZ, 0f, i, rng);
                BuildTree(root.transform, HouseX[i] + (i % 2 == 0 ? -14f : 15f), RearHouseFrontZ - 12f, rng);
            }
            for (int i = 0; i < HouseX.Length; i++)
            {
                float x = HouseX[i] + 14f;
                BuildHouse(root.transform, $"FrontHouse_{i}", x, FrontHouseFrontZ, 180f, i + 2, rng);
                BuildTree(root.transform, x + (i % 2 == 0 ? 18f : -17f), FrontHouseFrontZ + 11f, rng);
            }

            // Street lamps. Near sidewalk of the rear road, far sidewalk, and the front street.
            var lamps = new GameObject("StreetLamps"); lamps.transform.SetParent(root.transform, false);
            BuildLamp(lamps.transform, "Lamp_R1",  -2f, RearRoadZ - 1.5f,  +1);
            BuildLamp(lamps.transform, "Lamp_R2",  62f, RearRoadZ - 1.5f,  +1);
            BuildLamp(lamps.transform, "Lamp_R3",  30f, RearFarWalkZ + 1.5f, -1);
            BuildLamp(lamps.transform, "Lamp_R4", 100f, RearFarWalkZ + 1.5f, -1);
            BuildLamp(lamps.transform, "Lamp_F1",  -6f, -36.5f, -1);
            BuildLamp(lamps.transform, "Lamp_F2",  64f, -36.5f, -1);

            Selection.activeGameObject = root;
            Debug.Log("[Neighbourhood] Rear road, 10 neighbouring houses and 6 street lamps built.");
        }

        // ------------------------------------------------------------ ground and road

        static void BuildGround(Transform parent)
        {
            // Lawn beyond the lot, front and back, and out to the sides so no void shows.
            Slab(parent, "Yard_Rear",  -320f, 64f,  700f, 330f, -0.55f, "M_Grass", 14f);
            Slab(parent, "Yard_Front", -320f, -400f, 700f, 352f, -0.55f, "M_Grass", 14f);
            Slab(parent, "Yard_West",  -320f, -48f, 288f, 112f, -0.55f, "M_Grass", 14f);
            Slab(parent, "Yard_East",    92f, -48f, 288f, 112f, -0.55f, "M_Grass", 14f);
        }

        static void BuildRearRoad(Transform parent)
        {
            const float x0 = -320f, w = 700f;
            Slab(parent, "Rear_Sidewalk_Near", x0, RearSidewalkZ, w, SidewalkW, -0.28f, "M_Paving", 2.2f);
            Slab(parent, "Rear_Road",          x0, RearRoadZ,     w, RoadW,     -0.30f, "M_Asphalt", 4f);
            Slab(parent, "Rear_Sidewalk_Far",  x0, RearFarWalkZ,  w, SidewalkW, -0.28f, "M_Paving", 2.2f);

            // Broken yellow centre line.
            var line = FlatMat("N_RoadLine", new Color(0.55f, 0.48f, 0.18f), 0.2f);
            var lines = new GameObject("Rear_CentreLine"); lines.transform.SetParent(parent, false);
            for (float x = x0 + 4f; x < x0 + w; x += 18f)
            {
                var d = Cube(lines.transform, "Dash", new Vector3(x + 4f, -0.02f, RearRoadZ + RoadW * 0.5f), new Vector3(8f, 0.04f, 0.5f), line);
                Object.DestroyImmediate(d.GetComponent<Collider>());
            }
        }

        static GameObject Slab(Transform parent, string name, float x, float z, float w, float d, float centreY, string source, float metresPerTile)
        {
            var go = Cube(parent, name, new Vector3(x + w * 0.5f, centreY, z + d * 0.5f), new Vector3(w, 1.0f, d), Tiled(source, metresPerTile, w, d));
            go.isStatic = true;
            return go;
        }


        // ------------------------------------------------------------ houses

        static void BuildHouse(Transform parent, string name, float cx, float frontZ, float yaw, int style, System.Random rng)
        {
            float w = 38f + (style % 3) * 5f, d = 28f, h = 10f;
            var house = new GameObject(name);
            house.transform.SetParent(parent, false);
            // Local frame: front face at local z = 0 facing -Z, body extends to +Z.
            // Yaw 0 -> front faces south (rear-road houses). Yaw 180 -> front faces north.
            house.transform.position = new Vector3(cx * F, 0f, (yaw == 0f ? frontZ + 0f : frontZ) * F);
            house.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            var siding = TintedFrom("M_Siding", $"N_Siding_{style % Siding.Length}", Siding[style % Siding.Length]);
            var trim = Get("M_Trim");
            var shingle = TintedFrom("M_Roof_Shingle", "N_Roof_Dark", new Color(0.22f, 0.22f, 0.24f));
            var door = Get("M_Door_Painted");

            Local(house.transform, "Body", new Vector3(0, h * 0.5f, d * 0.5f), new Vector3(w, h, d), siding);

            // Gable roof, ridge along X.
            var roofGo = new GameObject("Roof");
            roofGo.transform.SetParent(house.transform, false);
            roofGo.transform.localPosition = new Vector3(0f, h * F, d * 0.5f * F);
            var mesh = GableMesh(w * F, d * F, 7.5f * F, 1.4f * F);
            roofGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            roofGo.AddComponent<MeshRenderer>().sharedMaterials = new[] { shingle, siding };
            roofGo.isStatic = true;

            // Door, offset from centre like a real facade, with a porch lamp glow.
            float doorX = -w * 0.18f;
            Local(house.transform, "Door", new Vector3(doorX, 3.4f, -0.15f), new Vector3(3f, 6.8f, 0.3f), door);
            Local(house.transform, "PorchLamp", new Vector3(doorX + 2.4f, 6.6f, -0.35f), new Vector3(0.5f, 0.8f, 0.5f),
                  Glow("N_Glow_Porch", new Color(1.0f, 0.62f, 0.28f), 2.2f));

            // Windows: some lit, most dark.
            float[] wx = { w * 0.12f, w * 0.31f, -w * 0.38f };
            for (int i = 0; i < wx.Length; i++)
            {
                bool lit = rng.NextDouble() < 0.28;
                var glass = lit ? Glow($"N_Glow_Win{(rng.Next(2))}", new Color(1.0f, 0.70f, 0.38f), 1.6f)
                                : Get("M_Glass");
                Local(house.transform, "Window", new Vector3(wx[i], 5.2f, -0.12f), new Vector3(3.8f, 4.2f, 0.25f), glass);
                Local(house.transform, "WinTrim", new Vector3(wx[i], 5.2f, -0.06f), new Vector3(4.4f, 4.8f, 0.2f), trim);
            }

            // A garage door on every other house.
            if (style % 2 == 1)
                Local(house.transform, "Garage", new Vector3(w * 0.5f - 7f, 3.5f, -0.1f), new Vector3(12f, 7f, 0.25f), trim);

            Local(house.transform, "Chimney", new Vector3(w * 0.28f, h + 6f, d * 0.55f), new Vector3(2.2f, 7f, 2.2f), shingle);

            foreach (var c in house.GetComponentsInChildren<Collider>())
                if (c.gameObject.name != "Body") Object.DestroyImmediate(c);
            house.isStatic = true;
            foreach (var t in house.GetComponentsInChildren<Transform>()) t.gameObject.isStatic = true;
        }

        /// <summary>Gable roof prism: two double-sided slopes (submesh 0) and two end triangles (submesh 1).</summary>
        static Mesh GableMesh(float w, float d, float rise, float overhang)
        {
            float x0 = -w * 0.5f - overhang, x1 = w * 0.5f + overhang;
            float z0 = -d * 0.5f - overhang, z1 = d * 0.5f + overhang;
            float low = -overhang * 0.28f;                                   // eaves droop slightly

            var v = new List<Vector3>(); var uv = new List<Vector2>(); var slope = new List<int>(); var gable = new List<int>();

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 e, bool flip)
            {
                int i = v.Count;
                v.AddRange(new[] { a, b, c, e });
                float len = Vector3.Distance(a, e) * 0.7f, wid = Vector3.Distance(a, b) * 0.7f;
                uv.AddRange(new[] { new Vector2(0, 0), new Vector2(wid, 0), new Vector2(wid, len), new Vector2(0, len) });
                if (!flip) slope.AddRange(new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
                else       slope.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            // South slope (eave at z0, ridge at z=0), north slope (eave at z1).
            var sa = new Vector3(x0, low, z0); var sb = new Vector3(x1, low, z0);
            var sc = new Vector3(x1, rise, 0f); var sd = new Vector3(x0, rise, 0f);
            Quad(sa, sb, sc, sd, false); Quad(sa, sb, sc, sd, true);
            var na = new Vector3(x1, low, z1); var nb = new Vector3(x0, low, z1);
            var nc = new Vector3(x0, rise, 0f); var nd = new Vector3(x1, rise, 0f);
            Quad(na, nb, nc, nd, false); Quad(na, nb, nc, nd, true);

            // Gable ends at body width.
            float gx0 = -w * 0.5f, gx1 = w * 0.5f, gz = d * 0.5f;
            float gr = rise * (gz / (gz + overhang));
            void Tri(float x, bool flip)
            {
                int i = v.Count;
                v.AddRange(new[] { new Vector3(x, 0, -gz), new Vector3(x, 0, gz), new Vector3(x, gr, 0) });
                uv.AddRange(new[] { new Vector2(0, 0), new Vector2(d, 0), new Vector2(d * 0.5f, gr) });
                if (!flip) gable.AddRange(new[] { i, i + 1, i + 2 }); else gable.AddRange(new[] { i, i + 2, i + 1 });
            }
            Tri(gx0, true); Tri(gx0, false); Tri(gx1, false); Tri(gx1, true);

            var m = new Mesh { name = "GableRoof", subMeshCount = 2 };
            m.SetVertices(v); m.SetUVs(0, uv);
            m.SetTriangles(slope, 0); m.SetTriangles(gable, 1);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static void BuildTree(Transform parent, float x, float z, System.Random rng)
        {
            var tree = new GameObject("Tree"); tree.transform.SetParent(parent, false);
            tree.transform.position = new Vector3(x * F, 0, z * F);
            float s = 0.85f + (float)rng.NextDouble() * 0.5f;
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.transform.SetParent(tree.transform, false);
            trunk.transform.localPosition = new Vector3(0, 1.6f * s, 0);
            trunk.transform.localScale = new Vector3(0.35f * s, 1.6f * s, 0.35f * s);
            trunk.GetComponent<Renderer>().sharedMaterial = FlatMat("N_Bark", new Color(0.10f, 0.08f, 0.06f), 0.1f);
            Object.DestroyImmediate(trunk.GetComponent<Collider>());
            var crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            crown.transform.SetParent(tree.transform, false);
            crown.transform.localPosition = new Vector3(0, 4.6f * s, 0);
            crown.transform.localScale = new Vector3(3.8f * s, 3.4f * s, 3.8f * s);
            crown.GetComponent<Renderer>().sharedMaterial = FlatMat("N_Foliage", new Color(0.05f, 0.09f, 0.05f), 0.05f);
            Object.DestroyImmediate(crown.GetComponent<Collider>());
        }

        // ------------------------------------------------------------ street lamps

        /// <param name="armDir">+1 arm reaches toward +Z, -1 toward -Z.</param>
        static void BuildLamp(Transform parent, string name, float xFt, float zFt, int armDir)
        {
            var lamp = new GameObject(name); lamp.transform.SetParent(parent, false);
            lamp.transform.position = new Vector3(xFt * F, 0, zFt * F);

            var metal = Get("M_Metal");
            const float poleH = 4.9f, armLen = 1.5f;

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole"; pole.transform.SetParent(lamp.transform, false);
            pole.transform.localPosition = new Vector3(0, poleH * 0.5f, 0);
            pole.transform.localScale = new Vector3(0.14f, poleH * 0.5f, 0.14f);
            pole.GetComponent<Renderer>().sharedMaterial = metal;

            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.name = "Arm"; arm.transform.SetParent(lamp.transform, false);
            arm.transform.localPosition = new Vector3(0, poleH, armDir * armLen * 0.5f);
            arm.transform.localScale = new Vector3(0.09f, 0.09f, armLen);
            arm.GetComponent<Renderer>().sharedMaterial = metal;
            Object.DestroyImmediate(arm.GetComponent<Collider>());

            var headPos = new Vector3(0, poleH - 0.06f, armDir * armLen);
            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.name = "Head"; head.transform.SetParent(lamp.transform, false);
            head.transform.localPosition = headPos;
            head.transform.localScale = new Vector3(0.38f, 0.12f, 0.7f);
            head.GetComponent<Renderer>().sharedMaterial = Glow("N_Glow_Street", new Color(1f, 0.66f, 0.30f), 3.0f);
            Object.DestroyImmediate(head.GetComponent<Collider>());

            var lgo = new GameObject("Light"); lgo.transform.SetParent(lamp.transform, false);
            lgo.transform.localPosition = headPos + Vector3.down * 0.1f;
            lgo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Spot;
            l.spotAngle = 105f; l.innerSpotAngle = 45f;
            l.useColorTemperature = true; l.colorTemperature = 2300f; l.color = Color.white;
            l.intensity = 12f; l.range = 15f;
            l.shadows = LightShadows.Soft;          // the fence, trees and houses cast real shadows
            l.shadowStrength = 0.9f; l.shadowBias = 0.04f; l.shadowNormalBias = 0.3f;
            l.bounceIntensity = 0.3f;

            // A faint visible cone of light, as if through damp night air.
            var cone = new GameObject("Cone"); cone.transform.SetParent(lgo.transform, false);
            cone.AddComponent<MeshFilter>().sharedMesh = ConeMesh(poleH - 0.2f, 3.6f);
            cone.AddComponent<MeshRenderer>().sharedMaterial = ConeMaterial();
            var mr = cone.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        /// <summary>Downward cone along the local +Z axis (the light's forward). Open at the base.</summary>
        static Mesh ConeMesh(float height, float baseRadius)
        {
            const int seg = 28;
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            v.Add(Vector3.zero); uv.Add(new Vector2(0.5f, 1f));   // apex: brightest
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                v.Add(new Vector3(Mathf.Cos(a) * baseRadius, Mathf.Sin(a) * baseRadius, height));
                uv.Add(new Vector2(i / (float)seg, 0f));            // base: faded out
            }
            for (int i = 1; i <= seg; i++)
            {
                t.AddRange(new[] { 0, i, i + 1 });      // outside
                t.AddRange(new[] { 0, i + 1, i });      // inside, so it reads from beneath
            }
            var m = new Mesh { name = "LampCone" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        static Material ConeMaterial()
        {
            const string path = MatDir + "/N_LampCone.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(m, path);
            }
            const int size = 16;
            string texPath = MatDir + "/T_LampCone.png";
            var tex = new Texture2D(1, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            {
                float f = y / (float)(size - 1);                     // 0 = base, 1 = apex
                float a = Mathf.Pow(f, 2.2f) * 0.20f;
                tex.SetPixel(0, y, new Color(1f, 0.72f, 0.40f, a));
            }
            tex.Apply();
            System.IO.File.WriteAllBytes(texPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(texPath);
            imp.wrapMode = TextureWrapMode.Clamp; imp.alphaIsTransparency = true; imp.mipmapEnabled = false;
            imp.SaveAndReimport();

            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Surface", 1f);                  // transparent
            m.SetFloat("_Blend", 2f);                    // additive
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.One);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetShaderPassEnabled("ShadowCaster", false);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ------------------------------------------------------------ materials and primitives

        static Material Get(string name)
        {
            if (mats.TryGetValue(name, out var m)) return m;
            m = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{name}.mat");
            if (m == null) m = FlatMat(name, Color.grey, 0.2f);
            return mats[name] = m;
        }

        /// <summary>A copy of an existing material with its own tiling, so a huge slab does not stretch one texture.</summary>
        static Material Tiled(string source, float metresPerTile, float wFt, float dFt)
        {
            string key = $"N_{source}_{wFt:0}x{dFt:0}";
            if (mats.TryGetValue(key, out var cached)) return cached;
            var m = Copy(source, key);
            // Slabs are modelled as cubes whose UVs span the whole face, so tiling must be large.
            m.mainTextureScale = new Vector2(wFt * F / metresPerTile, dFt * F / metresPerTile);
            EditorUtility.SetDirty(m);
            return mats[key] = m;
        }

        static Material TintedFrom(string source, string key, Color tint)
        {
            if (mats.TryGetValue(key, out var cached)) return cached;
            var m = Copy(source, key);
            m.SetColor("_BaseColor", tint);
            m.color = tint;
            EditorUtility.SetDirty(m);
            return mats[key] = m;
        }

        static Material Copy(string source, string key)
        {
            string path = $"{MatDir}/{key}.mat";
            var src = Get(source);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(src); AssetDatabase.CreateAsset(m, path); }
            else m.CopyPropertiesFromMaterial(src);
            return m;
        }

        static Material FlatMat(string key, Color c, float smooth)
        {
            if (mats.TryGetValue(key, out var cached)) return cached;
            string path = $"{MatDir}/{key}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
            return mats[key] = m;
        }

        static Material Glow(string key, Color c, float strength)
        {
            if (mats.TryGetValue(key, out var cached)) return cached;
            var m = FlatMat(key, c * 0.25f, 0.3f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", c * strength);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Cube placed in world feet.</summary>
        static GameObject Cube(Transform parent, string name, Vector3 centreFt, Vector3 sizeFt, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.transform.SetParent(parent, false);
            go.transform.position = centreFt * F;
            go.transform.localScale = sizeFt * F;
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }

        /// <summary>Cube placed in a house's local frame, in feet.</summary>
        static GameObject Local(Transform parent, string name, Vector3 posFt, Vector3 sizeFt, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = posFt * F;
            go.transform.localScale = sizeFt * F;
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }
    }
}
