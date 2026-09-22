using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionConfigurationCompositionTests
{
    [Fact]
    public async Task SharedThreeShiftScheduleAndMultipleContextsComposeExactly()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            new ObservationStreamId(machineId, "activity"));

        await using var provider = services.BuildServiceProvider();

        var schedules = await provider.GetRequiredService<IShiftScheduleReader>()
            .ReadAssignmentsAsync(
                new SiteId("SITE-1"),
                new DateOnly(2026, 8, 27),
                new DateOnly(2026, 8, 29),
                CancellationToken.None);
        Assert.Equal(3, schedules.Count);
        Assert.Equal(1, schedules.Count(static assignment => assignment.IsOvernight));

        var contexts = await provider.GetRequiredService<IProductionContextReader>()
            .ReadAsync(
                machineId,
                new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
                CancellationToken.None);
        Assert.Equal(2, contexts.Count);
        Assert.Equal("ORDER-1", contexts[0].ProductionOrderId?.Value);
        Assert.Equal("OP-10", contexts[0].OperationId?.Value);
        Assert.Equal("PART-A", contexts[0].PartId?.Value);
        Assert.Equal("OPERATOR-1", contexts[0].OperatorId?.Value);
        Assert.True(contexts[0].EffectiveFrom < contexts[1].EffectiveFrom);
    }

    [Fact]
    public void LegacyMachineShiftIsRejected()
    {
        var values = BaseValues();
        values["ProductionProcessing:Machines:0:Shift:AssignmentId"] = "LEGACY";

        var exception = Assert.Throws<InvalidOperationException>(
            () => Compose(values));

        Assert.Contains("Legacy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlappingSharedSchedulesAreRejected()
    {
        var values = BaseValues();
        AddShift(values, 1, "SHIFT-2", "SHIFT-2", "13:00:00", "21:00:00");

        Assert.Throws<InvalidOperationException>(() => Compose(values));
    }

    [Fact]
    public void DuplicateSharedScheduleIdsAreRejected()
    {
        var values = BaseValues();
        AddShift(values, 1, "SHIFT-SCHEDULE-1", "SHIFT-2", "14:00:00", "22:00:00");

        Assert.Throws<InvalidOperationException>(() => Compose(values));
    }

    [Fact]
    public void EmptyActiveDaysAreRejected()
    {
        var values = BaseValues();
        for (var index = 0; index < 7; index++)
        {
            values.Remove($"ProductionProcessing:ShiftSchedules:0:ActiveDays:{index}");
        }

        Assert.Throws<InvalidOperationException>(() => Compose(values));
    }

    [Fact]
    public void InvalidTimeZoneIsRejected()
    {
        var values = BaseValues();
        values["ProductionProcessing:ShiftSchedules:0:TimeZoneId"] =
            "FactoryConnect/Invalid-Time-Zone";

        Assert.Throws<TimeZoneNotFoundException>(() => Compose(values));
    }

    [Fact]
    public void MachineWithoutApplicableSharedScheduleIsRejected()
    {
        var values = BaseValues();
        values["ProductionProcessing:ShiftSchedules:0:ProductionLineId"] = "OTHER-LINE";

        Assert.Throws<InvalidOperationException>(() => Compose(values));
    }

    private static ServiceProvider Compose(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            new ObservationStreamId(
                new MachineId(Guid.NewGuid()),
                "activity"));
        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration()
    {
        var values = BaseValues();
        AddShift(values, 1, "SHIFT-SCHEDULE-2", "SHIFT-2", "14:00:00", "22:00:00");
        AddShift(values, 2, "SHIFT-SCHEDULE-3", "SHIFT-3", "22:00:00", "06:00:00");
        values["ProductionProcessing:Contexts:0:ProductionOrderId"] = "ORDER-1";
        values["ProductionProcessing:Contexts:0:OperationId"] = "OP-10";
        values["ProductionProcessing:Contexts:0:PartId"] = "PART-A";
        values["ProductionProcessing:Contexts:0:OperatorId"] = "OPERATOR-1";
        values["ProductionProcessing:Contexts:0:EffectiveTo"] = "2026-08-28T00:00:00+00:00";
        values["ProductionProcessing:Contexts:1:AssignmentId"] = "CTX-2";
        values["ProductionProcessing:Contexts:1:EffectiveFrom"] = "2026-08-28T00:00:00+00:00";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Dictionary<string, string?> BaseValues()
    {
        var values = new Dictionary<string, string?>
        {
            ["ProductionProcessing:BatchSize"] = "100",
            ["ProductionProcessing:PollingInterval"] = "00:00:01",
            ["ProductionProcessing:CompanyId"] = "COMP-1",
            ["ProductionProcessing:SiteId"] = "SITE-1",
            ["ProductionProcessing:ProductionLineId"] = "LINE-1",
            ["ProductionProcessing:Contexts:0:AssignmentId"] = "CTX-1",
            ["ProductionProcessing:Contexts:0:EffectiveFrom"] = "2026-01-01T00:00:00+00:00",
            ["ProductionProcessing:QuantityStreamKey"] = "production-quantity",
            ["ProductionProcessing:PlannedProduction:AssignmentId"] = "POT-1",
            ["ProductionProcessing:PlannedProduction:TimeZoneId"] = "UTC",
            ["ProductionProcessing:PlannedProduction:StartsAtLocal"] = "06:00:00",
            ["ProductionProcessing:PlannedProduction:EndsAtLocal"] = "14:00:00",
            ["ProductionProcessing:PlannedProduction:EffectiveFrom"] = "2026-01-01",
        };
        AddShift(values, 0, "SHIFT-SCHEDULE-1", "SHIFT-1", "06:00:00", "14:00:00");
        return values;
    }

    private static void AddShift(
        Dictionary<string, string?> values,
        int index,
        string assignmentId,
        string shiftId,
        string startsAt,
        string endsAt)
    {
        var prefix = $"ProductionProcessing:ShiftSchedules:{index}";
        values[$"{prefix}:AssignmentId"] = assignmentId;
        values[$"{prefix}:CompanyId"] = "COMP-1";
        values[$"{prefix}:SiteId"] = "SITE-1";
        values[$"{prefix}:ProductionLineId"] = "LINE-1";
        values[$"{prefix}:ShiftId"] = shiftId;
        values[$"{prefix}:Name"] = shiftId;
        values[$"{prefix}:TimeZoneId"] = "UTC";
        values[$"{prefix}:StartsAtLocal"] = startsAt;
        values[$"{prefix}:EndsAtLocal"] = endsAt;
        values[$"{prefix}:EffectiveFrom"] = "2026-01-01";
        var days = Enum.GetNames<DayOfWeek>();
        for (var day = 0; day < days.Length; day++)
        {
            values[$"{prefix}:ActiveDays:{day}"] = days[day];
        }
    }
}
