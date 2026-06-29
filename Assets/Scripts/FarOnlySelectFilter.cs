using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Select filter that restricts NearFarInteractor grabs to FAR mode only (hasOffset = true).
/// This ensures the right thumbstick always drives the InteractionAttachController's push/pull
/// manipulation, rather than falling through to snap-turn when grabbed in NEAR (direct) mode.
/// Attach this to the XRGrabInteractable's GameObject and register it in startingSelectFilters.
/// </summary>
public class FarOnlySelectFilter : MonoBehaviour, IXRSelectFilter
{
    public bool canProcess => isActiveAndEnabled;

    public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
    {
        if (interactor is NearFarInteractor nfi)
        {
            bool isFar = nfi.interactionAttachController.hasOffset;
            if (!isFar)
                Debug.Log($"[FarFilter] Blocked NEAR-mode grab of '{interactable.transform.name}' by '{interactor.transform.name}'");
            return isFar;
        }
        return true; // allow all other interactor types
    }
}
