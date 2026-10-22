// Headless walkthrough of the FactoryDemo cell, recorded as frames. Not part of the project: copy to
// Assets/Editor/ to use (see README). Needs the ROS stack; run WITHOUT -nographics (frames are rendered).
// Placement phase -> doors -> load demonstration -> plan -> preview -> execute (the part drops into
// the jaws) -> clamp -> panel -> release -> unload demonstration (vise to conveyor) -> plan -> execute
// (the part drops onto the belt). Every step the user would do on a panel is done by pressing that
// panel's button on camera (a pointer dot slides onto it, the button flashes, the button's own click
// handler runs). Frames go to PROBE_OUT/frames, stitched by ffmpeg.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using RosMessageTypes.MoveitTaskConstructorMsgs;

[InitializeOnLoad]
public static class DemoVideoProbe
{
    const string Flag = "DemoVideoProbe.active";
    const int Width = 1920, Height = 1080, Fps = 15;

    static DemoVideoProbe()
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
    static void Exit(int code) { recording = false; Log("exit " + code); EditorApplication.Exit(code); }

    // ---------------------------------------------------------------- camera / frames
    static Camera cam;
    static RenderTexture rt;
    static Texture2D tex;
    static string framesDir;
    static int frameIndex;
    static bool recording;
    static Vector3 viewPos, viewLook, fromPos, fromLook;
    static float viewT = 1f, viewSeconds = 1f;
    static string caption = "";
    static float frameTimeMax, frameTimeSum; static int longFrames;

    static void SetView(Vector3 pos, Vector3 look, float seconds = 1.2f, string text = null)
    {
        fromPos = CurrentPos(); fromLook = CurrentLook();
        viewPos = pos; viewLook = look;
        viewSeconds = Mathf.Max(0.01f, seconds); viewT = 0f;
        if (text != null) { caption = text; Log("caption: " + text); }
    }

    static Vector3 CurrentPos() => Vector3.Lerp(fromPos, viewPos, Mathf.SmoothStep(0f, 1f, viewT));
    static Vector3 CurrentLook() => Vector3.Lerp(fromLook, viewLook, Mathf.SmoothStep(0f, 1f, viewT));

    static async void RecordLoop()
    {
        var camGo = new GameObject("VideoCam");
        cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        cam.fieldOfView = 50f;
        cam.nearClipPlane = 0.05f;
        rt = new RenderTexture(Width, Height, 24) { antiAliasing = 4 };
        tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        cam.targetTexture = rt;
        float last = Time.realtimeSinceStartup;
        while (recording)
        {
            float wait = 1f / Fps - (Time.realtimeSinceStartup - last);
            await Task.Delay(Mathf.Max(1, (int)(wait * 1000f)));
            float now = Time.realtimeSinceStartup;
            viewT = Mathf.Min(1f, viewT + (now - last) / viewSeconds);
            last = now;
            if (!recording || cam == null) break;
            cam.transform.position = CurrentPos();
            cam.transform.LookAt(CurrentLook());
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(framesDir, $"frame_{frameIndex:D5}.jpg"), tex.EncodeToJPG(95));
            float dt = Time.unscaledDeltaTime;
            frameTimeMax = Mathf.Max(frameTimeMax, dt); frameTimeSum += dt; if (dt > 0.5f) longFrames++;
            File.AppendAllText(Path.Combine(framesDir, "captions.txt"), $"{frameIndex}\t{caption}\n");
            frameIndex++;
        }
    }

    static async Task Hold(float seconds) => await Task.Delay((int)(seconds * 1000f));

    // ---------------------------------------------------------------- pressing panel buttons on camera
    static GameObject pointer;
    static float lastDemonstratedYaw;

    static Vector3 PanelCentre(UIDocument doc)
    {
        var col = doc.GetComponent<BoxCollider>();
        return col != null ? doc.transform.TransformPoint(col.center) : doc.transform.position;
    }

    static Vector3 PanelFacing(UIDocument doc) => -doc.transform.root.forward;

    // World position of a panel element: its layout rect relative to the document root (both in
    // panel units) mapped onto the panel's face, whose world size and centre come from the
    // document's poke collider (sized to the panel by MtcSceneBuilder.BuildPanelMenu).
    static Vector3 ElementWorld(UIDocument doc, VisualElement element)
    {
        Vector3 facing = PanelFacing(doc);
        var col = doc.GetComponent<BoxCollider>();
        Vector3 centre = PanelCentre(doc);
        Rect rootRect = doc.rootVisualElement.worldBound, rect = element.worldBound;
        if (col == null || rootRect.width < 1f || rootRect.height < 1f || float.IsNaN(rect.x))
        {
            Log($"element '{element.name}': no layout yet (root {rootRect}, element {rect}); using the panel centre");
            return centre + facing * 0.012f;
        }
        float u = (rect.center.x - rootRect.xMin) / rootRect.width;   // 0 = left edge, 1 = right edge
        // The world-space document's layout y runs downward from the top edge at 0 (the root spans
        // yMin = -height .. yMax = 0), so distance from the top is yMax - y.
        float v = (rootRect.yMax - rect.center.y) / rootRect.height;  // 0 = top edge, 1 = bottom edge
        Vector3 size = Vector3.Scale(col.size, doc.transform.lossyScale);
        Vector3 topLeft = centre - doc.transform.right * (size.x * 0.5f) + doc.transform.up * (size.y * 0.5f);
        return topLeft + doc.transform.right * (u * size.x) - doc.transform.up * (v * size.y) + facing * 0.012f;
    }

    /// <summary>Cut to the panel, slide the pointer onto the button, flash it and run its click handler.</summary>
    static async Task Press(UIDocument doc, string buttonName, string text, bool cutToPanel = true)
    {
        var root = doc != null ? doc.rootVisualElement : null;
        var button = root?.Q<Button>(buttonName);
        if (button == null) { Log($"button '{buttonName}' not found; skipping the press"); if (text != null) caption = text; return; }
        Vector3 centre = PanelCentre(doc);
        Vector3 facing = PanelFacing(doc);
        if (cutToPanel)
        {
            SetView(centre + facing * 1.0f, centre, 1.0f, text);
            await Hold(1.1f);
        }
        else if (text != null) caption = text;

        Vector3 target = ElementWorld(doc, button);
        if (pointer == null)
        {
            pointer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pointer.name = "VideoPointer";
            UnityEngine.Object.Destroy(pointer.GetComponent<Collider>());
            var r = pointer.GetComponent<Renderer>();
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            r.material = new Material(unlit);
            r.material.SetColor("_BaseColor", new Color(1f, 0.85f, 0.2f));
            r.material.color = new Color(1f, 0.85f, 0.2f);
        }
        pointer.SetActive(true);
        pointer.transform.localScale = Vector3.one * 0.028f;
        Vector3 from = target + facing * 0.25f + Vector3.down * 0.12f + doc.transform.right * 0.08f;
        float dur = 0.6f, ts = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - ts < dur)
        {
            float k = Mathf.SmoothStep(0f, 1f, (Time.realtimeSinceStartup - ts) / dur);
            pointer.transform.position = Vector3.Lerp(from, target, k);
            await Task.Delay(20);
        }
        pointer.transform.position = target;
        await Hold(0.25f);
        // The press: the pointer shrinks, the button lights up, its own handler runs.
        pointer.transform.localScale = Vector3.one * 0.018f;
        button.style.backgroundColor = new Color(1f, 1f, 1f, 0.9f);
        button.style.color = Color.black;
        using (var submit = NavigationSubmitEvent.GetPooled())
        {
            submit.target = button;
            button.SendEvent(submit);
        }
        Log($"pressed '{buttonName}' ({button.text})");
        await Hold(0.35f);
        button.style.backgroundColor = StyleKeyword.Null;
        button.style.color = StyleKeyword.Null;
        pointer.transform.localScale = Vector3.one * 0.028f;
        await Hold(0.5f);
        pointer.SetActive(false);
    }

    // ---------------------------------------------------------------- scene handles
    static PickPlaceClient client;
    static Factory.MachineVise vise;
    static Factory.ReleasedPartGravity gravity;
    static PickPlaceTaskRecorder recorder;
    static MTCDashboardPanel dashboard;
    static MTCTrajectoryPlayer player;
    static UIDocument machineDoc, dashboardDoc, placementDoc;
    static Vector3 side, sideLook, dashboardView, dashboardLook;

    static async Task DumpStatistics()
    {
        await Task.Delay(1500);
        var desc = client.Description;
        var stats = client.Statistics;
        if (desc == null || stats == null) { Log($"no description/statistics (desc={(desc != null)} stats={(stats != null)}) task={client.TaskId}"); return; }
        var statById = stats.stages.ToDictionary(st => st.id, st => st);
        var byId = desc.stages.ToDictionary(st => st.id, st => st);
        string Path(uint id)
        {
            var names = new System.Collections.Generic.List<string>();
            while (byId.TryGetValue(id, out var st)) { names.Add(st.name); if (st.parent_id == id) break; id = st.parent_id; }
            names.Reverse();
            return string.Join(" / ", names);
        }
        foreach (var st in desc.stages)
        {
            statById.TryGetValue(st.id, out var sx);
            if ((sx?.solved?.Length ?? 0) + (sx?.failed?.Length ?? 0) + (sx?.num_failed ?? 0) == 0) continue;
            Log($"stage {st.id} '{Path(st.id)}' solved={sx?.solved?.Length ?? 0} failed={sx?.failed?.Length ?? 0} num_failed={sx?.num_failed ?? 0}");
        }
    }

    // ---------------------------------------------------------------- one pick-and-place through the UI
    /// <summary>
    /// Start Recording on the dashboard, carry <paramref name="part"/> along an arc to the hover pose,
    /// let go, Stop Recording, then watch the dashboard while MTC plans. Returns the chosen solution
    /// id (the one whose place yaw is closest to the demonstration) or null when planning failed.
    /// </summary>
    static async Task<uint?> DemonstrateAndPlan(GameObject part, Vector3 hover, Quaternion hoverRot, Vector3 carryView, Vector3 carryLook,
                                                 string recordCaption, string stopCaption, string planCaption, string browseCaption)
    {
        SelectionManager.Instance?.SetSelectedObject(part);
        await Hold(0.5f);
        await Press(dashboardDoc, "mtcPlanRecordButton", recordCaption);
        SetView(side, sideLook, 1.0f);
        await Hold(1.0f);
        SetView(carryView, carryLook, 2.5f);
        Vector3 p0 = part.transform.position;
        Quaternion r0 = part.transform.rotation;
        Vector3 mid = new Vector3((p0.x + hover.x) * 0.5f, Mathf.Max(p0.y, hover.y) + 0.25f, (p0.z + hover.z) * 0.5f);
        float dur = 3.5f, ts = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - ts < dur)
        {
            float k = Mathf.Clamp01((Time.realtimeSinceStartup - ts) / dur);
            Vector3 a = Vector3.Lerp(p0, mid, k), b = Vector3.Lerp(mid, hover, k);
            part.transform.SetPositionAndRotation(Vector3.Lerp(a, b, k), Quaternion.Slerp(r0, hoverRot, Mathf.SmoothStep(0f, 1f, k)));
            await Task.Delay(30);
        }
        part.transform.SetPositionAndRotation(hover, hoverRot);
        lastDemonstratedYaw = hoverRot.eulerAngles.y;
        await Hold(1.0f);
        recorder.NotifyObjectReleased(part);
        await Press(dashboardDoc, "mtcPlanRecordButton", stopCaption);

        SetView(dashboardView, dashboardLook, 1.0f, planCaption);
        ts = Time.realtimeSinceStartup;
        while ((client.Busy || client.SolutionIds.Count == 0) && Time.realtimeSinceStartup - ts < 120f && client.LastOutcome != PickPlaceOutcome.Aborted)
            await Task.Delay(200);
        Log($"plan: outcome={client.LastOutcome} status='{client.LastStatus}' solutions={client.SolutionIds.Count} after {Time.realtimeSinceStartup - ts:F1} s");
        if (client.SolutionIds.Count == 0) { caption = "   Planning failed: " + client.LastStatus; await DumpStatistics(); await Hold(3f); return null; }
        await Hold(1.0f);

        // Fetch every solution at once (each /get_solution reply is several MB of scene diffs and
        // takes ~7 s) so the browsing below reads from the cache and the rows change promptly.
        var prefetches = client.SolutionIds.ToArray().Select(id =>
        {
            var tcs = new TaskCompletionSource<bool>();
            client.FetchSolution(id, _ => tcs.TrySetResult(true), err => { Log($"solution {id} prefetch failed: {err}"); tcs.TrySetResult(false); });
            return tcs.Task;
        }).ToArray();
        float tf = Time.realtimeSinceStartup;
        await Task.WhenAny(Task.WhenAll(prefetches), Task.Delay(40000));
        Log($"prefetched {prefetches.Count(t => t.IsCompleted && t.Result)}/{prefetches.Length} solutions in {Time.realtimeSinceStartup - tf:F1} s");

        // The server samples the place yaw (the part is a box, so any 90-degree multiple is "the
        // same" to it): browse the solutions on the Solutions tab, selecting each in turn as a
        // user would, and keep the one that puts the part down closest to the demonstrated yaw.
        await Press(dashboardDoc, "mtcTabSolutions", browseCaption, cutToPanel: false);
        uint sid = client.SolutionIds[0];
        float bestSkew = float.MaxValue;
        string partId = part.GetComponent<CollisionObjectPublisher>().objectId;
        Transform originT = part.GetComponent<CollisionObjectPublisher>().worldOrigin.transform;
        float tb = Time.realtimeSinceStartup;
        foreach (uint id in client.SolutionIds.ToArray())
        {
            dashboard.SelectSolution(id);
            float tsel = Time.realtimeSinceStartup;
            while ((dashboard.SelectedSolution == null || dashboard.SelectedSolutionId != id) && Time.realtimeSinceStartup - tsel < 25f) await Task.Delay(100);
            SolutionMsg candidate = dashboard.SelectedSolutionId == id ? dashboard.SelectedSolution : null;
            if (candidate == null) { Log($"solution {id}: not fetched"); continue; }
            float skew = float.NaN;
            foreach (var sub in candidate.sub_trajectory ?? Array.Empty<SubTrajectoryMsg>())
                foreach (var co in sub.scene_diff?.world?.collision_objects ?? Array.Empty<RosMessageTypes.Moveit.CollisionObjectMsg>())
                {
                    if (co.id != partId || co.pose == null) continue;
                    Quaternion worldRot = originT.rotation * RosUnityConversion.RosToUnityQuaternion(co.pose.orientation);
                    Vector3 f = worldRot * Vector3.right; f.y = 0f;
                    Vector3 g = hoverRot * Vector3.right; g.y = 0f;
                    float a = Vector3.SignedAngle(g, f, Vector3.up);
                    skew = Mathf.Abs(Mathf.Repeat(a + 45f, 90f) - 45f);
                }
            Log($"solution {id}: placed yaw off the demonstration by {(float.IsNaN(skew) ? -1f : skew):F1} deg");
            if (!float.IsNaN(skew) && skew < bestSkew) { bestSkew = skew; sid = id; }
            await Hold(0.6f);
        }
        Log($"chosen solution {sid} (skew {bestSkew:F1} deg) after browsing for {Time.realtimeSinceStartup - tb:F1} s");
        if (dashboard.SelectedSolutionId != sid)
        {
            dashboard.SelectSolution(sid);
            float tsel = Time.realtimeSinceStartup;
            while (dashboard.SelectedSolutionId != sid || dashboard.SelectedSolution == null) { if (Time.realtimeSinceStartup - tsel > 20f) break; await Task.Delay(100); }
        }
        await Hold(0.8f);
        return sid;
    }

    /// <summary>Preview the selected solution from the dashboard and watch it, cutting to the drop.</summary>
    static async Task PreviewSelected(Vector3 dropView, Vector3 dropLook, string text)
    {
        await Press(dashboardDoc, "mtcPreviewButton", text, cutToPanel: true);
        if (!player.IsPlaying && dashboard.SelectedSolution != null) player.PlaySolution(dashboard.SelectedSolution);
        SetView(side, sideLook, 1.2f);
        await Hold(1.0f);
        float ts = Time.realtimeSinceStartup;
        bool switched = false;
        while (player.IsPlaying && Time.realtimeSinceStartup - ts < 90f)
        {
            if (!switched && gravity.SettlingCount > 0) { switched = true; SetView(dropView, dropLook, 0.8f); }
            await Task.Delay(100);
        }
        await Hold(1.5f);
    }

    /// <summary>Execute the selected solution from the dashboard; cut to the drop when the hand opens.</summary>
    static async Task ExecuteSelected(GameObject part, Vector3 dropView, Vector3 dropLook, string text, bool fromSolutionsTab)
    {
        bool dropSeen = false;
        Action<GameObject> onDrop = _ => { dropSeen = true; SetView(dropView, dropLook, 0.6f); };
        gravity.DropStarted += onDrop;
        await Press(dashboardDoc, "mtcExecuteButton", text, cutToPanel: true);
        await Press(dashboardDoc, "mtcTabPlan", null, cutToPanel: false);
        SetView(side, sideLook, 1.2f);
        float ts = Time.realtimeSinceStartup;
        while (client.Phase == PickPlacePhase.Idle && Time.realtimeSinceStartup - ts < 10f) await Task.Delay(100);
        while (client.Busy && Time.realtimeSinceStartup - ts < 180f) await Task.Delay(200);
        Log($"execute: outcome={client.LastOutcome} status='{client.LastStatus}'");
        ts = Time.realtimeSinceStartup;
        while (!dropSeen && Time.realtimeSinceStartup - ts < 25f) await Task.Delay(100); // the watcher's re-ADD releases the part
        while (gravity.SettlingCount > 0) await Task.Delay(100);
        gravity.DropStarted -= onDrop;
        if (!dropSeen) SetView(dropView, dropLook, 0.8f);
        Log($"drop seen={dropSeen}; last drop {gravity.LastDrop.dropHeight * 1000f:F0} mm tilt {gravity.LastDrop.tiltDegrees:F0}; part at {part.transform.position} yaw {part.transform.eulerAngles.y:F1} (demonstrated {lastDemonstratedYaw:F1})");
        await Hold(2.0f);
    }

    // ---------------------------------------------------------------- the walkthrough
    static async void Go()
    {
        try
        {
            string outDir = Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/tmp/demo_video";
            framesDir = Path.Combine(outDir, "frames");
            Directory.CreateDirectory(framesDir);
            foreach (string f in Directory.GetFiles(framesDir)) File.Delete(f);

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 60f)
            {
                client = UnityEngine.Object.FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
                if (client != null && client.Available) break;
                await Task.Delay(500);
            }
            if (client == null || !client.Available) { Log("client not ready: " + client?.LastStatus); Exit(2); return; }

            var ctl = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementController>();
            vise = UnityEngine.Object.FindFirstObjectByType<Factory.MachineVise>();
            gravity = UnityEngine.Object.FindFirstObjectByType<Factory.ReleasedPartGravity>();
            var set = UnityEngine.Object.FindFirstObjectByType<Factory.GrabbablePartSet>();
            recorder = UnityEngine.Object.FindFirstObjectByType<PickPlaceTaskRecorder>(FindObjectsInactive.Include);
            dashboard = UnityEngine.Object.FindFirstObjectByType<MTCDashboardPanel>(FindObjectsInactive.Include);
            player = UnityEngine.Object.FindFirstObjectByType<MTCTrajectoryPlayer>(FindObjectsInactive.Include);
            var panel = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoorPanel>(FindObjectsInactive.Include);
            var placementPanel = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementPanel>(FindObjectsInactive.Include);
            var doorsCtl = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoors>();
            Transform conveyorAnchor = GameObject.Find("FR3 Station")?.transform.Find("Conveyor/DropAnchor");
            if (ctl == null || vise == null || gravity == null || set == null || recorder == null || player == null || dashboard == null || panel == null || placementPanel == null || conveyorAnchor == null)
            { Log("scene incomplete"); Exit(2); return; }
            machineDoc = panel.GetComponent<UIDocument>();
            dashboardDoc = dashboard.GetComponent<UIDocument>();
            placementDoc = placementPanel.GetComponent<UIDocument>();

            // The robot's IK jog handle (cyan sphere at the gripper) is not part of the story; park it.
            foreach (var jog in UnityEngine.Object.FindObjectsByType<Quest3RobotInteractionController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var handleField = typeof(Quest3RobotInteractionController).GetField("handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var handleT = handleField?.GetValue(jog) as Transform;
                jog.enabled = false;
                if (handleT != null) handleT.gameObject.SetActive(false);
            }
            foreach (GameObject go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                if (go.name.StartsWith("Joint IK Handle")) go.SetActive(false);
            // The headset's controller models and pointer rays sit at the rig's resting pose with no
            // headset on; they are not part of the story either. The ray visuals re-enable their
            // LineRenderers every LateUpdate, so the visual components go first.
            int hidden = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string type = mb.GetType().Name;
                if (type == "InteractionRayVisual" || type == "XRInteractorLineVisual") { mb.enabled = false; hidden++; }
            }
            foreach (var lr in UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { lr.enabled = false; hidden++; }
            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("[BuildingBlock] Controller Tracking") || t.name.Contains("questController"))
                        foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; hidden++; }
            Log($"hidden {hidden} controller/ray visuals");
            // Long solutions (19 steps with scene diffs) can take longer than the client's default
            // 5 s to come back from /get_solution, which flashes "SOLUTION UNAVAILABLE" on the dashboard.
            typeof(PickPlaceClient).GetField("getSolutionTimeoutSeconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(client, 20f);
            // Log every executed step's attach/detach with its stage name (which step attached, and when).
            player.verboseLog = true;
            // Slow the doors so the slide is on camera after the button press.
            if (doorsCtl != null) foreach (var d in doorsCtl.doors) if (d != null) d.travelTime = 3f;

            Transform window = vise.jawWindow;
            Vector3 wide = new Vector3(1.9f, 1.9f, -1.3f), wideLook = new Vector3(-0.2f, 0.9f, 0.9f);
            Vector3 viseClose = window.position + new Vector3(0.55f, 0.45f, -0.05f); // inside the opening, right of the vise: the arm stays left of frame
            side = new Vector3(1.5f, 1.55f, 0.35f); sideLook = new Vector3(-0.15f, 1.0f, 1.15f);
            Vector3 doorsView = new Vector3(1.15f, 1.65f, -0.35f), doorsLook = new Vector3(0.15f, 1.15f, 1.3f);
            dashboardView = PanelCentre(dashboardDoc) + PanelFacing(dashboardDoc) * 1.0f; dashboardLook = PanelCentre(dashboardDoc);
            Vector3 belt = conveyorAnchor.position;
            Vector3 beltView = belt + new Vector3(-0.55f, 0.55f, -0.75f), beltLook = belt;

            fromPos = viewPos = wide; fromLook = viewLook = wideLook;
            recording = true;
            RecordLoop();
            await Hold(0.5f);

            if (Environment.GetEnvironmentVariable("PROBE_POINTER_TEST") == "1")
            {
                ctl.Handle.SetPositionAndRotation(ctl.DefaultHandlePose.position, ctl.DefaultHandlePose.rotation);
                await Hold(0.4f);
                ctl.Confirm();
                await Hold(1.0f);
                await Press(machineDoc, "doorOpenButton", "pointer test: Open Doors");
                await Press(dashboardDoc, "mtcPlanRecordButton", "pointer test: Start Recording");
                await Press(placementDoc, "placementRepositionButton", "pointer test: Reposition");
                recording = false;
                await Hold(0.3f);
                Log($"frames: {frameIndex}");
                Exit(0);
                return;
            }

            // --- 1. placement phase: the robot starts off its spot; drag the ring to the ideal spot ---
            SetView(wide, wideLook, 0.1f, "1. Placement: the user drags the ring until the reach markers turn green");
            await Hold(2.0f);
            Pose start = ctl.StartHandlePose, ideal = ctl.DefaultHandlePose;
            float dur = 4f, ts = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - ts < dur)
            {
                float k = Mathf.SmoothStep(0f, 1f, (Time.realtimeSinceStartup - ts) / dur);
                ctl.Handle.SetPositionAndRotation(Vector3.Lerp(start.position, ideal.position, k), Quaternion.Slerp(start.rotation, ideal.rotation, k));
                await Task.Delay(30);
            }
            ctl.Handle.SetPositionAndRotation(ideal.position, ideal.rotation);
            await Hold(1.0f);
            Log($"robot at {ctl.BaseDescription()}; markers {ctl.Markers?.ReachableCount}/{ctl.Markers?.Count}");
            await Press(placementDoc, "placementConfirmButton", "   Confirm placement: the planning scene is sent to MoveIt");
            await Hold(1.0f);

            // --- 2. doors ---
            await Press(machineDoc, "doorOpenButton", "2. Open the enclosure doors from the machine panel");
            SetView(doorsView, doorsLook, 0.5f);
            await Hold(3.8f);
            SetView(side, sideLook, 1.2f);
            await Hold(1.2f);

            // --- 3/4. load demonstration + plan ---
            GameObject part = set.Parts.LastOrDefault(p => p != null);
            // Let go 1.5 cm above the seated height: a careful release, under the panel's 2 cm drop warning.
            Vector3 hover = window.position + new Vector3(0.01f, 0.03f + 0.005f + 0.015f, 0.012f);
            Quaternion hoverRot = Quaternion.AngleAxis(10f, Vector3.up); // 5 cm side across the jaws, skewed 10 deg
            uint? sid = await DemonstrateAndPlan(part, hover, hoverRot, viseClose, window.position,
                "3. Start Recording, carry the billet over the open jaws, let go: that is the place pose",
                "   Stop Recording: the billet returns to the stack while MTC plans the pick and place",
                "4. MoveIt Task Constructor plans the load: the dashboard shows the planning status, then the solutions",
                "5. Solutions tab: the user looks through the solutions and keeps the one that sets the billet down closest to the demonstration");
            if (sid == null) { Exit(5); return; }

            // --- 5. preview, 6. execute ---
            await PreviewSelected(viseClose, window.position, "   Preview: a ghost run where the part drops as the hand opens");
            await ExecuteSelected(part, viseClose, window.position, "6. Execute: the robot loads the billet; the part falls onto the parallels when the gripper opens", fromSolutionsTab: true);

            // --- 7. clamp ---
            vise.ScanPresence();
            await Press(machineDoc, "viseClampButton", "7. Clamp: the self-centring jaws square the billet and centre it");
            SetView(viseClose, window.position, 0.8f);
            await Hold(0.5f);
            while (vise.IsMoving) await Task.Delay(100);
            Log($"vise: {vise.State} - {vise.Describe()}");
            await Hold(2.0f);

            // --- 8. panel ---
            {
                Vector3 pp = PanelCentre(machineDoc);
                SetView(pp + PanelFacing(machineDoc) * 1.0f, pp, 1.5f, "8. The machine panel reports the clamp and the drop (machining would happen here)");
                await Hold(4.0f);
            }

            // --- 9. release ---
            await Press(machineDoc, "viseReleaseButton", "9. Release the vise for the unload", cutToPanel: false);
            SetView(viseClose, window.position, 1.2f);
            await Hold(0.5f);
            while (vise.IsMoving) await Task.Delay(100);
            await Hold(1.5f);

            // --- 10/11. unload demonstration + plan: from the vise onto the conveyor ---
            Vector3 beltHover = belt + Vector3.up * 0.015f;
            Quaternion beltRot = part.transform.rotation; // squared by the vise; set it on the belt as it is
            uint? sid2 = await DemonstrateAndPlan(part, beltHover, beltRot, beltView, beltLook,
                "10. Unload: Start Recording, carry the billet from the vise onto the conveyor, let go",
                "   Stop Recording: the billet returns to the vise while MTC plans the unload",
                "11. MTC plans the unload from the open jaws to the belt",
                "   The user picks the unload solution the same way");
            if (sid2 == null)
            {
                Log("unload plan failed; demonstrating again");
                await Hold(1.0f);
                sid2 = await DemonstrateAndPlan(part, beltHover, beltRot, beltView, beltLook,
                    "10. Unload (again): Start Recording, carry the billet from the vise onto the conveyor, let go",
                    "   Stop Recording: the billet returns to the vise while MTC plans the unload",
                    "11. MTC plans the unload from the open jaws to the belt",
                    "   The user picks the unload solution the same way");
                if (sid2 == null) { Exit(6); return; }
            }

            // --- 12. execute the unload ---
            await ExecuteSelected(part, beltView, beltLook, "12. Execute: the robot lifts the billet out of the vise and sets it on the conveyor", fromSolutionsTab: false);
            SetView(wide, wideLook, 2.0f, "   Loaded, clamped, released and unloaded: the whole cycle from one demonstration each way");
            await Hold(3.5f);

            recording = false;
            await Hold(0.3f);
            Log($"frames: {frameIndex} in {framesDir}; frame time avg {frameTimeSum / Mathf.Max(1, frameIndex) * 1000f:F0} ms, max {frameTimeMax * 1000f:F0} ms, {longFrames} frames over 0.5 s");
            Exit(0);
        }
        catch (Exception e)
        {
            Log("EXCEPTION " + e);
            Exit(3);
        }
    }
}
