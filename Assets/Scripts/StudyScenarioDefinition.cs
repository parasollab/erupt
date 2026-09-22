using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "ERUPT/Study/Scenario Definition", fileName = "Scenario")]
public sealed class StudyScenarioDefinition : ScriptableObject
{
    public enum EnvironmentKind
    {
        None,
        Factory,
        Kitchen,
        Interlude,
        Survey,
        Tutorial,
    }

    [Serializable]
    public sealed class ObjectPlacement
    {
        public GameObject prefab;
        public Vector3 localPosition;
        public Vector3 localEulerAngles;
        public Vector3 localScale = Vector3.one;
    }

    [Tooltip("Keeps compatibility with the ROS study plan and existing logger output during migration.")]
    public string scenarioName;
    public EnvironmentKind environment;
    public Vector3 participantPosition;
    public Vector3 participantEulerAngles;
    public List<ObjectPlacement> objects = new List<ObjectPlacement>();
}
