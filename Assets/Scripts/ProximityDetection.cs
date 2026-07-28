using UnityEngine;

public class ProximityDetection : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private colorCollisionDetection main;
	[SerializeField] private SphereCollider sphereCollider;
    void Start()
    {
        main = GetComponentInParent<colorCollisionDetection>();
		sphereCollider = GetComponent<SphereCollider>();
    }

    // Update is called once per frame
    void Update()
    {
		if (sphereCollider != null)
		{
			sphereCollider.radius = CollisionSettings.Instance.proximityRadius;
			sphereCollider.enabled = CollisionSettings.Instance.useProximity;
		}
	}

	private void OnTriggerStay(Collider other) //Sphere trigger USED FOR PROXIMITY
	{

		if (other.CompareTag("robotPart") || other.CompareTag("Environment") || other.CompareTag("SnapSurface"))
		{
			return;
		}
		Debug.Log("IN PROXIMITY TO " + other);
		main.close = true;


		other.GetComponent<ObjectSideCollision>().inProximity(true);


	}

	private void OnTriggerExit(Collider other)
	{

		if (other.CompareTag("robotPart"))
		{
			return;
		}

		main.close = false;
		other.GetComponent<ObjectSideCollision>().inProximity(false);
	}
}
