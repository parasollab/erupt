using UnityEngine;

public class MouseRobotRayInteractor : MonoBehaviour
{
    [SerializeField] private Quest3RobotInteractionController robotInteraction;
    [SerializeField] private Camera rayCamera;
    [SerializeField] private float rayLength = 6f;
    [SerializeField] private LayerMask raycastLayers = ~0;
    [SerializeField] private float jointJogRadiansPerSecond = 0.8f;

    private bool isDraggingHandle;

    private void Awake()
    {
        if (rayCamera == null)
            rayCamera = Camera.main;
    }

    private void Update()
    {
        if (robotInteraction == null || rayCamera == null) return;

        Ray ray = rayCamera.ScreenPointToRay(Input.mousePosition);
        bool hasHit = Physics.Raycast(ray, out RaycastHit hit, rayLength, raycastLayers, QueryTriggerInteraction.Collide);

        if (Input.GetMouseButtonDown(0) && hasHit)
        {
            isDraggingHandle = robotInteraction.TryBeginHandleDrag(this, ray, hit);
            if (!isDraggingHandle)
                robotInteraction.SelectFromHit(hit);
        }

        if (isDraggingHandle && Input.GetMouseButton(0))
        {
            robotInteraction.UpdateHandleDrag(this, ray);
        }

        if (isDraggingHandle && Input.GetMouseButtonUp(0))
        {
            robotInteraction.EndHandleDrag(this);
            isDraggingHandle = false;
        }

        if (!isDraggingHandle)
        {
            float direction = 0f;
            if (Input.GetKey(KeyCode.RightArrow)) direction += 1f;
            if (Input.GetKey(KeyCode.LeftArrow)) direction -= 1f;
            // Called every frame (even at direction == 0) so JogSelectedJoint can detect the
            // moving-to-idle transition itself and log grab_end -- see Quest3ControllerRayInteractor
            // for the same pattern.
            robotInteraction.JogSelectedJoint(direction * jointJogRadiansPerSecond * Time.deltaTime);
        }
    }
}
