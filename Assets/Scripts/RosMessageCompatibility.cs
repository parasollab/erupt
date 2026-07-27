using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Std;

public static class RosMessageCompatibility
{
    public static TimeMsg CreateTime(float time)
    {
        int seconds = (int)time;
        uint nanoseconds = (uint)((time - seconds) * 1e9f);
        return CreateTime(seconds, nanoseconds);
    }

    public static TimeMsg CreateTime(long seconds, uint nanoseconds)
    {
#if ROS2
        return new TimeMsg((int)seconds, nanoseconds);
#else
        return new TimeMsg((uint)seconds, nanoseconds);
#endif
    }

    public static HeaderMsg CreateHeader(string frameId)
    {
        return CreateHeader(CreateTime(0, 0), frameId);
    }

    public static HeaderMsg CreateHeader(TimeMsg stamp, string frameId)
    {
#if ROS2
        return new HeaderMsg(stamp, frameId);
#else
        return new HeaderMsg(0, stamp, frameId);
#endif
    }

    public static DurationMsg CreateDuration(int seconds, uint nanoseconds)
    {
#if ROS2
        return new DurationMsg(seconds, nanoseconds);
#else
        return new DurationMsg(seconds, (int)nanoseconds);
#endif
    }
}
