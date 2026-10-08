using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Fixed camera positions for inspecting the building. Judging a roof needs a repeatable
    /// vantage point, not whatever angle the Scene view happens to be left at.
    /// </summary>
    public static class SceneViewShortcuts
    {
        const float F = 0.3048f;
        // Centre of the house footprint, in feet.
        static readonly Vector3 Centre = new Vector3(29.0f, 8.0f, 9.0f);

        [MenuItem("Horror House/View - Aerial (roof)", priority = 40)]
        public static void Aerial() => Look(Quaternion.Euler(28f, 140f, 0f), 26f);

        [MenuItem("Horror House/View - Front elevation", priority = 41)]
        public static void Front() => Look(Quaternion.Euler(6f, 0f, 0f), 24f);

        [MenuItem("Horror House/View - Rear elevation", priority = 42)]
        public static void Rear() => Look(Quaternion.Euler(6f, 180f, 0f), 24f);

        static void Look(Quaternion rotation, float sizeMetres)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) sv = EditorWindow.GetWindow<SceneView>();
            if (sv == null) return;

            sv.maximized = false;
            sv.Focus();
            sv.orthographic = false;
            sv.pivot = new Vector3(Centre.x * F, Centre.y * F, Centre.z * F);
            sv.rotation = rotation;
            sv.size = sizeMetres;
            sv.Repaint();
        }
    }
}
