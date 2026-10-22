// Headless MTC probe for the vise load. Not part of the project: copy to Assets/Editor/ to use (see README).
// Plays FactoryDemo against the live ROS stack, confirms the robot placement, opens the doors,
// dumps the planning scene and sends real /pick_place goals: the top billet into the open jaws
// (5 cm side across, then 8 cm side across) and, as a control, onto the conveyor.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Erupt.Ros;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;

[InitializeOnLoad]
public static class VisePlanProbe
{
    const string Flag = "VisePlanProbe.active";

    static VisePlanProbe()
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
    static void Exit(int code) { Log("exit " + code); EditorApplication.Exit(code); }

    static async void Go()
    {
        try
        {
            float t0 = Time.realtimeSinceStartup;
            PickPlaceClient client = null;
            while (Time.realtimeSinceStartup - t0 < 60f)
            {
                client = UnityEngine.Object.FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
                if (client != null && client.Available) break;
                await Task.Delay(500);
            }
            if (client == null || !client.Available) { Log("client not ready: " + client?.LastStatus); Exit(2); return; }
            Log($"client ready after {Time.realtimeSinceStartup - t0:F1}s status={client.LastStatus}");

            var ctl = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementController>();
            var vise = UnityEngine.Object.FindFirstObjectByType<Factory.MachineVise>();
            var doors = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoors>();
            var set = UnityEngine.Object.FindFirstObjectByType<Factory.GrabbablePartSet>();
            if (ctl != null)
            {
                // The phase opens with the robot at a deliberately bad start pose; put it on the ideal spot first.
                // PROBE_BASE_DZ (metres, world z) tries the ideal spot moved toward/away from the doors.
                float dz = float.TryParse(Environment.GetEnvironmentVariable("PROBE_BASE_DZ"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? parsed : 0f;
                float dx = float.TryParse(Environment.GetEnvironmentVariable("PROBE_BASE_DX"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsedX) ? parsedX : 0f;
                ctl.Handle.SetPositionAndRotation(ctl.DefaultHandlePose.position + new Vector3(dx, 0f, dz), ctl.DefaultHandlePose.rotation);
                await Task.Delay(400);
                Log($"robot at the ideal spot: {ctl.BaseDescription()}; reachable targets {ctl.Markers?.ReachableCount}/{ctl.Markers?.Count}; warnings: {string.Join(" | ", ctl.Warnings())}");
                ctl.Confirm();
                Log("placement confirmed");
            }
            await Task.Delay(500);
            if (doors != null) { doors.Open(); Log("doors opening"); }
            await Task.Delay(7000); // ADDs after confirm (table at 0.5 Hz), door MOVEs after the slide

            foreach (var pub in UnityEngine.Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                Log($"publisher id={pub.objectId} published={pub.hasBeenPublished} paused={pub.pausePublishing} attached={pub.attachedToRobot} pos={pub.transform.position} compound={(pub.compoundRoots?.Length ?? 0)}");
            await CheckCurrentState("doors OPEN");

            GameObject part = set?.Parts.LastOrDefault(p => p != null);
            if (part == null || vise == null) { Log("no billet / vise"); Exit(4); return; }

            // PROBE_GRID="x,z;x,z;..." (world, robot axis on the floor): for each base pose, re-confirm the
            // placement there and ask MoveIt about the ready pose, the place pose over the vise and the
            // retreat above it. No planning. The stock stand and conveyor stay where they are.
            string grid = Environment.GetEnvironmentVariable("PROBE_GRID");
            if (!string.IsNullOrEmpty(grid) && ctl != null)
            {
                var partPubG = part.GetComponent<CollisionObjectPublisher>();
                foreach (string cell in grid.Split(';'))
                {
                    string[] xy = cell.Split(',');
                    if (xy.Length != 2) continue;
                    float gx = float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), gz = float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture);
                    ctl.BeginReposition();
                    await Task.Delay(300);
                    ctl.Handle.SetPositionAndRotation(new Vector3(gx, 0f, gz), Quaternion.identity);
                    await Task.Delay(400);
                    ctl.Confirm();
                    await Task.Delay(7000);
                    Transform originG = partPubG.worldOrigin.transform;
                    Log($"##### base at ({gx:F2}, {gz:F2}): {ctl.BaseDescription()}; markers {ctl.Markers?.ReachableCount}/{ctl.Markers?.Count}");
                    await CheckCurrentState($"base ({gx:F2},{gz:F2}) ready pose");
                    Vector3 hover = vise.jawWindow.position + new Vector3(0.01f, 0.03f + 0.045f, 0.012f);
                    PointMsg rp = RosUnityConversion.UnityToRosPosition(originG.InverseTransformPoint(hover));
                    var obj = new Vector3((float)rp.x, (float)rp.y, (float)rp.z);
                    Quaternion td = Rpy(Math.PI, 0, 0);
                    await IkSweep(obj, td, $"base ({gx:F2},{gz:F2}) place over the vise");
                    await IkSweep(obj + new Vector3(0f, 0f, 0.15f), td, $"base ({gx:F2},{gz:F2}) retreat +0.15");
                }
                Exit(0);
                return;
            }
            var partPub = part.GetComponent<CollisionObjectPublisher>();
            Transform origin = partPub.worldOrigin.transform;
            Transform window = vise.jawWindow;
            Log($"vise window {window.position}, opening {vise.openGap}, part {partPub.objectId} at {part.transform.position} rot {part.transform.rotation.eulerAngles}");
            client.OnStatus += s => Log("status: " + s);

            // A: hover 4.5 cm above the parallels, 5 cm side across the jaws, 8 deg skewed, a little off centre.
            Vector3 hoverA = window.position + new Vector3(0.01f, 0.03f + 0.045f, 0.012f);
            Quaternion rotA = Quaternion.AngleAxis(8f, Vector3.up);
            await Plan(client, partPub, origin, hoverA, rotA, "A: into the vise, 5 cm side across the jaws");
            {
                Vector3 relA = origin.InverseTransformPoint(hoverA);
                PointMsg rosA = RosUnityConversion.UnityToRosPosition(relA);
                var objA = new Vector3((float)rosA.x, (float)rosA.y, (float)rosA.z);
                Quaternion topDown = Rpy(Math.PI, 0, 0);
                await IkSweep(objA, topDown, "place A (part centre)");
                await IkSweep(objA + new Vector3(0f, 0f, 0.15f), topDown, "retreat above place A (+0.15 z)");
                Vector3 relPick = origin.InverseTransformPoint(part.transform.position);
                PointMsg rosPick = RosUnityConversion.UnityToRosPosition(relPick);
                await IkSweep(new Vector3((float)rosPick.x, (float)rosPick.y, (float)rosPick.z), topDown, "pick (for reference)");
            }

            // B: as stacked (yawed -90 with the base): 8 cm side across the jaws, 2 cm per side to the plates.
            Vector3 hoverB = window.position + new Vector3(0f, 0.03f + 0.045f, 0f);
            Quaternion rotB = origin.rotation;
            await Plan(client, partPub, origin, hoverB, rotB, "B: into the vise, 8 cm side across the jaws");

            // C: control, onto the conveyor's drop anchor (no vise involved).
            Transform conveyorAnchor = GameObject.Find("FR3 Station")?.transform.Find("Conveyor/DropAnchor");
            if (conveyorAnchor != null)
                await Plan(client, partPub, origin, conveyorAnchor.position + Vector3.up * 0.02f, origin.rotation, "C: control, onto the conveyor");

            Exit(0);
        }
        catch (Exception e)
        {
            Log("EXCEPTION " + e);
            Exit(3);
        }
    }

    static async Task Plan(PickPlaceClient client, CollisionObjectPublisher partPub, Transform origin, Vector3 worldPos, Quaternion worldRot, string label)
    {
        while (client.Busy) await Task.Delay(500);
        await Task.Delay(2500);
        Vector3 relPos = origin.InverseTransformPoint(worldPos);
        Quaternion relRot = Quaternion.Inverse(origin.rotation) * worldRot;
        PoseStampedMsg placePose = client.PlacePose(RosUnityConversion.UnityToRosPosition(relPos), RosUnityConversion.UnityToRosQuaternion(relRot));
        Log($"===== {label}: object {partPub.objectId} pick(world)={partPub.transform.position} place(world)={worldPos} yaw={worldRot.eulerAngles.y:F0} " +
            $"place(ros)=({placePose.pose.position.x:F3},{placePose.pose.position.y:F3},{placePose.pose.position.z:F3}) frame={placePose.header.frame_id}");
        var plan = (Task)typeof(PickPlaceClient).GetMethod("PlanAsync").Invoke(client, new object[] { partPub.objectId, placePose, null });
        try { await plan; }
        catch (Exception e) { Log("plan threw: " + e.GetBaseException().Message); }
        Log($"RESULT {label}: outcome={client.LastOutcome} status='{client.LastStatus}' task={client.TaskId} solutions={client.SolutionIds.Count}");
        await Task.Delay(1500);
        await DumpStatistics(client);
    }

    static async Task DumpStatistics(PickPlaceClient client)
    {
        var desc = client.Description;
        var stats = client.Statistics;
        if (desc == null || stats == null) { Log($"no description/statistics (desc={(desc != null)} stats={(stats != null)}) task={client.TaskId}"); return; }
        var statById = stats.stages.ToDictionary(s => s.id, s => s);
        var byId = desc.stages.ToDictionary(s => s.id, s => s);
        string Path(uint id)
        {
            var names = new List<string>();
            while (byId.TryGetValue(id, out var s)) { names.Add(s.name); if (s.parent_id == id) break; id = s.parent_id; }
            names.Reverse();
            return string.Join(" / ", names);
        }
        foreach (var s in desc.stages)
        {
            statById.TryGetValue(s.id, out var st);
            if ((st?.solved?.Length ?? 0) + (st?.failed?.Length ?? 0) + (st?.num_failed ?? 0) == 0) continue;
            Log($"stage {s.id} '{Path(s.id)}' solved={st?.solved?.Length ?? 0} failed={st?.failed?.Length ?? 0} num_failed={st?.num_failed ?? 0}");
        }
        // Failure comments are only available while the task is alive (the server drops a failed plan).
        int fetched = 0;
        foreach (var st in stats.stages)
        {
            if (client.SolutionIds.Count == 0) break;
            if (st.failed == null || st.failed.Length == 0 || fetched >= 6) continue;
            fetched++;
            uint id = st.failed[0];
            var tcs = new TaskCompletionSource<SolutionMsg>();
            client.FetchSolution(id, sol => tcs.TrySetResult(sol), err => { Log($"fetch {id} failed: {err}"); tcs.TrySetResult(null); });
            var done = await Task.WhenAny(tcs.Task, Task.Delay(6000));
            var sol = done == tcs.Task ? tcs.Task.Result : null;
            if (sol == null) { Log($"failed solution {id} of stage {st.id}: no data"); continue; }
            var comments = new List<string>();
            foreach (var ss in sol.sub_solution ?? Array.Empty<SubSolutionMsg>()) if (!string.IsNullOrEmpty(ss.info?.comment)) comments.Add($"[{ss.info.stage_id}] {ss.info.comment}");
            foreach (var tr in sol.sub_trajectory ?? Array.Empty<SubTrajectoryMsg>()) if (!string.IsNullOrEmpty(tr.info?.comment)) comments.Add($"[{tr.info.stage_id}] {tr.info.comment}");
            Log($"failed solution {id} of stage {st.id} '{Path(st.id)}': {(comments.Count == 0 ? "(no comment)" : string.Join(" | ", comments))}");
        }
    }

    static RobotStateMsg lastRobotState;

    static Quaternion Rpy(double r, double p, double y) =>
        Quaternion.AngleAxis((float)(r * Mathf.Rad2Deg), Vector3.right) *
        Quaternion.AngleAxis((float)(p * Mathf.Rad2Deg), Vector3.up) *
        Quaternion.AngleAxis((float)(y * Mathf.Rad2Deg), Vector3.forward);

    static bool servicesRegistered;
    static async Task<T> Call<TReq, T>(string service, TReq req)
        where TReq : Unity.Robotics.ROSTCPConnector.MessageGeneration.Message
        where T : Unity.Robotics.ROSTCPConnector.MessageGeneration.Message, new()
    {
        var tcs = new TaskCompletionSource<T>();
        RosBus.Instance.SendServiceMessage<T>(service, req, r => tcs.TrySetResult(r));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(6000));
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    // Mirrors MTC GeneratePlacePose + ComputeIK with the top-down grasp_frame_transform
    // [0,0,0.1,pi,0,0]: link8 sits 0.1 above the object centre with z down, any yaw about world z.
    static async Task IkSweep(Vector3 objPos, Quaternion grasp, string label)
    {
        var ros = RosBus.Instance;
        if (!servicesRegistered)
        {
            try { ros.RegisterRosService<GetPositionIKRequest, GetPositionIKResponse>("/compute_ik"); } catch { }
            try { ros.RegisterRosService<GetStateValidityRequest, GetStateValidityResponse>("/check_state_validity"); } catch { }
            servicesRegistered = true;
        }
        Quaternion gInv = Quaternion.Inverse(grasp);
        Vector3 off = gInv * new Vector3(0f, 0f, -0.1f);
        int ikOk = 0, valid = 0, noResp = 0;
        var contacts = new Dictionary<string, int>();
        var seed = new RobotStateMsg
        {
            is_diff = true,
            joint_state = new RosMessageTypes.Sensor.JointStateMsg { name = new[] { "panda_finger_joint1", "panda_finger_joint2" }, position = new[] { 0.035, 0.035 } }
        };
        for (int i = 0; i < 24; i++)
        {
            Quaternion qz = Quaternion.AngleAxis(i * 15f, Vector3.forward);
            Quaternion q = qz * gInv;
            Vector3 p = objPos + qz * off;
            var pose = new PoseStampedMsg(new RosMessageTypes.Std.HeaderMsg(new RosMessageTypes.BuiltinInterfaces.TimeMsg(), "world"),
                new PoseMsg(new PointMsg(p.x, p.y, p.z), new QuaternionMsg(q.x, q.y, q.z, q.w)));
            var ik = new GetPositionIKRequest(new PositionIKRequestMsg
            {
                group_name = "panda_arm", robot_state = seed, avoid_collisions = false, ik_link_name = "panda_link8",
                pose_stamped = pose, ik_link_names = new string[0], pose_stamped_vector = new PoseStampedMsg[0],
                timeout = new RosMessageTypes.BuiltinInterfaces.DurationMsg { sec = 0, nanosec = 200000000 }
            });
            var resp = await Call<GetPositionIKRequest, GetPositionIKResponse>("/compute_ik", ik);
            if (resp == null) { noResp++; continue; }
            if (resp.error_code.val != 1) continue;
            ikOk++;
            var val = await Call<GetStateValidityRequest, GetStateValidityResponse>("/check_state_validity",
                new GetStateValidityRequest { robot_state = resp.solution, group_name = "", constraints = new ConstraintsMsg() });
            if (val == null) { noResp++; continue; }
            if (val.valid) { valid++; continue; }
            var pairs = new HashSet<string>();
            foreach (var c in val.contacts ?? Array.Empty<ContactInformationMsg>()) pairs.Add($"{c.contact_body_1} x {c.contact_body_2}");
            foreach (var pair in pairs) { contacts.TryGetValue(pair, out int n); contacts[pair] = n + 1; }
        }
        Log($"SWEEP {label} at ros ({objPos.x:F3},{objPos.y:F3},{objPos.z:F3}): IK solvable {ikOk}/24, collision-free {valid}/24, no response {noResp}; colliding pairs: " +
            (contacts.Count == 0 ? "none" : string.Join("; ", contacts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"))));
    }

    // Is the robot's current (ready) pose collision-free in MoveIt's scene, and if not, with what?
    static async Task CheckCurrentState(string label)
    {
        await DumpPlanningScene();
        if (lastRobotState == null) { Log($"check state ({label}): no robot state from the planning scene"); return; }
        var ros = RosBus.Instance;
        try { ros.RegisterRosService<GetStateValidityRequest, GetStateValidityResponse>("/check_state_validity"); } catch { }
        var tcs = new TaskCompletionSource<GetStateValidityResponse>();
        ros.SendServiceMessage<GetStateValidityResponse>("/check_state_validity",
            new GetStateValidityRequest { robot_state = lastRobotState, group_name = "", constraints = new ConstraintsMsg() }, r => tcs.TrySetResult(r));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
        var val = done == tcs.Task ? tcs.Task.Result : null;
        if (val == null) { Log($"check state ({label}): no response"); return; }
        var pairs = new HashSet<string>();
        foreach (var c in val.contacts ?? Array.Empty<ContactInformationMsg>()) pairs.Add($"{c.contact_body_1} x {c.contact_body_2} (depth {c.depth:F3})");
        Log($"CHECK current state ({label}): valid={val.valid}; contacts: {(pairs.Count == 0 ? "none" : string.Join("; ", pairs))}");
    }

    static async Task DumpPlanningScene()
    {
        var ros = RosBus.Instance;
        var tcs = new TaskCompletionSource<GetPlanningSceneResponse>();
        try
        {
            ros.RegisterRosService<GetPlanningSceneRequest, GetPlanningSceneResponse>("/get_planning_scene");
            var req = new GetPlanningSceneRequest { components = new PlanningSceneComponentsMsg { components = 2 | 4 | 8 | 16 } };
            ros.SendServiceMessage<GetPlanningSceneResponse>("/get_planning_scene", req, r => tcs.TrySetResult(r));
        }
        catch (Exception e) { Log("get_planning_scene call failed: " + e.Message); return; }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
        if (done != tcs.Task) { Log("get_planning_scene: no response in 8 s"); return; }
        var scene = tcs.Task.Result?.scene;
        if (scene == null) { Log("get_planning_scene: null scene"); return; }
        lastRobotState = scene.robot_state;
        var js = scene.robot_state?.joint_state;
        if (js?.name != null) Log("robot joints: " + string.Join(", ", js.name.Select((n, i) => $"{n}={(i < js.position.Length ? js.position[i] : double.NaN):F3}")));
        foreach (var aco in scene.robot_state?.attached_collision_objects ?? Array.Empty<AttachedCollisionObjectMsg>())
            Log($"attached: {aco.@object?.id} on {aco.link_name}");
        foreach (var co in scene.world?.collision_objects ?? Array.Empty<CollisionObjectMsg>())
        {
            var p = co.pose;
            Log($"world object '{co.id}' frame={co.header?.frame_id} pose=({p?.position.x:F3},{p?.position.y:F3},{p?.position.z:F3}) q=({p?.orientation.x:F2},{p?.orientation.y:F2},{p?.orientation.z:F2},{p?.orientation.w:F2}) prims={co.primitives?.Length ?? 0} primPoses={co.primitive_poses?.Length ?? 0} meshes={co.meshes?.Length ?? 0}");
            if (co.id == "table")
                for (int i = 0; i < (co.primitives?.Length ?? 0); i++)
                {
                    var pp = i < (co.primitive_poses?.Length ?? 0) ? co.primitive_poses[i] : null;
                    Log($"   table prim {i}: type={co.primitives[i].type} dims=({string.Join(",", co.primitives[i].dimensions.Select(d => d.ToString("F3")))}) at ({pp?.position.x:F3},{pp?.position.y:F3},{pp?.position.z:F3})");
                }
        }
    }
}
