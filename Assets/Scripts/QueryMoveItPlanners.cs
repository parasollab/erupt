// QueryMoveItPlanners.cs
// Unity Robotics Hub + ROS–TCP–Connector
// Requires generated C# for moveit_msgs/srv/QueryPlannerInterfaces

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;

// NOTE: Your generated namespace may differ:
// try RosMessageTypes.Moveit; or RosMessageTypes.MoveitMsgs;
using RosMessageTypes.Moveit;

public class QueryMoveItPlanners : MonoBehaviour
{
    [Tooltip("Service advertised by MoveIt (move_group).")]
    public string serviceName = "/query_planner_interface";

    // Parsed results available to other scripts/inspector
    [Serializable]
    public class PlannerListing
    {
        public string pipelineId;
        public string[] plannerIds;
    }
    public List<PlannerListing> Results = new();

    ROSConnection ros;
    bool isCalling = false;

    void Awake()
    {
        ros = ROSConnection.GetOrCreateInstance();
        // IMPORTANT: register the service before using it to avoid NullReference
        ros.RegisterRosService<QueryPlannerInterfacesRequest, QueryPlannerInterfacesResponse>(serviceName);
    }

    void Start()
    {
        // Wait for SystemPrewarmer to complete before starting
        // if (SystemPrewarmer.IsPrewarmingComplete)
        // {
        //     StartQuerying();
        // }
        // else
        // {
        //     // Subscribe to the prewarming complete event
        //     SystemPrewarmer.OnPrewarmingComplete += OnPrewarmingComplete;
        // }
    }

    private void OnPrewarmingComplete()
    {
        // Unsubscribe from the event
        // SystemPrewarmer.OnPrewarmingComplete -= OnPrewarmingComplete;
        
        // Start the querying process
        StartQuerying();
    }

    private void StartQuerying()
    {
        Debug.Log("[MoveIt] SystemPrewarmer complete, starting planner query...");
        // Kick off the query
        InvokeRepeating(nameof(TryCall), 0.5f, 1.0f); // retry until it succeeds
    }

    void TryCall()
    {
        if (isCalling) return;
        if (ros == null || !ros.HasConnectionThread)
        {
            Debug.LogWarning("[MoveIt] Waiting for ROS-TCP connection...");
            return;
        }

        isCalling = true;
        var req = new QueryPlannerInterfacesRequest();
        try
        {
            ros.SendServiceMessage<QueryPlannerInterfacesResponse>(
                serviceName, req,
                OnResponse
            );
        }
        catch (Exception e)
        {
            isCalling = false;
            Debug.LogError($"[MoveIt] Service call failed: {e.GetType().Name}: {e.Message}");
        }
    }

    void OnResponse(QueryPlannerInterfacesResponse resp)
    {
        isCalling = false;
        CancelInvoke(nameof(TryCall));

        if (resp == null || resp.planner_interfaces == null)
        {
            Debug.LogWarning("[MoveIt] Empty response from /query_planner_interface");
            return;
        }

        Results.Clear();
        foreach (var desc in resp.planner_interfaces)
        {
            var pipeline = desc.pipeline_id ?? string.Empty;
            var planners = desc.planner_ids ?? Array.Empty<string>();

            Results.Add(new PlannerListing
            {
                pipelineId = pipeline,
                plannerIds = planners
            });

            Debug.Log($"[MoveIt] Pipeline: {pipeline} | Planners: {string.Join(", ", planners)}");
        }

        // Optional: quick lookup dictionary
        var lookup = Results.ToDictionary(r => r.pipelineId, r => r.plannerIds);
        // e.g., lookup["ompl"] -> ["ur_manipulator"]
    }

    // void OnDestroy()
    // {
    //     if (ros != null)
    //         ros.UnregisterRosService(serviceName);
    // }

    // void OnDestroy()
    // {
    //     // Unsubscribe from the prewarming event to prevent memory leaks
    //     SystemPrewarmer.OnPrewarmingComplete -= OnPrewarmingComplete;
        
    //     // Cancel any pending invokes
    //     CancelInvoke(nameof(TryCall));
        
    //     // Unregister service if needed
    //     if (ros != null)
    //         ros.UnregisterRosService(serviceName);
    // }
}
