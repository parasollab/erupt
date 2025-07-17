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
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        sphere.AddComponent<Rigidbody>().useGravity = true; // Add a Rigidbody for physics
        sphere.AddComponent<XRGrabInteractable>(); // Assuming you have a Grabbable script for interaction
    }

    public void AddCube()
    {
        // Spawn a grabbable cube in front of the user
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        cube.AddComponent<Rigidbody>().useGravity = true; // Add a Rigidbody for physics
        cube.AddComponent<XRGrabInteractable>(); // Assuming you have a Grabbable script for interaction
    }

    public void AddCylinder()
    {
        // Spawn a grabbable cylinder in front of the user
        GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cylinder.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        cylinder.AddComponent<Rigidbody>().useGravity = true; // Add a Rigidbody for physics
        cylinder.AddComponent<XRGrabInteractable>(); // Assuming you have a Grabbable script for interaction
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
