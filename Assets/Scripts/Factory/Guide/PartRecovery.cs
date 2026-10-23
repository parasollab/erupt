using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Factory
{
    /// <summary>
    /// Puts a billet back on its stock slot when it has gone where nobody can get it: under the
    /// floor, or outside the working area (thrown, carried off, dropped behind the machine). A
    /// part that is in a hand, on the robot or still falling is left alone. Also the one place
    /// that knows every billet's home, so a demo restart can re-stack them.
    /// </summary>
    public class PartRecovery : MonoBehaviour
    {
        private const string LogTag = "[PartRecovery]";

        [SerializeField] private GrabbablePartSet billets;
        [SerializeField] private ReleasedPartGravity gravity;
        [SerializeField, Tooltip("Editor stand-in for the hands; a dragged part is not recovered.")] private DesktopMouseInteractor mouse;
        [SerializeField, Tooltip("World box a billet may be in; outside it the billet goes home.")]
        private Bounds workingArea = new Bounds(new Vector3(-1f, 1.2f, 0.6f), new Vector3(8f, 4f, 7f));
        [SerializeField, Tooltip("Below this height (metres) the billet has fallen through or off the floor.")]
        private float floorY = -0.05f;
        [SerializeField] private float checkSeconds = 0.5f;

        public event Action<GameObject> Recovered;
        public int RecoveredCount { get; private set; }

        private float _nextCheck;
        private readonly List<(GameObject part, Vector3 position, Quaternion rotation)> _homes = new List<(GameObject, Vector3, Quaternion)>();

        private void Awake()
        {
            if (mouse == null) mouse = FindFirstObjectByType<DesktopMouseInteractor>(FindObjectsInactive.Include);
        }

        private void Update()
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + Mathf.Max(0.1f, checkSeconds);
            if (_homes.Count == 0) CaptureHomes();
            foreach ((GameObject part, Vector3 home, Quaternion rot) in _homes)
            {
                if (part == null || !IsLost(part) || IsBusy(part)) continue;
                ReturnHome(part, home, rot);
                RecoveredCount++;
                Debug.Log($"{LogTag} '{part.name}' was out of reach and went back to the stand");
                Recovered?.Invoke(part);
            }
        }

        // The billets are registered in GrabbablePartSet.Start; their slot is where they stand then.
        private void CaptureHomes()
        {
            if (billets == null || billets.Parts == null) return;
            for (int i = 0; i < billets.Parts.Count; i++)
            {
                GameObject part = billets.Parts[i];
                if (part == null) continue;
                Transform slot = billets.slots != null && i < billets.slots.Length ? billets.slots[i] : null;
                Vector3 pos = slot != null ? slot.position : part.transform.position;
                _homes.Add((part, pos, part.transform.rotation));
            }
        }

        public bool IsLost(GameObject part)
        {
            Vector3 p = part.transform.position;
            return p.y < floorY || !workingArea.Contains(p);
        }

        // In a hand, on the robot, falling, or being watched by a recording that will put it back itself.
        private bool IsBusy(GameObject part)
        {
            var grab = part.GetComponent<XRGrabInteractable>();
            if (grab != null && grab.isSelected) return true;
            var pub = part.GetComponent<CollisionObjectPublisher>();
            if (pub != null && pub.attachedToRobot) return true;
            if (gravity != null && gravity.IsSettling(part)) return true;
            if (mouse != null && mouse.isActiveAndEnabled && mouse.Dragged != null && mouse.Dragged.IsChildOf(part.transform)) return true;
            return false;
        }

        private void ReturnHome(GameObject part, Vector3 position, Quaternion rotation)
        {
            if (gravity != null) gravity.Abort(part);
            var rb = part.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            part.transform.SetPositionAndRotation(position, rotation);
            var pub = part.GetComponent<CollisionObjectPublisher>();
            if (pub != null) pub.ForceRepublish();
        }

        /// <summary>Every billet back on its slot (demo restart). Parts in a hand or on the robot are skipped.</summary>
        public int ReturnAll()
        {
            if (_homes.Count == 0) CaptureHomes();
            int returned = 0;
            foreach ((GameObject part, Vector3 home, Quaternion rot) in _homes)
            {
                if (part == null || IsBusy(part)) continue;
                if (Vector3.Distance(part.transform.position, home) < 1e-3f && Quaternion.Angle(part.transform.rotation, rot) < 0.1f) continue;
                ReturnHome(part, home, rot);
                returned++;
            }
            return returned;
        }
    }
}
