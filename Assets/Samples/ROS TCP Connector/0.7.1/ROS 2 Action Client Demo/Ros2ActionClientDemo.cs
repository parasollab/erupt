using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RosMessageTypes.ExampleInterfaces;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

/// <summary>
/// Drop this component into a scene to exercise every ROS 2 action-client path.
/// All logs and the small IMGUI panel are updated from Unity's main thread.
/// </summary>
public class Ros2ActionClientDemo : MonoBehaviour
{
    const string k_ActionName = "/fibonacci";
    const int k_AbortOrder = 13;

    [SerializeField, Min(2)] int m_SuccessOrder = 12;
    [SerializeField] bool m_AutoRunOnReady = true;

    IRosActionClient<FibonacciGoal, FibonacciResult, FibonacciFeedback> m_Client;
    IRosActionGoal<FibonacciResult> m_ActiveGoal;
    bool m_AutoRunStarted;
    bool m_RequestInFlight;
    string m_Status = "Starting";

    void Start()
    {
        m_Client = ROSConnection.GetOrCreateInstance()
            .RegisterActionClient<FibonacciGoal, FibonacciResult, FibonacciFeedback>(
                k_ActionName);
        m_Client.StateChanged += OnStateChanged;
        OnStateChanged(m_Client.State);
    }

    void OnDestroy()
    {
        if (m_Client != null)
            m_Client.StateChanged -= OnStateChanged;
    }

    void OnStateChanged(RosActionClientState state)
    {
        SetStatus("Action client: " + state +
            (string.IsNullOrEmpty(m_Client.LastError) ? "" : " (" + m_Client.LastError + ")"));

        if (state == RosActionClientState.Ready && m_AutoRunOnReady && !m_AutoRunStarted)
        {
            m_AutoRunStarted = true;
            RunAutomaticDemo();
        }
    }

    async void RunAutomaticDemo()
    {
        SetStatus("Automatic headset demo started");
        await RunGoalAsync(m_SuccessOrder, "success");
        await RunGoalAsync(0, "rejection");
        await RunGoalAsync(k_AbortOrder, "abort");
        await RunGoalAsync(30, "cancellation", 1000);
        SetStatus("Automatic demo complete: success, rejection, abort, and cancellation tested");
    }

    public async void SendSuccessGoal()
    {
        await RunGoalAsync(m_SuccessOrder, "success");
    }

    public async void SendRejectedGoal()
    {
        await RunGoalAsync(0, "rejection");
    }

    public async void SendAbortedGoal()
    {
        await RunGoalAsync(k_AbortOrder, "abort");
    }

    async Task RunGoalAsync(int order, string scenario, int cancelAfterMilliseconds = 0)
    {
        if (m_RequestInFlight)
        {
            SetStatus("A goal is already active");
            return;
        }

        m_RequestInFlight = true;
        try
        {
            SetStatus($"Sending {scenario} goal (order={order})");
            RosActionGoalResponse<FibonacciResult> response = await m_Client.SendGoalAsync(
                new FibonacciGoal(order), OnFeedback);

            if (!response.Accepted)
            {
                SetStatus($"Goal rejected as expected (order={order})");
                return;
            }

            m_ActiveGoal = response.Goal;
            SetStatus($"Goal accepted: Unity ID={m_ActiveGoal.GoalId}, " +
                $"ROS ID={m_ActiveGoal.RosGoalId}");

            if (cancelAfterMilliseconds > 0)
            {
                await Task.Delay(cancelAfterMilliseconds);
                await CancelGoalAsync();
            }

            RosActionResult<FibonacciResult> result = await m_ActiveGoal.Result;
            SetStatus($"Result: status={result.Status}, sequence={Format(result.Result.sequence)}");
        }
        catch (RosActionException exception)
        {
            SetStatus($"Action error [{exception.Code}]: {exception.Message}", true);
        }
        catch (Exception exception)
        {
            SetStatus("Unexpected action error: " + exception, true);
        }
        finally
        {
            m_ActiveGoal = null;
            m_RequestInFlight = false;
        }
    }

    void OnFeedback(FibonacciFeedback feedback)
    {
        SetStatus("Feedback: " + Format(feedback.sequence));
    }

    public async void CancelActiveGoal()
    {
        await CancelGoalAsync();
    }

    async Task CancelGoalAsync()
    {
        if (m_ActiveGoal == null)
        {
            SetStatus("No accepted goal is available to cancel");
            return;
        }

        try
        {
            SetStatus("Requesting cancellation");
            RosActionCancelResponse response = await m_ActiveGoal.CancelAsync();
            SetStatus($"Cancel response: {response.ReturnCode}, accepted={response.Accepted}");
        }
        catch (RosActionException exception)
        {
            SetStatus($"Cancel error [{exception.Code}]: {exception.Message}", true);
        }
        catch (Exception exception)
        {
            SetStatus("Unexpected cancel error: " + exception, true);
        }
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(20, 20, 520, 210), "ROS 2 Action Demo", GUI.skin.window);
        GUILayout.Label("State: " + (m_Client == null ? "Starting" : m_Client.State.ToString()));
        GUILayout.Label(m_Status);

        bool oldEnabled = GUI.enabled;
        GUI.enabled = m_Client != null &&
            m_Client.State == RosActionClientState.Ready && !m_RequestInFlight;
        if (GUILayout.Button($"Send success goal (order {m_SuccessOrder})"))
            SendSuccessGoal();
        if (GUILayout.Button("Send rejected goal (order 0)"))
            SendRejectedGoal();
        if (GUILayout.Button($"Send aborted goal (order {k_AbortOrder})"))
            SendAbortedGoal();

        GUI.enabled = m_ActiveGoal != null;
        if (GUILayout.Button("Cancel accepted goal"))
            CancelActiveGoal();
        GUI.enabled = oldEnabled;
        GUILayout.EndArea();
    }

    void SetStatus(string message, bool error = false)
    {
        m_Status = message;
        string decorated = $"[ROS2 Action Demo] {message} " +
            $"(thread={Thread.CurrentThread.ManagedThreadId}, frame={Time.frameCount})";
        if (error)
            Debug.LogError(decorated, this);
        else
            Debug.Log(decorated, this);
    }

    static string Format(int[] sequence)
    {
        return sequence == null ? "<null>" : "[" + string.Join(", ", sequence.Select(v => v.ToString())) + "]";
    }
}
