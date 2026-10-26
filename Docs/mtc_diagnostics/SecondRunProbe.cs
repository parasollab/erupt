// Headless reproduction of "planning fails on the second run after Restart demo". Not part of the
// project: copy to Assets/Editor/ to use (see README). Needs the ROS stack; run WITHOUT -nographics.
// RUN1=full (default): load plan + execute, clamp, close, machining, open, release, unload plan + execute,
// then DemoReset.Restart and the load again. RUN1=load: load plan + execute, restart, load again.
// RUN1=plan: load plan only, restart, load again. RUN1=cancel: restart while the load executes (the part
// attached in MoveIt; CANCEL_AT seconds into it). RUNS=N repeats the cycle (default 2: one restart).
// RUN2_DX / RUN2_YAW (metres / degrees) offset every later placement from the ideal spot, as a hand would. Logs the second run's outcome, status and the MTC
// stage failures, plus the publishers' attached flags and the vise state before each plan.
using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class SecondRunProbe
{
    const string Flag = "SecondRunProbe.active";
    static SecondRunProbe() { if (SessionState.GetBool(Flag, false)) EditorApplication.update += Tick; }
    static void Tick() { if (!EditorApplication.isPlaying) return; EditorApplication.update -= Tick; SessionState.SetBool(Flag, false); Go(); }
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Demo Scenes/FactoryDemo.unity");
        SessionState.SetBool(Flag, true);
        Log("entering play mode");
        EditorApplication.EnterPlaymode();
    }
    static void Log(string s) => Debug.Log("[PROBE] " + s);
    static void Exit(int code) { Log("exit " + code); EditorApplication.Exit(code); }
    static async Task Hold(float s) => await Task.Delay((int)(s * 1000f));

    static PickPlaceClient client; static Factory.MachineVise vise; static Factory.ReleasedPartGravity gravity;
    static PickPlaceTaskRecorder recorder; static MTCDashboardPanel dashboard; static Factory.GuidedRecording guided;
    static Factory.DemoGuide guide; static Factory.RobotPlacementController ctl; static Factory.GrabbablePartSet set;
    static Factory.MachineDoors doorsCtl; static Factory.DemoReset reset;
    static UIDocument machineDoc, dashboardDoc, placementDoc;

    static async Task Press(UIDocument doc, string name)
    {
        var button = doc?.rootVisualElement?.Q<Button>(name);
        if (button == null) { Log($"button '{name}' not found"); return; }
        using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = button; button.SendEvent(submit); }
        Log($"pressed '{name}' ({button.text})");
        await Hold(0.3f);
    }

    static void DumpState(string when)
    {
        var pubs = UnityEngine.Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        string attached = string.Join(", ", pubs.Where(p => p.attachedToRobot).Select(p => p.objectId));
        string paused = pubs.Count(p => p.pausePublishing).ToString();
        string billets = string.Join(" | ", set.Parts.Select(p => p == null ? "null" : $"{p.name}@{p.transform.position:F2} active={p.activeInHierarchy} pub={(p.GetComponent<CollisionObjectPublisher>()?.hasBeenPublished)}"));
        Log($"state {when}: guide {guide.Step}, client {client.Phase}/{client.LastOutcome} '{client.LastStatus}', solutions {client.SolutionIds.Count}, " +
            $"vise {vise.State} present={vise.PresentPart?.name} heldByRobot={vise.PresentPartHeldByRobot}, doors {doorsCtl.State}, attached=[{attached}], paused pubs={paused}, " +
            $"recording={recorder.IsRecording}, robot base {ctl.BaseDescription()}");
        Log($"billets {when}: {billets}");
    }

    static async Task DumpStatistics()
    {
        await Hold(1.5f);
        var desc = client.Description; var stats = client.Statistics;
        if (desc == null || stats == null) { Log($"no description/statistics (desc={(desc != null)} stats={(stats != null)}) task={client.TaskId}"); return; }
        var statById = stats.stages.ToDictionary(st => st.id, st => st);
        var byId = desc.stages.ToDictionary(st => st.id, st => st);
        string Path(uint id) { var names = new System.Collections.Generic.List<string>(); while (byId.TryGetValue(id, out var st)) { names.Add(st.name); if (st.parent_id == id) break; id = st.parent_id; } names.Reverse(); return string.Join(" / ", names); }
        uint? firstFailed = null; string firstFailedStage = null;
        foreach (var st in desc.stages)
        {
            statById.TryGetValue(st.id, out var sx);
            if ((sx?.solved?.Length ?? 0) + (sx?.failed?.Length ?? 0) + (sx?.num_failed ?? 0) == 0) continue;
            Log($"stage {st.id} '{Path(st.id)}' solved={sx?.solved?.Length ?? 0} failed={sx?.failed?.Length ?? 0} num_failed={sx?.num_failed ?? 0}");
            if (firstFailed == null && sx?.failed != null && sx.failed.Length > 0 && (sx.solved == null || sx.solved.Length == 0)) { firstFailed = sx.failed[0]; firstFailedStage = Path(st.id); }
        }
        // The failed solution's comment names the reason (the colliding pair for a collision).
        if (firstFailed != null && client.SolutionIds.Count == 0)
        {
            bool done = false; string text = null;
            client.FetchSolution(firstFailed.Value,
                sol => { text = string.Join(" | ", (sol.sub_trajectory ?? new RosMessageTypes.MoveitTaskConstructorMsgs.SubTrajectoryMsg[0]).Select(t => $"[{t.info.stage_id}] cost {t.info.cost:F2} '{t.info.comment}'")); done = true; },
                err => { text = "fetch failed: " + err; done = true; });
            float ts = Time.realtimeSinceStartup;
            while (!done && Time.realtimeSinceStartup - ts < 20f) await Task.Delay(100);
            Log($"first failed solution {firstFailed} of '{firstFailedStage}': {text ?? "no answer"}");
        }
    }

    static async Task PlaceAndConfirm(string run, float dx = 0f, float yaw = 0f, float dz = 0f)
    {
        guide.CompleteWelcome();
        await Hold(0.3f);
        Pose ideal = ctl.DefaultHandlePose;
        ctl.Handle.SetPositionAndRotation(ideal.position + new Vector3(dx, 0f, dz), Quaternion.Euler(0f, ideal.rotation.eulerAngles.y + yaw, 0f));
        await Hold(0.6f);
        Log($"{run}: robot at {ctl.BaseDescription()}; markers {ctl.Markers?.ReachableCount}/{ctl.Markers?.Count}; clear {ctl.RobotClear}{(ctl.RobotClear ? "" : " (" + ctl.RobotBlockedBy + ")")}; guide {guide.Step}");
        await Press(placementDoc, "placementConfirmButton");
        await Hold(2.5f); // the re-ADDs go out
        Transform handT = GameObject.Find("fr3_hand")?.transform;
        Log($"{run}: confirmed; guide {guide.Step}; doors {doorsCtl.State}; unity clearance {(ctl.RobotClear ? "clear" : ctl.RobotBlockedBy)}; unity hand at {(handT != null ? handT.position.ToString("F2") : "?")} (base {ctl.BaseDescription()})");
        // The obstacle-carved ROS map arrives a moment after Confirm: does it still call every target reachable from here?
        float tm = Time.realtimeSinceStartup;
        while (!(ctl.Reachability != null && ctl.Reachability.MapSource.StartsWith("topic")) && Time.realtimeSinceStartup - tm < 10f) await Task.Delay(200);
        if (ctl.Markers != null)
        {
            ctl.Markers.Refresh();
            var dex = new System.Collections.Generic.List<string>();
            for (int i = 0; i < ctl.Markers.Count; i++) dex.Add($"{ctl.Markers.TargetLabel(i)}={ctl.Markers.TopDownDexterity[i]:F2}");
            Log($"{run}: ROS map '{ctl.Reachability?.MapSource}' after {Time.realtimeSinceStartup - tm:F1} s: markers {ctl.Markers.ReachableCount}/{ctl.Markers.Count} [{string.Join(", ", dex)}]");
        }
        foreach (string group in new[] { "panda_arm", "hand" })
        {
            bool answered = false; string text = null; bool valid = false;
            Factory.RobotClearanceCheck.QueryRosStateValidity(group, (v, t) => { valid = v; text = t; answered = true; });
            float tv = Time.realtimeSinceStartup;
            while (!answered && Time.realtimeSinceStartup - tv < 10f) await Task.Delay(100);
            Log($"{run}: ROS state validity for '{group}': {(answered ? (valid ? "valid" : "IN COLLISION: " + text) : "no answer")}");
        }
        if (!doorsCtl.IsOpen) { await Press(machineDoc, "doorOpenButton"); while (!doorsCtl.IsOpen) await Task.Delay(100); }
        await Hold(0.5f);
    }

    /// <summary>Select, grab (records), set in the target pose, let go (plans). Returns solution count.</summary>
    static async Task<int> DemonstrateAndPlan(string run, GameObject part, Vector3 hover, Quaternion hoverRot)
    {
        DumpState($"{run} before demonstration");
        SelectionManager.Instance?.SetSelectedObject(part);
        await Hold(0.5f);
        guided.NotifyGrabbed(part);
        guide.ExternalGrab = part;
        await Hold(0.5f);
        Log($"{run}: grab: recording={recorder.IsRecording} startedByGuide={guided.StartedByGuide} guide {guide.Step}");
        Vector3 p0 = part.transform.position; Quaternion r0 = part.transform.rotation;
        float dur = 1.5f, ts = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - ts < dur)
        {
            float k = Mathf.Clamp01((Time.realtimeSinceStartup - ts) / dur);
            Vector3 mid = new Vector3((p0.x + hover.x) * 0.5f, Mathf.Max(p0.y, hover.y) + 0.25f, (p0.z + hover.z) * 0.5f);
            part.transform.SetPositionAndRotation(Vector3.Lerp(Vector3.Lerp(p0, mid, k), Vector3.Lerp(mid, hover, k), k), Quaternion.Slerp(r0, hoverRot, k));
            await Task.Delay(30);
        }
        part.transform.SetPositionAndRotation(hover, hoverRot);
        await Hold(0.6f);
        guide.ExternalGrab = null;
        guided.NotifyReleased(part);
        ts = Time.realtimeSinceStartup;
        while (recorder.IsRecording && Time.realtimeSinceStartup - ts < 5f) await Task.Delay(100);
        Log($"{run}: release: recording={recorder.IsRecording} after {Time.realtimeSinceStartup - ts:F1} s; client {client.Phase}; guide {guide.Step}; notice '{guide.Notice}'");
        ts = Time.realtimeSinceStartup;
        while ((client.Busy || (client.SolutionIds.Count == 0 && client.LastOutcome == PickPlaceOutcome.None)) && Time.realtimeSinceStartup - ts < 120f) await Task.Delay(200);
        Log($"{run}: plan: outcome={client.LastOutcome} status='{client.LastStatus}' solutions={client.SolutionIds.Count} after {Time.realtimeSinceStartup - ts:F1} s; guide {guide.Step}; notice '{guide.Notice}'");
        await DumpStatistics();
        if (client.SolutionIds.Count == 0) return 0;
        float tsel = Time.realtimeSinceStartup;
        while ((dashboard.SelectedSolutionId == null || dashboard.SelectedSolution == null) && Time.realtimeSinceStartup - tsel < 40f) await Task.Delay(100);
        Log($"{run}: auto-selected {dashboard.SelectedSolutionId} fetched={dashboard.SelectedSolution != null} after {Time.realtimeSinceStartup - tsel:F1} s");
        return client.SolutionIds.Count;
    }

    static async Task Execute(string run, GameObject part, float restartAfter = -1f)
    {
        await Press(dashboardDoc, "mtcExecuteButton");
        float ts = Time.realtimeSinceStartup;
        while (client.Phase == PickPlacePhase.Idle && Time.realtimeSinceStartup - ts < 10f) await Task.Delay(100);
        if (restartAfter > 0f)
        {
            await Hold(restartAfter);
            var pubs = UnityEngine.Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Log($"{run}: restarting mid-execution at {Time.realtimeSinceStartup - ts:F1} s; attached=[{string.Join(", ", pubs.Where(q => q.attachedToRobot).Select(q => q.objectId))}]");
            reset.Restart();
            while (reset.InProgress && Time.realtimeSinceStartup - ts < 60f) await Task.Delay(100);
            Log($"{run}: restart done; client {client.Phase}/{client.LastOutcome} '{client.LastStatus}'");
            return;
        }
        while (client.Busy && Time.realtimeSinceStartup - ts < 180f) await Task.Delay(200);
        Log($"{run}: execute: outcome={client.LastOutcome} status='{client.LastStatus}' after {Time.realtimeSinceStartup - ts:F1} s");
        ts = Time.realtimeSinceStartup;
        while (gravity.SettlingCount == 0 && Time.realtimeSinceStartup - ts < 25f) await Task.Delay(100);
        while (gravity.SettlingCount > 0) await Task.Delay(100);
        await Hold(1.0f);
        vise.ScanPresence();
        Log($"{run}: after execute: part at {part.transform.position:F3}; vise present={vise.PresentPart?.name}; guide {guide.Step}");
    }

    static async void Go()
    {
        try
        {
            string mode = Environment.GetEnvironmentVariable("RUN1") ?? "full";
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 60f)
            {
                client = UnityEngine.Object.FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
                if (client != null && client.Available) break;
                await Task.Delay(500);
            }
            if (client == null || !client.Available) { Log("client not ready: " + client?.LastStatus); Exit(2); return; }
            ctl = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementController>();
            vise = UnityEngine.Object.FindFirstObjectByType<Factory.MachineVise>();
            gravity = UnityEngine.Object.FindFirstObjectByType<Factory.ReleasedPartGravity>();
            set = UnityEngine.Object.FindFirstObjectByType<Factory.GrabbablePartSet>();
            recorder = UnityEngine.Object.FindFirstObjectByType<PickPlaceTaskRecorder>(FindObjectsInactive.Include);
            dashboard = UnityEngine.Object.FindFirstObjectByType<MTCDashboardPanel>(FindObjectsInactive.Include);
            var panel = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoorPanel>(FindObjectsInactive.Include);
            var placementPanel = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementPanel>(FindObjectsInactive.Include);
            doorsCtl = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoors>();
            guided = UnityEngine.Object.FindFirstObjectByType<Factory.GuidedRecording>();
            guide = UnityEngine.Object.FindFirstObjectByType<Factory.DemoGuide>();
            reset = UnityEngine.Object.FindFirstObjectByType<Factory.DemoReset>();
            Transform conveyorAnchor = GameObject.Find("FR3 Station")?.transform.Find("Conveyor/DropAnchor");
            if (ctl == null || vise == null || gravity == null || set == null || recorder == null || dashboard == null || panel == null || placementPanel == null || conveyorAnchor == null || guided == null || guide == null || reset == null)
            { Log("scene incomplete"); Exit(2); return; }
            machineDoc = panel.GetComponent<UIDocument>(); dashboardDoc = dashboard.GetComponent<UIDocument>(); placementDoc = placementPanel.GetComponent<UIDocument>();
            typeof(PickPlaceClient).GetField("getSolutionTimeoutSeconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(client, 20f);
            Log($"mode {mode}");
            await Hold(2.5f); // a user takes seconds to reach Confirm: let the startup planning-scene clear land first

            Transform window = vise.jawWindow;
            Vector3 hover = window.position + new Vector3(0.01f, 0.03f + 0.005f + 0.015f, 0.012f);
            Quaternion hoverRot = Quaternion.AngleAxis(10f, Vector3.up);

            int runs = int.TryParse(Environment.GetEnvironmentVariable("RUNS"), out int nr) ? Mathf.Max(1, nr) : 2;
            float cancelAt = float.TryParse(Environment.GetEnvironmentVariable("CANCEL_AT"), out float ca) ? ca : 4f;
            float dx2 = float.TryParse(Environment.GetEnvironmentVariable("RUN2_DX"), out float fdx) ? fdx : 0f;
            float dz2 = float.TryParse(Environment.GetEnvironmentVariable("RUN2_DZ"), out float fdz) ? fdz : 0f;
            float yaw2 = float.TryParse(Environment.GetEnvironmentVariable("RUN2_YAW"), out float fyaw) ? fyaw : 0f;
            Log($"runs {runs}, mode {mode}, cancel at {cancelAt} s, later placements offset dx={dx2} dz={dz2} yaw={yaw2}");
            Vector3 beltHover = conveyorAnchor.position + Vector3.up * 0.015f;

            for (int run = 1; run <= runs; run++)
            {
                string tag = $"run{run}";
                await PlaceAndConfirm(tag, run == 1 ? 0f : dx2, run == 1 ? 0f : yaw2, run == 1 ? 0f : dz2);
                GameObject part = guide.TopBillet();
                Log($"{tag}: top billet {part?.name}");
                int n = await DemonstrateAndPlan(tag, part, hover, hoverRot);
                if (n == 0)
                {
                    DumpState($"{tag} plan failed");
                    Log($"{tag} load plan FAILED: retrying once with the same demonstration");
                    int nb = await DemonstrateAndPlan($"{tag}-retry", part, hover, hoverRot);
                    if (nb == 0) { Log($"{tag} FAILED twice"); Exit(run == 1 ? 5 : 7); return; }
                    Log($"{tag}: plan OK on retry");
                }
                else Log($"{tag} load plan OK");
                if (run == runs) break;

                if (mode == "cancel")
                    await Execute(tag, part, restartAfter: cancelAt); // restarts inside
                else
                {
                    if (mode != "plan")
                    {
                        await Execute(tag, part);
                        if (mode == "full")
                        {
                            await Press(machineDoc, "viseClampButton");
                            while (vise.IsMoving) await Task.Delay(100);
                            await Press(machineDoc, "doorCloseButton");
                            float ts = Time.realtimeSinceStartup;
                            while (!guide.Machined && Time.realtimeSinceStartup - ts < guide.MachiningSeconds + 15f) await Task.Delay(200);
                            Log($"{tag}: machined={guide.Machined}; guide {guide.Step}");
                            await Press(machineDoc, "doorOpenButton");
                            while (!doorsCtl.IsOpen) await Task.Delay(100);
                            await Press(machineDoc, "viseReleaseButton");
                            while (vise.IsMoving) await Task.Delay(100);
                            await Hold(0.5f);
                            int nu = await DemonstrateAndPlan($"{tag}-unload", part, beltHover, part.transform.rotation);
                            if (nu > 0) await Execute($"{tag}-unload", part);
                            else Log($"{tag} unload plan FAILED");
                        }
                    }
                    DumpState($"{tag} end");
                    reset.Restart();
                }
                float tr = Time.realtimeSinceStartup;
                while (reset.InProgress && Time.realtimeSinceStartup - tr < 20f) await Task.Delay(100);
                await Hold(1.5f);
                while (doorsCtl.IsMoving) await Task.Delay(100);
                DumpState($"after restart {run}");
            }
            Log($"all {runs} runs planned OK");
            Exit(0);
        }
        catch (Exception e) { Log("EXCEPTION " + e); Exit(3); }
    }
}
