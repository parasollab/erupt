using System.IO;
using UnityEditor;
using UnityEngine;

namespace Erupt.Editor
{
    /// <summary>
    /// ERUPT > Robots > Robot Setup…: pick a URDF (and, when there are any, its SRDF and
    /// ROS-names file), type in what the files do not say, look at what Robot Setup makes of
    /// it, and write the robot. See <see cref="RobotSetup"/>.
    /// </summary>
    public class RobotSetupWindow : EditorWindow
    {
        [SerializeField] private RobotSetupSettings settings = new RobotSetupSettings();
        [SerializeField] private bool showValues;

        private SerializedObject serialized;
        private RobotSetupReport report;
        private string reportHeading;
        private Vector2 scroll;

        [MenuItem("ERUPT/Robots/Robot Setup…", priority = 0)]
        public static void Open()
        {
            var window = GetWindow<RobotSetupWindow>(true, "ERUPT Robot Setup");
            window.minSize = new Vector2(560f, 620f);
        }

        private void OnEnable() => serialized = new SerializedObject(this);

        private void OnGUI()
        {
            serialized.Update();
            SerializedProperty s = serialized.FindProperty(nameof(settings));
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("Robot description", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            FileField(s.FindPropertyRelative(nameof(RobotSetupSettings.urdfPath)), "URDF", "urdf");
            bool urdfChanged = EditorGUI.EndChangeCheck();
            FileField(s.FindPropertyRelative(nameof(RobotSetupSettings.srdfPath)), "SRDF (optional)", "srdf");
            FileField(s.FindPropertyRelative(nameof(RobotSetupSettings.rosNamesPath)), "ROS names (optional)", "json");
            EditorGUILayout.PropertyField(s.FindPropertyRelative(nameof(RobotSetupSettings.displayName)));
            EditorGUILayout.PropertyField(s.FindPropertyRelative(nameof(RobotSetupSettings.assetName)));
            FolderField(s.FindPropertyRelative(nameof(RobotSetupSettings.outputFolder)));
            serialized.ApplyModifiedProperties();
            if (urdfChanged) FillFromUrdf();

            EditorGUILayout.Space();
            showValues = EditorGUILayout.Foldout(showValues, "What the files do not say (a value typed here wins over the files)", true);
            if (showValues)
            {
                serialized.Update();
                EditorGUI.indentLevel++;
                SerializedProperty field = s.FindPropertyRelative(nameof(RobotSetupSettings.planningFrame));
                do EditorGUILayout.PropertyField(field, true);
                while (field.NextVisible(false) && field.propertyPath.StartsWith(nameof(settings) + "."));
                EditorGUI.indentLevel--;
                serialized.ApplyModifiedProperties();
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !string.IsNullOrWhiteSpace(settings.urdfPath);
                if (GUILayout.Button(new GUIContent("Check", "Read the files and show what Robot Setup makes of them. Nothing is written."), GUILayout.Height(26f)))
                {
                    report = new RobotSetupReport();
                    RobotSetup.Describe(settings, report);
                    reportHeading = "Check (nothing was written)";
                }
                GUI.enabled = GUI.enabled && !string.IsNullOrWhiteSpace(settings.outputFolder);
                if (GUILayout.Button(new GUIContent("Set Up Robot", "Import the URDF and write the prefab, the ghost prefab, the profile and the report."), GUILayout.Height(26f)))
                {
                    RobotSetupResult result = RobotSetup.Run(settings);
                    report = result.Report;
                    reportHeading = result.Succeeded ? "Written: " + result.PrefabPath : "Nothing was written";
                    if (result.Succeeded)
                        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath));
                }
                GUI.enabled = true;
            }
            if (!RobotImport.Available)
                EditorGUILayout.HelpBox("The URDF Importer package is not installed; ERUPT > Project Setup offers to add it. Check works without it.", MessageType.Warning);

            DrawReport();
            EditorGUILayout.EndScrollView();
        }

        private void DrawReport()
        {
            if (report == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(reportHeading, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(report.Summary);
            foreach (string text in report.Texts(RobotSetupLevel.Problem)) EditorGUILayout.HelpBox(text, MessageType.Error);
            foreach (string text in report.Texts(RobotSetupLevel.Warning)) EditorGUILayout.HelpBox(text, MessageType.Warning);
            foreach (string text in report.Texts(RobotSetupLevel.Found)) EditorGUILayout.LabelField("• " + text, EditorStyles.wordWrappedLabel);
        }

        // A URDF usually sits next to its SRDF and ROS-names file, named alike.
        private void FillFromUrdf()
        {
            if (!File.Exists(settings.urdfPath)) return;
            string basePath = Path.Combine(Path.GetDirectoryName(settings.urdfPath), Path.GetFileNameWithoutExtension(settings.urdfPath));
            if (string.IsNullOrEmpty(settings.srdfPath) && File.Exists(basePath + ".srdf")) settings.srdfPath = basePath + ".srdf";
            if (string.IsNullOrEmpty(settings.rosNamesPath) && File.Exists(basePath + ".ros.json")) settings.rosNamesPath = basePath + ".ros.json";
            if (string.IsNullOrEmpty(settings.displayName))
            {
                try { settings.displayName = UrdfModel.Parse(File.ReadAllText(settings.urdfPath)).RobotName; }
                catch (System.FormatException) { }
            }
            if (string.IsNullOrEmpty(settings.outputFolder))
                settings.outputFolder = "Assets/Robots/" + settings.ResolvedAssetName;
            serialized.Update();
        }

        private static void FileField(SerializedProperty property, string label, string extension)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(property, new GUIContent(label, property.tooltip));
                if (GUILayout.Button("…", GUILayout.Width(28f)))
                {
                    string start = string.IsNullOrEmpty(property.stringValue) ? "" : Path.GetDirectoryName(property.stringValue);
                    string picked = EditorUtility.OpenFilePanel(label, start, extension);
                    if (!string.IsNullOrEmpty(picked)) property.stringValue = Relative(picked);
                    GUI.FocusControl(null);
                }
            }
        }

        private static void FolderField(SerializedProperty property)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(property);
                if (GUILayout.Button("…", GUILayout.Width(28f)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Output folder", "Assets", "");
                    if (!string.IsNullOrEmpty(picked)) property.stringValue = Relative(picked);
                    GUI.FocusControl(null);
                }
            }
        }

        // Paths inside the project are kept relative to it, so settings travel with the project.
        private static string Relative(string path)
        {
            string project = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";
            path = path.Replace('\\', '/');
            return path.StartsWith(project) ? path.Substring(project.Length) : path;
        }
    }
}
