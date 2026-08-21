namespace FactoryConnect.Abstractions;

public static class CanonicalSignalKeys
{
    public const string Running = "state.running";
    public const string Idle = "state.idle";
    public const string Fault = "state.fault";
    public const string PowerCurrent = "energy.current";
    public const string PartCount = "production.part-count";
    public const string CycleCount = "production.cycle-count";
    public const string SpindleSpeed = "process.spindle-speed";
    public const string FeedRate = "process.feed-rate";
    public const string Alarm = "condition.alarm";
}
