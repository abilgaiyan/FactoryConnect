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


    [Fact]
    public void TrulyAbsentPolicyReturnsUnavailable()
    {
        var request = Request() with
        {
            Policy = null!,
            CandidatePolicies = [],
        };
        var result = AssumedBreakReportingConsumer.Evaluate(request);
        Assert.Equal("PolicyNotEstablished", result.AvailabilityReason);
        Assert.Null(result.PlannedTime);
        Assert.Equal(Machine, result.MachineId);
    }

    [Fact]
    public void CompetingEffectivePoliciesAreRejectedRegardlessOfOrder()
    {
        var request = Request() with
        {
            CandidatePolicies = [Policy(), Policy() with { Id = "COMPETITOR" }],
        };
        Assert.Equal("PolicyNotEstablished",
            AssumedBreakReportingConsumer.Evaluate(request).AvailabilityReason);
    }

    [Fact]
    public void WrongScopePolicyIsNotSelected()
    {
        var request = Request() with
        {
            CandidatePolicies = [Policy() with { SiteId = new SiteId("WRONG-SITE") }],
        };
        Assert.Equal("PolicyNotEstablished",
            AssumedBreakReportingConsumer.Evaluate(request).AvailabilityReason);
    }

    [Fact]
    public void EffectiveEndIsExclusive()
    {
        var request = Request() with
        {
            CandidatePolicies = [Policy() with { EffectiveTo = Day }],
        };
        Assert.Equal("PolicyNotEstablished",
            AssumedBreakReportingConsumer.Evaluate(request).AvailabilityReason);
    }

    [Fact]
    public void EqualTotalsWithDifferentEvidenceHaveDifferentBindings()
    {
        var first = Request(exclusions:
            [new AssumedBreakReportingExclusion("E1", Start.AddHours(1), Start.AddMinutes(90), false)]);
        var second = Request(exclusions:
            [new AssumedBreakReportingExclusion("E2", Start.AddHours(1), Start.AddMinutes(90), false)]);
        var a = AssumedBreakReportingConsumer.Evaluate(first);
        var b = AssumedBreakReportingConsumer.Evaluate(second);
        Assert.Equal(a.PlannedTime!.PlannedProductionDuration, b.PlannedTime!.PlannedProductionDuration);
        Assert.NotEqual(a.ExclusionEvidenceFingerprint, b.ExclusionEvidenceFingerprint);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RunningEvidenceReferencesDistinguishEqualDurations()
    {
        var a = AssumedBreakReportingConsumer.Evaluate(Request() with { RunningEvidenceReference = "RUN-1" });
        var b = AssumedBreakReportingConsumer.Evaluate(Request() with { RunningEvidenceReference = "RUN-2" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ScopeBoundariesDistinguishDifferentShifts()
    {
        var a = AssumedBreakReportingConsumer.Evaluate(Request());
        var b = AssumedBreakReportingConsumer.Evaluate(Request() with
        {
            ShiftStartsAtUtc = Start.AddHours(8),
            ShiftEndsAtUtc = Start.AddHours(16),
        });
        Assert.NotEqual(a, b);
    }


    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingOrBlankRunningEvidenceDoesNotExposeMeasuredDuration(string? reference)
    {
        var result = AssumedBreakReportingConsumer.Evaluate(Request() with
        {
            RunningEvidenceReference = reference,
        });
        Assert.Equal("RunningEvidenceNotEstablished", result.AvailabilityReason);
        Assert.Equal(TimeSpan.FromMinutes(450), result.PlannedTime!.PlannedProductionDuration);
        Assert.Null(result.AvailabilityPercentage);
        Assert.Null(result.ObservedRunningDuration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingEvidenceDefinitionsAreRejectedInBothOrders(bool reverse)
    {
        var first = new AssumedBreakReportingExclusion("E1", Start.AddHours(1), Start.AddMinutes(90), false);
        var conflict = first with { IsBreak = true };
        AssumedBreakReportingExclusion[] exclusions = reverse ? [conflict, first] : [first, conflict];

        var result = AssumedBreakReportingConsumer.Evaluate(Request(exclusions: exclusions));
        Assert.Equal("ExclusionAuthorityAmbiguous", result.AvailabilityReason);
        Assert.Null(result.PlannedTime);
        Assert.Null(result.AvailabilityPercentage);
    }

    [Fact]
    public void ConflictingClassificationsDoNotDependOnInputOrder()
    {
        var first = new AssumedBreakReportingExclusion("E1", Start.AddHours(1), Start.AddMinutes(90), false);
        var conflict = first with { IsBreak = true };
        var forward = AssumedBreakReportingConsumer.Evaluate(Request(exclusions: [first, conflict]));
        var reverse = AssumedBreakReportingConsumer.Evaluate(Request(exclusions: [conflict, first]));
        Assert.Equal(forward.ExclusionEvidenceFingerprint, reverse.ExclusionEvidenceFingerprint);
        Assert.Equal(forward.AvailabilityReason, reverse.AvailabilityReason);
    }


    [Fact]
    public void EvidenceBackedZeroRunningRemainsNumericZero()
    {
        var result = AssumedBreakReportingConsumer.Evaluate(Request() with
        {
            ObservedRunningDuration = TimeSpan.Zero,
            RunningEvidenceReference = "MEASURED-RUNNING-ZERO",
        });

        Assert.Equal(TimeSpan.Zero, result.ObservedRunningDuration);
        Assert.Equal(TimeSpan.FromMinutes(450), result.PlannedTime!.PlannedProductionDuration);
        Assert.Equal("BreakAttributionNotEstablished", result.AvailabilityReason);
        Assert.Null(result.AvailabilityPercentage);
    }

    [Fact]
    public void MissingRunningDoesNotOverrideInvalidPolicy()
    {
        var result = AssumedBreakReportingConsumer.Evaluate(Request() with
        {
            CandidatePolicies = [],
            RunningEvidenceReference = null,
        });

        Assert.Null(result.PlannedTime);
        Assert.Null(result.ObservedRunningDuration);
        Assert.Equal("PolicyNotEstablished", result.AvailabilityReason);
    }

    [Fact]
    public void MissingRunningDoesNotOverrideConflictingExclusions()
    {
        var exclusion = new AssumedBreakReportingExclusion(
            "E1", Start.AddHours(1), Start.AddMinutes(90), false);
        var result = AssumedBreakReportingConsumer.Evaluate(Request(exclusions:
            [exclusion, exclusion with { IsBreak = true }]) with
        {
            RunningEvidenceReference = null,
        });

        Assert.Null(result.PlannedTime);
        Assert.Null(result.ObservedRunningDuration);
        Assert.Equal("ExclusionAuthorityAmbiguous", result.AvailabilityReason);
    }

    [Fact]
    public void ForecastPrecedenceRemainsExplicitWhenRunningEvidenceMissing()
    {
        var result = AssumedBreakReportingConsumer.Evaluate(Request(completed: false) with
        {
            RunningEvidenceReference = null,
        });

        Assert.True(result.PlannedTime!.IsForecast);
        Assert.Null(result.ObservedRunningDuration);
        Assert.Equal("BreakTimingNotEstablished", result.AvailabilityReason);
    }

    [Fact]
    public void ZeroDenominatorPrecedesMissingRunning()
    {
        var result = AssumedBreakReportingConsumer.Evaluate(Request() with
        {
            ShiftEndsAtUtc = Start.AddMinutes(20),
            RunningEvidenceReference = null,
        });

        Assert.Equal(TimeSpan.Zero, result.PlannedTime!.PlannedProductionDuration);
        Assert.Null(result.ObservedRunningDuration);
        Assert.Equal("ZeroDenominator", result.AvailabilityReason);
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
            completed, true, TimeSpan.FromHours(6), exclusions ?? [])
        {
            RunningEvidenceReference = "RUN-EVIDENCE-001",
        };
}
