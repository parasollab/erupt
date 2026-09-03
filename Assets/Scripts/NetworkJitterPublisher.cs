using System;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using RosMessageTypes.BuiltinInterfaces;

// Measures network jitter with small fixed-rate pings, independent of the
// (variable-size) collision object traffic. A ROS node echoes each ping back;
// jitter is the RFC 3550 estimator over successive round-trip times.
public class NetworkJitterPublisher : MonoBehaviour
{
    public string pingTopic = "/latency_ping";
    public string pongTopic = "/latency_ping_pong";
    public string dataTopic = "/network_jitter";
    public float pingRateHz = 10f;

    private ROSConnection ros;
    private float lastPingTime;
    private uint sequence;
    private double lastRttMs = -1.0;
    private double jitterMs;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<HeaderMsg>(pingTopic);
        ros.RegisterPublisher<StringMsg>(dataTopic);
        ros.Subscribe<HeaderMsg>(pongTopic, OnPong);
    }

    void Update()
    {
        if (Time.realtimeSinceStartup - lastPingTime < 1f / pingRateHz)
            return;
        lastPingTime = Time.realtimeSinceStartup;

        long ticks = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var ping = new HeaderMsg
        {
            frame_id = (sequence++).ToString(),
            stamp = new TimeMsg
            {
                sec = (int)(ticks / TimeSpan.TicksPerSecond),
                nanosec = (uint)((ticks % TimeSpan.TicksPerSecond) * 100L)
            }
        };
        ros.Publish(pingTopic, ping);
    }

    private void OnPong(HeaderMsg msg)
    {
        if (msg.stamp.sec == 0 && msg.stamp.nanosec == 0)
            return;

        long sentTicks = (long)msg.stamp.sec * TimeSpan.TicksPerSecond
                       + (long)msg.stamp.nanosec / 100L;
        var sentTime = new DateTimeOffset(DateTimeOffset.UnixEpoch.Ticks + sentTicks, TimeSpan.Zero);
        double rttMs = (DateTimeOffset.UtcNow - sentTime).TotalMilliseconds;

        // RFC 3550 interarrival jitter estimator over successive RTT samples
        if (lastRttMs >= 0.0)
        {
            double delta = Math.Abs(rttMs - lastRttMs);
            jitterMs += (delta - jitterMs) / 16.0;
        }
        lastRttMs = rttMs;

        ros.Publish(dataTopic, new StringMsg($"{msg.frame_id},{rttMs:F3},{jitterMs:F3}"));
    }
}
