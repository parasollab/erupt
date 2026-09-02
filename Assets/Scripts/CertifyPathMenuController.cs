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
    private MoveItPlanningRequestMenuUI planningMenu;

    // Read by StudyController's advance gate: Task 4 scenes can't be left until the
    // participant has certified a path.
    public bool HasCertified => hasCertified;

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

        planningMenu = FindFirstObjectByType<MoveItPlanningRequestMenuUI>(FindObjectsInactive.Include);
        if (planningMenu == null)
            Debug.LogWarning("CertifyPathMenuController: no MoveItPlanningRequestMenuUI in this scene; certify button stays enabled.");
        UpdateInteractability();
    }

    // Certification only makes sense once there is a successfully planned path to certify,
    // so the button stays disabled until then. Fails open (enabled) if the planning menu is
    // missing from the scene.
    private void Update()
    {
        UpdateInteractability();
    }

    private void UpdateInteractability()
    {
        if (certifyButton == null)
            return;
        certifyButton.SetEnabled(planningMenu == null || planningMenu.HasPlannedSuccessfully);
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
