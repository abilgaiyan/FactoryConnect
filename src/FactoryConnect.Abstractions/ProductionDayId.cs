namespace FactoryConnect.Abstractions;

public sealed record ProductionDayId
{
    public ProductionDayId(
        SiteId siteId,
        DateOnly businessDate)
    {
        if (siteId.IsEmpty)
        {
            throw new ArgumentException(
                "Site identifier must not be empty.",
                nameof(siteId));
        }

        if (businessDate == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(businessDate),
                "Production business date must not be the default date.");
        }

        SiteId = siteId;
        BusinessDate = businessDate;
    }

    public SiteId SiteId { get; }

    public DateOnly BusinessDate { get; }
}
