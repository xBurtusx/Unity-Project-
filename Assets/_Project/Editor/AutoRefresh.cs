using System.IO;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Makes the Editor notice script and data changes made from outside it without anyone clicking
    /// into its window. Unity normally only reimports when it regains focus, which stalls any
    /// external tool that edits files and then waits. This polls twice a second for newer files
    /// under Assets/_Project and refreshes the AssetDatabase when it finds one.
    /// </summary>
    [InitializeOnLoad]
    public static class AutoRefresh
    {
        const double Interval = 0.5;
        static double next;
        static System.DateTime newest = System.DateTime.MinValue;

        static AutoRefresh()
        {
            newest = Newest();
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + Interval;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            // The marker is the signal AutoRun uses; treat it as a change too, so a request
            // written while the Editor is idle is not left waiting for a domain reload.
            var latest = Newest();
            if (latest > newest)
            {
                newest = latest;
                AssetDatabase.Refresh();
            }
        }

        static System.DateTime Newest()
        {
            var t = System.DateTime.MinValue;
            foreach (var dir in new[] { "Assets/_Project", "Assets/NeighbourhoodHouse/Meshes" })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".meta")) continue;
                    var w = File.GetLastWriteTimeUtc(f);
                    if (w > t) t = w;
                }
            }
            return t;
        }
    }
}
