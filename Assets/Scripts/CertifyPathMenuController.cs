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
    private bool hasCertified = false;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

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
        certifyButton.text = hasCertified ? ButtonLabel + " ✓" : ButtonLabel;
    }
}
