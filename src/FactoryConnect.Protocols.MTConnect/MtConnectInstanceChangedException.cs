namespace FactoryConnect.Protocols.MTConnect;

public sealed class MtConnectInstanceChangedException :
    InvalidOperationException
{
    public ulong PreviousInstanceId { get; }

    public ulong CurrentInstanceId { get; }

    public ulong FirstSequence { get; }

    public MtConnectInstanceChangedException(
        ulong previousInstanceId,
        ulong currentInstanceId,
        ulong firstSequence)
        : base(
            $"MTConnect Agent instance changed from " +
            $"'{previousInstanceId}' to '{currentInstanceId}'.")
    {
        PreviousInstanceId = previousInstanceId;
        CurrentInstanceId = currentInstanceId;
        FirstSequence = firstSequence;
    }
}
