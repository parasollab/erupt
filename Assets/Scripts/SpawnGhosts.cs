using UnityEngine;

public class SpawnGhosts : MonoBehaviour
{

    public GameObject robotPrefab;

    // Default start color is transluscent green
    private Color startGhostColor = new Color(0, 1, 0, 0.5f); // RGBA

    // Default goal color is transluscent orange
    private Color goalGhostColor = new Color(1, 0.5f, 0, 0.5f); // RGBA

    public GameObject realRobot;

    private GameObject startGhost;
    private GameObject goalGhost;

    public void SpawnStartGhost(float[] jointAngles)
    {
        startGhost = Instantiate(robotPrefab);
        startGhost.name = "StartGhost";

        // Make the ghost transluscent
        startGhost.AddComponent<TranslucentOverride>().overlayColor = startGhostColor;

        // Move the ghost to the real robot's position and rotation
        startGhost.transform.position = realRobot.transform.position;
        startGhost.transform.rotation = realRobot.transform.rotation;

        // Get the RobotManager component attached to the ghost
        RobotManager robotManager = startGhost.GetComponent<RobotManager>();
        if (robotManager != null)
        {
            robotManager.SetJointAngles(jointAngles);
        }
    }

    public void SpawnGoalGhost(float[] jointAngles)
    {
        goalGhost = Instantiate(robotPrefab);
        goalGhost.name = "GoalGhost";

        // Make the ghost transluscent
        goalGhost.AddComponent<TranslucentOverride>().overlayColor = goalGhostColor;

        // Move the ghost to the real robot's position and rotation
        goalGhost.transform.position = realRobot.transform.position;
        goalGhost.transform.rotation = realRobot.transform.rotation;

        // Get the RobotManager component attached to the ghost
        RobotManager robotManager = goalGhost.GetComponent<RobotManager>();
        if (robotManager != null)
        {
            robotManager.SetJointAngles(jointAngles);
        }
    }

    public void ClearStartGhost()
    {
        if (startGhost != null)
        {
            Destroy(startGhost);
        }
    }

    public void ClearGoalGhost()
    {
        if (goalGhost != null)
        {
            Destroy(goalGhost);
        }
    }

    public void ClearGhosts()
    {
        ClearStartGhost();
        ClearGoalGhost();
    }
}
