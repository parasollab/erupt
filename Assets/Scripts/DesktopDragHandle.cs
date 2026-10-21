using UnityEngine;

/// <summary>
/// Marks an object DesktopMouseInteractor may drag on the floor plane in the editor even though
/// it has no CollisionObjectPublisher (e.g. the robot placement handle). The XR grab stack is
/// what moves it on the headset; this only gives the mouse stand-in a way to reach it.
/// </summary>
[DisallowMultipleComponent]
public class DesktopDragHandle : MonoBehaviour
{
}
