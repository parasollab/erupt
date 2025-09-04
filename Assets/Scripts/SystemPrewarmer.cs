using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Moveit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using System.Collections;

/// <summary>
/// Pre-warms systems that cause lag when first objects are created.
/// This component should be placed in the scene and will initialize
/// expensive operations during scene load instead of during first interaction.
/// </summary>
public class SystemPrewarmer : MonoBehaviour
{
    [Header("Pre-warming Settings")]
    [Tooltip("Delay before starting pre-warming to avoid interfering with scene startup")]
    public float prewarmDelay = 2f;
    
    private void Start()
    {
        StartCoroutine(PrewarmSystemsDelayed());
    }

    private IEnumerator PrewarmSystemsDelayed()
    {
        // Wait for scene to fully load
        yield return new WaitForSeconds(prewarmDelay);

        // Debug.Log("SystemPrewarmer: Starting system pre-warming...");

        // Pre-warm ROS connection and publisher registration
        PrewarmROSConnection();

        // Pre-warm primitive creation (this is usually the biggest cause of lag)
        yield return StartCoroutine(PrewarmPrimitiveCreation());

        // Pre-warm XR interaction system
        PrewarmXRInteractionSystem();

        // Debug.Log("SystemPrewarmer: System pre-warming completed!");

        // Spawn the MainScene
        UnityEngine.SceneManagement.SceneManager.LoadScene("MainScene");
    }
    
    private void PrewarmROSConnection()
    {
        try
        {
            // Initialize ROS connection
            ROSConnection ros = ROSConnection.GetOrCreateInstance();
            
            // Pre-register the publisher that will be used by collision objects
            ros.RegisterPublisher<CollisionObjectMsg>("/collision_object");
            
            // Debug.Log("SystemPrewarmer: ROS connection pre-warmed");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"SystemPrewarmer: ROS pre-warming failed: {e.Message}");
        }
    }
    
    private IEnumerator PrewarmPrimitiveCreation()
    {
        // Create a temporary primitive to force Unity to initialize its primitive generation systems
        GameObject tempPrimitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        
        // Add components that will be used on actual objects to pre-warm their initialization
        Rigidbody tempRb = tempPrimitive.AddComponent<Rigidbody>();
        XRGrabInteractable tempGrab = tempPrimitive.AddComponent<XRGrabInteractable>();
        
        // Wait one frame to ensure components are initialized
        yield return null;
        
        // Clean up the temporary object
        if (tempPrimitive != null)
        {
            DestroyImmediate(tempPrimitive);
        }
        
        // Debug.Log("SystemPrewarmer: Primitive creation systems pre-warmed");
    }
    
    private void PrewarmXRInteractionSystem()
    {
        // Pre-warm XR interaction system by accessing key components
        // This helps initialize internal systems that might cause delays
        
        // Find any existing XR components to ensure the system is initialized
        var existingInteractors = FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.Interactors.XRBaseInteractor>();
        var existingInteractables = FindObjectsOfType<XRBaseInteractable>();
        
        Debug.Log($"SystemPrewarmer: XR system pre-warmed (found {existingInteractors.Length} interactors, {existingInteractables.Length} interactables)");
    }
}