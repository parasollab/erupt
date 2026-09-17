using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

// The grab/scale component stack the wrist menu puts on every user-created shape, shared
// with the FERL object factory so both kinds of object move and scale identically in VR.
public static class GrabbableShapeSetup
{
    // GetComponent returns a placeholder (not a C# null) for a missing component in the
    // editor, so `??` must not be used to fall back to AddComponent.
    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        T existing = go.GetComponent<T>();
        return existing != null ? existing : go.AddComponent<T>();
    }

    public static XRGrabInteractable Configure(GameObject shape, Material litMaterial, float alpha)
    {
        // Physics
        Rigidbody rb = GetOrAdd<Rigidbody>(shape);
        rb.useGravity = false;
        rb.isKinematic = true;

        // Allow a second controller to join the grab so the multiple-grab scale transformer
        // can run. One-handed push/pull remains available whenever only one hand is attached.
        XRGrabInteractable gi = GetOrAdd<XRGrabInteractable>(shape);
        gi.selectMode = InteractableSelectMode.Multiple;
        // Keep the object where it's grabbed instead of snapping it to the controller
        gi.useDynamicAttach = true;
        // Don't match the ray hit point's position for the attach anchor — keep it at the
        // object's own pivot so joystick rotation spins the object about its own center
        // instead of orbiting around wherever the ray happened to hit its surface.
        gi.matchAttachPosition = false;
        // Don't apply release velocity, object should stop moving as soon as it's let go
        gi.throwOnDetach = false;

        // Controls grabbing based on SelectionManager selection state
        GetOrAdd<SelectableGrabController>(shape);

        shape.tag = "Selectable";

        var axisLock = GetOrAdd<XRGrabTransformerScaleAxisLock>(shape);
        var lockPose = GetOrAdd<XRGrabTransformerLockPose>(shape);
        // Don't freeze rotation by default — joystick manipulation should be able to spin
        // the object about its own center. SnapSelectedToSurface() still re-syncs this via
        // SyncInitialRotation() in case freezePose is turned back on elsewhere.
        lockPose.freezePose = false;

        var general = GetOrAdd<XRGeneralGrabTransformer>(shape);
        general.allowTwoHandedScaling = false;
        general.clampScaling = false;

        var twoHanded = GetOrAdd<XRTwoHandedScaleTransformer>(shape);
        var uiScale = GetOrAdd<XRUIScaleTransformer>(shape);

        gi.AddMultipleGrabTransformer(general);
        gi.AddMultipleGrabTransformer(twoHanded);
        gi.AddMultipleGrabTransformer(axisLock);
        gi.AddMultipleGrabTransformer(lockPose);
        gi.AddMultipleGrabTransformer(uiScale);

        if (litMaterial != null)
        {
            foreach (MeshRenderer meshRenderer in shape.GetComponentsInChildren<MeshRenderer>())
            {
                // .material instantiates a per-renderer copy, so making it transparent here
                // leaves the shared litMaterial asset (also used by CollisionObjectsListenerSimple
                // for RViz-synced objects) opaque.
                meshRenderer.material = litMaterial;
                WristMenuController.MakeMaterialTransparent(meshRenderer.material, alpha);
            }
        }

        foreach (Collider collider in shape.GetComponentsInChildren<Collider>())
            collider.enabled = true;

        return gi;
    }
}
