using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using Unity.XR.CoreUtils;

// Name-based lookups into the persistent XR rig's InputActionManager (SpawnHuman's
// pattern), so step displays need no per-scene InputActionReference wiring.
public static class StudyInputActions
{
    // Right-controller 'A' button; added to the "XRI Right Interaction" map alongside
    // AdvanceStudy (right 'B'). Returns null when the rig or action is unavailable.
    public static InputAction FindStepBack()
    {
        return Find("XRI Right Interaction", "StepBack");
    }

    private static InputAction Find(string mapName, string actionName)
    {
        XROrigin xrOrigin = PersistentXRInfrastructure.ResolveXROrigin();
        if (xrOrigin == null)
            return null;

        InputActionManager actionManager =
            xrOrigin.GetComponent<InputActionManager>() ?? xrOrigin.GetComponentInChildren<InputActionManager>();
        if (actionManager == null || actionManager.actionAssets.Count == 0)
            return null;

        return actionManager.actionAssets[0]?.FindActionMap(mapName)?.FindAction(actionName);
    }
}
