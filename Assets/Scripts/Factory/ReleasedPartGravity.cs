using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Factory
{
    // Parts the robot lets go of fall. The place pose MTC executes is wherever the user
    // released the part, usually hovering; once the gripper opens (the executing solution's
    // detach, or the preview's) the part becomes a dynamic rigidbody for a moment, drops onto
    // whatever is under it - the vise parallels, a jaw plate, the platter, the floor - and is
    // frozen again where it comes to rest. The rest pose is what the publisher then sends to
    // MoveIt, so the clamp and the later unload pick work from where the part really is.
    // A real cell never drops a part on the vise; the panel shows the drop height so the
    // user learns to release low without anything snapping the pose for them.
    public class ReleasedPartGravity : MonoBehaviour
    {
        [Serializable]
        public struct DropReport
        {
            public GameObject part;
            public string partId;
            public float dropHeight;    // metres the part's centre fell
            public float tiltDegrees;   // how far from upright it came to rest
            public bool timedOut;       // still moving when settleTimeout ran out
            public bool preview;        // happened during a solution preview (undone when it ends)
        }

        [Tooltip("Only parts under this root fall (the station's Parts group). Empty = any grabbable collision object.")]
        public Transform partsRoot;
        [Tooltip("After coming to rest the part is lifted this much (metres) so MoveIt sees no surface contact.")]
        public float restGap = 0.005f;
        [Tooltip("Give up and freeze the part after this many seconds.")]
        public float settleTimeout = 4f;
        [Tooltip("Speed (m/s) below which the part counts as at rest...")]
        public float restSpeed = 0.01f;
        [Tooltip("...once it has stayed that slow for this long (seconds).")]
        public float restSeconds = 0.3f;
        [Tooltip("Drops taller than this (metres) are flagged on the panel.")]
        public float dropWarning = 0.02f;
        [Tooltip("Found in the scene when empty.")]
        public AttachedCollisionObjectListener attachedListener;
        [Tooltip("Found in the scene when empty.")]
        public MTCTrajectoryPlayer trajectoryPlayer;

        public DropReport LastDrop { get; private set; }
        public bool HasDrop { get; private set; }
        public event Action<GameObject> DropStarted;
        public event Action<DropReport> Settled;

        // What was changed on a falling part, undone when it comes to rest or the drop is aborted.
        class SettleState
        {
            public Coroutine routine;
            public bool preview;
            public CollisionObjectPublisher[] publishers;
            public bool[] wasPaused;
            public SelectableGrabController grabLock;
            public RigidbodyInterpolation interpolation; // restored after the fall: an interpolating
            // kinematic body reports a blended transform for a frame after every teleport, and the
            // publisher sent such a blend to MoveIt right after the recorder put a part back.
        }

        readonly Dictionary<GameObject, SettleState> settling = new Dictionary<GameObject, SettleState>();

        public bool IsSettling(GameObject part) => part != null && settling.ContainsKey(part);
        public int SettlingCount => settling.Count;

        void Start()
        {
            if (attachedListener == null) attachedListener = FindFirstObjectByType<AttachedCollisionObjectListener>();
            if (trajectoryPlayer == null) trajectoryPlayer = FindFirstObjectByType<MTCTrajectoryPlayer>();
            if (attachedListener != null) attachedListener.Released += OnReleased;
            if (trajectoryPlayer != null)
            {
                trajectoryPlayer.PreviewReleased += OnPreviewReleased;
                trajectoryPlayer.PreviewEnding += OnPreviewEnding;
            }
        }

        void OnDestroy()
        {
            if (attachedListener != null) attachedListener.Released -= OnReleased;
            if (trajectoryPlayer != null)
            {
                trajectoryPlayer.PreviewReleased -= OnPreviewReleased;
                trajectoryPlayer.PreviewEnding -= OnPreviewEnding;
            }
            foreach (GameObject part in new List<GameObject>(settling.Keys))
                Abort(part);
        }

        void OnReleased(GameObject part) => Drop(part, preview: false);
        void OnPreviewReleased(GameObject part) => Drop(part, preview: true);

        // The preview is about to put its objects back where they started: freeze any that
        // are still falling first, or the restored pose is overwritten by physics.
        void OnPreviewEnding()
        {
            foreach (var kv in new List<KeyValuePair<GameObject, SettleState>>(settling))
                if (kv.Value.preview) Abort(kv.Key);
        }

        public bool IsEligible(GameObject part)
        {
            if (part == null) return false;
            if (partsRoot != null && !part.transform.IsChildOf(partsRoot)) return false;
            return part.GetComponent<Rigidbody>() != null && part.GetComponent<CollisionObjectPublisher>() != null;
        }

        /// <summary>Let <paramref name="part"/> fall from where it is and freeze it where it lands.</summary>
        public void Drop(GameObject part, bool preview = false)
        {
            if (!IsEligible(part)) return;
            Abort(part);

            var state = new SettleState
            {
                preview = preview,
                publishers = part.GetComponentsInChildren<CollisionObjectPublisher>(true),
                grabLock = part.GetComponent<SelectableGrabController>(),
                interpolation = part.GetComponent<Rigidbody>().interpolation,
            };
            state.wasPaused = new bool[state.publishers.Length];
            for (int i = 0; i < state.publishers.Length; i++)
            {
                state.wasPaused[i] = state.publishers[i].pausePublishing;
                state.publishers[i].pausePublishing = true; // no mid-air MOVEs
            }
            if (state.grabLock != null) state.grabLock.SetLocked(true);
            settling[part] = state;
            state.routine = StartCoroutine(Settle(part, state));
            DropStarted?.Invoke(part);
        }

        /// <summary>Freeze <paramref name="part"/> where it is now and undo the drop's bookkeeping.</summary>
        public void Abort(GameObject part)
        {
            if (part == null || !settling.TryGetValue(part, out SettleState state)) return;
            if (state.routine != null) StopCoroutine(state.routine);
            Finish(part, state);
        }

        void Finish(GameObject part, SettleState state)
        {
            settling.Remove(part);
            Freeze(part);
            var body = part != null ? part.GetComponent<Rigidbody>() : null;
            if (body != null) body.interpolation = state.interpolation;
            for (int i = 0; i < state.publishers.Length; i++)
                if (state.publishers[i] != null) state.publishers[i].pausePublishing = state.wasPaused[i];
            if (state.grabLock != null) state.grabLock.SetLocked(false);
        }

        IEnumerator Settle(GameObject part, SettleState state)
        {
            var rb = part.GetComponent<Rigidbody>();
            float startHeight = part.transform.position.y;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            float started = Time.time, slowSince = -1f;
            bool timedOut = false;
            while (true)
            {
                yield return new WaitForFixedUpdate();
                if (part == null) { settling.Remove(part); yield break; }
                if (rb.IsSleeping()) break;
                bool slow = rb.linearVelocity.magnitude < restSpeed && rb.angularVelocity.magnitude < 0.1f;
                if (slow && slowSince < 0f) slowSince = Time.time;
                if (!slow) slowSince = -1f;
                if (slowSince >= 0f && Time.time - slowSince >= restSeconds && Time.time - started > 0.1f) break;
                if (Time.time - started > settleTimeout) { timedOut = true; break; }
            }

            Finish(part, state);
            if (restGap > 0f) part.transform.position += Vector3.up * restGap;

            var pub = part.GetComponent<CollisionObjectPublisher>();
            var report = new DropReport
            {
                part = part,
                partId = pub != null ? pub.objectId : part.name,
                dropHeight = Mathf.Max(0f, startHeight - part.transform.position.y),
                tiltDegrees = Vector3.Angle(part.transform.up, Vector3.up),
                timedOut = timedOut,
                preview = state.preview,
            };
            LastDrop = report;
            HasDrop = true;
            Debug.Log($"[ReleasedPartGravity] '{report.partId}' dropped {report.dropHeight * 1000f:F0} mm, tilt {report.tiltDegrees:F0} deg{(timedOut ? " (timed out)" : "")}{(state.preview ? " (preview)" : "")}");
            Settled?.Invoke(report);
        }

        static void Freeze(GameObject part)
        {
            var rb = part != null ? part.GetComponent<Rigidbody>() : null;
            if (rb == null) return;
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            // Kinematic bodies only accept discrete / speculative detection.
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }
}
