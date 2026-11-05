// Renders 4K stills of the FactoryDemo walkthrough for posters and slides. Based on DemoVideoProbe.cs.
// Not part of the project: copy to Assets/Editor/ to use (see README). Needs the ROS stack; run WITHOUT
// -nographics. Delete the copy from Assets/Editor/ afterwards.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class PosterShotsProbe
{
    const string Flag = "PosterShotsProbe.active";
    const int Width = 3840, Height = 2160;

    static PosterShotsProbe()
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
    const int OverlayLayer = 31;
    static RenderTexture rt;
    static Texture2D tex;
    static string outDir, framesDir;
    static int frameIndex;
    static bool recording;
    static float fpsNow = 0.5f;
    static Vector3 viewPos, viewLook, fromPos, fromLook;
    static float viewT = 1f, viewSeconds = 1f;
    static string caption = "";

    static void SetView(Vector3 pos, Vector3 look, float seconds = 1.2f, string text = null)
    {
        fromPos = CurrentPos(); fromLook = CurrentLook();
        viewPos = pos; viewLook = look;
        viewSeconds = Mathf.Max(0.01f, seconds); viewT = 0f;
        if (text != null) { caption = text; Log("caption: " + text); }
    }

    static Vector3 CurrentPos() => Vector3.Lerp(fromPos, viewPos, Mathf.SmoothStep(0f, 1f, viewT));
    static Vector3 CurrentLook() => Vector3.Lerp(fromLook, viewLook, Mathf.SmoothStep(0f, 1f, viewT));

    static void SetupCamera()
    {
        var camGo = new GameObject("PosterCam");
        cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        cam.fieldOfView = 50f;
        cam.nearClipPlane = 0.05f;
        rt = new RenderTexture(Width, Height, 24) { antiAliasing = 4 };
        tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        cam.targetTexture = rt;
        cam.cullingMask = ~(1 << OverlayLayer);
        // The guide card and chevron are head-locked in the headset: a URP overlay camera stacked on the
        // base camera draws them on top of the scene (same trick as DemoVideoProbe).
        var overlayGo = new GameObject("PosterOverlayCam");
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
            Component baseData = camGo.GetComponent(dataType);
            if (baseData == null) baseData = camGo.AddComponent(dataType);
            Component overData = overlayGo.GetComponent(dataType);
            if (overData == null) overData = overlayGo.AddComponent(dataType);
            dataType.GetProperty("renderType").SetValue(overData, Enum.Parse(renderTypeEnum, "Overlay"));
            var stack = (System.Collections.IList)dataType.GetProperty("cameraStack").GetValue(baseData);
            stack.Add(overlayCam);
            Log("overlay camera stacked (URP)");
        }
        else Log("URP camera data type not found");
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
    }

    static void Capture(string path)
    {
        cam.transform.position = CurrentPos();
        cam.transform.LookAt(CurrentLook());
        cam.Render();
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(path, tex.EncodeToJPG(93));
    }

    /// <summary>Named still from an explicit camera pose (snaps the view there first).</summary>
    static async Task Shot(string name, Vector3 pos, Vector3 look, float fov = 50f, float settle = 0.6f)
    {
        SetView(pos, look, 0.01f);
        float oldFov = cam.fieldOfView;
        cam.fieldOfView = fov; overlayCam.fieldOfView = fov;
        foreach (var f in UnityEngine.Object.FindObjectsByType<Factory.GuideHeadFollow>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) { cam.transform.position = pos; cam.transform.LookAt(look); f.Snap(); }
        await Hold(settle);
        Capture(Path.Combine(outDir, name + ".jpg"));
        Log($"shot {name} from {pos} fov {fov}");
        cam.fieldOfView = oldFov; overlayCam.fieldOfView = oldFov;
    }

    static async void RecordLoop()
    {
        float last = Time.realtimeSinceStartup;
        float nextFrame = last;
        while (recording)
        {
            await Task.Delay(50);
            float now = Time.realtimeSinceStartup;
            viewT = Mathf.Min(1f, viewT + (now - last) / viewSeconds);
            last = now;
            if (!recording || cam == null) break;
            if (now < nextFrame) { cam.transform.position = CurrentPos(); cam.transform.LookAt(CurrentLook()); continue; }
            nextFrame = now + 1f / fpsNow;
            Capture(Path.Combine(framesDir, $"frame_{frameIndex:D4}.jpg"));
            File.AppendAllText(Path.Combine(framesDir, "captions.txt"), $"{frameIndex}\t{caption}\n");
            frameIndex++;
        }
    }

    static async Task Hold(float seconds) => await Task.Delay((int)(seconds * 1000f));

    // ---------------------------------------------------------------- guide visibility
    static readonly List<GameObject> cardRoots = new List<GameObject>();
    static readonly List<Behaviour> guideVisuals = new List<Behaviour>();
    static void ShowGuide(bool on)
    {
        foreach (var g in cardRoots) if (g != null) g.SetActive(on);
        foreach (var b in guideVisuals) if (b != null) b.enabled = on;
        if (on) foreach (var f in UnityEngine.Object.FindObjectsByType<Factory.GuideHeadFollow>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) { f.targetCamera = cam; f.Snap(); }
        Log("guide visible: " + on);
    }

    // ---------------------------------------------------------------- pressing panel buttons
    static Vector3 PanelCentre(UIDocument doc)
    {
        var col = doc.GetComponent<BoxCollider>();
        return col != null ? doc.transform.TransformPoint(col.center) : doc.transform.position;
    }
    static Vector3 PanelFacing(UIDocument doc) => -doc.transform.root.forward;

    static async Task Press(UIDocument doc, string buttonName, string text, bool cutToPanel = true)
    {
        var root = doc != null ? doc.rootVisualElement : null;
        var button = root?.Q<Button>(buttonName);
        if (button == null) { Log($"button '{buttonName}' not found; skipping the press"); if (text != null) caption = text; return; }
        Vector3 centre = PanelCentre(doc);
        Vector3 facing = PanelFacing(doc);
        if (cutToPanel)
        {
            Vector3 shift = doc.transform.right * 0.28f;
            SetView(centre + facing * 1.0f + shift, centre + shift, 0.8f, text);
            await Hold(0.9f);
        }
        else if (text != null) caption = text;
        using (var submit = NavigationSubmitEvent.GetPooled())
        {
            submit.target = button;
            button.SendEvent(submit);
        }
        Log($"pressed '{buttonName}' ({button.text})");
        await Hold(0.6f);
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
    static Factory.GuidedRecording guided;
    static Factory.DemoGuide guideRef;

    static async Task<uint?> DemonstrateAndPlan(GameObject part, Vector3 hover, Quaternion hoverRot, Vector3 carryView, Vector3 carryLook, string tag)
    {
        SetView(side, sideLook, 1.0f, tag + " select");
        await Hold(1.0f);
        SelectionManager.Instance?.SetSelectedObject(part);
        await Hold(1.5f);
        await Shot(tag + "_selected_side", side, sideLook);
        caption = tag + " grab";
        guided.NotifyGrabbed(part);
        guideRef.ExternalGrab = part;
        await Hold(0.8f);
        Log($"grab: recording={recorder.IsRecording} startedByGuide={guided.StartedByGuide}");
        SetView(carryView, carryLook, 2.5f);
        Vector3 p0 = part.transform.position;
        Quaternion r0 = part.transform.rotation;
        Vector3 mid = new Vector3((p0.x + hover.x) * 0.5f, Mathf.Max(p0.y, hover.y) + 0.25f, (p0.z + hover.z) * 0.5f);
        float dur = 3.5f, ts = Time.realtimeSinceStartup;
        bool midShot = false;
        while (Time.realtimeSinceStartup - ts < dur)
        {
            float k = Mathf.Clamp01((Time.realtimeSinceStartup - ts) / dur);
            Vector3 a = Vector3.Lerp(p0, mid, k), b = Vector3.Lerp(mid, hover, k);
            part.transform.SetPositionAndRotation(Vector3.Lerp(a, b, k), Quaternion.Slerp(r0, hoverRot, Mathf.SmoothStep(0f, 1f, k)));
            if (!midShot && k > 0.55f) { midShot = true; Capture(Path.Combine(outDir, tag + "_carry_mid.jpg")); Log("shot " + tag + "_carry_mid"); }
            await Task.Delay(30);
        }
        part.transform.SetPositionAndRotation(hover, hoverRot);
        await Hold(0.8f);
        Capture(Path.Combine(outDir, tag + "_carry_hover.jpg")); Log("shot " + tag + "_carry_hover");
        caption = tag + " release";
        guideRef.ExternalGrab = null;
        guided.NotifyReleased(part);
        ts = Time.realtimeSinceStartup;
        while (recorder.IsRecording && Time.realtimeSinceStartup - ts < 5f) await Task.Delay(100);
        Log($"release: recording={recorder.IsRecording} after {Time.realtimeSinceStartup - ts:F1} s; client {client.Phase}");
        await Hold(1.0f);

        SetView(dashboardView, dashboardLook, 1.0f, tag + " planning");
        ts = Time.realtimeSinceStartup;
        while ((client.Busy || client.SolutionIds.Count == 0) && Time.realtimeSinceStartup - ts < 120f && client.LastOutcome != PickPlaceOutcome.Aborted)
            await Task.Delay(200);
        Log($"plan: outcome={client.LastOutcome} status='{client.LastStatus}' solutions={client.SolutionIds.Count} after {Time.realtimeSinceStartup - ts:F1} s");
        if (client.SolutionIds.Count == 0) { caption = "planning failed: " + client.LastStatus; await Hold(2f); return null; }
        uint sid = client.SolutionIds[0];
        float tsel = Time.realtimeSinceStartup;
        while ((dashboard.SelectedSolutionId == null || dashboard.SelectedSolution == null) && Time.realtimeSinceStartup - tsel < 30f) await Task.Delay(100);
        if (dashboard.SelectedSolutionId != null) sid = dashboard.SelectedSolutionId.Value;
        Log($"auto-selected solution {sid} (fetched={dashboard.SelectedSolution != null}) after {Time.realtimeSinceStartup - tsel:F1} s");
        await Hold(1.0f);
        await Shot(tag + "_dashboard", dashboardView, dashboardLook, 40f);
        {
            Vector3 shift = dashboardDoc.transform.right * 0.28f;
            await Shot(tag + "_dashboard_angled", PanelCentre(dashboardDoc) + PanelFacing(dashboardDoc) * 1.0f + shift + Vector3.up * 0.1f, PanelCentre(dashboardDoc) + shift, 45f);
        }
        return sid;
    }

    static async Task ExecuteSelected(GameObject part, Vector3 dropView, Vector3 dropLook, string tag)
    {
        bool dropSeen = false;
        Action<GameObject> onDrop = _ => { dropSeen = true; SetView(dropView, dropLook, 0.6f); };
        gravity.DropStarted += onDrop;
        await Press(dashboardDoc, "mtcExecuteButton", tag + " execute", cutToPanel: true);
        SetView(side, sideLook, 1.2f);
        fpsNow = 2f;
        float ts = Time.realtimeSinceStartup;
        while (client.Phase == PickPlacePhase.Idle && Time.realtimeSinceStartup - ts < 10f) await Task.Delay(100);
        // alternate viewpoints while the robot moves so several angles are on file
        float tSwitch = Time.realtimeSinceStartup + 6f; int angle = 0;
        while (client.Busy && Time.realtimeSinceStartup - ts < 180f)
        {
            if (!dropSeen && Time.realtimeSinceStartup > tSwitch)
            {
                angle = (angle + 1) % 3;
                if (angle == 0) SetView(side, sideLook, 0.8f);
                else if (angle == 1) SetView(dropView, dropLook, 0.8f);
                else SetView(new Vector3(1.9f, 1.9f, -1.3f), new Vector3(-0.2f, 0.9f, 0.9f), 0.8f);
                tSwitch = Time.realtimeSinceStartup + 6f;
            }
            await Task.Delay(200);
        }
        Log($"execute: outcome={client.LastOutcome} status='{client.LastStatus}'");
        ts = Time.realtimeSinceStartup;
        while (!dropSeen && Time.realtimeSinceStartup - ts < 25f) await Task.Delay(100);
        while (gravity.SettlingCount > 0) await Task.Delay(100);
        gravity.DropStarted -= onDrop;
        fpsNow = 0.5f;
        if (!dropSeen) SetView(dropView, dropLook, 0.8f);
        await Hold(1.5f);
        await Shot(tag + "_placed_close", dropView, dropLook);
        await Shot(tag + "_placed_side", side, sideLook);
        Log($"drop seen={dropSeen}; part at {part.transform.position} yaw {part.transform.eulerAngles.y:F1}");
    }

    // ---------------------------------------------------------------- the walkthrough
    static async void Go()
    {
        try
        {
            outDir = Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/tmp/poster_shots";
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
            Transform conveyorAnchor = GameObject.Find("FR3 Station")?.transform.Find("Conveyor/DropAnchor");
            if (ctl == null || vise == null || gravity == null || set == null || recorder == null || player == null || dashboard == null || panel == null || placementPanel == null || conveyorAnchor == null || guided == null || guide == null)
            { Log("scene incomplete"); Exit(2); return; }
            machineDoc = panel.GetComponent<UIDocument>();
            dashboardDoc = dashboard.GetComponent<UIDocument>();
            placementDoc = placementPanel.GetComponent<UIDocument>();

            SetupCamera();
            foreach (var card in UnityEngine.Object.FindObjectsByType<Factory.GuideInstructionCard>(FindObjectsInactive.Include, FindObjectsSortMode.None)) cardRoots.Add(card.transform.root.gameObject);
            foreach (var h in UnityEngine.Object.FindObjectsByType<Factory.GuideTargetHighlight>(FindObjectsInactive.Include, FindObjectsSortMode.None)) guideVisuals.Add(h);
            foreach (var o in UnityEngine.Object.FindObjectsByType<Factory.GuideOffscreenIndicator>(FindObjectsInactive.Include, FindObjectsSortMode.None)) guideVisuals.Add(o);

            foreach (var jog in UnityEngine.Object.FindObjectsByType<Quest3RobotInteractionController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var handleField = typeof(Quest3RobotInteractionController).GetField("handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var handleT = handleField?.GetValue(jog) as Transform;
                jog.enabled = false;
                if (handleT != null) handleT.gameObject.SetActive(false);
            }
            foreach (GameObject go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                if (go.name.StartsWith("Joint IK Handle")) go.SetActive(false);
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
            typeof(PickPlaceClient).GetField("getSolutionTimeoutSeconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(client, 20f);
            if (doorsCtl != null) foreach (var d in doorsCtl.doors) if (d != null) d.travelTime = 1.5f;

            Transform window = vise.jawWindow;
            Vector3 wide = new Vector3(1.9f, 1.9f, -1.3f), wideLook = new Vector3(-0.2f, 0.9f, 0.9f);
            Vector3 viseClose = window.position + new Vector3(0.55f, 0.45f, -0.05f);
            side = new Vector3(1.5f, 1.55f, 0.35f); sideLook = new Vector3(-0.15f, 1.0f, 1.15f);
            Vector3 doorsView = new Vector3(1.15f, 1.65f, -0.35f), doorsLook = new Vector3(0.15f, 1.15f, 1.3f);
            dashboardView = PanelCentre(dashboardDoc) + PanelFacing(dashboardDoc) * 1.0f; dashboardLook = PanelCentre(dashboardDoc);
            Vector3 belt = conveyorAnchor.position;
            Vector3 beltView = belt + new Vector3(-0.55f, 0.55f, -0.75f), beltLook = belt;
            Vector3 userEye = new Vector3(0f, 1.6f, 0f), userLook = new Vector3(0f, 1.1f, 1.3f);
            Vector3 high = new Vector3(1.2f, 2.8f, -1.9f), highLook = new Vector3(-0.1f, 0.9f, 0.9f);
            Vector3 wideLeft = new Vector3(-1.7f, 1.8f, -0.4f), wideLeftLook = new Vector3(-0.1f, 1.0f, 1.2f);

            fromPos = viewPos = wide; fromLook = viewLook = wideLook;
            recording = true;
            RecordLoop();
            await Hold(0.5f);

            // --- placement phase, before Begin: reach shell + ring + markers, robot at the start pose ---
            ShowGuide(false);
            await Shot("01_placement_start_wide", wide, wideLook);
            await Shot("01_placement_start_high", high, highLook, 45f);
            await Shot("01_placement_start_user", userEye, userLook, 75f);
            ShowGuide(true);
            await Shot("02_welcome_user_guide", userEye, userLook, 75f, 1.0f);
            await Press(placementDoc, "placementBeginButton", "begin", cutToPanel: false);
            await Hold(0.8f);
            await Shot("03_place_robot_user_guide", userEye, userLook, 75f, 1.0f);
            await Shot("03_place_robot_wide_guide", wide, wideLook, 50f, 1.0f);
            ShowGuide(false);

            // --- drag the ring to the ideal spot ---
            Pose start = ctl.StartHandlePose, ideal = ctl.DefaultHandlePose;
            float dur = 3f, ts = Time.realtimeSinceStartup;
            SetView(wide, wideLook, 0.5f, "placement drag");
            while (Time.realtimeSinceStartup - ts < dur)
            {
                float k = Mathf.SmoothStep(0f, 1f, (Time.realtimeSinceStartup - ts) / dur);
                ctl.Handle.SetPositionAndRotation(Vector3.Lerp(start.position, ideal.position, k), Quaternion.Slerp(start.rotation, ideal.rotation, k));
                await Task.Delay(30);
            }
            ctl.Handle.SetPositionAndRotation(ideal.position, ideal.rotation);
            await Hold(1.5f);
            Log($"robot at {ctl.BaseDescription()}; markers {ctl.Markers?.ReachableCount}/{ctl.Markers?.Count}; guide step {guide.Step}");
            await Shot("04_placement_ideal_wide", wide, wideLook);
            await Shot("04_placement_ideal_high", high, highLook, 45f);
            await Shot("04_placement_ideal_user", userEye, userLook, 75f);
            await Shot("04_placement_ideal_left", wideLeft, wideLeftLook);
            ShowGuide(true);
            await Shot("05_confirm_user_guide", userEye, userLook, 75f, 1.0f);
            ShowGuide(false);
            await Press(placementDoc, "placementConfirmButton", "confirm", cutToPanel: false);
            await Hold(1.0f);

            // --- doors ---
            await Press(machineDoc, "doorOpenButton", "open doors", cutToPanel: false);
            await Hold(2.5f);
            await Shot("06_cell_wide", wide, wideLook);
            await Shot("06_cell_high", high, highLook, 45f);
            await Shot("06_cell_side", side, sideLook);
            await Shot("06_cell_left", wideLeft, wideLeftLook);
            await Shot("06_cell_user", userEye, userLook, 75f);
            await Shot("06_cell_doors", doorsView, doorsLook);
            {
                Vector3 pp = PanelCentre(machineDoc);
                await Shot("06_machine_panel", pp + PanelFacing(machineDoc) * 0.9f, pp, 40f);
            }

            // --- load: select, grab (records), release in the jaws (plans) ---
            GameObject part = set.Parts.LastOrDefault(p => p != null);
            Vector3 hover = window.position + new Vector3(0.01f, 0.03f + 0.005f + 0.015f, 0.012f);
            Quaternion hoverRot = Quaternion.AngleAxis(10f, Vector3.up);
            ShowGuide(true);
            uint? sid = await DemonstrateAndPlan(part, hover, hoverRot, viseClose, window.position, "07_load");
            if (sid == null) { Exit(5); return; }
            ShowGuide(false);

            // --- execute the load ---
            await ExecuteSelected(part, viseClose, window.position, "08_load");

            // --- clamp ---
            vise.ScanPresence();
            await Press(machineDoc, "viseClampButton", "clamp", cutToPanel: false);
            SetView(viseClose, window.position, 0.5f);
            await Hold(0.5f);
            while (vise.IsMoving) await Task.Delay(100);
            Log($"vise: {vise.State} - {vise.Describe()}; guide step {guide.Step}");
            await Hold(1.0f);
            await Shot("09_vise_clamped_close", viseClose, window.position);
            await Shot("09_vise_clamped_front", window.position + new Vector3(0.30f, 0.70f, -0.20f), window.position);
            {
                Vector3 pp = PanelCentre(machineDoc);
                await Shot("09_machine_panel_clamped", pp + PanelFacing(machineDoc) * 0.9f, pp, 40f);
            }

            // --- close the doors: the job runs ---
            await Press(machineDoc, "doorCloseButton", "close doors", cutToPanel: false);
            await Hold(2.5f);
            await Shot("10_doors_closed", doorsView, doorsLook);
            await Shot("10_doors_closed_wide", wide, wideLook);
            ts = Time.realtimeSinceStartup;
            while (!guide.Machined && Time.realtimeSinceStartup - ts < guide.MachiningSeconds + 10f) await Task.Delay(200);
            Log($"machined={guide.Machined} after {Time.realtimeSinceStartup - ts:F1} s; guide step {guide.Step}");

            // --- open, release ---
            await Press(machineDoc, "doorOpenButton", "open doors", cutToPanel: false);
            await Hold(2.5f);
            await Press(machineDoc, "viseReleaseButton", "release", cutToPanel: false);
            SetView(viseClose, window.position, 1.0f);
            while (vise.IsMoving) await Task.Delay(100);
            await Hold(1.0f);

            // --- unload: from the vise onto the conveyor ---
            Vector3 beltHover = belt + Vector3.up * 0.015f;
            Quaternion beltRot = part.transform.rotation;
            ShowGuide(true);
            uint? sid2 = await DemonstrateAndPlan(part, beltHover, beltRot, beltView, beltLook, "11_unload");
            if (sid2 == null)
            {
                Log("unload plan failed; demonstrating again");
                await Hold(1.0f);
                sid2 = await DemonstrateAndPlan(part, beltHover, beltRot, beltView, beltLook, "11_unload");
                if (sid2 == null) { Exit(6); return; }
            }
            ShowGuide(false);
            await ExecuteSelected(part, beltView, beltLook, "12_unload");
            await Shot("13_final_wide", wide, wideLook);
            await Shot("13_final_high", high, highLook, 45f);
            await Shot("13_final_left", wideLeft, wideLeftLook);
            ShowGuide(true);
            await Shot("13_final_user_guide", userEye, userLook, 75f, 1.0f);

            recording = false;
            await Hold(0.3f);
            Log($"frames: {frameIndex} in {framesDir}");
            Exit(0);
        }
        catch (Exception e)
        {
            Log("EXCEPTION " + e);
            Exit(3);
        }
    }
}
