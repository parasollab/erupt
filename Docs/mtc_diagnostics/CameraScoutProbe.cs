// Renders candidate camera views of the FactoryDemo cell (robot confirmed on its spot, doors open,
// a billet seated in the vise) so a recording camera can be chosen without a headset. Not part of
// the project: copy to Assets/Editor/ to use; run WITHOUT -nographics. No ROS needed.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class CameraScoutProbe
{
    const string Flag = "CameraScoutProbe.active";

    static CameraScoutProbe()
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
        EditorApplication.EnterPlaymode();
    }

    static void Log(string s) => Debug.Log("[PROBE] " + s);
    static void Exit(int code) { Log("exit " + code); EditorApplication.Exit(code); }

    static async void Go()
    {
        try
        {
            string outDir = Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/tmp/scout";
            Directory.CreateDirectory(outDir);
            await Task.Delay(2500);
            var ctl = UnityEngine.Object.FindFirstObjectByType<Factory.RobotPlacementController>();
            var vise = UnityEngine.Object.FindFirstObjectByType<Factory.MachineVise>();
            var doors = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoors>();
            var set = UnityEngine.Object.FindFirstObjectByType<Factory.GrabbablePartSet>();
            var panel = UnityEngine.Object.FindFirstObjectByType<Factory.MachineDoorPanel>(FindObjectsInactive.Include);
            Vector3 W = vise.jawWindow.position;

            // Before confirming: the placement phase as the user first sees it, with the new panel spots.
            Shot(outDir, "user_view_placement", new Vector3(0f, 1.6f, 0f), new Vector3(0f, 1.1f, 1.3f), 80f);
            Shot(outDir, "wide_placement", new Vector3(1.9f, 1.9f, -1.3f), new Vector3(-0.2f, 0.9f, 0.9f), 50f);

            ctl.Handle.SetPositionAndRotation(ctl.DefaultHandlePose.position, ctl.DefaultHandlePose.rotation);
            await Task.Delay(400);
            ctl.Confirm();
            await Task.Delay(500);
            doors.SetImmediate(true);
            GameObject part = set.Parts.LastOrDefault(p => p != null);
            part.transform.SetPositionAndRotation(W + new Vector3(0f, 0.03f + 0.005f, 0f), Quaternion.identity);
            Physics.SyncTransforms();
            await Task.Delay(800);

            // The user's view after confirm (panels shown), then the candidate vise cameras.
            Shot(outDir, "user_view_confirmed", new Vector3(0f, 1.6f, 0f), new Vector3(0f, 1.1f, 1.3f), 80f);
            Shot(outDir, "wide_confirmed", new Vector3(1.9f, 1.9f, -1.3f), new Vector3(-0.2f, 0.9f, 0.9f), 50f);
            Shot(outDir, "wide_left", new Vector3(-1.7f, 1.8f, -0.4f), new Vector3(-0.1f, 1.0f, 1.2f), 50f);
            Shot(outDir, "side_current", new Vector3(1.5f, 1.55f, 0.35f), new Vector3(-0.15f, 1.0f, 1.15f), 50f);
            Shot(outDir, "vise_current", W + new Vector3(0.42f, 0.42f, -0.62f), W, 50f);
            Shot(outDir, "vise_A_left_inside", W + new Vector3(-0.28f, 0.32f, -0.14f), W, 50f);
            Shot(outDir, "vise_B_left_front", W + new Vector3(-0.35f, 0.40f, -0.40f), W, 50f);
            Shot(outDir, "vise_C_right_inside", W + new Vector3(0.55f, 0.45f, -0.05f), W, 50f);
            Shot(outDir, "vise_D_high_front", W + new Vector3(0.30f, 0.70f, -0.20f), W, 50f);
            Shot(outDir, "vise_F_far_left_inside", W + new Vector3(-0.50f, 0.25f, 0.10f), W, 50f);
            Shot(outDir, "vise_G_low_left_front", W + new Vector3(-0.30f, 0.15f, -0.30f), W + Vector3.up * 0.03f, 45f);
            Shot(outDir, "vise_H_right_front_low", W + new Vector3(0.45f, 0.22f, -0.35f), W + Vector3.up * 0.03f, 45f);
            if (panel != null)
            {
                var col = panel.GetComponent<BoxCollider>();
                Vector3 pp = col != null ? panel.transform.TransformPoint(col.center) : panel.transform.position;
                Shot(outDir, "panel", pp + panel.transform.root.forward * -1.1f, pp, 45f);
            }
            Log($"vise window {W}; robot {ctl.BaseDescription()}");

            // Every capsule-shaped thing that renders, with its path: the floating "menu handle" hunt.
            foreach (var mf in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var r = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || r == null || !r.enabled || !mf.sharedMesh.name.Contains("Capsule")) continue;
                Log($"capsule: {TPath(mf.transform)} at {mf.transform.position} scale {mf.transform.lossyScale}");
            }
            // What the wide camera sees at the top-centre pixel where the stray capsule shows.
            var probeCam = new GameObject("RayCam").AddComponent<Camera>();
            probeCam.transform.position = new Vector3(1.9f, 1.9f, -1.3f);
            probeCam.transform.LookAt(new Vector3(-0.2f, 0.9f, 0.9f));
            probeCam.fieldOfView = 50f;
            foreach (var px in new[] { new Vector2(700f, 980f), new Vector2(690f, 975f), new Vector2(710f, 985f) })
            {
                Ray ray = probeCam.ViewportPointToRay(new Vector3(px.x / 1920f, px.y / 1080f, 0f));
                foreach (RaycastHit h in Physics.RaycastAll(ray, 30f, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance))
                    Log($"ray through ({px.x},{px.y}) hits {TPath(h.collider.transform)} at {h.distance:F2} m");
            }
            UnityEngine.Object.Destroy(probeCam.gameObject);
            Exit(0);
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
        var camGo = new GameObject("ScoutCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        cam.fieldOfView = fov;
        cam.nearClipPlane = 0.03f;
        var rt = new RenderTexture(1920, 1080, 24) { antiAliasing = 4 };
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
            File.WriteAllBytes(Path.Combine(dir, name + ".jpg"), tex.EncodeToJPG(92));
            UnityEngine.Object.Destroy(tex);
            Log($"shot {name} from {pos}");
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
