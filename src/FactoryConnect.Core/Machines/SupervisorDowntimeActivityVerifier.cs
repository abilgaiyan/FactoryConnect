using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>Fail-closed durable activity lookup for local downtime explanations.</summary>
public static class SupervisorDowntimeActivityVerifier
{
    public static async Task<bool> VerifyAsync(
        IProductionContextActivityReader reader,
        ObservationStreamId stream,
        ObservationProcessorId processor,
        ObservationPosition? evaluatedThrough,
        Guid machineId, long position, DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default)
    {
        if (evaluatedThrough is null || position <= 0 || (ulong)position > evaluatedThrough.Value)
            return false;

        const int pageSize = 1000;
        const int maximum = 100000;
        ObservationPosition? after = null;
        var count = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await reader.ReadAsync(stream, after, pageSize, cancellationToken);
            if (page.Count > pageSize)
                throw new InvalidDataException("Activity reader exceeded its page size.");
            var finished = page.Count < pageSize;
            foreach (var item in page)
            {
                if (item.StreamId != stream || item.ProcessorId != processor ||
                    item.Position <= after)
                    throw new InvalidDataException("Activity reader returned invalid identity or ordering.");
                after = item.Position;
                if (after > evaluatedThrough)
                    return false;
                if (++count > maximum)
                    return false;
                if (item.Position.Value == (ulong)position)
                    return SupervisorDowntimeExplanationEligibility.Matches(item, machineId,
                        processor.Value, position, startsAtUtc, endsAtUtc);
                if (item.Position.Value > (ulong)position)
                    return false;
            }
            if (finished)
                return false;
        }
    }
}
