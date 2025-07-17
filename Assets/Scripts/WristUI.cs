using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class WristUI : MonoBehaviour
{

    public InputActionAsset inputActions;
    private Canvas _wristUICanvas;
    private InputAction _menu;

    private GameObject addShapePanel;
    private GameObject resizePanel;

    public GameObject spherePrefab;
    public GameObject cubePrefab;
    public GameObject cylinderPrefab;

    public SelectionManager selectionManager;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        _wristUICanvas = GetComponent<Canvas>();
        _menu = inputActions.FindActionMap("XRI Left Interaction").FindAction("Menu");
        _menu.performed += ToggleMenu;

        // Set the game object this script is attached to as inactive initially
        if (_wristUICanvas != null)
        {
            _wristUICanvas.enabled = false;
        }

        // Get panels as children of the game object this script is attached to
        addShapePanel = transform.Find("ShapeSelectorPanel")?.gameObject;
        resizePanel = transform.Find("ResizePanel")?.gameObject;

        if (addShapePanel != null)
        {
            addShapePanel.SetActive(false);
        }

        if (resizePanel != null)
        {
            resizePanel.SetActive(false);
        }
    }

    public void OpenAddShapePanel()
    {
        if (addShapePanel != null)
        {
            addShapePanel.SetActive(true);
            if (resizePanel != null)
            {
                resizePanel.SetActive(false);
            }
        }
    }

    public void OpenResizePanel()
    {
        if (resizePanel != null)
        {
            resizePanel.SetActive(true);
            if (addShapePanel != null)
            {
                addShapePanel.SetActive(false);
            }
        }
    }

    public void ClosePanels()
    {
        if (addShapePanel != null)
        {
            addShapePanel.SetActive(false);
        }

        if (resizePanel != null)
        {
            resizePanel.SetActive(false);
        }
    }

    public void AddSphere()
    {
        // Spawn a grabbable sphere in front of the user
        GameObject sphere = Instantiate(spherePrefab);
        sphere.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        sphere.AddComponent<Rigidbody>().useGravity = true; // Add a Rigidbody for physics
        sphere.AddComponent<XRGrabInteractable>(); // Assuming you have a Grabbable script for interaction
        sphere.transform.localScale = Vector3.one; // Set default scale

        // Add tag for selection
        sphere.tag = "Selectable"; // Ensure the sphere is tagged as selectable for the SelectionManager

        // Give the shape a collider if it doesn't have one
        if (sphere.GetComponent<Collider>() == null)
        {
            sphere.AddComponent<SphereCollider>();
        }
        else
        {
            sphere.GetComponent<Collider>().enabled = true; // Ensure the collider is enabled
        }
    }

    public void AddCube()
    {
        // Spawn a grabbable cube in front of the user
        GameObject cube = Instantiate(cubePrefab);
        cube.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        cube.AddComponent<Rigidbody>().useGravity = true; // Add a Rigidbody for physics
        cube.AddComponent<XRGrabInteractable>(); // Assuming you have a Grabbable script for interaction
        cube.transform.localScale = Vector3.one; // Set default scale

        // Add tag for selection
        cube.tag = "Selectable"; // Ensure the cube is tagged as selectable for the SelectionManager

        // Give the shape a collider if it doesn't have one
        if (cube.GetComponent<Collider>() == null)
        {
            cube.AddComponent<BoxCollider>();
        }
        else
        {
            cube.GetComponent<Collider>().enabled = true; // Ensure the collider is enabled
        }
    }

    public void AddCylinder()
    {
        // Spawn a grabbable cylinder in front of the user
        GameObject cylinder = Instantiate(cylinderPrefab);
        cylinder.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        cylinder.AddComponent<Rigidbody>().useGravity = true; // Add a Rigidbody for physics
        cylinder.AddComponent<XRGrabInteractable>(); // Assuming you have a Grabbable script for interaction
        cylinder.transform.localScale = Vector3.one; // Set default scale

        // Add tag for selection
        cylinder.tag = "Selectable"; // Ensure the cylinder is tagged as selectable for the SelectionManager

        // Give the shape a collider if it doesn't have one
        if (cylinder.GetComponent<Collider>() == null)
        {
            cylinder.AddComponent<CapsuleCollider>();
        }
        else
        {
            cylinder.GetComponent<Collider>().enabled = true; // Ensure the collider is enabled
        }
    }

    public void DeleteSelectedObject()
    {
        selectionManager.DeleteSelectedObject();
    }

    private void ToggleMenu(InputAction.CallbackContext context)
    {
        if (_wristUICanvas != null)
        {
            _wristUICanvas.enabled = !_wristUICanvas.enabled;
        }
    }

    private void OnDestroy()
    {
        _menu.performed -= ToggleMenu;
    }
}
