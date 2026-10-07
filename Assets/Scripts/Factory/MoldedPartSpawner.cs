using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Factory
{
    // Turns each injection-moulder ejection into a real part on the station tray: a small
    // grabbable box with a CollisionObjectPublisher, so the visitor can demonstrate a pick and
    // place with it and MoveIt knows where it is. Parts slide down the chute, hop onto the first
    // free tray slot and only then start publishing. The oldest idle part is recycled once the
    // cap is reached.
    public class MoldedPartSpawner : MonoBehaviour
    {
        [Header("Source")]
        public InjectionMoldingCycle molder;

        [Header("Path")]
        [Tooltip("Where parts appear (the chute mouth at the moulder).")]
        public Transform chuteEntry;
        [Tooltip("Bottom of the chute; parts hop from here onto a tray slot.")]
        public Transform chuteExit;
        [Tooltip("Resting places on the tray, filled in order; a slot is free when no part is within slotRadius of it.")]
        public Transform[] traySlots;
        public float slotRadius = 0.04f;
        public float slideSeconds = 0.9f;
        public float hopSeconds = 0.35f;
        public float hopHeight = 0.06f;

        [Header("Part")]
        public Vector3 partSize = new Vector3(0.06f, 0.04f, 0.04f);
        public Material partMaterial;
        [Tooltip("Collision-object id prefix; ids are <prefix><n> with n increasing for the session.")]
        public string idPrefix = "molded_part_";
        [Tooltip("Keep the part's rotation while it is grabbed so a top-down grasp stays feasible.")]
        public bool freezeRotation = true;
        [Tooltip("Parts alive at once; the oldest idle part is removed to make room.")]
        public int maxTotal = 12;

        [Header("Scene")]
        [Tooltip("Planning frame origin (BaseTransform under the robot); parts spawn upright in this frame.")]
        public GameObject worldOrigin;
        public CollisionObjectsListenerSimple listener;
        [Tooltip("Optional parent for spawned parts.")]
        public Transform partsRoot;

        public event Action<GameObject, string> PartSpawned;
        public IReadOnlyList<GameObject> Parts => parts;

        readonly List<GameObject> parts = new List<GameObject>();
        int counter;

        void OnEnable()
        {
            if (molder != null) molder.Ejected += SpawnPart;
        }

        void OnDisable()
        {
            if (molder != null) molder.Ejected -= SpawnPart;
        }

        public void SpawnPart()
        {
            parts.RemoveAll(p => p == null);
            Transform slot = FreeSlot();
            if (slot == null)
            {
                Debug.Log("[MoldedPartSpawner] tray full; part skipped");
                return;
            }
            while (parts.Count >= Mathf.Max(1, maxTotal) && RecycleOldestIdle()) { }
            if (parts.Count >= Mathf.Max(1, maxTotal))
            {
                Debug.Log("[MoldedPartSpawner] every part is in use; part skipped");
                return;
            }

            string id = idPrefix + (++counter);
            Quaternion upright = worldOrigin != null ? worldOrigin.transform.rotation : Quaternion.identity;
            GameObject part = GrabbableCollisionObjectFactory.CreatePrimitive(
                PrimitiveType.Cube, id, partSize, id, worldOrigin, partMaterial, freezeRotation);
            if (partsRoot != null) part.transform.SetParent(partsRoot, true);
            Vector3 start = chuteEntry != null ? chuteEntry.position : slot.position + Vector3.up * 0.3f;
            part.transform.SetPositionAndRotation(start, upright);

            var publisher = part.GetComponent<CollisionObjectPublisher>();
            publisher.pausePublishing = true;
            var grab = part.GetComponent<XRGrabInteractable>();
            grab.enabled = false;
            parts.Add(part);
            StartCoroutine(Deliver(part, slot, publisher, grab, id));
        }

        IEnumerator Deliver(GameObject part, Transform slot, CollisionObjectPublisher publisher, XRGrabInteractable grab, string id)
        {
            Vector3 start = part.transform.position;
            Vector3 exit = chuteExit != null ? chuteExit.position : start;
            float t = 0f;
            while (t < 1f && part != null)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, slideSeconds);
                float u = Mathf.Clamp01(t);
                part.transform.position = Vector3.Lerp(start, exit, u * u);
                yield return null;
            }
            if (part == null) yield break;

            Vector3 land = slot.position;
            t = 0f;
            while (t < 1f && part != null)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, hopSeconds);
                float u = Mathf.Clamp01(t);
                Vector3 p = Vector3.Lerp(exit, land, u);
                p.y += hopHeight * 4f * u * (1f - u);
                part.transform.position = p;
                yield return null;
            }
            if (part == null) yield break;

            part.transform.position = land;
            grab.enabled = true;
            if (listener != null) listener.RegisterUnityOwnedObject(id, part);
            publisher.pausePublishing = false; // the next Update publishes ADD at the landed pose
            PartSpawned?.Invoke(part, id);
        }

        Transform FreeSlot()
        {
            if (traySlots == null) return null;
            foreach (Transform slot in traySlots)
            {
                if (slot == null) continue;
                bool occupied = false;
                foreach (GameObject p in parts)
                {
                    if (p != null && Vector3.Distance(p.transform.position, slot.position) < slotRadius) { occupied = true; break; }
                }
                if (!occupied) return slot;
            }
            return null;
        }

        // Removes the oldest part that nobody is using; false when none qualifies.
        bool RecycleOldestIdle()
        {
            GameObject selected = SelectionManager.Instance != null ? SelectionManager.Instance.SelectedObject : null;
            for (int i = 0; i < parts.Count; i++)
            {
                GameObject p = parts[i];
                if (p == null) { parts.RemoveAt(i); return true; }
                if (p == selected) continue;
                var grab = p.GetComponent<XRGrabInteractable>();
                if (grab != null && grab.isSelected) continue;
                var publisher = p.GetComponent<CollisionObjectPublisher>();
                if (publisher != null && publisher.attachedToRobot) continue;
                if (listener != null && publisher != null) listener.UnregisterUnityOwnedObject(publisher.objectId);
                parts.RemoveAt(i);
                Destroy(p); // the publisher's OnDestroy sends REMOVE
                return true;
            }
            return false;
        }
    }
}
