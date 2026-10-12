// Headless MTC diagnostics probe. Not part of the project: copy to Assets/Editor/ to use (see README).
// Plays FactoryDemo, dumps the planning scene, sweeps grasp IK through MoveIt's services and
// (via Plan) sends real /pick_place goals and reports per-stage statistics.
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
public static class MtcPlanProbe
{
    const string Flag = "MtcPlanProbe.active";

    static MtcPlanProbe()
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
            Log("play mode entered, scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
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

            await Task.Delay(8000); // let the collision publishers ADD (the table publishes after 2 s)
            foreach (var pub in UnityEngine.Object.FindObjectsByType<CollisionObjectPublisher>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                Log($"publisher id={pub.objectId} published={pub.hasBeenPublished} paused={pub.pausePublishing} attached={pub.attachedToRobot} pos={pub.transform.position} scale={pub.transform.lossyScale} compound={(pub.compoundRoots?.Length ?? 0)}");

            await DumpPlanningScene();

            var set = UnityEngine.Object.FindFirstObjectByType<Factory.GrabbablePartSet>();
            GameObject part = set?.Parts.FirstOrDefault(p => p != null);
            if (part == null) { Log("no blank part"); Exit(4); return; }
            var partPub = part.GetComponent<CollisionObjectPublisher>();
            Transform origin = partPub.worldOrigin.transform;
            Transform tote = GameObject.Find("ToteLeft")?.transform;
            Transform anchor = tote?.Find("DropAnchor");
            if (anchor == null) { Log("no ToteLeft/DropAnchor"); Exit(5); return; }

            Vector3 pickRel = origin.InverseTransformPoint(part.transform.position);
            Vector3 relPos = origin.InverseTransformPoint(anchor.position);
            Quaternion relRot = Quaternion.Inverse(origin.rotation) * part.transform.rotation;
            PointMsg rosPos = RosUnityConversion.UnityToRosPosition(relPos);
            QuaternionMsg rosRot = RosUnityConversion.UnityToRosQuaternion(relRot);
            PoseStampedMsg placePose = client.PlacePose(rosPos, rosRot);
            PointMsg pickRos = RosUnityConversion.UnityToRosPosition(pickRel);
            Log($"object={partPub.objectId} pick(ros)={pickRos.x:F3},{pickRos.y:F3},{pickRos.z:F3} place(ros)={placePose.pose.position.x:F3},{placePose.pose.position.y:F3},{placePose.pose.position.z:F3} frame={placePose.header.frame_id}");

            client.OnStatus += s => Log("status: " + s);

            await Plan(client, partPub, placePose, "plan blank_1");
            if (client.SolutionIds.Count == 0) { Exit(6); return; }
            uint sid = client.SolutionIds[0];

            var dashboard = UnityEngine.Object.FindFirstObjectByType<MTCDashboardPanel>();
            dashboard.SelectSolution(sid);
            await Task.Delay(1500);
            var showTab = typeof(MTCDashboardPanel).GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var panelField = typeof(MTCDashboardPanel).GetField("panelSolutions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            showTab.Invoke(dashboard, new[] { panelField.GetValue(dashboard) });
            await Task.Delay(500);

            string outDir = Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/tmp";
            Transform menu = GameObject.Find("MTCMenu")?.transform;
            var doc = menu?.GetComponentInChildren<UnityEngine.UIElements.UIDocument>(true);
            Vector3 centre = doc.transform.position + new Vector3(0.3f, -0.6f, 0f); // pivot is the top-left corner
            var panelStagesField = typeof(MTCDashboardPanel).GetField("panelStages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var panelPlanField = typeof(MTCDashboardPanel).GetField("panelPlan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Shot(outDir, "tab_solutions", centre + new Vector3(0, 0, -0.95f), centre, 70f);
            dashboard.SelectStage(6);
            showTab.Invoke(dashboard, new[] { panelStagesField.GetValue(dashboard) });
            await Task.Delay(500);
            Shot(outDir, "tab_stages", centre + new Vector3(0, 0, -0.95f), centre, 70f);
            showTab.Invoke(dashboard, new[] { panelPlanField.GetValue(dashboard) });
            await Task.Delay(500);
            Shot(outDir, "tab_plan", centre + new Vector3(0, 0, -0.95f), centre, 70f);
            Exit(0);
        }
        catch (Exception e)
        {
            Log("EXCEPTION " + e);
            Exit(3);
        }
    }

    static void Shot(string dir, string name, Vector3 pos, Vector3 look, float fov)
    {
        var camGo = new GameObject("ProbeCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        cam.fieldOfView = fov;
        cam.nearClipPlane = 0.05f;
        var rt = new RenderTexture(1600, 1600, 24);
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
            RenderTexture.active = null;
            UnityEngine.Object.Destroy(tex);
            Log("shot " + name);
        }
        catch (Exception e) { Log("shot failed: " + e.Message); }
        finally
        {
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(camGo);
            UnityEngine.Object.Destroy(rt);
        }
    }

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

    // Mirrors MTC GenerateGraspPose + ComputeIK: ik_frame = hand_frame * grasp_frame_transform must
    // coincide with the object frame rotated about its z, so link8 = obj * Rz(a) * grasp^-1.
    // Quaternions/vectors here are plain math on ROS-axis triples.
    static async Task IkSweep(Vector3 objPos, Quaternion grasp, string label)
    {
        var ros = RosBus.Instance;
        if (!servicesRegistered)
        {
            ros.RegisterRosService<GetPositionIKRequest, GetPositionIKResponse>("/compute_ik");
            ros.RegisterRosService<GetStateValidityRequest, GetStateValidityResponse>("/check_state_validity");
            servicesRegistered = true;
        }
        Quaternion gInv = Quaternion.Inverse(grasp);
        Vector3 off = gInv * new Vector3(0f, 0f, -0.1f);
        Vector3 z0 = gInv * Vector3.forward;
        Log($"----- {label}: link8 z-axis in world at a=0: ({z0.x:F2},{z0.y:F2},{z0.z:F2}), link8 offset from object ({off.x:F3},{off.y:F3},{off.z:F3})");
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
            if (i % 6 == 0) Log($"   a={i * 15}: IK ok, in collision: {string.Join(", ", pairs)}");
        }
        Log($"RESULT {label}: IK solvable {ikOk}/24, collision-free {valid}/24, no response {noResp}; colliding pairs over the sweep: " +
            (contacts.Count == 0 ? "none" : string.Join("; ", contacts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"))));
    }

    static async Task Plan(PickPlaceClient client, CollisionObjectPublisher partPub, PoseStampedMsg placePose, string label)
    {
        await Task.Delay(3500);
        Log($"===== {label}: object {partPub.objectId} pos={partPub.transform.position} scale={partPub.transform.lossyScale}");
        // PlanAsync's result type lives in an assembly the editor assembly does not reference.
        var plan = (Task)typeof(PickPlaceClient).GetMethod("PlanAsync").Invoke(client, new object[] { partPub.objectId, placePose, null });
        try { await plan; }
        catch (Exception e) { Log("plan threw: " + e.GetBaseException().Message); }
        Log($"outcome={client.LastOutcome} status='{client.LastStatus}' task={client.TaskId} solutions={client.SolutionIds.Count}");
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
        var js = scene.robot_state?.joint_state;
        if (js?.name != null) Log("robot joints: " + string.Join(", ", js.name.Select((n, i) => $"{n}={(i < js.position.Length ? js.position[i] : double.NaN):F3}")));
        foreach (var aco in scene.robot_state?.attached_collision_objects ?? Array.Empty<AttachedCollisionObjectMsg>())
            Log($"attached: {aco.@object?.id} on {aco.link_name}");
        foreach (var co in scene.world?.collision_objects ?? Array.Empty<CollisionObjectMsg>())
        {
            var p = co.pose;
            Log($"world object '{co.id}' frame={co.header?.frame_id} pose=({p?.position.x:F3},{p?.position.y:F3},{p?.position.z:F3}) q=({p?.orientation.x:F2},{p?.orientation.y:F2},{p?.orientation.z:F2},{p?.orientation.w:F2}) prims={co.primitives?.Length ?? 0} primPoses={co.primitive_poses?.Length ?? 0} meshes={co.meshes?.Length ?? 0}");
            for (int i = 0; i < (co.primitives?.Length ?? 0) && i < 30; i++)
            {
                var pr = co.primitives[i];
                var pp = i < (co.primitive_poses?.Length ?? 0) ? co.primitive_poses[i] : null;
                Log($"   prim[{i}] type={pr.type} dims=({string.Join(",", pr.dimensions.Select(d => d.ToString("F3")))}) pose=({pp?.position.x:F3},{pp?.position.y:F3},{pp?.position.z:F3})");
            }
        }
    }
}
