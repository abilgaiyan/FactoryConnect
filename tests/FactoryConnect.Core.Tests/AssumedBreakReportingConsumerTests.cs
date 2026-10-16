using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class AssumedBreakReportingConsumerTests
{
    private static readonly SiteId Site = new("GG-SITE");
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private static readonly DateOnly Day = new(2026, 10, 16);
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletedShiftRetainsBoundPolicyAndRefusesUnalignedAvailability()
    {
        var result = AssumedBreakReportingConsumer.Evaluate(Request());
        Assert.Equal("GG003-DEFAULT-BREAK", result.PolicyId);
        Assert.Equal("demo-v1", result.PolicyVersion);
        Assert.Equal(TimeSpan.FromMinutes(450), result.PlannedTime!.PlannedProductionDuration);
        Assert.Equal("BreakAttributionNotEstablished", result.AvailabilityReason);
        Assert.Null(result.AvailabilityPercentage);
    }

    [Fact]
    public void OverlappingExclusionsAreUnionedOnce()
    {
        var exclusions = new[]
        {
            new AssumedBreakReportingExclusion("A", Start.AddHours(1), Start.AddMinutes(80), false),
            new AssumedBreakReportingExclusion("B", Start.AddMinutes(70), Start.AddMinutes(90), false),
        };
        var result = AssumedBreakReportingConsumer.Evaluate(Request(exclusions: exclusions));
        Assert.Equal(TimeSpan.FromMinutes(420), result.PlannedTime!.PlannedProductionDuration);
    }

    [Fact]
    public void ExistingBreakExclusionIsRejectedToAvoidDoubleDeduction()
    {
        var exclusions = new[]
        {
            new AssumedBreakReportingExclusion("Break", Start.AddHours(1), Start.AddMinutes(90), true),
        };
        var result = AssumedBreakReportingConsumer.Evaluate(Request(exclusions: exclusions));
        Assert.Equal("ExclusionAuthorityAmbiguous", result.AvailabilityReason);
        Assert.Null(result.PlannedTime);
    }

    [Fact]
    public void OngoingShiftCannotProduceNumericAvailability()
    {
        var result = AssumedBreakReportingConsumer.Evaluate(Request(completed: false));
        Assert.True(result.PlannedTime!.IsForecast);
        Assert.Null(result.AvailabilityPercentage);
        Assert.Equal("BreakTimingNotEstablished", result.AvailabilityReason);
    }

    [Fact]
    public void MissingEffectivePolicyIsUnavailable()
    {
        var policy = Policy() with { EffectiveFrom = Day.AddDays(1) };
        var result = AssumedBreakReportingConsumer.Evaluate(Request(policy: policy));
        Assert.Equal("PolicyNotEstablished", result.AvailabilityReason);
        Assert.Null(result.PlannedTime);
    }

    [Fact]
    public void NonAdmittedDemoPolicyIsUnavailable()
    {
        var result = AssumedBreakReportingConsumer.Evaluate(
            Request(policy: Policy() with { IsDemoProvisional = false }));
        Assert.Equal("PolicyNotEstablished", result.AvailabilityReason);
    }

    [Fact]
    public void IdenticalEvidenceAndPolicyReplayIsEqual()
    {
        var request = Request();
        Assert.Equal(
            AssumedBreakReportingConsumer.Evaluate(request),
            AssumedBreakReportingConsumer.Evaluate(request));
    }

    private static AssumedBreakReportingPolicy Policy() =>
        new("GG003-DEFAULT-BREAK", "demo-v1", Site, Machine,
            Day, null, TimeSpan.FromMinutes(30),
            "Gajra customer-relayed demo instruction 2026-10-16", true);

    private static AssumedBreakReportingRequest Request(
        AssumedBreakReportingPolicy? policy = null,
        IReadOnlyList<AssumedBreakReportingExclusion>? exclusions = null,
        bool completed = true) =>
        new(policy ?? Policy(), Site, Machine, Day, Start, Start.AddHours(8),
            completed, true, TimeSpan.FromHours(6), exclusions ?? []);
}
