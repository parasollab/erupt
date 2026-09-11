using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using Erupt.Interaction;
using Erupt.Environment;

namespace Erupt.Obstacles
{
    /// <summary>
    /// Builds obstacles with their full component stack and registers them in the
    /// environment mirror. Planner registration is not done here: a planner's
    /// <see cref="IEnvironmentSync"/> reacts to <see cref="EnvironmentRegistry.Added"/>.
    /// </summary>
    /// <remarks>
    /// Lifted from WristMenuController.AddPrimitiveShape so that creation has one
    /// implementation shared by the Build-mode placement tool, the duplicate verb, and
    /// undo of a deletion. The component order and settings are reproduced exactly —
    /// the grab transformer stack in particular is order-sensitive.
    ///
    /// WristMenuController still keeps its own copy until Phase 3 retires it; it adopts
    /// its shapes into the registry so both paths produce the same mirror entry.
    /// </remarks>
    public static class ObstacleFactory
    {
        public static GameObject Create(ObstacleSnapshot snapshot,
                                        EnvironmentRegistry registry = null)
        {
            GameObject shape = GameObject.CreatePrimitive(snapshot.PrimitiveType);
            if (!string.IsNullOrEmpty(snapshot.Name)) shape.name = snapshot.Name;

            shape.transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
            shape.transform.localScale = snapshot.Scale;
            shape.tag = "Selectable";

            Rigidbody rb = shape.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;

            // Single, so only one hand holds it and the attach controller's push/pull
            // keeps working.
            var grab = shape.AddComponent<XRGrabInteractable>();
            grab.selectMode = InteractableSelectMode.Single;

            shape.AddComponent<SelectableGrabController>();
            shape.AddComponent<XRGrabTransformerScaleAxisLock>();
            shape.AddComponent<XRGrabTransformerLockPose>();

            var general = shape.AddComponent<XRGeneralGrabTransformer>();
            general.allowTwoHandedScaling = false;
            general.clampScaling = false;

            shape.AddComponent<XRTwoHandedScaleTransformer>();
            shape.AddComponent<XRUIScaleTransformer>();

            grab.AddMultipleGrabTransformer(general);
            grab.AddMultipleGrabTransformer(shape.GetComponent<XRTwoHandedScaleTransformer>());
            grab.AddMultipleGrabTransformer(shape.GetComponent<XRGrabTransformerScaleAxisLock>());
            grab.AddMultipleGrabTransformer(shape.GetComponent<XRGrabTransformerLockPose>());
            grab.AddMultipleGrabTransformer(shape.GetComponent<XRUIScaleTransformer>());

            // Typed selection, so tier 2 knows which verbs to offer.
            shape.AddComponent<SelectableMarker>().SetKind(SelectionKind.Obstacle);

            var renderer = shape.GetComponent<MeshRenderer>();
            if (renderer != null && snapshot.Material != null) renderer.sharedMaterial = snapshot.Material;

            Collider collider = shape.GetComponent<Collider>();
            if (collider != null) collider.enabled = true;

            // Identity first, then membership: a sync that reacts to Added sees a fully
            // initialised object and adds its own components (e.g. a planner publisher).
            var env = shape.AddComponent<EnvironmentObject>();
            env.Initialize(snapshot.ObjectId ?? NewObjectId(snapshot.PrimitiveType),
                           EnvironmentOwner.Unity, snapshot.PrimitiveType, mesh: false);
            registry?.Register(env);

            return shape;
        }

        /// <summary>Matches the id scheme WristMenuController has always used.</summary>
        public static string NewObjectId(PrimitiveType primitiveType) =>
            $"unity_{primitiveType.ToString().ToLower()}_{System.DateTime.Now.Ticks}";

        /// <summary>Remove an obstacle from the mirror and the scene.</summary>
        /// <param name="publishRemoval">
        /// False when the removal was commanded by the remote scene, so a sync does not
        /// echo the removal back to it. Delivered to syncs as <see cref="RemovalOrigin"/>.
        /// </param>
        public static void Destroy(GameObject obstacle,
                                   EnvironmentRegistry registry = null,
                                   bool publishRemoval = true)
        {
            if (obstacle == null) return;

            var env = obstacle.GetComponent<EnvironmentObject>();
            if (env != null)
                registry?.Unregister(env.Id, publishRemoval ? RemovalOrigin.Local : RemovalOrigin.Remote);

            Object.Destroy(obstacle);
        }
    }
}
