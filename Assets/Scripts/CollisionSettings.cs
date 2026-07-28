using TMPro.Examples;
using UnityEngine;

public class CollisionSettings : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created

    public bool useProximity { get; private set; } = true;
	public bool useContact { get; private set; } = true;
	public float proximityRadius { get; private set; } = 75;

	public static CollisionSettings Instance { get; private set; }

	void Start()
    {
        
    }
	private void Awake()
	{
		if (Instance != null && Instance != this)
		{
			Destroy(gameObject);
			return;
		}

		Instance = this;
	}
	// Update is called once per frame
	void Update()
    {
        
    }
	public void updateUseProximity(bool b)
	{
		useProximity = b;
	}
	public void updateUseContact(bool b)
	{
		useContact = b;
	}
	public void updateRadius(float rad)
    {
        proximityRadius = rad;
    }
}
