// Headless probe for the FactoryDemo vise: a billet dropped between the jaws, clamped, released.
// Not part of the project: copy to Assets/Editor/ to use (see README). No ROS needed.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class ViseProbe
{
    const string Flag = "ViseProbe.active";
    static int failures;

    static ViseProbe()
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
            Directory.CreateDirectory(outDir);
            await Task.Delay(2500);

            var ctl = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementController>();
            var vise = UnityEngine.Object.FindFirstObjectByType<Factory.MachineVise>();
            var gravity = UnityEngine.Object.FindFirstObjectByType<Factory.ReleasedPartGravity>();
            var doors = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoors>();
            var billets = UnityEngine.Object.FindFirstObjectByType<Factory.GrabbablePartSet>();
            var panel = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoorPanel>(FindObjectsInactive.Include);
            Check(vise != null, "MachineVise in the scene");
            Check(gravity != null, "ReleasedPartGravity in the scene");
            Check(panel != null && panel.Vise == vise, "machine panel bound to the vise");
            if (vise == null || gravity == null) { Exit(2); return; }

            if (ctl != null) { ctl.Confirm(); await Task.Delay(300); }
            if (doors != null) { doors.SetImmediate(true); }
            Transform window = vise.jawWindow;
            Log($"vise '{vise.name}' open gap {vise.openGap}, window {window.position}, state {vise.State}");
            Check(vise.State == Factory.MachineVise.ViseState.Open, "vise starts open");
            Check(vise.CurrentHalfGap > vise.openGap * 0.5f - 1e-4f, "jaws start fully open");
            Check(vise.PresentPart == null, "nothing between the jaws at start");

            Vector3 eye = window.position + new Vector3(0.35f, 0.45f, -0.55f);
            Shot(outDir, "vise_open", eye, window.position, 35f);

            GameObject part = billets != null && billets.Parts.Count > 0 ? billets.Parts[billets.Parts.Count - 1] : null;
            Check(part != null, "a billet to load");
            if (part == null) { Exit(2); return; }
            var pub = part.GetComponent<CollisionObjectPublisher>();

            // --- hover release, skewed and off centre: the visitor's rough place pose -------------
            Quaternion skew = Quaternion.AngleAxis(14f, Vector3.up); // 5 cm side across the jaws (the baked billets are yawed -90 with the base)
            Vector3 hover = window.position + new Vector3(0.012f, 0.03f + 0.045f, 0.015f);
            part.transform.SetPositionAndRotation(hover, skew);
            Physics.SyncTransforms();
            vise.ScanPresence();
            Check(vise.PresentPart == part, "part detected between the jaws while hovering");
            Shot(outDir, "vise_hover", eye, window.position, 35f);

            // Anything else with a collider in the jaw window would catch the falling part.
            foreach (Collider c in Physics.OverlapBox(window.position + Vector3.up * 0.06f, new Vector3(0.08f, 0.06f, 0.06f), window.rotation, ~0, QueryTriggerInteraction.Collide))
                if (!c.transform.IsChildOf(vise.transform) && c.gameObject != part)
                    Log($"collider in the jaw window: {TPath(c.transform)} ({c.GetType().Name}, trigger {c.isTrigger}, bounds {c.bounds.min} .. {c.bounds.max})");

            bool wasPaused = pub.pausePublishing;
            gravity.Drop(part);
            Check(gravity.IsSettling(part) && !part.GetComponent<Rigidbody>().isKinematic, "released part is a dynamic body");
            float t0 = Time.time;
            while (gravity.IsSettling(part) && Time.time - t0 < 6f) await Task.Delay(50);
            Check(!gravity.IsSettling(part), "part settled");
            var rb = part.GetComponent<Rigidbody>();
            Check(rb.isKinematic && !rb.useGravity, "part frozen again");
            Check(pub.pausePublishing == wasPaused, "publisher pause flag restored");
            float bottom = part.transform.position.y - part.transform.lossyScale.y * 0.5f;
            Log($"rest: bottom {bottom:F4} vs parallels {window.position.y:F4}, drop {gravity.LastDrop.dropHeight * 1000f:F0} mm, tilt {gravity.LastDrop.tiltDegrees:F1} deg");
            foreach (Collider c in Physics.OverlapBox(part.transform.position, part.transform.lossyScale * 0.5f + Vector3.one * 0.003f, part.transform.rotation, ~0, QueryTriggerInteraction.Collide))
                if (c.gameObject != part)
                    Log($"part touching: {TPath(c.transform)} ({c.GetType().Name}, bounds {c.bounds.min} .. {c.bounds.max})");
            Check(Mathf.Abs(bottom - (window.position.y + gravity.restGap)) < 0.004f, "part rests on the parallels (+rest gap)");
            Check(gravity.LastDrop.tiltDegrees < 3f, "landed flat");
            Check(gravity.LastDrop.dropHeight > 0.03f && gravity.LastDrop.dropHeight < 0.06f, $"drop height {gravity.LastDrop.dropHeight * 1000f:F0} mm as expected");
            Shot(outDir, "vise_dropped", eye, window.position, 35f);

            // --- clamp -------------------------------------------------------------------------
            vise.ScanPresence();
            Check(vise.CanClamp, "clamp enabled with a part between the jaws");
            vise.Clamp();
            t0 = Time.time;
            while (vise.IsMoving && Time.time - t0 < 6f) await Task.Delay(50);
            Check(vise.State == Factory.MachineVise.ViseState.Clamped, $"clamped: {vise.Describe()}");
            Vector3 lp = window.InverseTransformPoint(part.transform.position);
            Vector3 r = part.transform.right; r.y = 0f;
            float yaw = Mathf.Atan2(r.z, r.x) * Mathf.Rad2Deg;
            float yawError = Mathf.Abs(Mathf.Repeat(yaw + 45f, 90f) - 45f);
            Log($"after clamp: local ({lp.x:F4}, {lp.y:F4}, {lp.z:F4}), yaw error {yawError:F2} deg, half gap {vise.CurrentHalfGap:F4}");
            Check(Mathf.Abs(lp.z) < 1e-3f, "part centred on the clamp axis");
            Check(yawError < 0.5f, "part squared to the jaw plates");
            Check(Mathf.Abs(lp.x - 0.012f) < 2e-3f, "position along the jaws untouched");
            Shot(outDir, "vise_clamped", eye, window.position, 35f);
            Shot(outDir, "vise_clamped_close", window.position + new Vector3(0.18f, 0.16f, -0.22f), window.position, 30f);

            // --- release -----------------------------------------------------------------------
            vise.Release();
            t0 = Time.time;
            while (vise.IsMoving && Time.time - t0 < 6f) await Task.Delay(50);
            Check(vise.State == Factory.MachineVise.ViseState.Open, "released");
            Check(vise.CurrentHalfGap > vise.openGap * 0.5f - 1e-4f, "jaws fully open again");

            // --- a bad load: 40 degrees skewed ---------------------------------------------------
            part.transform.rotation = Quaternion.AngleAxis(40f, Vector3.up) * Quaternion.Euler(0f, part.transform.rotation.eulerAngles.y - (part.transform.rotation.eulerAngles.y % 90f), 0f);
            Physics.SyncTransforms();
            vise.ScanPresence();
            vise.Clamp();
            t0 = Time.time;
            while (vise.IsMoving && Time.time - t0 < 6f) await Task.Delay(50);
            Check(vise.State == Factory.MachineVise.ViseState.ClampedPoor, $"skewed load reported: {vise.Describe()}");
            Shot(outDir, "vise_clamped_poor", eye, window.position, 35f);
            vise.Release();
            while (vise.IsMoving) await Task.Delay(50);

            // --- clamp on nothing --------------------------------------------------------------
            part.transform.position = window.position + new Vector3(0f, 0.3f, -0.4f);
            Physics.SyncTransforms();
            vise.ScanPresence();
            Check(vise.PresentPart == null, "no part after moving it away");
            vise.Clamp();
            while (vise.IsMoving) await Task.Delay(50);
            Check(vise.State == Factory.MachineVise.ViseState.Empty, "closing on nothing reported");
            vise.Release();
            while (vise.IsMoving) await Task.Delay(50);

            // --- panel ---------------------------------------------------------------------------
            if (panel != null)
            {
                var doc = panel.GetComponent<UnityEngine.UIElements.UIDocument>();
                var root = doc != null ? doc.rootVisualElement : null;
                var clamp = root?.Q<UnityEngine.UIElements.Button>("viseClampButton");
                var status = root?.Q<UnityEngine.UIElements.Label>("viseStatusLabel");
                var drop = root?.Q<UnityEngine.UIElements.Label>("viseDropLabel");
                await Task.Delay(700);
                Check(clamp != null && status != null && drop != null, "panel has the vise controls");
                Log($"panel: status '{status?.text}', drop '{drop?.text}'");
                Check(status != null && status.text.StartsWith("Vise open"), "panel shows the vise open");
                Check(drop != null && drop.text.Contains("dropped"), "panel reports the drop");
                Shot(outDir, "machine_panel", panel.transform.position + panel.transform.forward * -1.0f + Vector3.up * 0.1f, panel.transform.position + new Vector3(0.3f, -0.4f, 0f), 40f);
            }

            Log($"done, {failures} failure(s)");
            Exit(failures == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Exit(3);
        }
    }

    static string TPath(Transform t) => t.parent == null ? t.name : TPath(t.parent) + "/" + t.name;

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
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            Log($"shot {name}");
        }
        catch (Exception e) { Log($"shot {name} failed: {e.Message}"); }
        finally
        {
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.Destroy(camGo);
        }
    }
}
