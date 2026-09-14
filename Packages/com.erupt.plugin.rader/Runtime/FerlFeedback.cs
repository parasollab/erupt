using System;
using RosMessageTypes.Std;
using Erupt.Ros;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// FERL feedback handshake, ported from RADER's <c>FERL</c> popup onto <see cref="IRosBus"/>.
    /// ROS asks for feedback (<c>/feedback_request</c>), later says the request is satisfied
    /// (<c>/req_satisfied</c>), and the user answers with <c>/feedback_response</c>. The popup
    /// became state on this object; the plugin's demos tab renders it (Guidelines Part 4: no
    /// new menus — the request summons the tier 3 tab).
    /// </summary>
    public sealed class FerlFeedback : IDisposable
    {
        private readonly IRosBus ros;
        private readonly Action<BoolMsg> onRequest, onSatisfied;
        private bool started;

        public string RequestTopic { get; }
        public string SatisfiedTopic { get; }
        public string ResponseTopic { get; }

        /// <summary>ROS asked for feedback and the user has not answered or dismissed it.</summary>
        public bool RequestPending { get; private set; }
        /// <summary>ROS has said the request is satisfied; "Resume" may be sent.</summary>
        public bool CanRespond { get; private set; }

        /// <summary>A new feedback request arrived (the plugin summons its tab).</summary>
        public event Action RequestReceived;
        /// <summary>Any state change.</summary>
        public event Action Changed;

        public FerlFeedback(IRosBus ros, string requestTopic, string satisfiedTopic, string responseTopic)
        {
            this.ros = ros ?? throw new ArgumentNullException(nameof(ros));
            RequestTopic = requestTopic;
            SatisfiedTopic = satisfiedTopic;
            ResponseTopic = responseTopic;
            onRequest = OnRequest;
            onSatisfied = OnSatisfied;
        }

        public void Start()
        {
            if (started) return;
            started = true;
            ros.Subscribe(RequestTopic, onRequest);
            ros.Subscribe(SatisfiedTopic, onSatisfied);
            ros.RegisterPublisher<BoolMsg>(ResponseTopic);
        }

        public void Dispose()
        {
            if (!started) return;
            started = false;
            ros.Unsubscribe(RequestTopic, onRequest);
            ros.Unsubscribe(SatisfiedTopic, onSatisfied);
            RequestPending = CanRespond = false;
        }

        /// <summary>"Resume": answer the request. Ignored unless it is pending and satisfied.</summary>
        public bool Respond()
        {
            if (!RequestPending || !CanRespond) return false;
            ros.Publish(ResponseTopic, new BoolMsg(true));
            RequestPending = CanRespond = false;
            Changed?.Invoke();
            return true;
        }

        /// <summary>"Close": drop the request without answering.</summary>
        public void Dismiss()
        {
            if (!RequestPending) return;
            RequestPending = CanRespond = false;
            Changed?.Invoke();
        }

        private void OnRequest(BoolMsg msg)
        {
            if (msg == null || !msg.data) return;
            RequestPending = true;
            CanRespond = false;
            RequestReceived?.Invoke();
            Changed?.Invoke();
        }

        private void OnSatisfied(BoolMsg msg)
        {
            if (msg == null || !msg.data) return;
            CanRespond = true;
            Changed?.Invoke();
        }
    }
}
