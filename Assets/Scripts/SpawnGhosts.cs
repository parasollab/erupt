using UnityEngine;

public class SpawnGhosts : MonoBehaviour
{

    public GameObject robotPrefab;

    // Default start color is transluscent green
    public Color startGhostColor = new Color(0, 1, 0, 0.5f); // RGBA

    // Default goal color is transluscent orange
    public Color goalGhostColor = new Color(1, 0.5f, 0, 0.5f); // RGBA

    public GameObject realRobot;

    private GameObject startGhost;
    private GameObject goalGhost;

    public void SpawnStartGhost(float[] jointAngles)
    {
        startGhost = Instantiate(robotPrefab);
        startGhost.name = "StartGhost";

        foreach (var col in startGhost.GetComponentsInChildren<Collider>())
            col.gameObject.tag = "robot";

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

    public void SpawnStartGhost()
    {
        // Get the joint angles from the real robot
        RobotManager robotManager = realRobot.GetComponent<RobotManager>();
        if (robotManager != null)
        {
            float[] jointAngles = robotManager.GetJointAngles();
            SpawnStartGhost(jointAngles);
        }
    }

    public void SpawnGoalGhost(float[] jointAngles)
    {
        goalGhost = Instantiate(robotPrefab);
        goalGhost.name = "GoalGhost";

        foreach (var col in goalGhost.GetComponentsInChildren<Collider>())
            col.gameObject.tag = "robot";

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

    public void SpawnGoalGhost()
    {
        // Get the joint angles from the real robot
        RobotManager robotManager = realRobot.GetComponent<RobotManager>();
        if (robotManager != null)
        {
            float[] jointAngles = robotManager.GetJointAngles();
            SpawnGoalGhost(jointAngles);
        }
    }

    public void UpdateStartGhost(float[] jointAngles)
    {
        if (startGhost != null)
        {
            RobotManager robotManager = startGhost.GetComponent<RobotManager>();
            if (robotManager != null)
            {
                robotManager.SetJointAngles(jointAngles);
                Debug.Log("Start ghost updated to: " + string.Join(", ", jointAngles));
            }
        }
    }

    public void UpdateStartGhost()
    {
        // Get the joint angles from the real robot
        RobotManager robotManager = realRobot.GetComponent<RobotManager>();
        if (robotManager != null)
        {
            float[] jointAngles = robotManager.GetJointAngles();
            UpdateStartGhost(jointAngles);
        }
    }

    public void UpdateGoalGhost(float[] jointAngles)
    {
        if (goalGhost != null)
        {
            RobotManager robotManager = goalGhost.GetComponent<RobotManager>();
            if (robotManager != null)
            {
                robotManager.SetJointAngles(jointAngles);
            }
        }
    }

    public void UpdateGoalGhost()
    {
        // Get the joint angles from the real robot
        RobotManager robotManager = realRobot.GetComponent<RobotManager>();
        if (robotManager != null)
        {
            float[] jointAngles = robotManager.GetJointAngles();
            UpdateGoalGhost(jointAngles);
            Debug.Log("Goal ghost updated to: " + string.Join(", ", jointAngles));
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
