using System;
using RosMessageTypes.Std;
using UnityEngine;
using Erupt.Ros;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// Logs <c>std_msgs/String</c> messages from ROS (RADER's <c>InfoLog</c>, on <see cref="IRosBus"/>)
    /// and keeps the last one for the demos tab.
    /// </summary>
    public sealed class InfoLog : IDisposable
    {
        private readonly IRosBus ros;
        private readonly Action<StringMsg> onMessage;
        private bool started;

        public string Topic { get; }
        public string Last { get; private set; } = string.Empty;
        public int Count { get; private set; }
        public event Action<string> Received;

        public InfoLog(IRosBus ros, string topic)
        {
            this.ros = ros ?? throw new ArgumentNullException(nameof(ros));
            Topic = topic;
            onMessage = OnMessage;
        }

        public void Start()
        {
            if (started) return;
            started = true;
            ros.Subscribe(Topic, onMessage);
        }

        public void Dispose()
        {
            if (!started) return;
            started = false;
            ros.Unsubscribe(Topic, onMessage);
        }

        private void OnMessage(StringMsg msg)
        {
            Last = msg?.data ?? string.Empty;
            Count++;
            Debug.Log($"[rader] {Last}");
            Received?.Invoke(Last);
        }
    }
}
