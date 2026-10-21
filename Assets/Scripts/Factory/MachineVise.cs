using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Factory
{
    // A pneumatic self-centring vise on the machine table. The operator (or the robot) only
    // has to put a part roughly between the open jaws; on Clamp the two jaws travel inward
    // together, the first jaw to touch the part slides and turns it until its face lies flat
    // on the jaw plate, and the part ends up centred on the clamp axis. Nothing about the
    // final pose is authored: it is computed at clamp time from where the part actually is
    // and the jaw geometry, so a part set down skewed, off-centre, on a jaw plate or not at
    // all gives a different result (ClampedPoor / Empty) and the panel says why.
    //
    // Frames: jawWindow sits on the parallels' top surface at the centre of the opening, with
    // local x along the jaw faces (the free axis a vise never corrects), local z across them
    // (the clamp axis) and y up. The jaws are SlidingParts authored in their OPEN pose with an
    // openOffset that points inward to the closed pose (faces meeting at the centre), so a
    // jaw's openAmount reads as its closed fraction here.
    public class MachineVise : MonoBehaviour
    {
        public enum ViseState { Open, Closing, Clamped, ClampedPoor, Empty, Opening }

        [Serializable]
        public class ClampReport
        {
            public GameObject part;
            public string partId;
            public float skewDegrees;      // yaw error to the nearest jaw-parallel face before the clamp
            public float tiltDegrees;      // the part's up axis against the vise's before the clamp
            public float stickout;         // metres of part above the jaw faces after the clamp
            public bool squared;           // skew and tilt within limits: the jaws could turn it flat
            public bool seated;            // bottom face on the parallels
            public bool overhang;          // part sticks out past the ends of the jaw faces
            public bool stickoutOk;
            public List<string> problems = new List<string>();
            public bool Good => squared && seated && !overhang && stickoutOk;
        }

        [Tooltip("The two jaws, authored OPEN; each openOffset points inward to its closed pose (faces meeting at the centre).")]
        public SlidingPart[] jaws;
        [Tooltip("Centre of the jaw opening on the parallels' top surface: local x along the jaw faces, z across them, y up.")]
        public Transform jawWindow;
        [Tooltip("Distance between the jaw faces when fully open (metres).")]
        public float openGap = 0.12f;
        [Tooltip("Length of the jaw faces along local x (metres).")]
        public float jawWidth = 0.16f;
        [Tooltip("Height of the jaw faces above the parallels (metres); stickout is measured from here.")]
        public float jawHeight = 0.03f;
        [Tooltip("Largest yaw error (degrees) the closing jaws can still turn flat; beyond it the part is clamped on its corners.")]
        public float skewLimitDegrees = 30f;
        [Tooltip("Largest tilt of the part's up axis (degrees) that still counts as lying flat.")]
        public float tiltLimitDegrees = 5f;
        [Tooltip("A bottom face within this height of the parallels counts as seated (metres).")]
        public float seatTolerance = 0.008f;
        [Tooltip("The job needs at least this much part above the jaw faces (metres).")]
        public float requiredStickout = 0.025f;
        [Tooltip("The jaws stop this short of the part faces (metres) so MoveIt sees no jaw/part contact.")]
        public float clampGap = 0.001f;
        [Tooltip("Seconds for a jaw to travel from fully open to the centre.")]
        public float travelTime = 1.2f;
        [Tooltip("How often the jaw window is scanned for a part (Hz).")]
        public float presenceRateHz = 10f;

        [Header("Signals")]
        public Renderer presentLamp;
        public Renderer clampLamp;
        public Color lampOff = new Color(0.25f, 0.25f, 0.25f);
        public Color lampGood = new Color(0.15f, 0.95f, 0.25f);
        public Color lampWarn = new Color(1f, 0.65f, 0.1f);
        public Color lampBad = new Color(0.95f, 0.15f, 0.1f);

        [Tooltip("The compound collision object the jaws belong to (the station 'table'); re-sent whenever the jaws stop moving.")]
        public CollisionObjectPublisher republishOnMove;
        public string machineName = "Vise";

        public ViseState State { get; private set; } = ViseState.Open;
        public ClampReport LastReport { get; private set; }
        /// <summary>The part currently between the jaw faces (closest to the centre), or null.</summary>
        public GameObject PresentPart { get; private set; }
        public bool PresentPartHeldByRobot { get; private set; }
        public bool IsMoving => State == ViseState.Closing || State == ViseState.Opening;
        public bool IsClosed => State == ViseState.Clamped || State == ViseState.ClampedPoor || State == ViseState.Empty;
        public bool CanClamp => State == ViseState.Open && !PresentPartHeldByRobot;
        public bool CanRelease => IsClosed;
        /// <summary>Jaw face half-gap at the current jaw travel (metres).</summary>
        public float CurrentHalfGap => HalfGapAt(jaws != null && jaws.Length > 0 && jaws[0] != null ? jaws[0].openAmount : 0f);

        public event Action<ViseState> StateChanged;
        public event Action<GameObject> PresenceChanged;

        Coroutine motion;
        float nextScan;
        MaterialPropertyBlock block;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        void Awake()
        {
            block = new MaterialPropertyBlock();
        }

        void Start()
        {
            ApplyLamps();
        }

        void Update()
        {
            if (Time.time < nextScan) return;
            nextScan = Time.time + 1f / Mathf.Max(1f, presenceRateHz);
            ScanPresence();
        }

        // ---------------------------------------------------------------- presence

        /// <summary>Re-scan the jaw window now (the panel and tests call it after moving a part).</summary>
        public void ScanPresence()
        {
            GameObject found = FindPartInWindow(out bool held);
            bool changed = found != PresentPart || held != PresentPartHeldByRobot;
            PresentPart = found;
            PresentPartHeldByRobot = held;
            if (changed)
            {
                ApplyLamps();
                PresenceChanged?.Invoke(found);
            }
        }

        GameObject FindPartInWindow(out bool heldByRobot)
        {
            heldByRobot = false;
            if (jawWindow == null) return null;
            float half = Mathf.Max(0.01f, openGap * 0.5f);
            Vector3 extents = new Vector3(jawWidth * 0.5f, half, half);
            Vector3 centre = jawWindow.position + jawWindow.up * half;
            Collider[] hits = Physics.OverlapBox(centre, extents, jawWindow.rotation, ~0, QueryTriggerInteraction.Ignore);
            GameObject best = null;
            float bestDistance = float.MaxValue;
            CollisionObjectPublisher bestPub = null;
            foreach (Collider hit in hits)
            {
                if (hit == null || hit.transform.IsChildOf(transform)) continue;
                var pub = hit.GetComponentInParent<CollisionObjectPublisher>();
                if (pub == null || pub == republishOnMove) continue;
                if (pub.GetComponent<Rigidbody>() == null) continue; // static props are not parts
                float d = Vector3.Distance(pub.transform.position, centre);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = pub.gameObject;
                bestPub = pub;
            }
            heldByRobot = bestPub != null && bestPub.attachedToRobot;
            return best;
        }

        // ---------------------------------------------------------------- clamp / release

        public void Clamp()
        {
            if (!CanClamp) return;
            ScanPresence();
            if (PresentPartHeldByRobot) return;
            if (motion != null) StopCoroutine(motion);
            motion = StartCoroutine(ClampRoutine(PresentPart));
        }

        public void Release()
        {
            if (!CanRelease) return;
            if (motion != null) StopCoroutine(motion);
            motion = StartCoroutine(ReleaseRoutine());
        }

        /// <summary>Jumps the jaws to open or closed without animating (probes, editor previews).</summary>
        public void SetImmediate(bool open)
        {
            if (motion != null) { StopCoroutine(motion); motion = null; }
            foreach (SlidingPart jaw in jaws ?? Array.Empty<SlidingPart>())
                if (jaw != null) jaw.Set(open ? 0f : 1f);
            SetState(open ? ViseState.Open : ViseState.Empty);
            Republish();
        }

        IEnumerator ClampRoutine(GameObject part)
        {
            SetState(ViseState.Closing);
            float travel = Mathf.Max(0.001f, openGap * 0.5f);

            if (part == null)
            {
                // Nothing between the jaws: they meet in the middle.
                yield return DriveJaws(1f, travelTime);
                LastReport = null;
                motion = null;
                SetState(ViseState.Empty);
                Republish();
                yield break;
            }

            ClampReport report = Analyse(part);
            LastReport = report;

            // Where the near jaw first touches the part, and where both jaws stop.
            float contactHalfGap = Mathf.Min(travel, report_partOffsetZ + report_extentZ);
            float finalHalfGap = (report.squared ? report_finalExtentZ : report_extentZ) + clampGap;
            float contactAmount = Mathf.Clamp01(1f - contactHalfGap / travel);
            float finalAmount = Mathf.Clamp01(1f - finalHalfGap / travel);
            finalAmount = Mathf.Max(finalAmount, contactAmount);

            // Free travel until the first jaw touches.
            yield return DriveJaws(contactAmount, travelTime * contactAmount);

            // Pushing: the part slides to the centre and (if it can) turns flat, in step with the jaws.
            Vector3 fromPos = part.transform.position;
            Quaternion fromRot = part.transform.rotation;
            float pushAmount = finalAmount - contactAmount;
            float duration = Mathf.Max(0.05f, travelTime * pushAmount);
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float k = Mathf.Clamp01(t);
                float amount = Mathf.Lerp(contactAmount, finalAmount, k);
                foreach (SlidingPart jaw in jaws ?? Array.Empty<SlidingPart>())
                    if (jaw != null) jaw.Set(amount);
                if (part != null)
                    part.transform.SetPositionAndRotation(Vector3.Lerp(fromPos, report_finalPos, k), Quaternion.Slerp(fromRot, report_finalRot, k));
                yield return null;
            }
            if (part != null) part.transform.SetPositionAndRotation(report_finalPos, report_finalRot);
            foreach (SlidingPart jaw in jaws ?? Array.Empty<SlidingPart>())
                if (jaw != null) jaw.Set(finalAmount);

            // Measured after the clamp, where the part really is.
            if (part != null) MeasureAfterClamp(part, report);
            motion = null;
            ScanPresence();
            SetState(report.Good ? ViseState.Clamped : ViseState.ClampedPoor);
            Republish();
        }

        IEnumerator ReleaseRoutine()
        {
            SetState(ViseState.Opening);
            float from = jaws != null && jaws.Length > 0 && jaws[0] != null ? jaws[0].openAmount : 1f;
            yield return DriveJaws(0f, travelTime * from);
            motion = null;
            SetState(ViseState.Open);
            Republish();
        }

        IEnumerator DriveJaws(float target, float seconds)
        {
            float from = jaws != null && jaws.Length > 0 && jaws[0] != null ? jaws[0].openAmount : 0f;
            float duration = Mathf.Max(0.01f, seconds);
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float amount = Mathf.Lerp(from, target, Mathf.Clamp01(t));
                foreach (SlidingPart jaw in jaws ?? Array.Empty<SlidingPart>())
                    if (jaw != null) jaw.Set(amount);
                yield return null;
            }
            foreach (SlidingPart jaw in jaws ?? Array.Empty<SlidingPart>())
                if (jaw != null) jaw.Set(target);
        }

        float HalfGapAt(float amount) => openGap * 0.5f * (1f - Mathf.Clamp01(amount));

        void Republish()
        {
            if (republishOnMove != null) republishOnMove.ForceRepublish();
        }

        // ---------------------------------------------------------------- geometry

        // Scratch results of Analyse for the clamp routine (world pose the part ends in, extents).
        float report_partOffsetZ, report_extentZ, report_finalExtentZ;
        Vector3 report_finalPos;
        Quaternion report_finalRot;

        /// <summary>
        /// Works out what the closing jaws will do to <paramref name="part"/> from its current
        /// pose: which of its faces will meet the jaw plates, how far it is skewed from them,
        /// whether it lies flat on the parallels, and the pose it will be pushed into.
        /// </summary>
        public ClampReport Analyse(GameObject part)
        {
            var report = new ClampReport { part = part, partId = PartId(part) };
            Transform w = jawWindow;
            Transform p = part.transform;
            Vector3 half = BoxHalfExtents(part);

            // The part axis closest to vertical is its "up"; the other two span its footprint.
            Vector3[] axes = { p.right, p.up, p.forward };
            float[] halves = { half.x, half.y, half.z };
            int upIndex = 0;
            float bestDot = -1f;
            for (int i = 0; i < 3; i++)
            {
                float d = Mathf.Abs(Vector3.Dot(axes[i], w.up));
                if (d > bestDot) { bestDot = d; upIndex = i; }
            }
            int aIndex = (upIndex + 1) % 3, bIndex = (upIndex + 2) % 3;
            float hUp = halves[upIndex], hA = halves[aIndex], hB = halves[bIndex];
            report.tiltDegrees = Mathf.Acos(Mathf.Clamp01(bestDot)) * Mathf.Rad2Deg;

            // Yaw of footprint axis A in the window's horizontal plane, folded to the nearest
            // jaw-parallel orientation (a box looks the same every 90 degrees).
            Vector3 aLocal = w.InverseTransformDirection(axes[aIndex]);
            aLocal.y = 0f;
            float theta = Mathf.Atan2(aLocal.z, aLocal.x) * Mathf.Rad2Deg; // 0 = along the jaws
            float skew = Mathf.Repeat(theta + 45f, 90f) - 45f;            // (-45, 45]
            report.skewDegrees = skew;
            float thetaRad = theta * Mathf.Deg2Rad;
            report_extentZ = hA * Mathf.Abs(Mathf.Sin(thetaRad)) + hB * Mathf.Abs(Mathf.Cos(thetaRad));
            float extentX = hA * Mathf.Abs(Mathf.Cos(thetaRad)) + hB * Mathf.Abs(Mathf.Sin(thetaRad));

            Vector3 lp = w.InverseTransformPoint(p.position);
            report_partOffsetZ = Mathf.Abs(lp.z);
            report.squared = Mathf.Abs(skew) <= skewLimitDegrees && report.tiltDegrees <= tiltLimitDegrees;
            report.overhang = Mathf.Abs(lp.x) + extentX > jawWidth * 0.5f + 1e-4f;
            report.seated = report.tiltDegrees <= tiltLimitDegrees && Mathf.Abs(lp.y - hUp) <= seatTolerance;

            if (report.squared)
            {
                // Turn footprint axis A onto the nearest of +-x / +-z (a rotation about the
                // window's up axis, since both vectors are horizontal), then centre on z.
                float thetaFinal = (theta - skew) * Mathf.Deg2Rad;
                Vector3 target = w.TransformDirection(new Vector3(Mathf.Cos(thetaFinal), 0f, Mathf.Sin(thetaFinal)));
                Vector3 current = w.TransformDirection(aLocal.normalized);
                report_finalRot = Quaternion.FromToRotation(current, target) * p.rotation;
                report_finalExtentZ = hA * Mathf.Abs(Mathf.Sin(thetaFinal)) + hB * Mathf.Abs(Mathf.Cos(thetaFinal));
            }
            else
            {
                report_finalRot = p.rotation;
                report_finalExtentZ = report_extentZ;
            }
            report_finalPos = w.TransformPoint(new Vector3(lp.x, lp.y, 0f));

            if (!report.squared)
                report.problems.Add(report.tiltDegrees > tiltLimitDegrees
                    ? $"not lying flat (tilted {report.tiltDegrees:F0} deg)"
                    : $"clamped on its corners (skewed {Mathf.Abs(skew):F0} deg, limit {skewLimitDegrees:F0})");
            if (!report.seated) report.problems.Add("not seated on the parallels");
            if (report.overhang) report.problems.Add("overhangs the jaw faces");
            return report;
        }

        void MeasureAfterClamp(GameObject part, ClampReport report)
        {
            Vector3 half = BoxHalfExtents(part);
            Transform p = part.transform;
            float top = float.MinValue;
            Vector3[] axes = { p.right, p.up, p.forward };
            float[] halves = { half.x, half.y, half.z };
            // Height of the highest corner above the parallels.
            float centreHeight = Vector3.Dot(p.position - jawWindow.position, jawWindow.up);
            float rise = 0f;
            for (int i = 0; i < 3; i++) rise += Mathf.Abs(Vector3.Dot(axes[i], jawWindow.up)) * halves[i];
            top = centreHeight + rise;
            report.stickout = top - jawHeight;
            report.stickoutOk = report.stickout >= requiredStickout - 1e-4f;
            if (!report.stickoutOk)
                report.problems.Add($"stickout {report.stickout * 1000f:F0} mm is under the {requiredStickout * 1000f:F0} mm the job needs");
        }

        static Vector3 BoxHalfExtents(GameObject part)
        {
            var box = part.GetComponent<BoxCollider>();
            if (box != null)
                return Vector3.Scale(box.size, Abs(part.transform.lossyScale)) * 0.5f;
            var renderer = part.GetComponentInChildren<Renderer>();
            if (renderer != null) return renderer.localBounds.extents; // local, unscaled: approximate
            return Abs(part.transform.lossyScale) * 0.5f;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static string PartId(GameObject part)
        {
            var pub = part != null ? part.GetComponent<CollisionObjectPublisher>() : null;
            return pub != null ? pub.objectId : (part != null ? part.name : "");
        }

        // ---------------------------------------------------------------- signals

        void SetState(ViseState s)
        {
            if (State == s) return;
            State = s;
            ApplyLamps();
            StateChanged?.Invoke(s);
        }

        void ApplyLamps()
        {
            Tint(presentLamp, PresentPart != null ? (PresentPartHeldByRobot ? lampWarn : lampGood) : lampOff);
            Color clamp = State switch
            {
                ViseState.Clamped => lampGood,
                ViseState.ClampedPoor => lampWarn,
                ViseState.Empty => lampBad,
                ViseState.Closing => lampWarn,
                _ => lampOff,
            };
            Tint(clampLamp, clamp);
        }

        void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);
            block.SetColor(ColorId, c);
            r.SetPropertyBlock(block);
        }

        /// <summary>One-line description of the last clamp for panels and logs.</summary>
        public string Describe()
        {
            switch (State)
            {
                case ViseState.Open:
                    if (PresentPart == null) return "Jaws open, nothing between them";
                    return PresentPartHeldByRobot ? $"{PartId(PresentPart)} between the jaws, still in the gripper" : $"{PartId(PresentPart)} between the jaws";
                case ViseState.Closing: return "Clamping...";
                case ViseState.Opening: return "Releasing...";
                case ViseState.Empty: return "Jaws closed on nothing";
                case ViseState.Clamped:
                    return LastReport != null
                        ? $"{LastReport.partId} clamped: squared from {Mathf.Abs(LastReport.skewDegrees):F0} deg, stickout {LastReport.stickout * 1000f:F0} mm"
                        : "Clamped";
                case ViseState.ClampedPoor:
                    return LastReport != null
                        ? $"{LastReport.partId} clamped badly: {string.Join("; ", LastReport.problems)}"
                        : "Clamped badly";
            }
            return State.ToString();
        }
    }
}
