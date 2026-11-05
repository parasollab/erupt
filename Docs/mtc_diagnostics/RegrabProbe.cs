// Headless reproduction of "the plan sometimes fails when the billet takes a couple of tries" (2026-11-05). Not part of
// the project: copy to Assets/Editor/ to use (see README). Needs the ROS stack; run WITHOUT -nographics.
// Flow: placement + confirm, a clean demonstration (control) and its plan; Restart demo; placement + confirm again, then a
// fumbled demonstration: grab, release hovering outside the vise, a stray trigger pull that clears the selection (as a
// novice does), re-select, re-grab, set between the jaws, release. Logs the recorder's pick-pose snapshot after the
// re-selection, where the billet is reset to when the recording stops, every MOVE published for it, and the plan outcome.
// VERDICT lines summarise. Exit 0 always (verification, not a test).
using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class RegrabProbe
{
    const string Flag = "RegrabProbe.active";
    static RegrabProbe() { if (SessionState.GetBool(Flag, false)) EditorApplication.update += Tick; }
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

    static readonly System.Collections.Generic.List<string> captured = new System.Collections.Generic.List<string>();
    static string watchId;
    static void OnLog(string msg, string stack, LogType type)
    {
        if (watchId == null) return;
        if ((msg.StartsWith("Publishing collision object") && msg.Contains($"'{watchId}'")) || msg.StartsWith("PickPlaceTaskRecorder: watching") || msg.StartsWith("PickPlaceTaskRecorder: reset"))
            captured.Add($"{Time.realtimeSinceStartup:F2}s {msg}");
    }

    static Vector3 PickPoseOf(PickPlaceTaskRecorder r) =>
        (Vector3)typeof(PickPlaceTaskRecorder).GetField("pickWorldPos", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(r);

    static async Task Carry(GameObject part, Vector3 to, Quaternion toRot, float dur = 1.2f)
    {
        Vector3 p0 = part.transform.position; Quaternion r0 = part.transform.rotation;
        float ts = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - ts < dur)
        {
            float k = Mathf.Clamp01((Time.realtimeSinceStartup - ts) / dur);
            Vector3 mid = new Vector3((p0.x + to.x) * 0.5f, Mathf.Max(p0.y, to.y) + 0.2f, (p0.z + to.z) * 0.5f);
            part.transform.SetPositionAndRotation(Vector3.Lerp(Vector3.Lerp(p0, mid, k), Vector3.Lerp(mid, to, k), k), Quaternion.Slerp(r0, toRot, k));
            await Task.Delay(30);
        }
        part.transform.SetPositionAndRotation(to, toRot);
    }

    static async Task<int> WaitPlan(string run)
    {
        float ts = Time.realtimeSinceStartup;
        while ((client.Busy || (client.SolutionIds.Count == 0 && client.LastOutcome == PickPlaceOutcome.None)) && Time.realtimeSinceStartup - ts < 120f) await Task.Delay(200);
        Log($"{run}: plan: outcome={client.LastOutcome} status='{client.LastStatus}' solutions={client.SolutionIds.Count} after {Time.realtimeSinceStartup - ts:F1} s; guide {guide.Step}; notice '{guide.Notice}'");
        await DumpStatistics();
        return client.SolutionIds.Count;
    }

    /// <summary>Grab, release hovering outside the vise, stray deselect + re-select, re-grab, set between the jaws, release.</summary>
    static async Task<int> DemonstrateWithFumble(string run, GameObject part, Vector3 hover, Quaternion hoverRot)
    {
        DumpState($"{run} before demonstration");
        Vector3 standPose = part.transform.position;
        var pub = part.GetComponent<CollisionObjectPublisher>();
        watchId = pub.objectId; captured.Clear();
        SelectionManager.Instance?.SetSelectedObject(part);
        await Hold(0.5f);
        guided.NotifyGrabbed(part);
        guide.ExternalGrab = part;
        await Hold(0.3f);
        Log($"{run}: first grab: recording={recorder.IsRecording} pickPose={PickPoseOf(recorder):F3} (stand {standPose:F3})");
        // Fumble: let go hovering over the stand, a hand's width up and toward the machine.
        Vector3 fumble = standPose + new Vector3(0.05f, 0.18f, 0.12f);
        await Carry(part, fumble, Quaternion.AngleAxis(25f, Vector3.up), 0.8f);
        guide.ExternalGrab = null;
        guided.NotifyReleased(part);
        await Hold(1.3f); // past the settle: released outside the zone, still recording
        Log($"{run}: after the fumbled release: recording={recorder.IsRecording} guide {guide.Step} part at {part.transform.position:F3}");
        // A stray trigger pull at the machine clears the selection (SelectionManager.TrySelect on a non-selectable hit).
        SelectionManager.Instance.ClearSelection();
        await Hold(0.2f);
        var grab = part.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        Log($"{run}: after the stray deselect: selected={SelectionManager.Instance.SelectedObject?.name ?? "none"} grabInteractable.enabled={grab?.enabled} recording={recorder.IsRecording} pickPose={PickPoseOf(recorder):F3}");
        // The user re-selects the hovering billet to be able to grip it again.
        SelectionManager.Instance.SetSelectedObject(part);
        await Hold(0.2f);
        Vector3 pickAfterReselect = PickPoseOf(recorder);
        bool resnapshotted = Vector3.Distance(pickAfterReselect, standPose) > 0.01f;
        Log($"{run}: after re-select: grabInteractable.enabled={grab?.enabled} pickPose={pickAfterReselect:F3} -> {(resnapshotted ? "RE-SNAPSHOTTED at the hover pose" : "still the stand pose")}");
        // Second try: grab again, carry to the vise, let go.
        guided.NotifyGrabbed(part);
        guide.ExternalGrab = part;
        await Hold(0.3f);
        await Carry(part, hover, hoverRot, 1.5f);
        await Hold(0.6f);
        guide.ExternalGrab = null;
        guided.NotifyReleased(part);
        float ts = Time.realtimeSinceStartup;
        while (recorder.IsRecording && Time.realtimeSinceStartup - ts < 5f) await Task.Delay(100);
        await Hold(0.5f); // let the publisher's next tick run
        bool backOnStand = Vector3.Distance(part.transform.position, standPose) < 0.01f;
        Log($"{run}: recording stopped after {Time.realtimeSinceStartup - ts:F1} s; billet now at {part.transform.position:F3} -> {(backOnStand ? "back on the stand" : "NOT on the stand (hovering where it was fumbled)")}");
        foreach (string line in captured) Log($"{run}: captured: {line}");
        bool movedMidAir = captured.Any(l => l.Contains("Publishing collision object"));
        Log($"{run}: VERDICT unity: pick pose re-snapshotted={resnapshotted}, billet reset off the stand={!backOnStand}, MOVE published after the stop={movedMidAir}");
        int n = await WaitPlan(run);
        Log($"{run}: VERDICT plan: {(n > 0 ? "OK" : "FAILED")} ({n} solutions, outcome {client.LastOutcome})");
        watchId = null;
        return n;
    }

    static async void Go()
    {
        try
        {
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
            if (ctl == null || vise == null || gravity == null || set == null || recorder == null || dashboard == null || panel == null || placementPanel == null || guided == null || guide == null || reset == null)
            { Log("scene incomplete"); Exit(2); return; }
            machineDoc = panel.GetComponent<UIDocument>(); dashboardDoc = dashboard.GetComponent<UIDocument>(); placementDoc = placementPanel.GetComponent<UIDocument>();
            typeof(PickPlaceClient).GetField("getSolutionTimeoutSeconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(client, 20f);
            Application.logMessageReceived += OnLog;
            await Hold(2.5f);

            Transform window = vise.jawWindow;
            Vector3 hover = window.position + new Vector3(0.01f, 0.03f + 0.005f + 0.015f, 0.012f);
            Quaternion hoverRot = Quaternion.AngleAxis(10f, Vector3.up);
            int reps = int.TryParse(Environment.GetEnvironmentVariable("REPS"), out int nr) ? Mathf.Max(1, nr) : 1;
            bool control = (Environment.GetEnvironmentVariable("CONTROL") ?? "1") != "0";

            if (control)
            {
                await PlaceAndConfirm("control");
                GameObject part0 = guide.TopBillet();
                SelectionManager.Instance?.SetSelectedObject(part0);
                await Hold(0.5f);
                guided.NotifyGrabbed(part0); guide.ExternalGrab = part0;
                await Hold(0.3f);
                await Carry(part0, hover, hoverRot, 1.5f);
                await Hold(0.6f);
                guide.ExternalGrab = null; guided.NotifyReleased(part0);
                float ts = Time.realtimeSinceStartup;
                while (recorder.IsRecording && Time.realtimeSinceStartup - ts < 5f) await Task.Delay(100);
                int n0 = await WaitPlan("control");
                Log($"control: VERDICT plan: {(n0 > 0 ? "OK" : "FAILED")} ({n0} solutions)");
                reset.Restart();
                ts = Time.realtimeSinceStartup;
                while (reset.InProgress && Time.realtimeSinceStartup - ts < 60f) await Task.Delay(100);
                await Hold(2f);
            }
            for (int rep = 1; rep <= reps; rep++)
            {
                string tag = reps > 1 ? $"fumble{rep}" : "fumble";
                await PlaceAndConfirm(tag);
                GameObject part = guide.TopBillet();
                await DemonstrateWithFumble(tag, part, hover, hoverRot);
                if (rep < reps)
                {
                    reset.Restart();
                    float ts = Time.realtimeSinceStartup;
                    while (reset.InProgress && Time.realtimeSinceStartup - ts < 60f) await Task.Delay(100);
                    await Hold(2f);
                }
            }
            Exit(0);
        }
        catch (Exception e) { Log("EXCEPTION " + e); Exit(3); }
    }
}
