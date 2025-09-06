// using UnityEngine;
// using UnityEngine.XR.Interaction.Toolkit;
// using UnityEngine.XR.Interaction.Toolkit.Interactables;
// using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace UnityEngine.XR.Interaction.Toolkit.Transformers
{
    public class XRUIScaleTransformer : XRBaseGrabTransformer
    {
        [Tooltip("Minimum allowed local scale per axis")] public float minScale = 0.01f;

        // Accumulated delta from UI this frame (in local-scale units)
        public Vector3 queuedDelta;   // set by your slider code

        // XRITK calls this every frame while selected; exact signature may vary by XRITK version.
        // If your version uses a different Process signature, adapt accordingly.
        public override void Process(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grab, XRInteractionUpdateOrder.UpdatePhase phase,
                                     ref Pose targetPose, ref Vector3 localScale)
        {
            if (phase != XRInteractionUpdateOrder.UpdatePhase.Dynamic)
                return;

            if (queuedDelta == Vector3.zero)
                return;

            var axisLock = grab.GetComponent<XRGrabTransformerScaleAxisLock>();
            bool allowX = axisLock == null || !axisLock.freezeXScale;
            bool allowY = axisLock == null || !axisLock.freezeYScale;
            bool allowZ = axisLock == null || !axisLock.freezeZScale;

            var d = queuedDelta;
            queuedDelta = Vector3.zero; // consume

            if (allowX) localScale.x = Mathf.Max(minScale, localScale.x + d.x);
            if (allowY) localScale.y = Mathf.Max(minScale, localScale.y + d.y);
            if (allowZ) localScale.z = Mathf.Max(minScale, localScale.z + d.z);
        }
    }
}
