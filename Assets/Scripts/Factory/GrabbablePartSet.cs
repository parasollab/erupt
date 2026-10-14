using System.Collections.Generic;
using UnityEngine;

namespace Factory
{
    // Spawns a fixed set of grabbable collision-object parts at Start (e.g. raw blanks on a
    // tray), one per slot, through the shared GrabbableCollisionObjectFactory so they behave
    // exactly like wrist-menu shapes and moulded parts.
    public class GrabbablePartSet : MonoBehaviour
    {
        public Transform[] slots;
        public Vector3 partSize = new Vector3(0.08f, 0.03f, 0.05f);
        public Material partMaterial;
        public string idPrefix = "blank_";
        public bool freezeRotation = true;
        public GameObject worldOrigin;
        public CollisionObjectsListenerSimple listener;
        public Transform partsRoot;
        [Tooltip("Parts already baked into the scene (one per slot, in slot order; visible in the editor). " +
                 "They are registered instead of created; slots beyond this list still get a new part.")]
        public GameObject[] prePlaced;

        public IReadOnlyList<GameObject> Parts => parts;
        readonly List<GameObject> parts = new List<GameObject>();

        void Start()
        {
            if (listener != null) listener.ClaimIdPrefix(idPrefix); // stale blanks of earlier sessions are removed
            if (slots == null) return;
            Quaternion upright = worldOrigin != null ? worldOrigin.transform.rotation : Quaternion.identity;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                string id = idPrefix + (i + 1);
                if (prePlaced != null && i < prePlaced.Length && prePlaced[i] != null)
                {
                    GameObject baked = prePlaced[i];
                    var pub = baked.GetComponent<CollisionObjectPublisher>();
                    if (pub != null) id = pub.objectId;
                    if (listener != null) listener.RegisterUnityOwnedObject(id, baked);
                    parts.Add(baked);
                    continue;
                }
                GameObject part = GrabbableCollisionObjectFactory.CreatePrimitive(
                    PrimitiveType.Cube, id, partSize, id, worldOrigin, partMaterial, freezeRotation);
                if (partsRoot != null) part.transform.SetParent(partsRoot, true);
                part.transform.SetPositionAndRotation(slots[i].position, upright);
                if (listener != null) listener.RegisterUnityOwnedObject(id, part);
                parts.Add(part);
            }
        }
    }
}
