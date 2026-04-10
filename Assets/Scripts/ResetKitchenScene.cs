using UnityEngine;
using UnityEngine.InputSystem;

public class ResetKitchenScene : MonoBehaviour
{
    [SerializeField]
    private InputActionReference _resetAction;

    private void Start()
    {
        _resetAction.action.performed += OnResetPressed;
    }

    private void OnResetPressed(InputAction.CallbackContext context)
    {
        // Load the current scene again to reset it
        UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
    }

    private void OnDestroy()
    {
        _resetAction.action.performed -= OnResetPressed;
    }
}
