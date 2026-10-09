using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Per-lot variety: backyard set-ups and the little props in them. Kept out of HouseScene so that
    /// file stays about the house and the street. Every material here is a plain, realistic colour.
    /// </summary>
    public static partial class HouseScene
    {
        static GameObject Piece(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material m, Vector3? euler = null)
        {
            var g = GameObject.CreatePrimitive(type);
            g.name = name; g.transform.SetParent(parent, false);
            g.transform.position = pos; g.transform.localScale = scale;
            if (euler.HasValue) g.transform.rotation = Quaternion.Euler(euler.Value);
            g.GetComponent<Renderer>().sharedMaterial = m;
            g.isStatic = true;
            return g;
        }

        /// <summary>One of several ordinary backyard set-ups, chosen by lot, plus a tree or two.</summary>
        static void Backyard(Transform lot, float cx, float sgn, float backZ, float rearZ, int idx)
        {
            var yard = new GameObject("Backyard"); yard.transform.SetParent(lot, false);
            float zLo = backZ + 1.2f, zHi = rearZ - 1.0f;            // distance from the road axis
            float zMid = (zLo + zHi) * 0.5f;
            Vector3 At(float x, float zDist, float y = 0f) => new Vector3(cx + x, y, sgn * zDist);

            switch (idx % 5)
            {
                case 0: Shed(yard.transform, At(6.5f, zHi - 1.4f), sgn, idx); break;
                case 1: Trampoline(yard.transform, At(-5f, zMid)); break;
                case 2: PicnicTable(yard.transform, At(-4f, zMid), sgn); Barbecue(yard.transform, At(-1.2f, zMid + 0.3f)); break;
                case 3: Pool(yard.transform, At(-4.5f, zMid)); break;
                case 4: RaisedBeds(yard.transform, At(0f, zHi - 0.9f)); Clothesline(yard.transform, At(-4f, zMid - 0.2f), sgn); break;
            }
            if (idx % 2 == 0) Shed(yard.transform, At(-9f, zHi - 0.5f), sgn, idx + 2);   // some yards also have a second small shed

            // Trees: mature, dark, a little irregular.
            var rng = new System.Random(idx * 131 + 7);
            int trees = 1 + idx % 2;
            for (int t = 0; t < trees; t++)
            {
                float tx = (t == 0 ? 8.5f : -8.8f) + (float)(rng.NextDouble() - 0.5) * 1.2f;
                Tree(yard.transform, At(tx, zHi - 1.4f - (float)rng.NextDouble() * 1.2f), 0.9f + (float)rng.NextDouble() * 0.7f, rng);
            }
        }

        /// <summary>
        /// Worn, bare patches of earth on the lawns - under a swing, along a fence, where people cut across.
        /// Flat lenses, so the edge tapers into the grass rather than showing a hard disc.
        /// </summary>
        static void DirtPatches(Transform lot, float cx, float sgn, float frontZ, float backZ, float rearZ, int idx)
        {
            var dirt = AcTiled("ac_dirt", "AC_Ground037", "Ground037", new Color(0.85f, 0.82f, 0.78f), 0.05f, 2.0f, 2.0f, 1.3f);
            var g = new GameObject("DirtPatches"); g.transform.SetParent(lot, false);
            var rng = new System.Random(idx * 977 + 13);
            for (int i = 0; i < 5; i++)
            {
                bool front = i < 2;
                float zDist = front ? Mathf.Lerp(frontZ + 0.8f, frontZ + 3.5f, (float)rng.NextDouble())
                                    : Mathf.Lerp(backZ + 0.8f, rearZ - 0.6f, (float)rng.NextDouble());
                float x = (rng.NextDouble() < 0.5 ? -1f : 1f) * Mathf.Lerp(front ? 3.2f : 1.0f, 10f, (float)rng.NextDouble());
                float d = Mathf.Lerp(0.9f, 2.4f, (float)rng.NextDouble());
                var p = Piece(g.transform, PrimitiveType.Sphere, "Dirt", new Vector3(cx + x, 0f, sgn * zDist), new Vector3(d, 0.05f, d * Mathf.Lerp(0.7f, 1.2f, (float)rng.NextDouble())), dirt,
                              new Vector3(0, (float)rng.NextDouble() * 180f, 0));
                Object.DestroyImmediate(p.GetComponent<Collider>());
            }
        }

        static void Shed(Transform p, Vector3 pos, float sgn, int idx)
        {
            var cols = new[] { new Color(0.13f, 0.20f, 0.16f), new Color(0.36f, 0.26f, 0.17f), new Color(0.46f, 0.47f, 0.48f), new Color(0.22f, 0.24f, 0.28f) };
            var wall = FlatMat("shed" + (idx % 4), cols[idx % 4], 0.1f);
            var roof = FlatMat("shedroof", new Color(0.18f, 0.18f, 0.19f), 0.2f);
            var door = FlatMat("sheddoor", new Color(0.09f, 0.09f, 0.10f), 0.2f);
            var g = new GameObject("Shed"); g.transform.SetParent(p, false);
            Piece(g.transform, PrimitiveType.Cube, "Body", pos + Vector3.up * 1.1f, new Vector3(2.9f, 2.2f, 2.3f), wall);
            Piece(g.transform, PrimitiveType.Cube, "Roof", pos + Vector3.up * 2.35f, new Vector3(3.2f, 0.1f, 2.7f), roof, new Vector3(sgn > 0 ? 7f : -7f, 0, 0));
            Piece(g.transform, PrimitiveType.Cube, "Door", pos + new Vector3(0.4f, 0.95f, -sgn * 1.17f), new Vector3(0.95f, 1.9f, 0.05f), door);
        }

        static void Trampoline(Transform p, Vector3 pos)
        {
            var mat = FlatMat("tramp", new Color(0.05f, 0.05f, 0.06f), 0.3f);
            var frame = FlatMat("trampframe", new Color(0.10f, 0.16f, 0.30f), 0.4f);
            var g = new GameObject("Trampoline"); g.transform.SetParent(p, false);
            Piece(g.transform, PrimitiveType.Cylinder, "Mat", pos + Vector3.up * 0.85f, new Vector3(3.0f, 0.02f, 3.0f), mat);
            Piece(g.transform, PrimitiveType.Cylinder, "Pad", pos + Vector3.up * 0.84f, new Vector3(3.4f, 0.04f, 3.4f), frame);
            for (int i = 0; i < 6; i++)
            {
                float a = i / 6f * Mathf.PI * 2f;
                Piece(g.transform, PrimitiveType.Cube, "Leg", pos + new Vector3(Mathf.Cos(a) * 1.55f, 0.42f, Mathf.Sin(a) * 1.55f), new Vector3(0.05f, 0.84f, 0.05f), frame);
            }
        }

        static void PicnicTable(Transform p, Vector3 pos, float sgn)
        {
            var wood = FlatMat("picnic", new Color(0.33f, 0.24f, 0.16f), 0.15f);
            var g = new GameObject("PicnicTable"); g.transform.SetParent(p, false);
            Piece(g.transform, PrimitiveType.Cube, "Top", pos + Vector3.up * 0.76f, new Vector3(1.8f, 0.05f, 0.8f), wood);
            foreach (int s in new[] { -1, 1 })
            {
                Piece(g.transform, PrimitiveType.Cube, "Bench", pos + new Vector3(0, 0.45f, s * 0.58f), new Vector3(1.8f, 0.05f, 0.28f), wood);
                foreach (int e in new[] { -1, 1 })
                    Piece(g.transform, PrimitiveType.Cube, "Leg", pos + new Vector3(e * 0.7f, 0.38f, s * 0.35f), new Vector3(0.06f, 0.76f, 0.06f), wood, new Vector3(0, 0, e * 8f));
            }
        }

        static void Barbecue(Transform p, Vector3 pos)
        {
            var black = FlatMat("bbq", new Color(0.04f, 0.04f, 0.045f), 0.35f);
            var g = new GameObject("Barbecue"); g.transform.SetParent(p, false);
            Piece(g.transform, PrimitiveType.Cylinder, "Bowl", pos + Vector3.up * 0.9f, new Vector3(0.6f, 0.12f, 0.6f), black);
            Piece(g.transform, PrimitiveType.Sphere, "Lid", pos + Vector3.up * 1.0f, new Vector3(0.6f, 0.34f, 0.6f), black);
            foreach (var o in new[] { new Vector2(-0.2f, -0.2f), new Vector2(0.2f, -0.2f), new Vector2(0, 0.25f) })
                Piece(g.transform, PrimitiveType.Cube, "Leg", pos + new Vector3(o.x, 0.45f, o.y), new Vector3(0.03f, 0.9f, 0.03f), black);
        }

        static void Pool(Transform p, Vector3 pos)
        {
            var wall = FlatMat("poolwall", new Color(0.55f, 0.57f, 0.60f), 0.3f);
            var water = FlatMat("poolwater", new Color(0.12f, 0.36f, 0.45f), 0.9f);
            var g = new GameObject("AbovegroundPool"); g.transform.SetParent(p, false);
            Piece(g.transform, PrimitiveType.Cylinder, "Wall", pos + Vector3.up * 0.55f, new Vector3(4.4f, 0.55f, 4.4f), wall);
            Piece(g.transform, PrimitiveType.Cylinder, "Water", pos + Vector3.up * 1.0f, new Vector3(4.15f, 0.02f, 4.15f), water);
        }

        static void RaisedBeds(Transform p, Vector3 pos)
        {
            var wood = FlatMat("bedwood", new Color(0.32f, 0.23f, 0.15f), 0.1f);
            var soil = FlatMat("soil", new Color(0.10f, 0.07f, 0.05f), 0.05f);
            var leaf = FlatMat("veg", new Color(0.12f, 0.26f, 0.09f), 0.1f);
            var g = new GameObject("RaisedBeds"); g.transform.SetParent(p, false);
            for (int i = -1; i <= 1; i++)
            {
                var c = pos + new Vector3(i * 2.8f, 0, 0);
                Piece(g.transform, PrimitiveType.Cube, "Bed", c + Vector3.up * 0.2f, new Vector3(2.4f, 0.4f, 0.9f), wood);
                Piece(g.transform, PrimitiveType.Cube, "Soil", c + Vector3.up * 0.41f, new Vector3(2.2f, 0.04f, 0.7f), soil);
                for (int k = 0; k < 4; k++)
                    Piece(g.transform, PrimitiveType.Sphere, "Plant", c + new Vector3(-0.8f + k * 0.55f, 0.6f, 0), new Vector3(0.4f, 0.35f, 0.4f), leaf);
            }
        }

        static void Clothesline(Transform p, Vector3 pos, float sgn)
        {
            var pole = Mat("castiron");
            var line = FlatMat("cord", new Color(0.7f, 0.7f, 0.7f), 0.1f);
            var g = new GameObject("Clothesline"); g.transform.SetParent(p, false);
            foreach (int s in new[] { -1, 1 })
                Piece(g.transform, PrimitiveType.Cylinder, "Pole", pos + new Vector3(s * 2.2f, 1.1f, 0), new Vector3(0.06f, 1.1f, 0.06f), pole);
            Piece(g.transform, PrimitiveType.Cube, "Line", pos + new Vector3(0, 2.1f, 0), new Vector3(4.4f, 0.01f, 0.01f), line);
            var cloth = new[] { new Color(0.75f, 0.75f, 0.72f), new Color(0.2f, 0.25f, 0.4f), new Color(0.55f, 0.2f, 0.2f) };
            for (int i = 0; i < 3; i++)
                Piece(g.transform, PrimitiveType.Cube, "Cloth", pos + new Vector3(-1.2f + i * 1.2f, 1.7f, 0), new Vector3(0.7f, 0.8f, 0.02f), FlatMat("cloth" + i, cloth[i], 0.05f));
        }

        static void Tree(Transform p, Vector3 pos, float s, System.Random rng)
        {
            var g = new GameObject("Tree"); g.transform.SetParent(p, false);
            Piece(g.transform, PrimitiveType.Cylinder, "Trunk", pos + Vector3.up * 1.6f * s, new Vector3(0.35f * s, 1.6f * s, 0.35f * s), FlatMat("bark", new Color(0.13f, 0.10f, 0.08f), 0.1f));
            for (int i = 0; i < 3; i++)
            {
                var off = new Vector3((float)(rng.NextDouble() - 0.5) * 1.6f, (float)rng.NextDouble() * 0.9f, (float)(rng.NextDouble() - 0.5) * 1.6f) * s;
                float c = (float)rng.NextDouble();
                var m = FlatMat("crown" + i, Color.Lerp(new Color(0.08f, 0.17f, 0.06f), new Color(0.15f, 0.27f, 0.09f), c), 0.08f);
                Piece(g.transform, PrimitiveType.Sphere, "Crown", pos + new Vector3(0, 4.3f * s, 0) + off, new Vector3(3.4f, 2.9f, 3.4f) * s * (0.8f + i * 0.12f), m);
            }
            foreach (var c in g.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
        }
    }
}
