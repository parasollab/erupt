using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// The component stack that turns a GameObject into a selectable, grabbable MoveIt collision
/// object: kinematic rigidbody, XRI grab interactable with the project's grab transformers,
/// SelectableGrabController gating, the "Selectable" tag and a CollisionObjectPublisher.
/// Shared by the wrist menu's shape palette, the planning-scene mirror and the factory
/// station's part spawner so the three stay identical.
/// </summary>
public static class GrabbableCollisionObjectFactory
{
    /// <summary>
    /// Adds the grab/publish stack to <paramref name="go"/> (which must already have a
    /// collider and renderer, e.g. from <see cref="GameObject.CreatePrimitive"/>).
    /// </summary>
    /// <param name="markAlreadyPublished">true for objects mirrored from ROS, so the
    /// publisher does not re-ADD what the planning scene already holds.</param>
    /// <param name="freezePose">true keeps the authored rotation during grabs (the
    /// XRGrabTransformerLockPose freeze); false lets the joystick spin the object.</param>
    public static CollisionObjectPublisher Configure(GameObject go, string objectId, GameObject worldOrigin,
        bool markAlreadyPublished = false, bool freezePose = false)
    {
        Rigidbody rb = GetOrAdd<Rigidbody>(go);
        rb.useGravity = false;
        rb.isKinematic = true;

        // Allow a second controller to join the grab so the multiple-grab scale transformer
        // can run. One-handed push/pull remains available whenever only one hand is attached.
        var gi = GetOrAdd<XRGrabInteractable>(go);
        gi.selectMode = InteractableSelectMode.Multiple;
        // Keep the object where it's grabbed instead of snapping it to the controller.
        gi.useDynamicAttach = true;
        // Keep the attach anchor at the object's own pivot so joystick rotation spins the
        // object about its centre instead of orbiting the ray hit point.
        gi.matchAttachPosition = false;
        // No release velocity: the object stops as soon as it is let go.
        gi.throwOnDetach = false;

        // Grabbing is gated on SelectionManager selection state.
        GetOrAdd<SelectableGrabController>(go);

        go.tag = "Selectable";

        var axisLock = GetOrAdd<XRGrabTransformerScaleAxisLock>(go);
        var lockPose = GetOrAdd<XRGrabTransformerLockPose>(go);
        lockPose.freezePose = freezePose;

        var general = GetOrAdd<XRGeneralGrabTransformer>(go);
        general.allowTwoHandedScaling = false;
        general.clampScaling = false;

        var twoHanded = GetOrAdd<XRTwoHandedScaleTransformer>(go);
        var uiScale = GetOrAdd<XRUIScaleTransformer>(go);

        gi.AddMultipleGrabTransformer(general);
        gi.AddMultipleGrabTransformer(twoHanded);
        gi.AddMultipleGrabTransformer(axisLock);
        gi.AddMultipleGrabTransformer(lockPose);
        gi.AddMultipleGrabTransformer(uiScale);

        Collider collider = go.GetComponent<Collider>();
        if (collider != null)
            collider.enabled = true;

        CollisionObjectPublisher publisher = GetOrAdd<CollisionObjectPublisher>(go);
        publisher.isMesh = false;
        publisher.objectId = objectId;
        publisher.worldOrigin = worldOrigin;
        publisher.hasBeenPublished = markAlreadyPublished;
        return publisher;
    }

    // GetComponent on a missing component returns Unity's placeholder, not a C# null, so the
    // ?? operator never adds anything; compare explicitly.
    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    /// <summary>Creates a primitive of the given world size and configures it.</summary>
    public static GameObject CreatePrimitive(PrimitiveType type, string name, Vector3 size, string objectId,
        GameObject worldOrigin, Material material = null, bool freezePose = false)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.localScale = size;
        if (material != null)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = material;
        }
        Configure(go, objectId, worldOrigin, markAlreadyPublished: false, freezePose: freezePose);
        return go;
    }
}
