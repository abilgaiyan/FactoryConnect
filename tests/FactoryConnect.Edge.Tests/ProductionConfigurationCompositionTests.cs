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
        Assert.Null(contexts[1].EffectiveTo);
    }


    [Fact]
    public void SiteLevelScheduleServesLineScopedMachine()
    {
        var values = BaseValues();
        values.Remove("ProductionProcessing:ShiftSchedules:0:ProductionLineId");

        using var provider = Compose(values);
        Assert.NotNull(provider.GetRequiredService<IShiftScheduleReader>());
    }

    [Fact]
    public void LegacyScalarContextIsRejectedAlongsideContextsCollection()
    {
        var values = BaseValues();
        values["ProductionProcessing:ContextAssignmentId"] = "LEGACY-CTX";
        values["ProductionProcessing:ContextEffectiveFromUtc"] =
            "2026-01-01T00:00:00+00:00";

        var exception = Assert.Throws<InvalidOperationException>(
            () => Compose(values));

        Assert.Contains("Legacy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateContextIdsAreRejected()
    {
        var values = BaseValues();
        values["ProductionProcessing:Contexts:0:EffectiveTo"] =
            "2026-08-28T00:00:00+00:00";
        values["ProductionProcessing:Contexts:1:AssignmentId"] = "CTX-1";
        values["ProductionProcessing:Contexts:1:EffectiveFrom"] =
            "2026-08-28T00:00:00+00:00";

        Assert.Throws<InvalidOperationException>(() => Compose(values));
    }

    [Fact]
    public void OverlappingContextRangesAreRejected()
    {
        var values = BaseValues();
        values["ProductionProcessing:Contexts:0:EffectiveTo"] =
            "2026-08-29T00:00:00+00:00";
        values["ProductionProcessing:Contexts:1:AssignmentId"] = "CTX-2";
        values["ProductionProcessing:Contexts:1:EffectiveFrom"] =
            "2026-08-28T00:00:00+00:00";

        Assert.Throws<InvalidOperationException>(() => Compose(values));
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

    [Theory]
    [InlineData("06:00:00", "14:00:00", "10:00:00", "10:30:00", 0, "UTC", 0)]
    [InlineData("22:00:00", "06:00:00", "02:00:00", "02:30:00", 1, "UTC", 0)]
    [InlineData("22:00:00", "06:00:00", "23:45:00", "00:15:00", 0, "UTC", 0)]
    [InlineData("06:00:00", "14:00:00", "10:00:00", "10:30:00", 0, "Asia/Kolkata", 330)]
    [InlineData("22:00:00", "06:00:00", "02:00:00", "02:30:00", 1, "Asia/Kolkata", 330)]
    [InlineData("22:00:00", "06:00:00", "23:45:00", "00:15:00", 0, "Asia/Kolkata", 330)]
    public async Task ConfiguredBreakIsExcludedFromPlannedTime(
        string startsAt, string endsAt, string breakStart, string breakEnd, int breakDayOffset,
        string timeZoneId, int offsetMinutes)
    {
        var values = BaseValues();
        const string prefix = "ProductionProcessing:PlannedProduction";
        values[$"{prefix}:TimeZoneId"] = timeZoneId;
        values[$"{prefix}:StartsAtLocal"] = startsAt;
        values[$"{prefix}:EndsAtLocal"] = endsAt;
        values[$"{prefix}:BreakWindows:0:StartsAtLocal"] = breakStart;
        values[$"{prefix}:BreakWindows:0:EndsAtLocal"] = breakEnd;
        using var provider = Compose(values);
        var date = new DateOnly(2026, 8, 27);
        var reader = provider.GetRequiredService<IPlannedProductionScheduleReader>();
        var assignments = await reader.ReadAssignmentsAsync(
            new SiteId("SITE-1"), date, date.AddDays(1), CancellationToken.None);
        Assert.Single(Assert.Single(assignments).BreakWindows);
        var intervals = await new PlannedProductionIntervalResolver(reader).ResolveAsync(
            new SiteId("SITE-1"), new ProductionLineId("LINE-1"),
            date, date.AddDays(1), CancellationToken.None);
        Assert.Equal(2, intervals.Count);
        Assert.Equal(TimeSpan.FromHours(7.5),
            TimeSpan.FromTicks(intervals.Sum(static interval =>
                (interval.EndsAtUtc - interval.StartsAtUtc).Ticks)));
        var expectedBreakStart = new DateTimeOffset(
            date.AddDays(breakDayOffset).ToDateTime(TimeOnly.Parse(breakStart, System.Globalization.CultureInfo.InvariantCulture)),
            TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
        Assert.Equal(expectedBreakStart, intervals[0].EndsAtUtc);
        Assert.Equal(expectedBreakStart.AddMinutes(30), intervals[1].StartsAtUtc);
        Assert.All(intervals, interval => Assert.Equal(date, interval.FactoryDate));
    }

    [Theory]
    [InlineData("10:00:00", "10:00:00")]
    [InlineData("05:00:00", "05:30:00")]
    [InlineData("13:45:00", "14:15:00")]
    [InlineData("invalid", "10:30:00")]
    [InlineData("10:00:00", null)]
    public void InvalidConfiguredBreakIsRejected(string startsAt, string? endsAt)
    {
        var values = BaseValues();
        values["ProductionProcessing:PlannedProduction:BreakWindows:0:StartsAtLocal"] = startsAt;
        values["ProductionProcessing:PlannedProduction:BreakWindows:0:EndsAtLocal"] = endsAt;
        Assert.ThrowsAny<Exception>(() => Compose(values));
    }

    [Theory]
    [InlineData("10:15:00", false)]
    [InlineData("10:30:00", true)]
    public void ConfiguredBreakOverlapIsRejectedButAdjacentBreaksAreAllowed(
        string secondStart, bool accepted)
    {
        var values = BaseValues();
        values["ProductionProcessing:PlannedProduction:BreakWindows:0:StartsAtLocal"] = "10:00:00";
        values["ProductionProcessing:PlannedProduction:BreakWindows:0:EndsAtLocal"] = "10:30:00";
        values["ProductionProcessing:PlannedProduction:BreakWindows:1:StartsAtLocal"] = secondStart;
        values["ProductionProcessing:PlannedProduction:BreakWindows:1:EndsAtLocal"] = "11:00:00";
        if (accepted)
        {
            using var provider = Compose(values);
            Assert.NotNull(provider.GetRequiredService<IPlannedProductionScheduleReader>());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Compose(values));
        }
    }

    [Theory]
    [InlineData("10:00–10:30")]
    [InlineData("")]
    public void ScalarBreakWindowsIsRejectedEvenWithValidChildren(string scalar)
    {
        var values = BaseValues();
        const string prefix = "ProductionProcessing:PlannedProduction:BreakWindows";
        values[prefix] = scalar;
        values[$"{prefix}:0:StartsAtLocal"] = "10:00:00";
        values[$"{prefix}:0:EndsAtLocal"] = "10:30:00";
        var exception = Assert.Throws<InvalidOperationException>(() => Compose(values));
        Assert.Contains(prefix, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"10:00–10:30\"", false)]
    [InlineData("true", false)]
    [InlineData("30", false)]
    [InlineData("\"\"", false)]
    [InlineData("[]", false)]
    [InlineData(null, true)]
    public async Task JsonBreakWindowsShapeIsValidated(string? jsonValue, bool accepted)
    {
        var json = jsonValue is null ? "{}" :
            "{\"ProductionProcessing\":{\"PlannedProduction\":{\"BreakWindows\":" + jsonValue + "}}}";
        if (!accepted)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Compose(BaseValues(), json));
            Assert.Contains("BreakWindows", exception.Message, StringComparison.Ordinal);
            return;
        }

        using var provider = Compose(BaseValues(), json);
        var date = new DateOnly(2026, 8, 27);
        var reader = provider.GetRequiredService<IPlannedProductionScheduleReader>();
        var assignments = await reader.ReadAssignmentsAsync(
            new SiteId("SITE-1"), date, date.AddDays(1), CancellationToken.None);
        Assert.Empty(Assert.Single(assignments).BreakWindows);
        var intervals = await new PlannedProductionIntervalResolver(reader).ResolveAsync(
            new SiteId("SITE-1"), new ProductionLineId("LINE-1"),
            date, date.AddDays(1), CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(8), Assert.Single(intervals).Duration);
    }

    [Fact]
    public async Task MultiWindowBinderResolvesFollowingMorningUnderOwningDate()
    {
        var values = BaseValues();
        const string prefix = "ProductionProcessing:PlannedProduction";
        values.Remove($"{prefix}:StartsAtLocal");
        values.Remove($"{prefix}:EndsAtLocal");
        values[$"{prefix}:PlannedWindows:0:StartsAtLocal"] = "00:00:00";
        values[$"{prefix}:PlannedWindows:0:EndsAtLocal"] = "07:30:00";
        values[$"{prefix}:PlannedWindows:0:StartDayOffset"] = "1";
        values[$"{prefix}:BreakWindows:0:StartsAtLocal"] = "03:30:00";
        values[$"{prefix}:BreakWindows:0:EndsAtLocal"] = "04:00:00";
        values[$"{prefix}:BreakWindows:0:StartDayOffset"] = "1";
        values["ProductionProcessing:ShiftSchedules:0:StartsAtLocal"] = "00:00:00";
        values["ProductionProcessing:ShiftSchedules:0:EndsAtLocal"] = "07:30:00";
        values["ProductionProcessing:ShiftSchedules:0:StartDayOffset"] = "1";
        using var provider = Compose(values);
        var day = new DateOnly(2026, 10, 22);
        var planned = await provider.GetRequiredService<PlannedProductionIntervalResolver>()
            .ResolveAsync(new SiteId("SITE-1"), new ProductionLineId("LINE-1"), day, day.AddDays(1), CancellationToken.None);
        Assert.Equal(420, planned.Sum(item => item.Duration.TotalMinutes));
        Assert.All(planned, item => Assert.Equal(day, item.FactoryDate));
        var shifts = await provider.GetRequiredService<ShiftOccurrenceResolver>()
            .ResolveAsync(new SiteId("SITE-1"), new ProductionLineId("LINE-1"), day, day.AddDays(1), CancellationToken.None);
        Assert.Equal(day, Assert.Single(shifts).FactoryDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero), shifts[0].StartsAtUtc);
    }

    [Fact]
    public void MixedScalarAndCollectionPlannedConfigurationFails()
    {
        var values = BaseValues();
        values["ProductionProcessing:PlannedProduction:PlannedWindows:0:StartsAtLocal"] = "07:30:00";
        values["ProductionProcessing:PlannedProduction:PlannedWindows:0:EndsAtLocal"] = "16:00:00";
        Assert.Throws<InvalidOperationException>(() => Compose(values));
    }

    [Fact]
    public async Task RetainedSchedulesPreserveHistoryAcrossBoundaryAndRestart()
    {
        var values = RetainedPlannedValues();
        for (var restart = 0; restart < 2; restart++)
        {
            using var provider = Compose(values);
            var reader = provider.GetRequiredService<IPlannedProductionScheduleReader>();
            var assignments = await reader.ReadAssignmentsAsync(new SiteId("SITE-1"),
                new DateOnly(2026, 10, 22), new DateOnly(2026, 11, 2), CancellationToken.None);
            Assert.Equal(2, assignments.Count);
            Assert.Equal("OLD", assignments[0].Id.Value);
            Assert.Equal("NEW", assignments[1].Id.Value);
            Assert.Empty(assignments[0].BreakWindows);
            Assert.Equal(3, assignments[1].BreakWindows.Count);
            var resolver = provider.GetRequiredService<PlannedProductionIntervalResolver>();
            var days = await resolver.ResolveAsync(new SiteId("SITE-1"), new ProductionLineId("LINE-1"),
                new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 31), CancellationToken.None);
            var oldDay = days.Where(static item => item.FactoryDate == new DateOnly(2026, 10, 29)).ToArray();
            var newDay = days.Where(static item => item.FactoryDate == new DateOnly(2026, 10, 30)).ToArray();
            Assert.All(oldDay, item => Assert.Equal("OLD", item.SourceAssignmentId.Value));
            Assert.All(newDay, item => Assert.Equal("NEW", item.SourceAssignmentId.Value));
            Assert.Equal(1440d, oldDay.Sum(static item => (item.EndsAtUtc - item.StartsAtUtc).TotalMinutes));
            Assert.Equal(1350d, newDay.Sum(static item => (item.EndsAtUtc - item.StartsAtUtc).TotalMinutes));
            Assert.Equal(oldDay.Max(static item => item.EndsAtUtc), newDay.Min(static item => item.StartsAtUtc));
            Assert.Equal(2, oldDay.Length);
            Assert.Equal(DateTimeOffset.Parse("2026-10-29T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture), oldDay[0].StartsAtUtc);
            var shiftBounds = new[] { ("2026-10-30T02:00:00Z", "2026-10-30T10:30:00Z", 480d),
                ("2026-10-30T10:30:00Z", "2026-10-30T18:30:00Z", 450d),
                ("2026-10-30T18:30:00Z", "2026-10-31T02:00:00Z", 420d) };
            foreach (var (start, end, expected) in shiftBounds)
            {
                var from = DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
                var to = DateTimeOffset.Parse(end, System.Globalization.CultureInfo.InvariantCulture);
                var minutes = newDay.Sum(item => Math.Max(0, (Math.Min(to.UtcTicks, item.EndsAtUtc.UtcTicks)
                    - Math.Max(from.UtcTicks, item.StartsAtUtc.UtcTicks)) / (double)TimeSpan.TicksPerMinute));
                Assert.Equal(expected, minutes);
            }

            Assert.DoesNotContain(newDay, item => item.StartsAtUtc < DateTimeOffset.Parse("2026-10-30T06:30:00Z", System.Globalization.CultureInfo.InvariantCulture)
                && item.EndsAtUtc > DateTimeOffset.Parse("2026-10-30T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            // The third break is physically on the following calendar date.
            Assert.DoesNotContain(newDay, item => item.StartsAtUtc < DateTimeOffset.Parse("2026-10-30T22:30:00Z", System.Globalization.CultureInfo.InvariantCulture)
                && item.EndsAtUtc > DateTimeOffset.Parse("2026-10-30T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var next = await resolver.ResolveAsync(new SiteId("SITE-1"), new ProductionLineId("LINE-1"),
                new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1), CancellationToken.None);
            Assert.Equal(newDay.Max(static item => item.EndsAtUtc), next.Min(static item => item.StartsAtUtc));
        }
    }

    [Theory]
    [InlineData("2026-10-29")]
    [InlineData("2026-10-31")]
    [InlineData(null)]
    public void RetainedReplacementGapOrOverlapFailsAtComposition(string? end)
    {
        var values = RetainedPlannedValues();
        values["ProductionProcessing:PlannedProductionAssignments:1:EffectiveTo"] = end;
        Assert.Throws<InvalidOperationException>(() => Compose(values));
    }

    [Fact]
    public void RetainedSchedulesRejectDualShapesDuplicateIdsAndPhysicalCollision()
    {
        var both = RetainedPlannedValues();
        both["ProductionProcessing:PlannedProduction:AssignmentId"] = "LEGACY";
        Assert.Throws<InvalidOperationException>(() => Compose(both));
        var duplicate = RetainedPlannedValues();
        duplicate["ProductionProcessing:PlannedProductionAssignments:0:AssignmentId"] = "OLD";
        Assert.Throws<InvalidOperationException>(() => Compose(duplicate));
        var collision = RetainedPlannedValues();
        collision["ProductionProcessing:PlannedProductionAssignments:1:PlannedWindows:1:EndsAtLocal"] = "08:00:00";
        Assert.Throws<InvalidOperationException>(() => Compose(collision));
        var ambiguous = RetainedPlannedValues();
        ambiguous["ProductionProcessing:PlannedProductionAssignments:0:BreakWindows:2:StartDayOffset"] = "0";
        Assert.Throws<InvalidOperationException>(() => Compose(ambiguous));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"scalar\"")]
    [InlineData("{\"named\":{}}")]
    public void InvalidRetainedCollectionShapeFails(string shape)
    {
        var values = BaseValues();
        foreach (var key in values.Keys.Where(static key => key.StartsWith("ProductionProcessing:PlannedProduction:", StringComparison.Ordinal)).ToArray()) values.Remove(key);
        Assert.Throws<InvalidOperationException>(() => Compose(values,
            "{\"ProductionProcessing\":{\"PlannedProductionAssignments\":" + shape + "}}"));
    }

    [Fact]
    public async Task MultiMachineCollectionLeavesOtherLineAndAdmissionUntouched()
    {
        var values = RetainedPlannedValues();
        var cnc = new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
        var moxa = new MachineId(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0"));
        var root = "ProductionProcessing:";
        var machineValues = values.Where(pair => !pair.Key.StartsWith(root + "ShiftSchedules:", StringComparison.Ordinal)
            && pair.Key != root + "BatchSize" && pair.Key != root + "PollingInterval").ToArray();
        foreach (var (key, value) in machineValues)
        {
            values.Remove(key);
            values[root + "Machines:0:" + key[root.Length..]] = value;
        }
        values[root + "Machines:0:MachineId"] = cnc.ToString();
        values[root + "Machines:0:ActivityStreamKey"] = "cnc";
        values[root + "Machines:0:ProductionStartsAt"] = "2026-10-22T02:00:00Z";
        foreach (var (key, value) in BaseValues().Where(pair => !pair.Key.StartsWith(root + "ShiftSchedules:", StringComparison.Ordinal)
            && pair.Key != root + "BatchSize" && pair.Key != root + "PollingInterval"))
        {
            values[root + "Machines:1:" + key[root.Length..]] = value;
        }
        values[root + "Machines:1:MachineId"] = moxa.ToString();
        values[root + "Machines:1:ActivityStreamKey"] = "moxa";
        values[root + "Machines:1:ProductionLineId"] = "LINE-2";
        values[root + "Machines:1:Contexts:0:AssignmentId"] = "MOXA-CTX";
        values[root + "Machines:1:PlannedProduction:AssignmentId"] = "MOXA-PLANNED";
        values[root + "Machines:1:ProductionStartsAt"] = "2026-10-25T02:00:00Z";
        // A site-wide shift serves both distinct line scopes.
        values.Remove(root + "ShiftSchedules:0:ProductionLineId");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectProductionMetricInputs(configuration,
            [new ObservationStreamId(cnc, "cnc"), new ObservationStreamId(moxa, "moxa")]);
        await using var provider = services.BuildServiceProvider();
        var planned = await provider.GetRequiredService<IPlannedProductionScheduleReader>().ReadAssignmentsAsync(
            new SiteId("SITE-1"), new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 31), CancellationToken.None);
        var untouched = Assert.Single(planned, static assignment => assignment.ProductionLineId == new ProductionLineId("LINE-2"));
        Assert.Equal("MOXA-PLANNED", untouched.Id.Value);
        Assert.Empty(untouched.BreakWindows);
        Assert.Equal("2026-10-22T02:00:00Z", configuration[root + "Machines:0:ProductionStartsAt"]);
        Assert.Equal("2026-10-25T02:00:00Z", configuration[root + "Machines:1:ProductionStartsAt"]);
        values[root + "Machines:1:ProductionLineId"] = "LINE-1";
        var conflictingServices = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => conflictingServices.AddFactoryConnectProductionMetricInputs(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            [new ObservationStreamId(cnc, "cnc"), new ObservationStreamId(moxa, "moxa")]));
        values[root + "Machines:1:MachineId"] = cnc.ToString();
        Assert.Throws<InvalidOperationException>(() => conflictingServices.AddFactoryConnectProductionMetricInputs(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            [new ObservationStreamId(cnc, "cnc"), new ObservationStreamId(moxa, "moxa")]));
    }

    private static Dictionary<string, string?> RetainedPlannedValues()
    {
        var values = BaseValues();
        foreach (var key in values.Keys.Where(static key => key.StartsWith("ProductionProcessing:PlannedProduction:", StringComparison.Ordinal)).ToArray()) values.Remove(key);
        // Reversed input proves effective-date ordering is independent of array order.
        foreach (var index in new[] { 0, 1 })
        {
            var prefix = $"ProductionProcessing:PlannedProductionAssignments:{index}";
            values[$"{prefix}:AssignmentId"] = index == 1 ? "OLD" : "NEW";
            values[$"{prefix}:TimeZoneId"] = "India Standard Time";
            values[$"{prefix}:EffectiveFrom"] = index == 1 ? "2026-10-22" : "2026-10-30";
            if (index == 1) values[$"{prefix}:EffectiveTo"] = "2026-10-30";
            values[$"{prefix}:PlannedWindows:0:StartsAtLocal"] = "07:30:00";
            values[$"{prefix}:PlannedWindows:0:EndsAtLocal"] = "00:00:00";
            values[$"{prefix}:PlannedWindows:0:StartDayOffset"] = "0";
            values[$"{prefix}:PlannedWindows:1:StartsAtLocal"] = "00:00:00";
            values[$"{prefix}:PlannedWindows:1:EndsAtLocal"] = "07:30:00";
            values[$"{prefix}:PlannedWindows:1:StartDayOffset"] = "1";
        }
        var breaks = new[] { ("11:30:00", "12:00:00", "0"), ("20:00:00", "20:30:00", "0"), ("03:30:00", "04:00:00", "1") };
        for (var index = 0; index < breaks.Length; index++)
        {
            var prefix = $"ProductionProcessing:PlannedProductionAssignments:0:BreakWindows:{index}";
            values[$"{prefix}:StartsAtLocal"] = breaks[index].Item1;
            values[$"{prefix}:EndsAtLocal"] = breaks[index].Item2;
            values[$"{prefix}:StartDayOffset"] = breaks[index].Item3;
        }
        return values;
    }

    private static ServiceProvider Compose(Dictionary<string, string?> values, string? json = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(values);
        if (json is not null)
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            builder.AddJsonStream(stream);
            return ComposeConfiguration(builder.Build());
        }

        return ComposeConfiguration(builder.Build());
    }

    private static ServiceProvider ComposeConfiguration(IConfiguration configuration)
    {
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
