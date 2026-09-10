using Erupt.Ros;
using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.StudyInterfaces;

/// <summary>
/// Client for the <c>/pick_place</c> action (study_interfaces/action/PickPlace) served by
/// pick_place_dynamic_demo — the native-action equivalent of publishing /pick_place_task.
/// One goal is in flight at a time, matching the demo node's serial task queue.
/// </summary>
/// <remarks>
/// Same call the CLI makes:
///   ros2 action send_goal /pick_place study_interfaces/action/PickPlace \
///     '{object_id: "object", place_pose: {header: {frame_id: "world"},
///       pose: {position: {x: 0.6, y: -0.15, z: 0.0}, orientation: {w: 1.0}}}}' --feedback
/// Unlike the fire-and-forget topic, this reports acceptance, per-stage feedback
/// ("planning" / "executing"), a success/message result, and supports cancellation.
/// </remarks>
public class PickPlaceActionClient : MonoBehaviour
{
    [Tooltip("Action name served by pick_place_dynamic_demo.")]
    [SerializeField] private string actionName = "/pick_place";

    [Tooltip("Frame the place pose is expressed in.")]
    [SerializeField] private string placeFrameId = "world";

    public string ActionName => actionName;
    public string PlaceFrameId => placeFrameId;

    /// <summary>Latest status string, mirroring MTCDataManager.LastExecutionStatus.</summary>
    public string LastStatus { get; private set; } = "";

    /// <summary>Latest feedback stage reported by the server ("planning", "executing", …).</summary>
    public string LastStage { get; private set; } = "";

    public event Action<string> OnStatus;
    public event Action<PickPlaceFeedback> OnFeedback;

    public bool Available => client != null && client.State == RosActionClientState.Ready;
    public bool IsRunning => activeGoal != null;

    private IRosBus ros;
    private IRosActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> client;
    private IRosActionGoal<PickPlaceResult> activeGoal;

    void Start()
    {
        ros = RosBus.Instance;
        client = ros.RegisterActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>(actionName);
        client.StateChanged += OnClientStateChanged;
        OnClientStateChanged(client.State);
    }

    void OnDestroy()
    {
        if (client != null)
            client.StateChanged -= OnClientStateChanged;
    }

    /// <summary>
    /// Send a pick-and-place goal built from a ROS-frame position and orientation.
    /// </summary>
    public Task<RosActionResult<PickPlaceResult>> SendGoalAsync(
        string objectId, PointMsg position, QuaternionMsg orientation, bool execute = true)
    {
        var placePose = new PoseStampedMsg(
            new RosMessageTypes.Std.HeaderMsg(new RosMessageTypes.BuiltinInterfaces.TimeMsg(), placeFrameId),
            new PoseMsg(position, orientation));
        return SendGoalAsync(objectId, placePose, execute);
    }

    /// <summary>
    /// Send a pick-and-place goal and await its result. Feedback arrives on the main
    /// thread via <see cref="OnFeedback"/>; progress is mirrored to <see cref="OnStatus"/>.
    /// </summary>
    public async Task<RosActionResult<PickPlaceResult>> SendGoalAsync(
        string objectId, PoseStampedMsg placePose, bool execute = true)
    {
        if (string.IsNullOrEmpty(objectId)) throw new ArgumentException("object_id is required", nameof(objectId));
        if (placePose == null) throw new ArgumentNullException(nameof(placePose));
        if (!Available)
            throw new RosActionException("action_not_ready",
                client?.LastError ?? $"{actionName} is not ready");
        if (activeGoal != null)
            throw new InvalidOperationException("A pick-and-place goal is already running");

        LastStage = "";
        SetStatus("SENDING");
        try
        {
            var response = await client.SendGoalAsync(
                new PickPlaceGoal(objectId, placePose, execute), OnFeedbackReceived);
            if (!response.Accepted)
            {
                SetStatus("REJECTED");
                throw new RosActionException("goal_rejected", $"{actionName} goal was rejected");
            }

            activeGoal = response.Goal;
            SetStatus("RUNNING");
            var result = await activeGoal.Result;
            string message = result.Result?.message ?? "";
            bool success = result.Result != null && result.Result.success;

            if (result.Status == RosActionGoalStatus.Succeeded && success)
                SetStatus("SUCCEEDED" + Suffix(message));
            else if (result.Status == RosActionGoalStatus.Canceled)
                SetStatus("CANCELED" + Suffix(message));
            else if (result.Status == RosActionGoalStatus.Aborted)
                SetStatus("ABORTED" + Suffix(message));
            else
                SetStatus($"FAILED ({result.Status})" + Suffix(message));
            return result;
        }
        catch (RosActionException exception)
        {
            switch (exception.Code)
            {
                case "goal_rejected":
                    SetStatus("REJECTED");
                    break;
                case "server_unavailable":
                case "action_not_ready":
                    SetStatus("UNAVAILABLE: " + exception.Message);
                    break;
                case "connection_lost":
                    SetStatus("DISCONNECTED: remote goal may still be running");
                    break;
                case "unsupported_endpoint":
                    SetStatus("ENDPOINT UPGRADE REQUIRED: ROS 2 actions unsupported");
                    break;
                default:
                    SetStatus("ACTION ERROR: " + exception.Message);
                    break;
            }
            throw;
        }
        catch (Exception exception)
        {
            SetStatus("ERROR: " + exception.Message);
            throw;
        }
        finally
        {
            activeGoal = null;
        }
    }

    /// <summary>Ask the server to abandon the running goal.</summary>
    public async Task<RosActionCancelResponse> CancelAsync()
    {
        if (activeGoal == null)
            throw new InvalidOperationException("No pick-and-place goal is running");

        IRosActionGoal<PickPlaceResult> goal = activeGoal;
        var response = await goal.CancelAsync();
        bool terminal = goal.Status == RosActionGoalStatus.Succeeded ||
            goal.Status == RosActionGoalStatus.Canceled ||
            goal.Status == RosActionGoalStatus.Aborted;
        if (!terminal)
            SetStatus(response.Accepted ? "CANCELING" : $"CANCEL REJECTED ({response.ReturnCode})");
        return response;
    }

    private void OnFeedbackReceived(PickPlaceFeedback feedback)
    {
        LastStage = feedback?.stage ?? "";
        OnFeedback?.Invoke(feedback);
        if (!string.IsNullOrEmpty(LastStage))
            SetStatus(LastStage.ToUpperInvariant());
    }

    private void OnClientStateChanged(RosActionClientState state)
    {
        switch (state)
        {
            case RosActionClientState.Ready:
                if (!IsRunning) SetStatus("READY");
                break;
            case RosActionClientState.Unsupported:
                SetStatus("ENDPOINT UPGRADE REQUIRED: ROS 2 actions unsupported");
                break;
            case RosActionClientState.Faulted:
                SetStatus("ACTION ERROR: " + client.LastError);
                break;
            default:
                if (!IsRunning) SetStatus("WAITING FOR ACTION ENDPOINT");
                break;
        }
    }

    private static string Suffix(string message) =>
        string.IsNullOrEmpty(message) ? "" : ": " + message;

    private void SetStatus(string status)
    {
        LastStatus = status;
        OnStatus?.Invoke(status);
    }
}
