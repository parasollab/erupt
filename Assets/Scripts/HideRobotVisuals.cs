using System.Collections.Generic;
using UnityEngine;

// Hides the robot in scenes whose task doesn't involve it, without deactivating the
// ur5e_robot hierarchy itself. Deactivating the root breaks things that depend on it
// staying active: ObjectMetricsLogger's GameObject.Find("BaseTransform") pose frame,
// StudySceneExporter/Task4CollisionObjectSetup's robot lookups, and the planning menu's
// articulation reads. Disabling just the Renderers leaves all of that working.
//
// Add to any GameObject in the scene (the robot root works). Runs in Awake so the robot
// never draws a first frame.
public class HideRobotVisuals : MonoBehaviour
{
    private const string RobotRootName = "ur5e_robot";

    [Tooltip("Robot root whose renderers get disabled. Left empty, finds 'ur5e_robot' in the scene.")]
    [SerializeField] private GameObject robotRoot;

    [Tooltip("Also disable the robot's colliders, so the invisible robot doesn't block rays/grabs or physically obstruct object placement.")]
    [SerializeField] private bool alsoDisableColliders = true;

    // Robot-attached objects that live OUTSIDE the ur5e_robot hierarchy but track it at
    // runtime (the IK grab handle is repositioned to the end effector every frame), so
    // hiding the robot alone leaves them floating in mid-air. Skipped quietly when a
    // scene doesn't have them.
    [Tooltip("Additional scene objects to hide the same way (renderers + optionally colliders). These track the robot but aren't children of it.")]
    [SerializeField] private string[] additionalObjectNames = { "EndEffectorHandle" };

    private void Awake()
    {
        if (robotRoot == null)
        {
            robotRoot = FindInOwnScene(RobotRootName);
        }
        if (robotRoot == null)
        {
            Debug.LogWarning($"HideRobotVisuals: no '{RobotRootName}' found in scene " +
                             $"'{gameObject.scene.name}'; nothing hidden.");
            return;
        }

        int hidden = 0, collidersOff = 0;
        List<string> hiddenNames = new List<string> { robotRoot.name };
        Hide(robotRoot, ref hidden, ref collidersOff);

        foreach (string name in additionalObjectNames)
        {
            GameObject go = FindInOwnScene(name);
            if (go == null) continue;
            Hide(go, ref hidden, ref collidersOff);
            hiddenNames.Add(go.name);
        }

        Debug.Log($"HideRobotVisuals: disabled {hidden} renderer(s)" +
                  (alsoDisableColliders ? $" and {collidersOff} collider(s)" : "") +
                  $" under: {string.Join(", ", hiddenNames)}.");
    }

    // The study controller loads scenes additively and unloads the outgoing scene only
    // after the incoming one activates, so during Awake two 'ur5e_robot's can be loaded
    // at once and GameObject.Find may return the outgoing scene's copy. Search only this
    // component's own scene (inactive objects included) so the right robot gets hidden.
    private GameObject FindInOwnScene(string name)
    {
        foreach (GameObject root in gameObject.scene.GetRootGameObjects())
        {
            if (root.name == name) return root;
            Transform found = FindChildRecursive(root.transform, name);
            if (found != null) return found.gameObject;
        }
        return null;
    }

    private static Transform FindChildRecursive(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform found = FindChildRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private void Hide(GameObject root, ref int hidden, ref int collidersOff)
    {
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r.enabled)
            {
                r.enabled = false;
                hidden++;
            }
        }
        if (alsoDisableColliders)
        {
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c.enabled)
                {
                    c.enabled = false;
                    collidersOff++;
                }
            }
        }
    }
}
