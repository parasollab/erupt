using System.Collections;
using UnityEngine;

namespace Factory
{
    // Production cycle for an injection moulding machine: the clamp closes (moving platen plus toggle
    // links), holds for injection and cooling, opens, and a moulded part drops out of the mould.
    public class InjectionMoldingCycle : MachineCycle
    {
        [Header("Parts")]
        public SlidingPart movingPlaten;
        public SlidingPart toggleLinks;
        [Tooltip("Optional part that appears at the mould face when the clamp opens and falls away.")]
        public Transform moldedPart;

        public float injectionTime = 2.5f;
        public float coolingTime = 3f;
        [Tooltip("How far the ejected part drops before it disappears, in metres.")]
        public float dropDistance = 0.9f;
        public float dropTime = 0.7f;

        Vector3 partRest;
        bool captured;

        void Awake()
        {
            if (moldedPart != null)
            {
                partRest = moldedPart.localPosition;
                moldedPart.gameObject.SetActive(false);
                captured = true;
            }
        }

        protected override IEnumerator RunCycle()
        {
            // The authored pose is the clamped (closed) mould; "open" drives the platen toward the
            // clamp end by its openOffset. Inject and cool closed, then open and eject.
            yield return Dwell(injectionTime + coolingTime);
            yield return Together(Open(movingPlaten), Open(toggleLinks));
            yield return Eject();
            yield return Together(Close(movingPlaten), Close(toggleLinks));
        }

        IEnumerator Eject()
        {
            if (moldedPart == null || !captured) yield break;
            moldedPart.localPosition = partRest;
            moldedPart.gameObject.SetActive(true);
            yield return Dwell(0.4f);
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / dropTime;
                float u = Mathf.Clamp01(t);
                moldedPart.localPosition = partRest + Vector3.down * (dropDistance * u * u);
                yield return null;
            }
            moldedPart.gameObject.SetActive(false);
        }
    }
}
