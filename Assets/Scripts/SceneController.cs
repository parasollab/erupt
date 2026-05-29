using UnityEngine;
using UnityEngine.InputSystem;

public class SceneController : MonoBehaviour
{
    [SerializeField] private InputActionReference _togglePlanesAction;
    [SerializeField] private SceneAnchorCollisionBridge _anchorBridge;

    private bool _isVisible = true;

    void Start()
    {
        _togglePlanesAction.action.performed += OnTogglePlanesAction;
    }

    private void OnTogglePlanesAction(InputAction.CallbackContext context)
    {
        _isVisible = !_isVisible;
        if (_anchorBridge != null)
            _anchorBridge.SetVisualsVisible(_isVisible);
    }

    void OnDestroy()
    {
        _togglePlanesAction.action.performed -= OnTogglePlanesAction;
    }
}
