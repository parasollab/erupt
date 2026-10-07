using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

/// <summary>
/// Connects the interaction router to the robot handle and joint controls. Replaces the
/// input-reading half of Quest3ControllerRayInteractor; the ray drawing half is now
/// InteractionRayVisual.
/// </summary>
/// <remarks>
/// This component reads no device state and contains no platform branch — it consumes
/// intents whose filtering, deadzone and target resolution already happened in the
/// router. Guidelines Part 3.
/// </remarks>
public class RobotInteractionRouterBinding : MonoBehaviour
{
    [SerializeField] private InteractionRouter router;
    [SerializeField] private Quest3RobotInteractionController robotInteraction;

    [Tooltip("Matches the pre-refactor jog rate in Quest3ControllerRayInteractor.")]
    [SerializeField] private float jointJogRadiansPerSecond = 0.8f;

    private readonly HashSet<string> dragOwnerSourceIds = new HashSet<string>();

    private void Awake()
    {
        if (router == null) router = FindFirstObjectByType<InteractionRouter>();
        if (robotInteraction == null) robotInteraction = FindFirstObjectByType<Quest3RobotInteractionController>();
    }

    private void OnEnable()
    {
        if (router == null || robotInteraction == null)
        {
            Debug.LogError("RobotInteractionRouterBinding: router or robotInteraction not assigned.");
            enabled = false;
            return;
        }

        router.Select += OnSelect;
        router.BeginDrag += OnBeginDrag;
        router.Drag += OnDrag;
        router.EndDrag += OnEndDrag;
        router.Axis += OnAxis;
    }

    private void OnDisable()
    {
        if (router == null) return;

        router.Select -= OnSelect;
        router.BeginDrag -= OnBeginDrag;
        router.Drag -= OnDrag;
        router.EndDrag -= OnEndDrag;
        router.Axis -= OnAxis;

        foreach (string sourceId in dragOwnerSourceIds)
        {
            robotInteraction.EndHandleDragBySource(sourceId);
        }
        dragOwnerSourceIds.Clear();
    }

    private void OnSelect(InteractionIntent intent)
    {
        if (dragOwnerSourceIds.Count > 0) return;

        // The pre-refactor script called SelectFromHit only when the ray hit something;
        // a miss left the joint highlight alone rather than clearing it.
        if (!intent.HasHit) return;

        robotInteraction.SelectFromIntent(intent);
    }

    private void OnBeginDrag(InteractionIntent intent)
    {
        if (!intent.HasHit) return;

        if (robotInteraction.TryBeginHandleDrag(intent))
        {
            dragOwnerSourceIds.Add(intent.Sample.SourceId ?? "default");
        }
    }

    private void OnDrag(InteractionIntent intent)
    {
        if (!dragOwnerSourceIds.Contains(intent.Sample.SourceId ?? "default")) return;
        robotInteraction.UpdateHandleDrag(intent);
        Surface(robotInteraction.LastRefusal);
    }

    private void OnEndDrag(InteractionIntent intent)
    {
        string sourceId = intent.Sample.SourceId ?? "default";
        if (!dragOwnerSourceIds.Contains(sourceId)) return;

        robotInteraction.EndHandleDrag(intent);
        dragOwnerSourceIds.Remove(sourceId);
    }

    private void OnAxis(InteractionIntent intent)
    {
        // Deadzone and precision scaling already applied by the router.
        if (dragOwnerSourceIds.Count > 0) return;

        float delta = intent.Axis.y * jointJogRadiansPerSecond * Time.deltaTime;
        if (Mathf.Approximately(delta, 0f)) return;

        robotInteraction.JogSelectedJoint(delta);
        Surface(robotInteraction.LastRefusal);
    }

    private void Surface(InteractionRefusal refusal)
    {
        if (refusal.IsRefused) router.ReportRefusal(refusal);
    }
}
