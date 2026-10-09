using System.Linq;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    public static partial class HouseScene
    {
        /// <summary>Photographs the existing scene without rebuilding it, so baked lighting survives. Output: Temp/after_*.png</summary>
        public static void ShotsOnly()
        {
            var probes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None).OrderBy(p => p.name).ToList();
            int k = 0;
            foreach (var p in probes)
            {
                foreach (int ya in new[] { 45, 225 })
                    Snapshot($"Temp/after_{p.name.Replace("ReflectionProbe_", "")}_{ya}.png", p.transform.position + Vector3.up * 0.2f, Quaternion.Euler(14, ya + 180f, 0), 85f);
                k++;
            }
            var player = GameObject.Find("Player");
            if (player != null)
                Snapshot("Temp/after_front.png", player.transform.position + Vector3.up * 1.6f, Quaternion.Euler(0, 0, 0), 70f);
            Debug.Log($"[ShotsOnly] {k} rooms photographed");
        }
    }
}
