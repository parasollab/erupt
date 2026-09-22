using UnityEngine;
using UnityEngine.UIElements;

// Standalone menu, placed only in Task 3 scenes (not part of WristMenuController/WristMenu.uxml,
// since this feature is specific to Task 3). Has a single "Get Indicator Sphere" button that
// spawns the draggable red sphere the participant places on the collision location they
// identified, logged via IndicatorSphereController on the spawned prefab.
public class IndicatorMenuController : MonoBehaviour
{
    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;

    [Header("Indicator Sphere")]
    [SerializeField] private GameObject indicatorSpherePrefab;
    [SerializeField] private float shapeSpawnDistance = 0.75f;

    private Button spawnIndicatorButton;
    private GameObject spawnedIndicatorSphere;

    private void OnEnable()
    {
        if (uiDocument == null)
            uiDocument = GetComponent<UIDocument>();

        VisualElement root = uiDocument?.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("IndicatorMenuController: No UIDocument/rootVisualElement found.");
            return;
        }

        spawnIndicatorButton = root.Q<Button>("indicatorMenuSpawnButton");
        if (spawnIndicatorButton == null)
        {
            Debug.LogError("IndicatorMenuController: 'indicatorMenuSpawnButton' not found in UXML.");
            return;
        }

        spawnIndicatorButton.clicked += OnSpawnIndicatorClicked;
    }

    private void OnDisable()
    {
        if (spawnIndicatorButton != null)
            spawnIndicatorButton.clicked -= OnSpawnIndicatorClicked;
    }

    private void OnSpawnIndicatorClicked()
    {
        if (indicatorSpherePrefab == null)
        {
            Debug.LogError("IndicatorMenuController: indicatorSpherePrefab is not assigned.");
            return;
        }

        // Only one indicator sphere at a time -- pressing the button again replaces it
        // (Destroy() triggers IndicatorSphereController's "object_deleted" log).
        if (spawnedIndicatorSphere != null)
        {
            Destroy(spawnedIndicatorSphere);
        }

        Vector3 spawnPosition = Camera.main != null
            ? Camera.main.transform.position + Camera.main.transform.forward * shapeSpawnDistance
            : Vector3.forward * shapeSpawnDistance;

        spawnedIndicatorSphere = Instantiate(indicatorSpherePrefab, spawnPosition, indicatorSpherePrefab.transform.rotation);

        IndicatorSphereController controller = spawnedIndicatorSphere.GetComponent<IndicatorSphereController>();
        if (controller != null)
        {
            controller.objectId = $"unity_indicator_sphere_{System.DateTime.Now.Ticks}";
        }
        else
        {
            Debug.LogError("IndicatorMenuController: indicatorSpherePrefab has no IndicatorSphereController component.");
        }

        Debug.Log("IndicatorMenuController: Spawned indicator sphere.");
    }
}
