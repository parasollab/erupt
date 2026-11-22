using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using Erupt.Environment;

namespace Erupt.Obstacles
{
    /// <summary>
    /// The component stack that makes an object selectable and grabbable: a kinematic
    /// rigidbody, a collider, an XRI grab interactable gated on selection by
    /// <see cref="SelectableGrabController"/>, the "Selectable" tag and the grab transformers.
    /// One implementation for the obstacles core builds and for the objects a planner plugin
    /// mirrors, so they all handle the same way.
    /// </summary>
    public static class GrabbableObjectSetup
    {
        /// <summary>
        /// Adds the grab stack to <paramref name="go"/>; components it already has are reused.
        /// </summary>
        /// <param name="scalable">
        /// true (primitives) adds the scale transformers and the rotation lock next to the
        /// general grab transformer; false (meshes) leaves the object movable only.
        /// </param>
        public static XRGrabInteractable Configure(GameObject go, bool scalable = true)
        {
            // The rigidbody and the collider come first: the interactable reads both when it wakes.
            Rigidbody rb = GetOrAdd<Rigidbody>(go);
            rb.useGravity = false;
            rb.isKinematic = true;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null) collider.enabled = true;
            else go.AddComponent<MeshCollider>();

            // Single, so only one hand holds it and the attach controller's push/pull
            // keeps working.
            var grab = GetOrAdd<XRGrabInteractable>(go);
            grab.selectMode = InteractableSelectMode.Single;

            GetOrAdd<SelectableGrabController>(go);
            go.tag = "Selectable";

            if (!scalable)
            {
                GetOrAdd<XRGeneralGrabTransformer>(go);
                return grab;
            }

            var axisLock = GetOrAdd<XRGrabTransformerScaleAxisLock>(go);
            var lockPose = GetOrAdd<XRGrabTransformerLockPose>(go);

            var general = GetOrAdd<XRGeneralGrabTransformer>(go);
            general.allowTwoHandedScaling = false;
            general.clampScaling = false;

            var twoHanded = GetOrAdd<XRTwoHandedScaleTransformer>(go);
            var uiScale = GetOrAdd<XRUIScaleTransformer>(go);

            // The order matters: each transformer works on the pose the previous one left.
            grab.AddMultipleGrabTransformer(general);
            grab.AddMultipleGrabTransformer(twoHanded);
            grab.AddMultipleGrabTransformer(axisLock);
            grab.AddMultipleGrabTransformer(lockPose);
            grab.AddMultipleGrabTransformer(uiScale);
            return grab;
        }

        // GetComponent on a missing component returns Unity's placeholder, not a C# null, so
        // the ?? operator never adds anything; compare explicitly.
        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            T c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }
    }
}
