// Headless probe for the FactoryDemo robot placement phase. Not part of the project: copy to Assets/Editor/ to use (see README).
using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class PlacementProbe
{
    const string Flag = "PlacementProbe.active";
    static int failures;

    static PlacementProbe()
    {
        if (!SessionState.GetBool(Flag, false)) return;
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        EditorApplication.update -= Tick;
        SessionState.SetBool(Flag, false);
        Go();
    }

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Demo Scenes/FactoryDemo.unity");
        SessionState.SetBool(Flag, true);
        Log("entering play mode");
        EditorApplication.EnterPlaymode();
    }

    static void Log(string s) => Debug.Log("[PROBE] " + s);
    static void Check(bool ok, string what) { Debug.Log((ok ? "[PROBE-OK]   " : "[PROBE-FAIL] ") + what); if (!ok) failures++; }
    static void Exit(int code) { Log("exit " + code); EditorApplication.Exit(code); }

    static async void Go()
    {
        try
        {
            string outDir = Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/tmp";
            await Task.Delay(2500);
            var ctl = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementController>();
            if (ctl == null) { Log("no RobotPlacementController"); Exit(2); return; }

            GameObject fr3 = GameObject.Find("fr3");
            Transform baseT = fr3.transform.Find("BaseTransform");
            Transform pedestal = GameObject.Find("RobotPedestal").transform;
            ArticulationBody rootBody = fr3.GetComponentsInChildren<ArticulationBody>().First(b => b.isRoot);
            Transform fixtureAnchor = GameObject.Find("FR3 Station").transform.Find("MachineFixture/DropAnchor");
            GameObject mtcMenu = GameObject.Find("MTCMenu") ?? FindInactiveRoot("MTCMenu");
            GameObject doorMenu = GameObject.Find("MachineDoorMenu") ?? FindInactiveRoot("MachineDoorMenu");
            GameObject handle = ctl.Handle.gameObject;
            var markers = ctl.Markers;
            var vis = ctl.Reachability;
            var pubs = UnityEngine.Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            // --- initial state: placing -------------------------------------------------
            Check(ctl.Phase == Factory.RobotPlacementController.PlacementPhase.Placing, "starts in Placing");
            Check(handle.activeInHierarchy, "handle active while placing");
            Check(mtcMenu != null && !mtcMenu.activeSelf, "MTCMenu hidden while placing");
            Check(doorMenu != null && !doorMenu.activeSelf, "MachineDoorMenu hidden while placing");
            Check(pubs.Length > 0 && pubs.All(p => p.pausePublishing), $"all {pubs.Length} publishers paused while placing");
            Check(pubs.All(p => !p.hasBeenPublished), "nothing published to MoveIt yet");
            Check(vis != null && vis.HasMap && vis.MapSource.StartsWith("baked") && !vis.RosMapEnabled, $"visualizer holds the baked map ({vis?.MapSource})");
            Check(markers != null && markers.Count == 6 && markers.GetComponentsInChildren<MeshRenderer>().Length == 6, $"6 reach markers exist ({markers?.Count})");
            Log($"reachable at default pose: {markers.ReachableCount}/{markers.Count}; warnings: {string.Join(" | ", ctl.Warnings())}");
            for (int i = 0; i < markers.Count; i++) Log($"  {markers.TargetLabel(i)}: topdown={markers.TopDownDexterity[i]:F2} reachable={markers.IsReachable(i)}");
            Check(markers.ReachableCount < markers.Count, "the start pose leaves targets unreachable (the visitor has work to do)");
            Check(Vector3.Distance(ctl.Handle.position, ctl.StartHandlePose.position) < 1e-3f && Vector3.Distance(ctl.StartHandlePose.position, ctl.DefaultHandlePose.position) > 0.5f,
                  $"robot starts at the start pose {ctl.StartHandlePose.position}, away from the ideal {ctl.DefaultHandlePose.position}");
            Check(Vector3.Distance(fr3.transform.position, ctl.StartHandlePose.position + new Vector3(0f, 0.75f, 0f)) < 1e-3f, "fr3 root was moved to the start pose");
            Shot(outDir, "placing_start", new Vector3(0f, 2.3f, -1.9f), new Vector3(-0.4f, 0.5f, 0.8f), 65f);
            // Bring it to the ideal spot the way the panel would: nudge until the handle matches.
            Vector3 toIdeal = ctl.DefaultHandlePose.position - ctl.Handle.position;
            ctl.Nudge(toIdeal.x, toIdeal.z);
            ctl.Rotate(-Mathf.DeltaAngle(ctl.DefaultHandlePose.rotation.eulerAngles.y, ctl.Handle.rotation.eulerAngles.y));
            await Task.Delay(300);
            Check(Vector3.Distance(ctl.Handle.position, ctl.DefaultHandlePose.position) < 1e-3f && Mathf.Abs(Mathf.DeltaAngle(ctl.Handle.rotation.eulerAngles.y, ctl.DefaultHandlePose.rotation.eulerAngles.y)) < 0.1f, "nudged back to the ideal pose");
            Log($"reachable at the ideal pose: {markers.ReachableCount}/{markers.Count}");
            Check(markers.ReachableCount == markers.Count, "all targets reachable at the ideal pose");
            for (int i = 0; i < markers.Count; i++) Log($"  {markers.TargetLabel(i)}: topdown={markers.TopDownDexterity[i]:F2}");
            // The visitor's view of the fixture from the spawn spot, past the robot.
            Shot(outDir, "visitor_view_ideal", new Vector3(0f, 1.6f, 0f), fixtureAnchor.position, 60f);

            Vector3 fr3Pos0 = fr3.transform.position, base0 = baseT.position, ped0 = pedestal.position, body0 = rootBody.transform.position;
            float dex0 = vis.SampleDexterity(fixtureAnchor.position, true);
            Shot(outDir, "placing_default", new Vector3(0f, 1.9f, -1.4f), new Vector3(0f, 0.6f, 1.0f), 60f);

            // --- nudge + turn ---------------------------------------------------------------
            ctl.Nudge(0.2f, 0f);
            ctl.Rotate(30f);
            await Task.Delay(300); // several physics steps: TeleportRoot must survive them
            Vector3 dRobot = fr3.transform.position - fr3Pos0, dBody = rootBody.transform.position - body0, dPed = pedestal.position - ped0, dBase = baseT.position - base0;
            Log($"after nudge: robot d={dRobot} body d={dBody} pedestal d={dPed} base d={dBase} yaw={fr3.transform.eulerAngles.y:F1}");
            Check(Vector3.Distance(dRobot, new Vector3(0.2f, 0f, 0f)) < 1e-3f, "fr3 root moved +0.2 x");
            Check(Vector3.Distance(dBody, new Vector3(0.2f, 0f, 0f)) < 1e-3f, "root ArticulationBody moved with it (TeleportRoot took)");
            Check(Vector3.Distance(dBase, new Vector3(0.2f, 0f, 0f)) < 1e-3f, "BaseTransform moved with it");
            Check(Mathf.Abs(Mathf.DeltaAngle(fr3.transform.eulerAngles.y, 30f)) < 0.1f, "fr3 yawed 30 deg");
            // pedestal centre is 3 cm behind the pivot: a 30 deg turn swings it sideways a little
            Vector3 expectedPed = Quaternion.Euler(0f, 30f, 0f) * new Vector3(0f, 0.375f, -0.03f) + new Vector3(fr3Pos0.x + 0.2f, 0f, fr3Pos0.z);
            Check(Vector3.Distance(pedestal.position, expectedPed) < 1e-3f, $"pedestal followed (at {pedestal.position}, expected {expectedPed})");
            float dex1 = vis.SampleDexterity(fixtureAnchor.position, true);
            Check(Mathf.Abs(dex1 - dex0) > 1e-3f || dex0 > 0.99f, $"fixture dexterity re-sampled from the new base ({dex0:F2} -> {dex1:F2})");
            Check(pubs.All(p => p.pausePublishing), "publishers still paused after the move");
            Log($"reachable after move: {markers.ReachableCount}/{markers.Count}; warnings: {string.Join(" | ", ctl.Warnings())}");
            Shot(outDir, "placing_moved", new Vector3(0f, 1.9f, -1.4f), new Vector3(0f, 0.6f, 1.0f), 60f);

            // --- move far away: fixture must become unreachable -----------------------------
            ctl.Nudge(-1.2f, -0.6f);
            await Task.Delay(200);
            Log($"far pose: handle={ctl.Handle.position} reachable={markers.ReachableCount}/{markers.Count}");
            Check(markers.ReachableCount < markers.Count, "moving the robot away makes targets unreachable");
            Check(ctl.Handle.position.x >= -1.2f - 1e-4f && ctl.Handle.position.z >= -0.2f - 1e-4f, "nudge respects the cell bounds");
            Shot(outDir, "placing_far", new Vector3(-0.5f, 2.2f, -1.8f), new Vector3(-0.5f, 0.5f, 0.8f), 65f);
            ctl.ResetToStart();
            await Task.Delay(200);
            Check(Vector3.Distance(ctl.Handle.position, ctl.StartHandlePose.position) < 1e-3f && Mathf.Abs(Mathf.DeltaAngle(ctl.Handle.rotation.eulerAngles.y, ctl.StartHandlePose.rotation.eulerAngles.y)) < 0.1f, "Start over returns the robot to the start pose");
            Vector3 back = ctl.DefaultHandlePose.position - ctl.Handle.position;
            ctl.Nudge(back.x + 0.1f, back.z);
            ctl.Rotate(-Mathf.DeltaAngle(ctl.DefaultHandlePose.rotation.eulerAngles.y, ctl.Handle.rotation.eulerAngles.y));
            await Task.Delay(200);

            // --- panel ---------------------------------------------------------------------
            var panel = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementPanel>();
            Check(panel != null && panel.Controller == ctl, "placement panel bound");
            var doc = panel.GetComponent<UnityEngine.UIElements.UIDocument>();
            var confirmBtn = doc.rootVisualElement?.Q<UnityEngine.UIElements.Button>("placementConfirmButton");
            var status = doc.rootVisualElement?.Q<UnityEngine.UIElements.Label>("placementStatusLabel");
            Check(confirmBtn != null && confirmBtn.enabledSelf, "Confirm button present and enabled");
            Log($"panel status: '{status?.text}'");
            Transform menuT = panel.transform.root;
            Vector3 panelCentre = doc.transform.position + menuT.rotation * new Vector3(0.3f, -0.45f, 0f);
            Shot(outDir, "placement_panel", panelCentre - menuT.forward * 1.0f, panelCentre, 55f); // the document faces its local -z

            // --- confirm --------------------------------------------------------------------
            confirmBtn?.Focus();
            ctl.Confirm();
            // Read the publish flags in the same frame: with ROS up the ADDs go out on the next Update.
            bool flaggedForAdd = pubs.Where(p => !p.transform.IsChildOf(baseT)).All(p => !p.hasBeenPublished);
            await Task.Delay(100);
            Check(ctl.Phase == Factory.RobotPlacementController.PlacementPhase.Confirmed, "Confirm -> Confirmed");
            Check(!handle.activeInHierarchy, "handle hidden after confirm");
            Check(mtcMenu.activeSelf && doorMenu.activeSelf, "MTCMenu and MachineDoorMenu shown after confirm");
            Check(!vis.ShellVisible && !vis.SliceVisible, "reachability volume hidden after confirm");
            Check(pubs.All(p => !p.pausePublishing), "publishers unpaused after confirm");
            int room = pubs.Count(p => !p.transform.IsChildOf(baseT)), riding = pubs.Length - room;
            Log($"publishers: {room} room, {riding} riding with the base");
            Check(flaggedForAdd, "room publishers flagged for a fresh ADD on confirm");
            await Task.Delay(2500);
            Check(vis.RosMapEnabled, "ROS map re-enabled after the delay");
            Log($"published now: {pubs.Count(p => p.hasBeenPublished)}/{pubs.Length}; map source: {vis.MapSource}");
            Shot(outDir, "confirmed", new Vector3(0f, 1.9f, -1.4f), new Vector3(0f, 0.6f, 1.0f), 60f);

            // --- reposition -----------------------------------------------------------------
            Check(ctl.CanReposition, "can reposition when idle");
            ctl.BeginReposition();
            await Task.Delay(100);
            Check(ctl.Phase == Factory.RobotPlacementController.PlacementPhase.Placing, "BeginReposition -> Placing");
            Check(handle.activeInHierarchy && !mtcMenu.activeSelf, "handle back, menus hidden");
            Check(pubs.All(p => p.pausePublishing), "publishers paused again");
            Check(!vis.RosMapEnabled && vis.MapSource.StartsWith("baked"), $"baked map held again ({vis.MapSource})");
            Check(vis.ShellVisible, "reachability shell shown again when repositioning");

            Log($"failures={failures}");
            Exit(failures == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            Log("EXCEPTION " + e);
            Exit(3);
        }
    }

    static GameObject FindInactiveRoot(string name) =>
        UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(r => r.name == name);

    static void Shot(string dir, string name, Vector3 pos, Vector3 look, float fov)
    {
        var camGo = new GameObject("ProbeCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        cam.fieldOfView = fov;
        cam.nearClipPlane = 0.05f;
        var rt = new RenderTexture(1600, 1000, 24);
        try
        {
            camGo.transform.position = pos;
            camGo.transform.LookAt(look);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            Log("shot " + name);
        }
        finally
        {
            RenderTexture.active = null;
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(camGo);
            UnityEngine.Object.Destroy(rt);
        }
    }
}
