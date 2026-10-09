using System.Linq;
using UnityEditor;
using UnityEngine;
using HorrorHouse;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Drops a working first-person player into the scene, standing on the front path looking
    /// at the house. Re-running replaces the existing one rather than stacking up duplicates.
    /// </summary>
    public static class PlayerSetup
    {
        const float F = 0.3048f;

        // On the front walk, a little south of the porch, facing north towards the front door.
        static readonly Vector3 SpawnFeet = new Vector3(20.75f, 0.2f, -14.0f);

        [MenuItem("Horror House/Stage 3 - Create Player", priority = 20)]
        public static void Create() => CreateAt(new Vector3(SpawnFeet.x * F, SpawnFeet.y * F, SpawnFeet.z * F), 0f);

        /// <summary>Creates the player standing at a world position, facing the given yaw.</summary>
        public static void CreateAt(Vector3 worldPos, float yaw)
        {
            var existing = GameObject.Find("Player");
            if (existing != null) Undo.DestroyObjectImmediate(existing);

            var player = new GameObject("Player");
            Undo.RegisterCreatedObjectUndo(player, "Create Player");
            player.transform.position = worldPos;
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.80f;
            cc.radius = 0.28f;
            cc.center = new Vector3(0f, 0.90f, 0f);
            cc.slopeLimit = 50f;
            // Generous enough for a kerb or a porch step, too small to climb furniture.
            cc.stepOffset = 0.35f;
            cc.skinWidth = 0.02f;

            var camGo = new GameObject("PlayerCamera");
            camGo.transform.SetParent(player.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.68f, 0f);

            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.03f;      // small, so you can stand close to a wall without it clipping
            cam.farClipPlane = 400f;
            cam.fieldOfView = 68f;          // vertical FOV; a believable human-ish view on a 16:9 screen
            camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";

            var fpc = player.AddComponent<FirstPersonController>();
            fpc.cameraPivot = camGo.transform;

            var interactor = player.AddComponent<PlayerInteractor>();
            interactor.eye = cam;

            // Two audio listeners in one scene produces a warning every frame and broken
            // positional audio, so retire the template camera's listener.
            foreach (var other in Object.FindObjectsByType<AudioListener>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (other != camGo.GetComponent<AudioListener>()) other.enabled = false;

            foreach (var other in Object.FindObjectsByType<Camera>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None)
                                       .Where(c => c != cam && c.GetComponent<FirstPersonController>() == null))
            {
                if (other.gameObject.name == "Main Camera") other.gameObject.SetActive(false);
            }

            Selection.activeGameObject = player;
            SceneView.lastActiveSceneView?.FrameSelected();

            Debug.Log("[PlayerSetup] Player created on the front walk, facing the house.\n" +
                      "Press Play, then: W A S D to walk, mouse to look, Left Shift to hurry, " +
                      "Left Ctrl to crouch. Walking speed is 1.35 m/s - real human pace, " +
                      "deliberately slower than most games. Press Escape to free the cursor.");
        }
    }
}
