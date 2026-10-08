using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Runs a rebuild automatically on the next script reload, if a marker file is present.
    ///
    /// This exists so the build can be driven from outside the Editor - by a script, a tool, or
    /// an agent - without anyone having to click through menus. Drop a file at
    /// Temp/horrorhouse_autorun and the next recompile rebuilds the house, validates it, points
    /// the Scene view at the roof and saves. The marker is deleted first, so it never loops.
    ///
    /// Temp/ is outside Assets/, so writing the marker does not itself trigger an import.
    /// </summary>
    [InitializeOnLoad]
    public static class AutoRun
    {
        const string Marker = "Temp/horrorhouse_autorun";

        static AutoRun()
        {
            // Wait for the Editor to finish settling before touching the scene.
            EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            if (!File.Exists(Marker)) return;

            // Critical: a refresh can reload the domain for an ASSET import before the changed
            // SCRIPTS have finished compiling. Run here and the marker gets eaten by the old
            // assembly, which silently ignores any action the new code just added - the request
            // appears to succeed while doing nothing. Wait for the Editor to go quiet first.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryRun;
                return;
            }

            string request = File.ReadAllText(Marker).Trim().ToLowerInvariant();
            File.Delete(Marker);   // delete first - a crash must not leave this looping

            Debug.Log($"[AutoRun] Marker found, running: {(request.Length == 0 ? "build" : request)}");

            if (request.Contains("build") || request.Length == 0) HouseBuilder.Build();
            if (request.Contains("player")) PlayerSetup.Create();
            if (request.Contains("textures")) TextureFactory.Generate();
            if (request.Contains("atmosphere")) AtmosphereSetup.Setup();
            if (request.Contains("interior")) InteriorPlacer.Place();
            if (request.Contains("doortest")) InteriorAudit.TestDoors();
            if (request.Contains("audit")) InteriorAudit.Run();
            if (request.Contains("diagnose")) SceneDiagnostics.Report();
            if (request.Contains("validate") || request.Length == 0) HouseValidator.Validate();

            if (request.Contains("aerial") || request.Length == 0) SceneViewShortcuts.Aerial();
            else if (request.Contains("front")) SceneViewShortcuts.Front();
            else if (request.Contains("rear")) SceneViewShortcuts.Rear();

            try { EditorSceneManager.SaveOpenScenes(); }
            catch (System.Exception e) { Debug.LogWarning($"[AutoRun] Scene save failed: {e.Message}"); }

            // Receipt of what actually ran, so a request can be confirmed from outside the Editor
            // rather than assumed from the fact that the marker disappeared.
            try
            {
                File.WriteAllText("Temp/autorun_result.txt",
                    $"request: {request}\ncompleted: {System.DateTime.Now:HH:mm:ss}\n" +
                    $"supported actions in this build: build, player, textures, atmosphere, " +
                    $"validate, aerial, front, rear\n");
            }
            catch { }

            Debug.Log("[AutoRun] Done. Scene saved.");

            // Play mode last: entering it reloads the domain, so nothing after this would run.
            if (request.Contains("play")) EditorApplication.EnterPlaymode();
        }
    }
}
