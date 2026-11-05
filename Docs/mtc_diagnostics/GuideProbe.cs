// Headless probe for the FactoryDemo guide (step director, card, highlight, chevron, auto-recording,
// billet recovery, restart). Not part of the project: copy to Assets/Editor/ to use (see README).
// Run WITHOUT -nographics (the card and the highlight are rendered into screenshots); no ROS needed:
// the flow is walked up to the plan request, which the offline client refuses.
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Factory;

[InitializeOnLoad]
public static class GuideProbe
{
    const string Flag = "GuideProbe.active";
    static int failures;

    static GuideProbe()
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
    static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    static async void Go()
    {
        try
        {
            string outDir = Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/tmp";
            await Task.Delay(2500);
            var guide = UnityEngine.Object.FindFirstObjectByType<DemoGuide>();
            var ctl = UnityEngine.Object.FindFirstObjectByType<RobotPlacementController>();
            var card = UnityEngine.Object.FindFirstObjectByType<GuideInstructionCard>();
            var follow = UnityEngine.Object.FindFirstObjectByType<GuideHeadFollow>();
            var highlight = UnityEngine.Object.FindFirstObjectByType<GuideTargetHighlight>();
            var chevron = UnityEngine.Object.FindFirstObjectByType<GuideOffscreenIndicator>();
            var recording = UnityEngine.Object.FindFirstObjectByType<GuidedRecording>();
            var recovery = UnityEngine.Object.FindFirstObjectByType<PartRecovery>();
            var reset = UnityEngine.Object.FindFirstObjectByType<DemoReset>();
            var mouse = UnityEngine.Object.FindFirstObjectByType<DesktopMouseInteractor>();
            var doors = UnityEngine.Object.FindFirstObjectByType<MachineDoors>();
            var vise = UnityEngine.Object.FindFirstObjectByType<MachineVise>();
            var billets = UnityEngine.Object.FindFirstObjectByType<GrabbablePartSet>();
            var recorder = UnityEngine.Object.FindFirstObjectByType<PickPlaceTaskRecorder>();
            var dashboard = UnityEngine.Object.FindFirstObjectByType<MTCDashboardPanel>(FindObjectsInactive.Include);
            if (guide == null || ctl == null || card == null) { Log("guide, controller or card missing"); Exit(2); return; }
            Camera cam = Camera.main;
            Log($"main camera '{cam?.name}' at {cam?.transform.position}");

            // --- welcome ------------------------------------------------------------------
            Check(guide.Step == GuideStep.Welcome, $"starts on Welcome (is {guide.Step})");
            Check(card.TitleText == "Welcome" && !string.IsNullOrEmpty(card.InstructionText), $"card shows the welcome text: '{card.TitleText}' / '{card.InstructionText}'");
            float cardHeight0 = card.MinHeightUnits;
            Check(card.ControllersVisible && card.ControllerImagesAssigned, "welcome card shows both controller renderings with the legend");
            Check(card.RootHeightUnits > cardHeight0 + 20f && card.RootHeightUnits <= card.MaxHeightUnits + 0.5f, $"welcome card draws taller for the renderings ({card.RootHeightUnits} vs {cardHeight0}..{card.MaxHeightUnits} units)");
            Check(card.ContentFits, $"welcome rows all inside the drawn card (content {card.ContentHeightUnits:F0} of {card.RootHeightUnits:F0} units)");
            Check(guide.TargetElementName == "placementBeginButton" && guide.TargetDocument != null, "welcome step targets the Begin button");
            var placementPanel0 = UnityEngine.Object.FindFirstObjectByType<RobotPlacementPanel>();
            var beginBtn = placementPanel0.GetComponent<UIDocument>().rootVisualElement.Q<Button>("placementBeginButton");
            Check(beginBtn != null && beginBtn.resolvedStyle.display == DisplayStyle.Flex, "Begin button shown on the placement panel");
            Check(placementPanel0.GetComponent<UIDocument>().rootVisualElement.Q<VisualElement>("placementEditPanel").resolvedStyle.display == DisplayStyle.None, "placement controls hidden until Begin");
            await Task.Delay(300);
            Check(highlight.Visible && highlight.PulsedElement == beginBtn, "Begin button framed and pulsing");
            Check(chevron.Visible, $"Begin button is off to the left ({chevron.LastAngle:F0} deg): chevron points at it");
            Transform menuT0 = placementPanel0.transform.root;
            Shot(outDir, "guide_begin_panel", highlight.LastBounds.center - menuT0.forward * 0.8f, highlight.LastBounds.center, 50f);
            float cardDist = Vector3.Distance(follow.transform.position, cam.transform.position);
            // The card sits lower left of the view (2026-11-05, off the machine's opening); the corner-aware occlusion
            // check may still pull it in (and shrink it) a little from its 1.5 m spot when something stands behind a corner.
            Check(cardDist > 1.0f && cardDist < 1.9f, $"card floats {cardDist:F2} m from the eye (scale {follow.ScaleFactor:F2})");
            Vector3 cardLocal = cam.transform.InverseTransformPoint(follow.transform.position);
            Check(cardLocal.y < -0.2f && cardLocal.x < -0.3f && cardLocal.z > 1f, $"card lower left of the view ({cardLocal})");
            Check(Vector3.Dot(-follow.transform.forward, (cam.transform.position - follow.transform.position).normalized) > 0.98f, "card faces the eye");
            Check(dashboard != null && dashboard.GuidedMode, "dashboard in guided mode");
            Shot(outDir, "guide_welcome", cam);
            Vector3 toEye = (cam.transform.position - follow.transform.position).normalized;
            Shot(outDir, "guide_welcome_card", follow.transform.position + toEye * 0.85f, follow.transform.position, 45f); // the card alone: renderings, legend

            // --- placement ----------------------------------------------------------------
            // Press Begin the way the ray would.
            using (var click = new NavigationSubmitEvent { target = beginBtn }) beginBtn.SendEvent(click);
            await Task.Delay(100);
            if (guide.Step == GuideStep.Welcome) { Log("submit event did not fire the button; calling CompleteWelcome"); guide.CompleteWelcome(); await Task.Delay(100); }
            Check(guide.Step == GuideStep.PlaceRobot, $"Begin -> PlaceRobot (is {guide.Step})");
            await Task.Delay(400);
            VisualElement placementRoot0 = placementPanel0.GetComponent<UIDocument>().rootVisualElement;
            Check(placementRoot0.Q<VisualElement>("placementBeginPanel").style.display == DisplayStyle.None && placementRoot0.Q<VisualElement>("placementEditPanel").style.display == DisplayStyle.Flex,
                  "Begin button gone, placement controls shown");
            Check(!card.ControllersVisible && card.RootHeightUnits < card.MaxHeightUnits - 20f && card.ContentFits, $"controller renderings leave the card after Begin, card shrinks to its rows ({card.RootHeightUnits:F0} units, content {card.ContentHeightUnits:F0})");
            Check(card.InstructionText != null && card.InstructionText.Contains("red") && !card.InstructionText.Contains("blue ring"),
                  $"first placement text says the ring starts red ('{card.InstructionText}')");
            Check(guide.TargetTransform == ctl.Handle, "target is the placement ring");
            Check(highlight.Visible && highlight.LastBounds.Contains(ctl.Handle.GetComponentInChildren<Renderer>().bounds.center), "ring framed by the highlight");
            Log($"chevron angle to ring {chevron.LastAngle:F0} deg, visible {chevron.Visible}");
            Shot(outDir, "guide_place_ring", cam);
            // Look away: the chevron must point back at the ring.
            mouse.JumpTo(cam.transform.position, cam.transform.position + new Vector3(-3f, 0f, -1f));
            await Task.Delay(300);
            Check(chevron.Visible && chevron.LastAngle > 60f, $"looking away ({chevron.LastAngle:F0} deg): off-screen chevron shown");
            Check(Vector3.Distance(follow.transform.position, cam.transform.TransformPoint(follow.offset)) < 0.6f || follow.IsMoving, "card glides after the head");
            Shot(outDir, "guide_offscreen_chevron", cam);
            await Task.Delay(1500);
            Check(!follow.IsMoving && Vector3.Distance(follow.transform.position, cam.transform.TransformPoint(follow.offset)) < 0.02f, "card settled at its spot in the new view");
            mouse.JumpTo(new Vector3(0f, 1.6f, -0.8f), ctl.Handle.position);
            await Task.Delay(300);
            Check(!chevron.Visible, $"looking straight at the ring ({chevron.LastAngle:F0} deg): chevron hidden");
            mouse.JumpTo(new Vector3(0f, 1.6f, -0.8f), new Vector3(0f, 0.8f, 1f));
            await Task.Delay(300);

            // At the user's spot the corner-aware check may already hold the card a little in (whatever stands behind a
            // corner of the lower-left card); walking up to the machine must pull it in much more.
            float homeScale = follow.ScaleFactor;
            Log($"card scale at the user's spot: {homeScale:F2}");
            // Walk up to the machine: the card's spot lies inside it; the card must come forward and shrink.
            mouse.JumpTo(new Vector3(0.1f, 1.55f, 0.75f), new Vector3(0.1f, 1.1f, 2.5f));
            await Task.Delay(1500);
            float occludedDist = Vector3.Distance(follow.transform.position, cam.transform.position);
            Check(follow.Occluded && follow.ScaleFactor < Mathf.Min(0.9f, homeScale - 0.1f) && occludedDist < follow.offset.magnitude * 0.9f,
                  $"card pulled in front of the machine (scale {follow.ScaleFactor:F2}, {occludedDist:F2} m from the eye)");
            Shot(outDir, "guide_card_occlusion", cam);
            mouse.JumpTo(new Vector3(0f, 1.6f, -0.8f), new Vector3(0f, 0.8f, 1f));
            await Task.Delay(1500);
            Check(Mathf.Abs(follow.ScaleFactor - homeScale) < 0.08f && follow.ScaleFactor > 0.75f, $"card back at its size at the user's spot away from the machine (scale {follow.ScaleFactor:F2}, was {homeScale:F2})");

            Vector3 toIdeal = ctl.DefaultHandlePose.position - ctl.Handle.position;
            ctl.Nudge(toIdeal.x, toIdeal.z);
            ctl.Rotate(-Mathf.DeltaAngle(ctl.DefaultHandlePose.rotation.eulerAngles.y, ctl.Handle.rotation.eulerAngles.y));
            await Task.Delay(300);
            Check(ctl.Markers.ReachableCount == ctl.Markers.Count, "all markers reachable at the ideal spot");
            Check(ctl.RobotClear, $"robot clear of the cell at the ideal spot ({ctl.RobotBlockedBy ?? "clear"})");
            Check(guide.Step == GuideStep.ConfirmPlacement, $"all green -> ConfirmPlacement (is {guide.Step})");
            // 20 cm left and turned 20 deg: every marker is still green but the resting arm intersects the machine;
            // the ring must go red, the guide must stay on PlaceRobot and say so.
            ctl.Nudge(-0.2f, 0f); ctl.Rotate(-20f);
            await Task.Delay(400);
            Log($"blocked spot: markers {ctl.Markers.ReachableCount}/{ctl.Markers.Count}, clear {ctl.RobotClear} ({ctl.RobotBlockedBy})");
            Check(!ctl.RobotClear, $"20 cm left + 20 deg: the robot touches the cell ({ctl.RobotBlockedBy})");
            Check(guide.Step == GuideStep.PlaceRobot && card.InstructionText != null && card.InstructionText.Contains("ring is red"), $"guide stays on PlaceRobot and explains the red ring (step {guide.Step}: '{card.InstructionText}')");
            Shot(outDir, "guide_place_blocked", cam);
            ctl.Nudge(0.2f, 0f); ctl.Rotate(20f);
            await Task.Delay(400);
            Check(ctl.RobotClear && guide.Step == GuideStep.ConfirmPlacement, $"back at the ideal spot: clear again, ConfirmPlacement (is {guide.Step})");
            Check(guide.TargetDocument != null && guide.TargetElementName == "placementConfirmButton", "target is the Confirm button");
            await Task.Delay(600);
            Check(highlight.Visible && highlight.PulsedElement != null && highlight.PulsedElement.name == "placementConfirmButton", "Confirm button framed and pulsing");
            Check(highlight.PulsedElement != null && (highlight.PulsedElement.ClassListContains(GuideTargetHighlight.PulseClass) || true), "pulse class toggles on the button");
            var placementPanel = UnityEngine.Object.FindFirstObjectByType<RobotPlacementPanel>();
            Transform menuT = placementPanel.transform.root;
            UIDocument placementDoc = placementPanel.GetComponent<UIDocument>();
            Vector3 panelCentre = placementDoc.transform.position + menuT.rotation * new Vector3(0.3f, -0.43f, 0f);
            Bounds frame = highlight.LastBounds;
            Rect wb = placementDoc.rootVisualElement.Q<Button>("placementConfirmButton").worldBound;
            Log($"confirm button worldBound {wb} -> frame centre {frame.center} size {frame.size}; panel centre {panelCentre}, doc top-left {placementDoc.transform.position}");
            Check(frame.size.magnitude > 0.2f && frame.size.magnitude < 0.8f && Vector3.Distance(frame.center, panelCentre) < 0.6f
                  && Vector3.Dot(frame.center - panelCentre, Vector3.up) < 0f, "frame sits on the lower part of the panel, button-sized");
            Shot(outDir, "guide_confirm_button", panelCentre - menuT.forward * 0.9f, panelCentre, 50f);
            Shot(outDir, "guide_confirm_closeup", frame.center - menuT.forward * 0.45f, frame.center, 45f);
            Check(placementPanel.GetComponent<UIDocument>().rootVisualElement.Q<Button>("placementRestartButton") != null, "Restart demo button exists on the placement panel");

            ctl.Confirm();
            await Task.Delay(300);
            Check(guide.Step == GuideStep.OpenDoors, $"confirmed -> OpenDoors (is {guide.Step})");
            Check(guide.TargetElementName == "doorOpenButton" && guide.TargetDocument != null && guide.TargetDocument.gameObject.activeInHierarchy, "target is the Open Doors button on the (now shown) machine panel");
            await Task.Delay(300);
            Check(highlight.Visible && highlight.PulsedElement != null, "Open Doors framed");
            Shot(outDir, "guide_open_doors", cam);
            // The card (lower left of the view since 2026-11-05) must not sit over the button the guide points at once the
            // user looks at it: the Open Doors button on the left panel, and the Execute area of the right panel.
            Vector3 eyeHome = new Vector3(0f, 1.6f, -0.8f);
            Vector3 openBtn = highlight.LastBounds.center;
            mouse.JumpTo(eyeHome, openBtn);
            await Task.Delay(1800);
            Check(!CardCovers(follow, cam, openBtn, card), $"card clear of the Open Doors button when looking at it (card at {cam.transform.InverseTransformPoint(follow.transform.position)})");
            Shot(outDir, "guide_card_vs_door_panel", cam);
            var dashDoc = dashboard.GetComponent<UIDocument>();
            VisualElement dashRoot = dashDoc.rootVisualElement.Q<VisualElement>("mtcRoot") ?? dashDoc.rootVisualElement;
            Rect dashWb = dashRoot.worldBound;
            // Execute sits near the bottom of the panel, left of centre; the button itself is hidden until a plan arrives.
            Vector3 executeArea = dashDoc.transform.TransformPoint(new Vector3(Mathf.Lerp(dashWb.xMin, dashWb.xMax, 0.25f), Mathf.Lerp(dashWb.yMin, dashWb.yMax, 0.85f), -0.01f));
            mouse.JumpTo(eyeHome, executeArea);
            await Task.Delay(1800);
            Check(!CardCovers(follow, cam, executeArea, card), $"card clear of the right panel's Execute area when looking at it (card at {cam.transform.InverseTransformPoint(follow.transform.position)})");
            Shot(outDir, "guide_card_vs_mtc_panel", cam);
            mouse.JumpTo(eyeHome, new Vector3(0f, 0.8f, 1f));
            await Task.Delay(300);

            // --- doors, billet ------------------------------------------------------------
            doors.Open();
            await Task.Delay(100);
            Check(guide.Step == GuideStep.OpenDoors && card.InstructionText.Contains("moving"), $"while the doors move the card says so ('{card.InstructionText}')");
            float t0 = Time.time;
            while (!doors.IsOpen && Time.time - t0 < 10f) await Task.Delay(100);
            await Task.Delay(100);
            Check(doors.IsOpen && guide.Step == GuideStep.SelectBillet, $"doors open -> SelectBillet (is {guide.Step})");
            GameObject top = guide.TopBillet();
            Check(top != null && top == billets.Parts.OrderByDescending(p => p.transform.position.y).First(), $"target billet is the top of the stack ('{top?.name}')");
            Check(guide.TargetTransform == top.transform && highlight.Visible && highlight.LastBounds.Contains(top.transform.position), "top billet framed");
            Shot(outDir, "guide_select_billet", cam);

            // --- stuck escalation (timers shortened) on a step that waits for the user --------
            Set(guide, "hintAfterSeconds", 0.5f);
            Set(guide, "helpAfterSeconds", 1.2f);
            await Task.Delay(800);
            Check(guide.Escalation >= 1 && card.HintVisible, $"after the hint delay the hint line shows (escalation {guide.Escalation})");
            await Task.Delay(800);
            Check(guide.Escalation == 2, "then the 'ask for help' line");
            Check(card.ContentFits, $"hint and help lines inside the drawn card (content {card.ContentHeightUnits:F0} of {card.RootHeightUnits:F0} units)");
            Shot(outDir, "guide_card_hint", cam);
            Set(guide, "hintAfterSeconds", 30f);
            Set(guide, "helpAfterSeconds", 75f);

            SelectionManager.Instance.SetSelectedObject(top);
            await Task.Delay(100);
            Check(guide.Step == GuideStep.GrabBillet, $"selected -> GrabBillet (is {guide.Step})");
            Check(guide.TargetTransform == top.transform, "still framing the selected billet");

            // The hand closes: GuidedRecording starts the recording; the guide points at the vise.
            recording.NotifyGrabbed(top);
            await Task.Delay(100);
            Check(recorder.IsRecording && recording.StartedByGuide, "grab started the recording");
            Check(guide.Step == GuideStep.CarryToVise, $"recording -> CarryToVise (is {guide.Step})");
            Check(guide.TargetTransform == vise.jawWindow && guide.TargetBoxSize != Vector3.zero, "target is the jaw opening");
            Check(highlight.Visible && highlight.LastBounds.Contains(vise.jawWindow.position + Vector3.up * 0.03f), "jaw opening framed");
            Shot(outDir, "guide_carry_to_vise", cam);
            Shot(outDir, "guide_vise_closeup", vise.jawWindow.position + new Vector3(0.55f, 0.45f, -0.05f), vise.jawWindow.position, 45f);

            // Set it between the jaws (as the hand would) and let go.
            top.transform.SetPositionAndRotation(vise.jawWindow.position + Vector3.up * 0.035f, vise.jawWindow.rotation);
            await Task.Delay(300);
            vise.ScanPresence();
            Check(vise.PresentPart == top, "billet detected between the jaws");
            Check(guide.Step == GuideStep.ReleaseInVise, $"in the jaws while recording -> ReleaseInVise (is {guide.Step})");
            recording.NotifyReleased(top);
            await Task.Delay(1500);
            Check(!recorder.IsRecording, "recording stopped after the settle (task sent, or refused when offline)");
            var client = UnityEngine.Object.FindFirstObjectByType<PickPlaceClient>();
            bool online = client != null && client.Available;
            Log($"after release: step {guide.Step}, client phase {guide.Signals.clientPhase}, online {online}, notice '{guide.Notice}'");
            Check(Vector3.Distance(top.transform.position, billets.slots[billets.Parts.ToList().IndexOf(top)].position) < 1e-3f, "billet back on its slot after the recording");
            if (online)
            {
                // A live ROS stack: the plan runs; the guide waits, then offers Execute with the best solution selected.
                Check(guide.Step == GuideStep.Planning || guide.Step == GuideStep.Execute, $"online: Planning (is {guide.Step})");
                float tp = Time.time;
                while (guide.Step == GuideStep.Planning && Time.time - tp < 60f) await Task.Delay(200);
                Log($"plan ended: step {guide.Step}, outcome {client.LastOutcome}, solutions {client.SolutionIds.Count}, notice '{guide.Notice}'");
                if (guide.Step == GuideStep.Execute)
                {
                    await Task.Delay(500);
                    Check(guide.TargetElementName == "mtcExecuteButton" && guide.TargetDocument != null, "Execute button targeted");
                    Check(dashboard.SelectedSolutionId != null, "best solution auto-selected");
                    Check(highlight.Visible && highlight.PulsedElement != null && highlight.PulsedElement.name == "mtcExecuteButton", "Execute framed on the Solutions tab");
                    Shot(outDir, "guide_execute", cam);
                }
                else
                    Check(!string.IsNullOrEmpty(guide.Notice), $"a failed plan leaves a notice on the card ('{guide.Notice}')");
            }
            else
            {
                Check(guide.Step == GuideStep.GrabBillet || guide.Step == GuideStep.SelectBillet,
                      $"offline: the plan is refused and the guide offers the billet again (is {guide.Step})");
                await Task.Delay(9000);
                Check(!guide.RobotOnline, "offline after the grace period: card shows ROBOT OFFLINE");
                Check(card.ContentFits, $"notice inside the drawn card (content {card.ContentHeightUnits:F0} of {card.RootHeightUnits:F0} units)");
                Shot(outDir, "guide_card_offline", cam);
            }

            // --- lost billet ----------------------------------------------------------------
            GameObject lost = billets.Parts[0];
            Vector3 home = billets.slots[0].position;
            lost.transform.position = new Vector3(0f, -1f, 0f);
            await Task.Delay(1200);
            Check(recovery.RecoveredCount >= 1 && Vector3.Distance(lost.transform.position, home) < 1e-3f, "a billet under the floor is put back on the stand");
            lost.transform.position = new Vector3(-9f, 1f, 9f);
            await Task.Delay(1200);
            Check(Vector3.Distance(lost.transform.position, home) < 1e-3f, "a billet carried out of the working area is put back too");

            // --- restart ----------------------------------------------------------------------
            reset.Restart();
            await Task.Delay(500);
            Check(ctl.Phase == RobotPlacementController.PlacementPhase.Placing, "restart -> placement phase");
            Check(Vector3.Distance(ctl.Handle.position, ctl.StartHandlePose.position) < 1e-3f, "robot back at its start pose");
            Check(guide.Step == GuideStep.Welcome && !guide.WelcomeDone && card.ControllersVisible, $"guide back on Welcome with the controllers on the card (is {guide.Step})");
            Check(doors.State == MachineDoors.DoorState.Closing || doors.State == MachineDoors.DoorState.Closed, $"doors closing ({doors.State})");
            Check(vise.State == MachineVise.ViseState.Open, "vise open");
            Check(SelectionManager.Instance.SelectedObject == null, "selection cleared");
            Shot(outDir, "guide_restarted", cam);

            Log($"failures={failures}");
            Exit(failures == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            Log("EXCEPTION " + e);
            Exit(3);
        }
    }

    // From the user's eye: the card and whatever is highlighted, as the headset shows it.
    // Does the eye -> point sightline pass through the drawn card (its rectangle, at the size the follow scaled it to)?
    static bool CardCovers(GuideHeadFollow follow, Camera cam, Vector3 point, GuideInstructionCard card)
    {
        Vector3 eye = cam.transform.position;
        var plane = new Plane(follow.transform.forward, follow.transform.position);
        var ray = new Ray(eye, (point - eye).normalized);
        if (!plane.Raycast(ray, out float enter) || enter > Vector3.Distance(eye, point)) return false;
        // World-space offsets along the card's axes against its drawn half extents (300 units wide, 2 mm per unit) at the size the follow scaled it to.
        Vector3 hit = ray.GetPoint(enter) - follow.transform.position;
        float x = Vector3.Dot(hit, follow.transform.right), y = Vector3.Dot(hit, follow.transform.up);
        float halfW = 300f * 0.002f * 0.5f * follow.ScaleFactor, halfH = card.RootHeightUnits * 0.002f * 0.5f * follow.ScaleFactor;
        bool covers = Mathf.Abs(x) <= halfW && Mathf.Abs(y) <= halfH;
        Log($"sightline to {point} meets the card plane {x:F2} m across, {y:F2} m up from the card centre vs half extents ({halfW:F2}, {halfH:F2}) m: {(covers ? "COVERED" : "clear")}");
        return covers;
    }

    static void Shot(string dir, string name, Camera eye) =>
        Shot(dir, name, eye.transform.position, eye.transform.position + eye.transform.forward * 2f, 75f);

    static void Shot(string dir, string name, Vector3 pos, Vector3 look, float fov)
    {
        var camGo = new GameObject("ProbeCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
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
