using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MoveItPlanningRequestMenuUI))]
public class TrajectoryBakerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        if (!Application.isPlaying) return;

        var menu = (MoveItPlanningRequestMenuUI)target;
        var trajectory = menu.LastPlannedTrajectory;
        if (!TrajectoryData.HasWaypoints(trajectory)) return;

        EditorGUILayout.Space();
        if (GUILayout.Button("Bake Last Planned Trajectory to Asset"))
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save Trajectory Asset",
                "TrajectoryData",
                "asset",
                "Choose where to save the baked trajectory.",
                "Assets/Trajectories");

            if (string.IsNullOrEmpty(path)) return;

            var data = TrajectoryData.CreateFromTrajectory(trajectory);
            AssetDatabase.CreateAsset(data, path);
            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = data;
            Debug.Log($"[TrajectoryBaker] Saved trajectory to {path}");
        }
    }
}
