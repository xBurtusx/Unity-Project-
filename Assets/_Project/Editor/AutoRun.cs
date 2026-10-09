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

        static double nextPoll;

        static AutoRun()
        {
            // Wait for the Editor to finish settling before touching the scene.
            EditorApplication.delayCall += TryRun;

            // A one-shot callback can be lost across reloads, leaving a request unread forever.
            // Polling once a second also means a request no longer needs a recompile to be noticed.
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (File.Exists(Marker)) TryRun();
        }

        static void TryRun()
        {
            if (!File.Exists(Marker)) return;

            // Critical: a refresh can reload the domain for an ASSET import before the changed
            // SCRIPTS have finished compiling. Run here and the marker gets eaten by the old
            // assembly, which silently ignores any action the new code just added - the request
            // appears to succeed while doing nothing. Wait for the Editor to go quiet first.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
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
            if (request.Contains("dumpinfo")) SceneInfoDump.Run();
            if (request.Contains("furnaudit")) FurnitureAudit.Run();
            if (request.Contains("genaudio")) ProceduralAudio.Generate();
            if (request.Contains("recorded")) RecordedAudioPrep.Run();
            if (request.Contains("mainhouse")) MainHouseDiag.Run();
            if (request.Contains("realism")) RealismPass.Run();
            if (request.Contains("furnrepair")) FurnitureRepair.Run();
            if (request.Contains("furniture")) FurnitureTexturing.Run();
            if (request.Contains("nightlife")) NightLifeSetup.Run();
            if (request.Contains("setnight")) NightLifeSetup.SetNight();
            if (request.Contains("setday")) NightLifeSetup.SetDay();
            if (request.Contains("sunview")) RealismPass.LookAtSun();
            if (request.Contains("interior")) InteriorPlacer.Place();
            if (request.Contains("grass")) GrassSetup.Setup();
            if (request.Contains("shotsonly")) HouseScene.ShotsOnly();
            if (request.Contains("bake")) HouseLighting.Bake();
            if (request.Contains("inspectrails")) RailsInspect.Run();
            if (request.Contains("newscene")) HouseScene.Build();
            if (request.Contains("inspecthouse")) HouseInspect.Run();
            if (request.Contains("snaphouse")) HouseSnap.Run();
            if (request.Contains("neighbourhood")) NeighbourhoodBuilder.Build();
            if (request.Contains("doortest")) InteriorAudit.TestDoors();
            if (request.Contains("audit")) InteriorAudit.Run();
            if (request.Contains("diagnose")) SceneDiagnostics.Report();
            if (request.Contains("validate") || request.Length == 0) HouseValidator.Validate();

            if (request.Contains("aerial") || request.Length == 0) SceneViewShortcuts.Aerial();
            else if (request.Contains("front")) SceneViewShortcuts.Front();
            else if (request.Contains("rear")) SceneViewShortcuts.Rear();

            // Only scenes that already have a file: SaveOpenScenes on an Untitled scene pops a
            // modal Save dialog and stalls the Editor.
            try
            {
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    var sc = EditorSceneManager.GetSceneAt(i);
                    if (sc.isLoaded && !string.IsNullOrEmpty(sc.path)) EditorSceneManager.SaveScene(sc);
                }
            }
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
// touch 1791496647
// t 1791496994
// t2 1791497025
// t3 1791497066
// t4 1791497479
// t5 1791497609
// t6 1791497846
// t7 1791497903
// t8 1791499054
// t9 1791499091
// t10 1791499342
// t11 1791499590
// t12 1791499634
// t13 1791499815
// t14 1791500149
// t15 1791500339
// t16 1791500483
// t17 1791500828
// t18 1791500854
// t19 1791500888
// t20 1791501415
// t21 1791501500
// t22 1791501606
// t23 1791501750
// t24 1791502170
// t25 1791502216
// t26 1791502252
// t27 1791502287
// t28 1791502312
// t29 1791502370
// t30 1791502456
// t31 1791502484
// t32 1791502548
// t33 1791502593
// t34 1791502622
// t35 1791502655
// t36 1791502720
// t37 1791502759
// t38 1791502804
// t39 1791502830
// t40 1791502870
// t41 1791503108
// t42 1791503219
// t43 1791504246
// t44 1791504483
// t45 1791504545
// t46 1791504624
// t47 1791504684
// t48 1791504728
// t49 1791504822
// t50 1791504865
// t51 1791504897
// t52 1791504945
// t53 1791505112
// t54 1791505153
// t55 1791507706
// t56 1791507749
// t57 1791507777
// t58 1791507846
// t59 1791507877
// t60 1791507954
// t61 1791507984
// t62 1791508020
// t63 1791508076
// t64 1791508121
// t65 1791508166
// t66 1791508201
// t67 1791508234
// t68 1791508291
// t21 1791521059
// t22 1791521166
// t23 1791521307
// t24 1791521385
// t25 1791522472
// t26 1791522709
// t27 1791522959
// t28 1791523070
// t29 1791523133
// t30 1791523740
// t31 1791524808
// t32 1791525269
// t33 1791525483
// t34 1791525556
// t35 1791525692
// t36 1791525828
// t37 1791528910
// t38 1791529558
// t39 1791530955
