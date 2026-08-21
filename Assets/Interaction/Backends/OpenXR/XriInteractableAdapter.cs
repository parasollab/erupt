using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Erupt.Interaction;

namespace Erupt.Interaction.Backends
{
    /// <summary>
    /// Publishes XRI grab frames to the sample bus so direct-manipulation telemetry is
    /// complete without rewriting the working grab and scale transformers.
    /// </summary>
    /// <remarks>
    /// Guidelines Part 3 requires every manipulation to emit samples. XRI grabs are a
    /// manipulation path that does not flow through the router, so they are adapted here
    /// rather than left as a silent hole in the stream.
    /// </remarks>
    [RequireComponent(typeof(XRGrabInteractable))]
    public class XriInteractableAdapter : MonoBehaviour
    {
        [SerializeField] private string sourceId = "xri-grab";

        private XRGrabInteractable grabInteractable;
        private bool isHeld;

        private void Awake() => grabInteractable = GetComponent<XRGrabInteractable>();

        private void OnEnable()
        {
            grabInteractable.selectEntered.AddListener(OnSelectEntered);
            grabInteractable.selectExited.AddListener(OnSelectExited);
        }

        private void OnDisable()
        {
            grabInteractable.selectEntered.RemoveListener(OnSelectEntered);
            grabInteractable.selectExited.RemoveListener(OnSelectExited);
            isHeld = false;
        }

        private void OnSelectEntered(SelectEnterEventArgs _) => isHeld = true;
        private void OnSelectExited(SelectExitEventArgs _) => isHeld = false;

        private void LateUpdate()
        {
            if (!isHeld) return;
            InteractionSampleBus.Publish(new InteractionSample(
                Modality.Controller, 1f, Time.unscaledTimeAsDouble,
                new Pose(transform.position, transform.rotation), sourceId));
        }
    }
}
