using UnityEngine;

// The MoveIt wrist menu lives on the persistent XR rig and spawns /collision_object shapes,
// which mean nothing to the FERL bridge; scenes that tick hideWristMenu switch it off while
// they are loaded and restore it when they unload.
public class WristMenuVisibility : MonoBehaviour
{
    [SerializeField] private bool hideWristMenu = true;

    private GameObject hidden;

    private void Start()
    {
        if (!hideWristMenu)
            return;
        WristMenuController wristMenu = FindFirstObjectByType<WristMenuController>(FindObjectsInactive.Include);
        if (wristMenu == null)
            return;
        hidden = wristMenu.gameObject;
        hidden.SetActive(false);
    }

    private void OnDestroy()
    {
        if (hidden != null)
            hidden.SetActive(true);
    }
}
