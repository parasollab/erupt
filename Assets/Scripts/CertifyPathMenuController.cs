using UnityEngine;
using UnityEngine.UIElements;

// Standalone menu, placed only in Task 4 scenes (same pattern as IndicatorMenuController in
// Task 3 -- not part of WristMenuController/WristMenu.uxml, since this feature is specific to
// Task 4). Has a single "Certify Path Collision-Free" button the participant presses once
// they've visually confirmed their planned path avoids collisions.
public class CertifyPathMenuController : MonoBehaviour
{
    private const string ObjectId = "path_certification";
    private const string ButtonLabel = "Certify Path Collision-Free";

    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;

    private Button certifyButton;
    private UnityEngine.UI.Button uguiCertifyButton;
    private UnityEngine.UI.Text uguiCertifyButtonText;
    private Canvas uguiCanvas;
    private GameObject uguiRoot;
    private bool hasCertified = false;
    private bool useUGUIRuntimeMenu;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        useUGUIRuntimeMenu = VisionOSSampleControlsUI.ShouldUseRuntimeUGUI() || uiDocument == null;
        if (useUGUIRuntimeMenu)
        {
            if (uiDocument != null)
                uiDocument.enabled = false;

            EnsureUGUIMenu();
            hasCertified = false;
            UpdateButtonLabel();
            return;
        }

        VisualElement root = uiDocument?.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("CertifyPathMenuController: No UIDocument/rootVisualElement found.");
            return;
        }

        certifyButton = root.Q<Button>("certifyPathMenuButton");
        if (certifyButton == null)
        {
            Debug.LogError("CertifyPathMenuController: 'certifyPathMenuButton' not found in UXML.");
            return;
        }

        hasCertified = false;
        UpdateButtonLabel();
        certifyButton.clicked += OnCertifyClicked;
    }

    private void OnDisable()
    {
        if (certifyButton != null)
            certifyButton.clicked -= OnCertifyClicked;
        if (uguiCertifyButton != null)
            uguiCertifyButton.onClick.RemoveListener(OnCertifyClicked);
    }

    private void EnsureUGUIMenu()
    {
        uguiCanvas = VisionOSSampleControlsUI.EnsureCanvas(
            transform,
            "VisionOS Certify Path Menu Canvas",
            new Vector2(1050f, 300f),
            new Vector2(1.05f, 0.3f),
            new Vector3(0f, -0.1f, 0f),
            sortingOrder: 130);

        uguiRoot = uguiCanvas.gameObject;
        VisionOSSampleControlsUI.ClearChildren(uguiRoot.transform);

        var panel = VisionOSSampleControlsUI.CreateVerticalPanel(
            uguiRoot.transform,
            "Certify Path Menu Panel",
            new Vector2(1050f, 300f));

        uguiCertifyButton = VisionOSSampleControlsUI.CreateButton(
            panel.transform,
            ButtonLabel,
            OnCertifyClicked,
            950f,
            100f,
            35);
        uguiCertifyButtonText = uguiCertifyButton.GetComponentInChildren<UnityEngine.UI.Text>();
    }

    private void OnCertifyClicked()
    {
        ObjectMetricsLogger.Instance?.LogEvent("path_certified", ObjectId);
        Debug.Log("CertifyPathMenuController: Path certified collision-free.");

        hasCertified = true;
        UpdateButtonLabel();
    }

    private void UpdateButtonLabel()
    {
        string label = hasCertified ? ButtonLabel + " ✓" : ButtonLabel;
        if (certifyButton != null)
            certifyButton.text = label;
        if (uguiCertifyButtonText != null)
            uguiCertifyButtonText.text = label;
    }
}
