namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectSampleResult
{
    public required ulong InstanceId { get; init; }

    public required ulong FirstSequence { get; init; }

    public required ulong LastSequence { get; init; }

    public required ulong NextSequence { get; init; }

    public required IReadOnlyList<MtConnectSampleObservation> Observations
    {
        get;
        init;
    }
}
