using System.Collections;
using UnityEngine;

namespace Factory
{
    /// <summary>
    /// Snaps the headset to the spawn point again whenever it is put on (the demo is launched by
    /// the administrator and handed over, so the first snap at start aligned their grip, not the
    /// user's head) or recentred, a moment after the event so tracking has settled. Headset only.
    /// </summary>
    public class UserSpawnRefresh : MonoBehaviour
    {
        private const string LogTag = "[UserSpawn]";

        [SerializeField] private SpawnHuman spawn;
        [SerializeField, Tooltip("Seconds between the headset being put on / recentred and the snap.")]
        private float settleSeconds = 1.0f;

        private Coroutine _pending;
        private bool _recenterHooked;

        public int SnapCount { get; private set; }

        private void Awake()
        {
            if (spawn == null) spawn = GetComponent<SpawnHuman>();
        }

        private void OnEnable()
        {
            OVRManager.HMDMounted += OnMounted;
            StartCoroutine(HookRecenter());
        }

        private void OnDisable()
        {
            OVRManager.HMDMounted -= OnMounted;
            if (_recenterHooked && OVRManager.display != null) OVRManager.display.RecenteredPose -= OnRecentered;
            _recenterHooked = false;
        }

        // OVRManager.display exists only once the Meta runtime is up.
        private IEnumerator HookRecenter()
        {
            float deadline = Time.unscaledTime + 30f;
            while (OVRManager.display == null && Time.unscaledTime < deadline) yield return null;
            if (OVRManager.display == null) yield break;
            OVRManager.display.RecenteredPose += OnRecentered;
            _recenterHooked = true;
        }

        private void OnMounted() => Schedule("headset put on");
        private void OnRecentered() => Schedule("view recentred");

        /// <summary>Snap after the settle delay (also for probes).</summary>
        public void Schedule(string reason)
        {
            if (_pending != null) StopCoroutine(_pending);
            _pending = StartCoroutine(SnapAfterDelay(reason));
        }

        private IEnumerator SnapAfterDelay(string reason)
        {
            yield return new WaitForSecondsRealtime(settleSeconds);
            _pending = null;
            if (spawn == null) yield break;
            spawn.MoveToSpawnPoint();
            SnapCount++;
            Camera cam = Camera.main;
            if (cam != null)
                Debug.Log($"{LogTag} snapped to the spawn point ({reason}): head at {cam.transform.position:F2}, facing {Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized:F2}");
        }
    }
}
