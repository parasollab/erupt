using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

// Confirm/Cancel dialog shown by StudyController before any study advancement. Assembled at
// runtime from the XRI "UI Toolkit Grab UI" prefab (same world-space UI stack as
// CertifyPathMenuController), so the participant resolves it by pointing the controller ray
// at a button and pulling the trigger.
public class AdvanceConfirmDialogController : MonoBehaviour
{
    // Dedicated layer/camera keeps the prompt visible over scene geometry while leaving the
    // document in world space so XR controller rays can continue to pick its buttons.
    private const int kOverlayLayer = 30;

    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;

    public event System.Action Confirmed;
    public event System.Action Cancelled;

    private Button confirmButton;
    private Button cancelButton;
    private Transform dialogRoot;
    private Camera sourceCamera;
    private Camera overlayCamera;
    private List<Camera> sourceCameraStack;
    private Vector3 cameraLocalOffset;
    private bool sourceCameraRenderedOverlayLayer;

    public void ConfigureHeadLockedOverlay(
        Transform root,
        float distance,
        float referenceDistance,
        float referenceVerticalOffset)
    {
        dialogRoot = root;
        sourceCamera = FindDisplayCamera();
        if (dialogRoot == null || sourceCamera == null)
        {
            Debug.LogWarning("AdvanceConfirmDialogController: Could not configure the head-locked overlay because no display camera was found.");
            return;
        }

        float scale = referenceDistance > 0f ? distance / referenceDistance : 1f;
        dialogRoot.localScale *= scale;
        cameraLocalOffset = new Vector3(0f, -referenceVerticalOffset * scale, distance);
        SetLayerRecursively(dialogRoot.gameObject, kOverlayLayer);
        CreateOverlayCamera();
        UpdateOverlayPose();
    }

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        VisualElement root = uiDocument?.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("AdvanceConfirmDialogController: No UIDocument/rootVisualElement found.");
            return;
        }

        confirmButton = root.Q<Button>("advanceConfirmButton");
        cancelButton = root.Q<Button>("advanceCancelButton");
        if (confirmButton == null || cancelButton == null)
        {
            Debug.LogError("AdvanceConfirmDialogController: Confirm/Cancel buttons not found in UXML.");
            return;
        }

        confirmButton.clicked += OnConfirmClicked;
        cancelButton.clicked += OnCancelClicked;
    }

    // Reconfigures the dialog as a requirements-not-met notice: no Confirm choice, just a
    // single OK button that dismisses (StudyController wires both events to close).
    public void SetBlockedMode(string title, string body)
    {
        VisualElement root = uiDocument?.rootVisualElement;
        Label titleLabel = root?.Q<Label>("advanceConfirmTitleLabel");
        Label bodyLabel = root?.Q<Label>("advanceConfirmInstructionsLabel");
        if (titleLabel != null)
            titleLabel.text = title;
        if (bodyLabel != null)
            bodyLabel.text = body;
        if (confirmButton != null)
            confirmButton.style.display = DisplayStyle.None;
        if (cancelButton != null)
            cancelButton.text = "OK";
    }

    private void OnDisable()
    {
        if (confirmButton != null)
            confirmButton.clicked -= OnConfirmClicked;
        if (cancelButton != null)
            cancelButton.clicked -= OnCancelClicked;

        ReleaseOverlayCamera();
    }

    private void LateUpdate()
    {
        UpdateOverlayPose();
    }

    private void Update()
    {
        // Editor/desktop convenience: Enter confirms, Escape cancels, so the flow can be
        // tested without a headset (the ray-click path needs XR controllers).
        if (Keyboard.current == null)
            return;
        if (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame)
            OnConfirmClicked();
        else if (Keyboard.current.escapeKey.wasPressedThisFrame)
            OnCancelClicked();
    }

    private void OnConfirmClicked()
    {
        Confirmed?.Invoke();
    }

    private void OnCancelClicked()
    {
        Cancelled?.Invoke();
    }

    private void UpdateOverlayPose()
    {
        if (dialogRoot == null || sourceCamera == null)
            return;

        Transform cameraTransform = sourceCamera.transform;
        dialogRoot.SetPositionAndRotation(
            cameraTransform.TransformPoint(cameraLocalOffset),
            cameraTransform.rotation);
    }

    private void CreateOverlayCamera()
    {
        int overlayMask = 1 << kOverlayLayer;
        sourceCameraRenderedOverlayLayer = (sourceCamera.cullingMask & overlayMask) != 0;
        sourceCamera.cullingMask &= ~overlayMask;

        GameObject cameraObject = new GameObject("Confirmation Dialog Overlay Camera");
        cameraObject.transform.SetParent(sourceCamera.transform, false);
        overlayCamera = cameraObject.AddComponent<Camera>();
        overlayCamera.CopyFrom(sourceCamera);
        overlayCamera.cullingMask = overlayMask;
        overlayCamera.useOcclusionCulling = false;

        UniversalAdditionalCameraData sourceCameraData = sourceCamera.GetUniversalAdditionalCameraData();
        UniversalAdditionalCameraData overlayCameraData = overlayCamera.GetUniversalAdditionalCameraData();
        overlayCameraData.renderType = CameraRenderType.Overlay;
        sourceCameraStack = sourceCameraData.cameraStack;
        if (sourceCameraStack != null)
        {
            sourceCameraStack.Add(overlayCamera);
        }
        else
        {
            // The head-locked placement still prevents the common table/robot overlap case
            // when a custom URP renderer does not support overlay-camera stacking.
            if (sourceCameraRenderedOverlayLayer)
                sourceCamera.cullingMask |= overlayMask;
            overlayCamera.enabled = false;
            Debug.LogWarning("AdvanceConfirmDialogController: The active renderer does not support camera stacking; using head-locked placement without forced draw-on-top rendering.");
        }
    }

    private void ReleaseOverlayCamera()
    {
        if (sourceCameraStack != null && overlayCamera != null)
            sourceCameraStack.Remove(overlayCamera);

        if (sourceCamera != null && sourceCameraRenderedOverlayLayer)
            sourceCamera.cullingMask |= 1 << kOverlayLayer;

        if (overlayCamera != null)
        {
            overlayCamera.enabled = false;
            Destroy(overlayCamera.gameObject);
            overlayCamera = null;
        }

        sourceCameraStack = null;
    }

    private static Camera FindDisplayCamera()
    {
        if (Camera.main != null)
            return Camera.main;

        Camera[] cameras = Camera.allCameras;
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] != null && cameras[i].isActiveAndEnabled)
                return cameras[i];
        }

        return null;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        foreach (Transform child in root.transform)
            SetLayerRecursively(child.gameObject, layer);
    }
}
