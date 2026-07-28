using UnityEngine;
using UnityEngine.UI;

public class UIHandler : MonoBehaviour
{
    [SerializeField] private GameObject UICanvas;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private bool showUI;
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            showUI = !showUI;
        }
        UICanvas.SetActive(showUI);
    }
}
