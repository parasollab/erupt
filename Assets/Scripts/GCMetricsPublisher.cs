using System;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

// Publishes garbage collector activity once per interval:
// collection counts per generation, total GC time and worst single-frame
// GC pause inside the interval (from the "GC.Collect" profiler marker,
// available in the editor and development builds), and managed heap size.
public class GCMetricsPublisher : MonoBehaviour
{
    public string topic = "/gc_metrics";
    public float publishIntervalSeconds = 1f;

    private ROSConnection ros;
    private Recorder gcRecorder;
    private int lastGen0, lastGen1, lastGen2;
    private double gcTimeMsAccum;
    private double maxFrameGcMs;
    private int frameCount;
    private float elapsed;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>(topic);

        gcRecorder = Recorder.Get("GC.Collect");
        gcRecorder.enabled = true;
        if (!gcRecorder.isValid)
            Debug.LogWarning("[GCMetrics] 'GC.Collect' profiler marker unavailable (release build?) — gc_time columns will be 0");

        lastGen0 = GC.CollectionCount(0);
        lastGen1 = GC.CollectionCount(1);
        lastGen2 = GC.CollectionCount(2);
    }

    void Update()
    {
        frameCount++;
        if (gcRecorder != null && gcRecorder.isValid)
        {
            // Time spent in GC during the previous frame
            double frameGcMs = gcRecorder.elapsedNanoseconds / 1e6;
            gcTimeMsAccum += frameGcMs;
            if (frameGcMs > maxFrameGcMs)
                maxFrameGcMs = frameGcMs;
        }

        elapsed += Time.unscaledDeltaTime;
        if (elapsed < publishIntervalSeconds)
            return;

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long totalMemory = GC.GetTotalMemory(false);

        ros.Publish(topic, new StringMsg(
            $"{gen0 - lastGen0},{gen1 - lastGen1},{gen2 - lastGen2}," +
            $"{gcTimeMsAccum:F3},{maxFrameGcMs:F3},{totalMemory},{frameCount}"));

        lastGen0 = gen0;
        lastGen1 = gen1;
        lastGen2 = gen2;
        gcTimeMsAccum = 0.0;
        maxFrameGcMs = 0.0;
        frameCount = 0;
        elapsed = 0f;
    }
}
