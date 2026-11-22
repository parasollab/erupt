using UnityEngine;

namespace Erupt.Ui
{
    public class Billboard : MonoBehaviour
    {
        public Transform cameraTransform;

        void LateUpdate()
        {
            transform.LookAt(transform.position + cameraTransform.forward);
        }
    }
}
