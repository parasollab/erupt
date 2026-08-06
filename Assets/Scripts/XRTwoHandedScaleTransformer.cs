using UnityEngine;

namespace UnityEngine.XR.Interaction.Toolkit.Transformers
{
    /// <summary>
    /// Two-handed grab transformer that uniformly scales an object based on the distance
    /// between the two controllers. Moving the controllers together shrinks the object;
    /// moving them apart grows it. The object's orientation is held at the rotation it had
    /// when the second controller joined the grab.
    /// </summary>
    public class XRTwoHandedScaleTransformer : XRBaseGrabTransformer
    {
        [Tooltip("Minimum allowed local scale per axis")]
        public float minScale = 0.001f;

        [Tooltip("Maximum scale increase allowed during one continuous two-handed grab")]
        [Min(1f)]
        public float maxScaleMultiplier = 10f;

        [Tooltip("How quickly controller tracking changes are applied. Higher values are more responsive.")]
        [Range(1f, 30f)]
        public float distanceSmoothing = 20f;

        // Ignore invalid/coincident controller samples rather than dividing by a nearly zero
        // baseline. This is below the controllers' physical size and does not limit normal use.
        private const float MinUsableControllerDistance = 0.01f;

        private bool _initialized = false;
        private float _startControllerDistance;
        private float _filteredControllerDistance;
        private Vector3 _scaleAtTwoHandStart;
        private bool _orientationLocked;
        private Quaternion _lockedOrientation;

        public override void Process(
            UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grab,
            XRInteractionUpdateOrder.UpdatePhase phase,
            ref Pose targetPose,
            ref Vector3 localScale)
        {
            var interactors = grab.interactorsSelecting;
            if (interactors.Count < 2)
            {
                _initialized = false;
                _orientationLocked = false;
                return;
            }

            // The general multiple-grab transformer runs before this transformer and would
            // otherwise rotate the object as the controllers move relative to one another.
            // Capture the visible orientation when two-hand scaling begins and override the
            // target pose in every update phase until a controller releases.
            if (!_orientationLocked)
            {
                _lockedOrientation = grab.transform.rotation;
                _orientationLocked = true;
            }
            targetPose.rotation = _lockedOrientation;

            if (phase != XRInteractionUpdateOrder.UpdatePhase.Dynamic)
                return;

            // Use controller positions rather than the minimum distance between their rays.
            // Rays normally intersect at the object, making their closest distance approach
            // zero and change discontinuously as they cross, which caused freezes and jumps.
            Transform tfA = interactors[0].transform;
            Transform tfB = interactors[1].transform;
            float currentDistance = Vector3.Distance(tfA.position, tfB.position);

            if (currentDistance < MinUsableControllerDistance)
                return;

            if (!_initialized)
            {
                _startControllerDistance = currentDistance;
                _filteredControllerDistance = currentDistance;
                _scaleAtTwoHandStart = localScale;
                _initialized = true;
                return;
            }

            float smoothingFactor = 1f - Mathf.Exp(-distanceSmoothing * Time.unscaledDeltaTime);
            _filteredControllerDistance = Mathf.Lerp(
                _filteredControllerDistance,
                currentDistance,
                smoothingFactor);

            // Calculate from the scale captured when the second hand joined rather than
            // multiplying frame-to-frame ratios. This prevents tracking noise from compounding.
            float scaleRatio = Mathf.Min(
                _filteredControllerDistance / _startControllerDistance,
                maxScaleMultiplier);

            float minimumRatio = 0f;
            minimumRatio = Mathf.Max(minimumRatio, MinimumRatioForAxis(_scaleAtTwoHandStart.x));
            minimumRatio = Mathf.Max(minimumRatio, MinimumRatioForAxis(_scaleAtTwoHandStart.y));
            minimumRatio = Mathf.Max(minimumRatio, MinimumRatioForAxis(_scaleAtTwoHandStart.z));
            scaleRatio = Mathf.Max(scaleRatio, minimumRatio);

            localScale = _scaleAtTwoHandStart * scaleRatio;
        }

        private float MinimumRatioForAxis(float startingAxisScale)
        {
            float magnitude = Mathf.Abs(startingAxisScale);
            return magnitude > Mathf.Epsilon ? minScale / magnitude : 0f;
        }
    }
}
