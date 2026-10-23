// Headless walkthrough of the FactoryDemo cell, recorded as frames. Not part of the project: copy to
// Assets/Editor/ to use (see README). Needs the ROS stack; run WITHOUT -nographics (frames are rendered).
// The self-guided flow (Factory.DemoGuide): Begin -> placement -> confirm -> doors -> select the top
// billet -> grabbing it records -> let go between the jaws (the plan goes out, the best solution is
// selected) -> execute (the part drops into the jaws) -> clamp -> close the doors -> the job runs ->
// open the doors -> release -> select the finished part -> grab (records) -> let go on the conveyor ->
// execute (the part drops onto the belt) -> done. Every step
// the user would do on a panel is done by pressing that panel's button on camera (a pointer dot slides
// onto it, the button flashes, the button's own click handler runs). VIDEO_GUIDE=1 keeps the instruction
// card, the target highlight and the off-screen chevron on camera (they follow the video camera as they
// would the headset); VIDEO_GUIDE=0 hides them. Frames go to PROBE_OUT/frames, stitched by ffmpeg.
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
    static Camera cam, overlayCam;
    const int OverlayLayer = 31; // the guide card and chevron: drawn over everything, as the headset shows them
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
        cam.cullingMask = ~(1 << OverlayLayer);
        // The card and the off-screen chevron are head-locked in the headset and never hidden by the
        // scene; a second pass with depth cleared draws them on top of the main render.
        // URP: a standalone depth-only camera leaves the colour buffer uninitialised, so the overlay
        // is a URP overlay camera stacked on the base one (set up by reflection: the editor assembly
        // does not reference the URP runtime).
        var overlayGo = new GameObject("VideoOverlayCam");
        overlayGo.transform.SetParent(camGo.transform, false);
        overlayCam = overlayGo.AddComponent<Camera>();
        overlayCam.CopyFrom(cam);
        overlayCam.clearFlags = CameraClearFlags.Depth;
        overlayCam.cullingMask = 1 << OverlayLayer;
        overlayCam.targetTexture = null;
        Type dataType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        Type renderTypeEnum = Type.GetType("UnityEngine.Rendering.Universal.CameraRenderType, Unity.RenderPipelines.Universal.Runtime");
        if (dataType != null && renderTypeEnum != null)
        {
            // Explicit null checks: a missing component is a fake-null object that ?? does not catch.
            Component baseData = camGo.GetComponent(dataType);
            if (baseData == null) baseData = camGo.AddComponent(dataType);
            Component overData = overlayGo.GetComponent(dataType);
            if (overData == null) overData = overlayGo.AddComponent(dataType);
            dataType.GetProperty("renderType").SetValue(overData, Enum.Parse(renderTypeEnum, "Overlay"));
            // (overlay cameras clear depth by default; clearDepth has no public setter)
            var stack = (System.Collections.IList)dataType.GetProperty("cameraStack").GetValue(baseData);
            stack.Add(overlayCam);
            Log("overlay camera stacked on the video camera (URP)");
        }
        else Log("URP camera data type not found; the guide card may be hidden by scene geometry");
        foreach (var f in UnityEngine.Object.FindObjectsByType<Factory.GuideHeadFollow>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            f.targetCamera = cam; f.Snap();
            foreach (Transform t in f.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = OverlayLayer;
        }
        foreach (var o in UnityEngine.Object.FindObjectsByType<Factory.GuideOffscreenIndicator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            o.targetCamera = cam;
            Transform chevron = o.transform.Find("Offscreen Chevron");
            if (chevron != null) chevron.gameObject.layer = OverlayLayer;
        }
        foreach (var h in UnityEngine.Object.FindObjectsByType<Factory.GuideTargetHighlight>(FindObjectsInactive.Include, FindObjectsSortMode.None)) h.targetCamera = cam;
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
            cam.Render(); // renders its URP stack: the overlay pass draws the card and chevron on top
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
            // The panel sits left of centre so the guide card (lower right of the view) never covers it.
            Vector3 shift = doc.transform.right * 0.28f;
            SetView(centre + facing * 1.0f + shift, centre + shift, 1.0f, text);
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
    static Factory.GuidedRecording guided;
    static Factory.DemoGuide guideRef;

    static async Task<uint?> DemonstrateAndPlan(GameObject part, Vector3 hover, Quaternion hoverRot, Vector3 carryView, Vector3 carryLook,
                                                 string selectCaption, string recordCaption, string stopCaption, string planCaption, string browseCaption)
    {
        SetView(side, sideLook, 1.0f, selectCaption);
        await Hold(1.0f);
        SelectionManager.Instance?.SetSelectedObject(part);
        await Hold(1.5f);
        caption = recordCaption; Log("caption: " + recordCaption);
        guided.NotifyGrabbed(part); // the hand closes on the selected billet: the recording starts
        guideRef.ExternalGrab = part; // and the guide sees it in a hand while the probe carries it
        await Hold(0.8f);
        Log($"grab: recording={recorder.IsRecording} startedByGuide={guided.StartedByGuide}");
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
        caption = stopCaption; Log("caption: " + stopCaption);
        guideRef.ExternalGrab = null;
        guided.NotifyReleased(part); // the hand opens in the target zone: the recording stops after the settle
        ts = Time.realtimeSinceStartup;
        while (recorder.IsRecording && Time.realtimeSinceStartup - ts < 5f) await Task.Delay(100);
        Log($"release: recording={recorder.IsRecording} after {Time.realtimeSinceStartup - ts:F1} s; client {client.Phase}");
        await Hold(1.0f);

        SetView(dashboardView, dashboardLook, 1.0f, planCaption);
        ts = Time.realtimeSinceStartup;
        while ((client.Busy || client.SolutionIds.Count == 0) && Time.realtimeSinceStartup - ts < 120f && client.LastOutcome != PickPlaceOutcome.Aborted)
            await Task.Delay(200);
        Log($"plan: outcome={client.LastOutcome} status='{client.LastStatus}' solutions={client.SolutionIds.Count} after {Time.realtimeSinceStartup - ts:F1} s");
        if (client.SolutionIds.Count == 0) { caption = "   Planning failed: " + client.LastStatus; await DumpStatistics(); await Hold(3f); return null; }
        await Hold(1.0f);

        // Guided mode: the dashboard selects the best (first) solution and shows the Solutions tab on
        // its own; wait for the fetch (each /get_solution reply is several MB and takes ~7 s).
        caption = browseCaption; Log("caption: " + browseCaption);
        uint sid = client.SolutionIds[0];
        float tsel = Time.realtimeSinceStartup;
        while ((dashboard.SelectedSolutionId == null || dashboard.SelectedSolution == null) && Time.realtimeSinceStartup - tsel < 30f) await Task.Delay(100);
        if (dashboard.SelectedSolutionId != null) sid = dashboard.SelectedSolutionId.Value;
        Log($"auto-selected solution {sid} (fetched={dashboard.SelectedSolution != null}) after {Time.realtimeSinceStartup - tsel:F1} s");
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
            guided = UnityEngine.Object.FindFirstObjectByType<Factory.GuidedRecording>();
            var guide = UnityEngine.Object.FindFirstObjectByType<Factory.DemoGuide>();
            guideRef = guide;
            bool showGuide = Environment.GetEnvironmentVariable("VIDEO_GUIDE") != "0";
            if (!showGuide)
            {
                // The plain version: the step director keeps running (the Begin button, auto-recording and
                // auto-selection are the demo's behaviour) but nothing of the guide is on camera.
                foreach (var card in UnityEngine.Object.FindObjectsByType<Factory.GuideInstructionCard>(FindObjectsInactive.Include, FindObjectsSortMode.None)) card.transform.root.gameObject.SetActive(false);
                foreach (var h in UnityEngine.Object.FindObjectsByType<Factory.GuideTargetHighlight>(FindObjectsInactive.Include, FindObjectsSortMode.None)) h.enabled = false;
                foreach (var o in UnityEngine.Object.FindObjectsByType<Factory.GuideOffscreenIndicator>(FindObjectsInactive.Include, FindObjectsSortMode.None)) o.enabled = false;
            }
            Log($"guide on camera: {showGuide}");
            Transform conveyorAnchor = GameObject.Find("FR3 Station")?.transform.Find("Conveyor/DropAnchor");
            if (ctl == null || vise == null || gravity == null || set == null || recorder == null || player == null || dashboard == null || panel == null || placementPanel == null || conveyorAnchor == null || guided == null || guide == null)
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

            // --- 1. begin ---
            SetView(wide, wideLook, 0.1f, "1. The guide: a card in the user's view says what to do next and the next thing to touch is framed");
            await Hold(2.5f);
            await Press(placementDoc, "placementBeginButton", "   Begin on the placement panel: the first lesson is pointing at a panel and pulling the trigger");
            await Hold(0.8f);

            // --- 2. placement phase: the robot starts off its spot; drag the ring to the ideal spot ---
            SetView(wide, wideLook, 1.0f, "2. Placement: drag the ring until all six reach markers turn green");
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
            await Hold(1.5f);
            Log($"robot at {ctl.BaseDescription()}; markers {ctl.Markers?.ReachableCount}/{ctl.Markers?.Count}; guide step {guide.Step}");
            await Press(placementDoc, "placementConfirmButton", "3. Confirm placement: the planning scene is sent to MoveIt");
            await Hold(1.0f);

            // --- 4. doors ---
            await Press(machineDoc, "doorOpenButton", "4. Open the enclosure doors from the machine panel");
            SetView(doorsView, doorsLook, 0.5f);
            await Hold(3.8f);
            SetView(side, sideLook, 1.2f);
            await Hold(1.2f);

            // --- 5/6/7. select, grab (records), release in the jaws (plans) ---
            GameObject part = set.Parts.LastOrDefault(p => p != null);
            // Let go 1.5 cm above the seated height: a careful release, under the panel's 2 cm drop warning.
            Vector3 hover = window.position + new Vector3(0.01f, 0.03f + 0.005f + 0.015f, 0.012f);
            Quaternion hoverRot = Quaternion.AngleAxis(10f, Vector3.up); // 5 cm side across the jaws, skewed 10 deg
            uint? sid = await DemonstrateAndPlan(part, hover, hoverRot, viseClose, window.position,
                "5. Select the top billet: point at it and pull the trigger",
                "6. Grab it: picking the billet up starts the pick-and-place recording on its own",
                "   Let go between the open jaws: the recording stops and the plan goes out; the billet returns to the stack",
                "7. MoveIt Task Constructor plans the load; the dashboard shows the planning status",
                "   The best solution is selected and shown on its own: nothing to browse");
            if (sid == null) { Exit(5); return; }

            // --- 8. execute ---
            Log($"guide step before execute: {guide.Step}");
            await ExecuteSelected(part, viseClose, window.position, "8. Execute: the robot loads the billet; the part falls onto the parallels when the gripper opens", fromSolutionsTab: true);
            Log($"guide step after execute: {guide.Step}");

            // --- 9. clamp ---
            vise.ScanPresence();
            await Press(machineDoc, "viseClampButton", "9. Clamp: the self-centring jaws square the billet and centre it");
            SetView(viseClose, window.position, 0.8f);
            await Hold(0.5f);
            while (vise.IsMoving) await Task.Delay(100);
            Log($"vise: {vise.State} - {vise.Describe()}; guide step {guide.Step}");
            await Hold(2.0f);
            {
                Vector3 pp = PanelCentre(machineDoc);
                Vector3 shift = machineDoc.transform.right * 0.28f;
                SetView(pp + PanelFacing(machineDoc) * 1.0f + shift, pp + shift, 1.5f, "   The machine panel reports the clamp and the drop");
                await Hold(3.0f);
            }

            // --- 10. close the doors: the job runs ---
            await Press(machineDoc, "doorCloseButton", "10. Close the doors: the machine runs the job", cutToPanel: false);
            SetView(doorsView, doorsLook, 0.8f);
            await Hold(3.8f);
            Log($"guide step after closing: {guide.Step}");
            caption = "   Machining: the guide waits for the job to finish"; Log("caption: " + caption);
            ts = Time.realtimeSinceStartup;
            while (!guide.Machined && Time.realtimeSinceStartup - ts < guide.MachiningSeconds + 10f) await Task.Delay(200);
            Log($"machined={guide.Machined} after {Time.realtimeSinceStartup - ts:F1} s; guide step {guide.Step}");
            await Hold(0.5f);

            // --- 11/12. open the doors, release the vise ---
            await Press(machineDoc, "doorOpenButton", "11. The job is done: open the doors to unload");
            SetView(doorsView, doorsLook, 0.5f);
            await Hold(3.8f);
            await Press(machineDoc, "viseReleaseButton", "12. Release the vise so the robot can take the part");
            SetView(viseClose, window.position, 1.2f);
            await Hold(0.5f);
            while (vise.IsMoving) await Task.Delay(100);
            await Hold(1.0f);
            Log($"guide step before the unload: {guide.Step}; machined billet {guide.MachinedBillet?.name}");

            // --- 13/14/15. unload demonstration + plan: from the vise onto the conveyor ---
            Vector3 beltHover = belt + Vector3.up * 0.015f;
            Quaternion beltRot = part.transform.rotation; // squared by the vise; set it on the belt as it is
            uint? sid2 = await DemonstrateAndPlan(part, beltHover, beltRot, beltView, beltLook,
                "13. Select the finished part in the vise: point at it and pull the trigger",
                "   Grab it: picking it up starts the recording again",
                "14. Let go on the conveyor belt: the recording stops and the unload plan goes out; the part returns to the vise",
                "15. MTC plans the unload from the open jaws to the belt",
                "   The best solution is selected on its own");
            if (sid2 == null)
            {
                Log("unload plan failed; demonstrating again");
                await Hold(1.0f);
                sid2 = await DemonstrateAndPlan(part, beltHover, beltRot, beltView, beltLook,
                    "13. Select the finished part in the vise (again)",
                    "   Grab it: picking it up starts the recording again",
                    "14. Let go on the conveyor belt: the recording stops and the unload plan goes out",
                    "15. MTC plans the unload from the open jaws to the belt",
                    "   The best solution is selected on its own");
                if (sid2 == null) { Exit(6); return; }
            }

            // --- 16. execute the unload: done ---
            await ExecuteSelected(part, beltView, beltLook, "16. Execute: the robot lifts the part out of the vise and sets it on the conveyor", fromSolutionsTab: true);
            Log($"guide step at the end: {guide.Step}");
            SetView(wide, wideLook, 2.0f, "   Demo finished: loaded, machined and unloaded, one guided step at a time");
            await Hold(4.0f);

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
