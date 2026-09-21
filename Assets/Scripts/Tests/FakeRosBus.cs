using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using Unity.Robotics.ROSTCPConnector;
using Erupt.Ros;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// In-memory ROS transport. Records everything published and lets a test inject
    /// inbound messages, so the five core flows can be exercised without a TCP endpoint.
    /// </summary>
    public sealed class FakeRosBus : IRosBus
    {
        public sealed class Published
        {
            public string Topic;
            public Message Message;
        }

        public readonly List<Published> Publishes = new();
        public readonly List<string> RegisteredPublishers = new();
        public readonly List<string> RegisteredServices = new();
        public readonly List<string> Subscriptions = new();

        private readonly Dictionary<string, List<Delegate>> callbacks = new();
        private readonly Dictionary<string, Func<Message, Message>> serviceHandlers = new();
        private readonly Dictionary<string, object> actionClients = new();

        public bool HasConnectionThread { get; set; } = true;

        public void Subscribe<T>(string topic, Action<T> callback) where T : Message
        {
            Subscriptions.Add(topic);
            if (!callbacks.TryGetValue(topic, out var list))
                callbacks[topic] = list = new List<Delegate>();
            list.Add(callback);
        }

        public void Unsubscribe<T>(string topic, Action<T> callback) where T : Message
        {
            if (!callbacks.TryGetValue(topic, out var list)) return;
            list.Remove(callback);
            if (list.Count == 0) callbacks.Remove(topic);
        }

        public void RegisterPublisher<T>(string topic) where T : Message =>
            RegisteredPublishers.Add(topic);

        public void RegisterRosService<TRequest, TResponse>(string serviceName)
            where TRequest : Message where TResponse : Message =>
            RegisteredServices.Add(serviceName);

        public void SendServiceMessage<TResponse>(string serviceName, Message request, Action<TResponse> callback)
            where TResponse : Message, new()
        {
            if (serviceHandlers.TryGetValue(serviceName, out var handler))
                callback?.Invoke((TResponse)handler(request));
        }

        public IRosActionClient<TGoal, TResult, TFeedback>
            RegisterActionClient<TGoal, TResult, TFeedback>(string actionName)
            where TGoal : Message where TResult : Message where TFeedback : Message
        {
            var client = new FakeActionClient<TGoal, TResult, TFeedback>();
            actionClients[actionName] = client;
            return client;
        }

        public void Publish(string topic, Message message) =>
            Publishes.Add(new Published { Topic = topic, Message = message });

        public void Publish(string topic, Message message, bool useTrailingPad) =>
            Publish(topic, message);

        // --- Test-side controls ---

        /// <summary>Deliver a message as though ROS had published it.</summary>
        public void Inbound<T>(string topic, T message) where T : Message
        {
            if (!callbacks.TryGetValue(topic, out var list)) return;
            foreach (var cb in list.ToArray()) ((Action<T>)cb).Invoke(message);
        }

        public void SetServiceHandler(string serviceName, Func<Message, Message> handler) =>
            serviceHandlers[serviceName] = handler;

        public FakeActionClient<TGoal, TResult, TFeedback>
            ActionClient<TGoal, TResult, TFeedback>(string actionName)
            where TGoal : Message where TResult : Message where TFeedback : Message =>
            (FakeActionClient<TGoal, TResult, TFeedback>)actionClients[actionName];

        public IEnumerable<Message> PublishedOn(string topic)
        {
            foreach (var p in Publishes)
                if (p.Topic == topic) yield return p.Message;
        }

        public int CountOn(string topic)
        {
            int n = 0;
            foreach (var p in Publishes) if (p.Topic == topic) n++;
            return n;
        }

        public void Clear()
        {
            Publishes.Clear();
            RegisteredPublishers.Clear();
            RegisteredServices.Clear();
            Subscriptions.Clear();
        }

        public sealed class FakeActionClient<TGoal, TResult, TFeedback> :
            IRosActionClient<TGoal, TResult, TFeedback>
            where TGoal : Message where TResult : Message where TFeedback : Message
        {
            TaskCompletionSource<RosActionGoalResponse<TResult>> pendingResponse;
            Action<TFeedback> feedbackCallback;

            public RosActionClientState State { get; private set; } = RosActionClientState.Ready;
            public string LastError { get; private set; }
            public event Action<RosActionClientState> StateChanged;
            public TGoal LastGoal { get; private set; }
            public FakeActionGoal<TResult> ActiveGoal { get; private set; }

            public Task<RosActionGoalResponse<TResult>> SendGoalAsync(
                TGoal goal, Action<TFeedback> feedback = null)
            {
                LastGoal = goal;
                feedbackCallback = feedback;
                pendingResponse = new TaskCompletionSource<RosActionGoalResponse<TResult>>();
                return pendingResponse.Task;
            }

            public FakeActionGoal<TResult> Accept(string rosGoalId = "fake-ros-goal")
            {
                ActiveGoal = new FakeActionGoal<TResult>(rosGoalId);
                pendingResponse.SetResult(new RosActionGoalResponse<TResult>(true, ActiveGoal));
                return ActiveGoal;
            }

            public void Reject()
            {
                pendingResponse.SetResult(new RosActionGoalResponse<TResult>(false, null));
            }

            public void Feedback(TFeedback feedback)
            {
                feedbackCallback?.Invoke(feedback);
            }

            public void SetState(RosActionClientState state, string error = null)
            {
                State = state;
                LastError = error;
                StateChanged?.Invoke(state);
            }

            public void Fail(RosActionException exception)
            {
                pendingResponse?.TrySetException(exception);
                ActiveGoal?.Fail(exception);
            }
        }

        public sealed class FakeActionGoal<TResult> : IRosActionGoal<TResult>
            where TResult : Message
        {
            readonly TaskCompletionSource<RosActionResult<TResult>> result = new();
            readonly TaskCompletionSource<RosActionCancelResponse> cancel = new();

            public string GoalId { get; } = Guid.NewGuid().ToString("N");
            public string RosGoalId { get; }
            public RosActionGoalStatus Status { get; private set; } = RosActionGoalStatus.Accepted;
            public Task<RosActionResult<TResult>> Result => result.Task;

            internal FakeActionGoal(string rosGoalId)
            {
                RosGoalId = rosGoalId;
            }

            /// <summary>True once the client under test asked to cancel this goal.</summary>
            public bool CancelRequested { get; private set; }

            public Task<RosActionCancelResponse> CancelAsync()
            {
                CancelRequested = true;
                return cancel.Task;
            }

            public void Complete(RosActionGoalStatus status, TResult message)
            {
                Status = status;
                result.SetResult(new RosActionResult<TResult>(status, message));
            }

            public void CompleteCancel(RosActionCancelReturnCode returnCode)
            {
                if (returnCode == RosActionCancelReturnCode.None)
                    Status = RosActionGoalStatus.Canceling;
                cancel.SetResult(new RosActionCancelResponse(returnCode));
            }

            public void Fail(Exception exception)
            {
                result.TrySetException(exception);
                cancel.TrySetException(exception);
            }
        }
    }
}
