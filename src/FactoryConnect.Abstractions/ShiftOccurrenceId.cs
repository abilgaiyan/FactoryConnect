namespace FactoryConnect.Abstractions;

public sealed record ShiftOccurrenceId
{
    public ShiftOccurrenceId(
        SiteId siteId,
        ShiftScheduleAssignmentId shiftScheduleAssignmentId,
        ShiftId shiftId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        if (siteId.IsEmpty)
        {
            throw new ArgumentException(
                "Site identifier must not be empty.",
                nameof(siteId));
        }

        if (shiftScheduleAssignmentId.IsEmpty)
        {
            throw new ArgumentException(
                "Shift schedule assignment identifier must not be empty.",
                nameof(shiftScheduleAssignmentId));
        }

        if (shiftId.IsEmpty)
        {
            throw new ArgumentException(
                "Shift identifier must not be empty.",
                nameof(shiftId));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException(
                "Shift occurrence end must be after its start.",
                nameof(endsAtUtc));
        }

        SiteId = siteId;
        ShiftScheduleAssignmentId = shiftScheduleAssignmentId;
        ShiftId = shiftId;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    public SiteId SiteId { get; }

    public ShiftScheduleAssignmentId ShiftScheduleAssignmentId { get; }

    public ShiftId ShiftId { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset EndsAtUtc { get; }
}
