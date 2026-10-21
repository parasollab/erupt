using UnityEngine;

/// <summary>
/// Moves a URDF-imported robot root. The links are an ArticulationBody chain and PhysX ignores
/// Transform writes on an articulation root: the pose shows for one frame and the next physics
/// step restores the old one. TeleportRoot moves the articulation itself; the Transform is set
/// first so the root body's world pose (and any non-articulated children such as BaseTransform)
/// reflect the target. Shared by the AprilTag placement (AR) and the factory placement phase.
/// </summary>
public static class ArticulationRootMover
{
    public static void MoveRoot(GameObject robot, Vector3 position, Quaternion rotation)
    {
        robot.transform.SetPositionAndRotation(position, rotation);

        foreach (var body in robot.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (!body.isRoot) continue;
            body.TeleportRoot(body.transform.position, body.transform.rotation);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }
}
