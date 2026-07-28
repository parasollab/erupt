using UnityEngine;
using static Codice.Client.Common.EventTracking.TrackFeatureUseEvent.Features.DesktopGUI.Filters;

public class colorCollisionDetection : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private Material yellow;
    [SerializeField] private Material red;
    [SerializeField] private MeshRenderer mr;
    private Material originalMaterialSelf; //The Material of the object itself
    
    
    private bool touching = false;//For physical contact
	public bool close = false;
	private int contacts = 0;
    void Start()
    {
        mr = GetComponent<MeshRenderer>();
        originalMaterialSelf = mr.material;
    }

    // Update is called once per frame
    void Update()
    {
		if (touching)
		{
			mr.material = red;
		}
		else if (close) {
			mr.material = yellow;
		}
		else
		{
			mr.material = originalMaterialSelf;
		}
		
		
		
    }
	private void OnTriggerStay(Collider other) //Sphere trigger USED FOR PROXIMITY
	{
		
        if (other.CompareTag("robotPart")|| other.CompareTag("Environment")||other.CompareTag("SnapSurface")) 
        {
            return;
        }
        Debug.Log("TOUCHING " + other);
		touching = true;
		
		
        other.GetComponent<ObjectSideCollision>().isTouching(true);
        
   
	}

	private void OnTriggerExit(Collider other)
	{
		
		if (other.CompareTag("robotPart"))
		{
			return;
		}
		
		touching = false;
		other.GetComponent<ObjectSideCollision>().isTouching(false);
	}

}
