using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.Geometry;

namespace Factory
{
    // Optional "auto-sort" for the FR3 station: once the user has demonstrated one pick and
    // place (PickPlaceTaskRecorder), every part the moulder delivers afterwards is planned and
    // executed to the same place pose, with a small spread so parts do not stack. Off by
    // default; toggle autoSort in the Inspector.
    public class PickPlaceStationController : MonoBehaviour
    {
        public PickPlaceTaskRecorder recorder;
        public PickPlaceClient client;
        public MoldedPartSpawner spawner;
        [Tooltip("Planning frame origin; must match the recorder's worldOrigin.")]
        public GameObject worldOrigin;

        [Tooltip("Repeat the demonstrated task for each newly delivered part.")]
        public bool autoSort = false;
        [Tooltip("World-space offset between successive placements.")]
        public Vector3 perPartSpread = new Vector3(0.05f, 0f, 0f);
        public int spreadSlots = 3;

        public bool HasDemonstration { get; private set; }
        public Vector3 LastPlacePosition { get; private set; }
        public Quaternion LastPlaceRotation { get; private set; }

        readonly Queue<string> pending = new Queue<string>();
        int placed;
        Coroutine drain;

        void OnEnable()
        {
            if (recorder != null) recorder.OnTaskCaptured += OnTaskCaptured;
            if (spawner != null) spawner.PartSpawned += OnPartSpawned;
        }

        void OnDisable()
        {
            if (recorder != null) recorder.OnTaskCaptured -= OnTaskCaptured;
            if (spawner != null) spawner.PartSpawned -= OnPartSpawned;
        }

        public void SetAutoSort(bool enabled)
        {
            autoSort = enabled;
            if (!enabled) pending.Clear();
        }

        void OnTaskCaptured(string objectId, Vector3 worldPos, Quaternion worldRot)
        {
            HasDemonstration = true;
            LastPlacePosition = worldPos;
            LastPlaceRotation = worldRot;
            placed = 0;
        }

        void OnPartSpawned(GameObject part, string objectId)
        {
            if (!autoSort || !HasDemonstration || client == null) return;
            pending.Enqueue(objectId);
            if (drain == null) drain = StartCoroutine(Drain());
        }

        IEnumerator Drain()
        {
            while (pending.Count > 0)
            {
                if (!autoSort) { pending.Clear(); break; }
                while (client.Busy) yield return null;
                string id = pending.Dequeue();
                Vector3 worldPos = LastPlacePosition + perPartSpread * ((placed % Mathf.Max(1, spreadSlots)) - (spreadSlots - 1) * 0.5f);
                placed++;
                Transform origin = worldOrigin != null ? worldOrigin.transform : null;
                Vector3 relPos = origin != null ? origin.InverseTransformPoint(worldPos) : worldPos;
                Quaternion relRot = origin != null ? Quaternion.Inverse(origin.rotation) * LastPlaceRotation : LastPlaceRotation;
                PointMsg rosPos = RosUnityConversion.UnityToRosPosition(relPos);
                QuaternionMsg rosRot = RosUnityConversion.UnityToRosQuaternion(relRot);
                var task = client.PlanAndExecuteAsync(id, client.PlacePose(rosPos, rosRot));
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted)
                    Debug.LogWarning($"[PickPlaceStationController] auto-sort of '{id}' failed: {task.Exception?.GetBaseException().Message}");
            }
            drain = null;
        }
    }
}
