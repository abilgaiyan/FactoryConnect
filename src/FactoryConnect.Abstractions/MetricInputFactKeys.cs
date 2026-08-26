namespace FactoryConnect.Abstractions;

public static class MetricInputFactKeys
{
    public const string ScheduledDuration = "duration.scheduled";
    public const string PlannedProductionDuration = "duration.planned-production";
    public const string RunningDuration = "duration.running";
    public const string IdleDuration = "duration.idle";
    public const string StoppedDuration = "duration.stopped";
    public const string AlarmDuration = "duration.alarm";
    public const string OfflineDuration = "duration.offline";
    public const string PartCountIncrement = "quantity.part-count-increment";
    public const string GoodQuantity = "quantity.good";
    public const string RejectedQuantity = "quantity.rejected";
}
