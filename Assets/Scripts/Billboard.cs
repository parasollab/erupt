using UnityEngine;

public class Billboard : MonoBehaviour
{
    public Transform cameraTransform;

    void LateUpdate()
    {
        if (cameraTransform == null) return;
        transform.LookAt(transform.position + cameraTransform.forward);
    }
}