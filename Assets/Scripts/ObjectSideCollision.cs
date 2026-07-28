using UnityEngine;

public class ObjectSideCollision : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private Material yellow;
    [SerializeField] private Material red;
    [SerializeField] private Material original;
    [SerializeField] private MeshRenderer _meshRenderer;
    [SerializeField] private bool touching = false;
    [SerializeField] private bool close = false;
    void Start()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        original = GetComponent<MeshRenderer>().material;
    }

    // Update is called once per frame
    void Update()
    {
        if (touching)
        {
            _meshRenderer.material = red;
        }else if (close)
        {
            _meshRenderer.material = yellow;
        }
        else
        {
            _meshRenderer.material = original;
        }
    }

    public void inProximity(bool b)
    {
        close = b;
    }
    public void isTouching(bool b)
    {
        touching = b;
    }

}
