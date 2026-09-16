using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class CurrentMachineStateFreshnessUsabilityTests
{
    private static readonly CurrentStatePolicyReference ContinuityReference =
        new("continuity/preserve", "1.0");

    private static readonly CurrentStatePolicyReference FreshnessReference =
        new("freshness/default", "1.0");

    [Theory]
    [InlineData(PolicyResolutionKind.Missing, CurrentStateAuthorityFailureReason.MissingPolicyAuthority)]
    [InlineData(PolicyResolutionKind.Ambiguous, CurrentStateAuthorityFailureReason.AmbiguousPolicyAuthority)]
    [InlineData(PolicyResolutionKind.Unsupported, CurrentStateAuthorityFailureReason.UnsupportedPolicyAuthority)]
    public async Task FreshnessPolicyFailureIsTerminalBeforeReadAsOf(
        PolicyResolutionKind kind,
        CurrentStateAuthorityFailureReason expectedReason)
    {
        var fixture = CreateFixture(
            acquisitionContact: null,
            freshnessResolution: CreateFreshnessResolution(kind),
            timeProvider: new ThrowingTimeProvider());

        var result = await fixture.Reader.ReadAsync(fixture.Binding.MachineId, CancellationToken.None);

        var failure = Assert.IsType<CurrentMachineStateAuthorityFailure>(result);
        Assert.Equal(expectedReason, failure.Reason);
    }

    [Fact]
    public async Task EvidenceWithoutAcquisitionIsFreshnessIndeterminateAndCapturesReadAsOfExactlyOnce()
    {
        var readAsOf = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new CountingTimeProvider(readAsOf);
        var fixture = CreateFixture(null, ExactlyOneFreshness(TimeSpan.FromMinutes(5)), timeProvider);

        var result = await fixture.Reader.ReadAsync(fixture.Binding.MachineId, CancellationToken.None);

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(result);
        Assert.Equal(CurrentStateFreshness.Indeterminate, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Indeterminate, evidence.Usability);
        Assert.Equal(readAsOf, evidence.ReadAsOf);
        Assert.Equal(1, timeProvider.CallCount);
    }

    [Fact]
    public async Task ContactExactlyAtMaximumAgeIsCurrent()
    {
        var readAsOf = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var maximumAge = TimeSpan.FromMinutes(5);
        var fixture = CreateFixture(
            CreateAcquisition(readAsOf - maximumAge),
            ExactlyOneFreshness(maximumAge),
            new CountingTimeProvider(readAsOf));

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await fixture.Reader.ReadAsync(fixture.Binding.MachineId, CancellationToken.None));

        Assert.Equal(CurrentStateFreshness.Current, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Current, evidence.Usability);
    }

    [Fact]
    public async Task ContactOlderThanMaximumAgeIsStale()
    {
        var readAsOf = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var fixture = CreateFixture(
            CreateAcquisition(readAsOf - TimeSpan.FromMinutes(6)),
            ExactlyOneFreshness(TimeSpan.FromMinutes(5)),
            new CountingTimeProvider(readAsOf));

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await fixture.Reader.ReadAsync(fixture.Binding.MachineId, CancellationToken.None));

        Assert.Equal(CurrentStateFreshness.Stale, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Stale, evidence.Usability);
    }

    [Fact]
    public async Task FutureContactIsIndeterminateWithoutNormalization()
    {
        var readAsOf = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var fixture = CreateFixture(
            CreateAcquisition(readAsOf + TimeSpan.FromSeconds(1)),
            ExactlyOneFreshness(TimeSpan.FromMinutes(5)),
            new CountingTimeProvider(readAsOf));

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await fixture.Reader.ReadAsync(fixture.Binding.MachineId, CancellationToken.None));

        Assert.Equal(CurrentStateFreshness.Indeterminate, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Indeterminate, evidence.Usability);
    }

    [Theory]
    [InlineData(CurrentStateCoverage.Behind, CurrentStateFreshness.Current, CurrentStateUsability.Behind)]
    [InlineData(CurrentStateCoverage.Behind, CurrentStateFreshness.Stale, CurrentStateUsability.Behind)]
    [InlineData(CurrentStateCoverage.Behind, CurrentStateFreshness.Indeterminate, CurrentStateUsability.Behind)]
    [InlineData(CurrentStateCoverage.Indeterminate, CurrentStateFreshness.Current, CurrentStateUsability.Indeterminate)]
    [InlineData(CurrentStateCoverage.Indeterminate, CurrentStateFreshness.Stale, CurrentStateUsability.Indeterminate)]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Current, CurrentStateUsability.Current)]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Stale, CurrentStateUsability.Stale)]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Indeterminate, CurrentStateUsability.Indeterminate)]
    public void UsabilityFollowsFrozenCoveragePrecedence(
        CurrentStateCoverage coverage,
        CurrentStateFreshness freshness,
        CurrentStateUsability expected)
    {
        Assert.Equal(expected, CurrentMachineStateReader.ClassifyUsability(coverage, freshness));
    }

    [Fact]
    public async Task BehindCoverageRetainsFreshnessButDominatesUsability()
    {
        var readAsOf = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var fixture = CreateFixture(
            CreateAcquisition(readAsOf),
            ExactlyOneFreshness(TimeSpan.FromMinutes(5)),
            new CountingTimeProvider(readAsOf),
            evaluatedThrough: 4,
            mappedHighWater: 5);

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await fixture.Reader.ReadAsync(fixture.Binding.MachineId, CancellationToken.None));

        Assert.Equal(CurrentStateCoverage.Behind, evidence.Coverage);
        Assert.Equal(CurrentStateFreshness.Current, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Behind, evidence.Usability);
        Assert.Equal(readAsOf, evidence.ReadAsOf);
    }

    [Fact]
    public async Task IndeterminateCoverageRetainsStaleFreshnessButDominatesUsability()
    {
        var readAsOf = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var fixture = CreateFixture(
            CreateAcquisition(readAsOf - TimeSpan.FromMinutes(10)),
            ExactlyOneFreshness(TimeSpan.FromMinutes(5)),
            new CountingTimeProvider(readAsOf),
            includeMapping: false);

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await fixture.Reader.ReadAsync(fixture.Binding.MachineId, CancellationToken.None));

        Assert.Equal(CurrentStateCoverage.Indeterminate, evidence.Coverage);
        Assert.Equal(CurrentStateFreshness.Stale, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Indeterminate, evidence.Usability);
    }

    private static Fixture CreateFixture(
        AcquisitionContactAuthority? acquisitionContact,
        CurrentStatePolicyResolution<ICurrentStateFreshnessPolicy> freshnessResolution,
        ICurrentStateTimeProvider timeProvider,
        ulong evaluatedThrough = 5,
        ulong mappedHighWater = 5,
        bool includeMapping = true)
    {
        var binding = CreateBinding();
        var evaluation = new EvaluationAuthority(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            new ObservationPosition(evaluatedThrough),
            MachineState.Running,
            1,
            ContinuityReference,
            new StateProjectionAuthorityRevision(1));
        MappingCoverageAuthority? mapping = includeMapping
            ? new MappingCoverageAuthority(
                binding.MappingProcessorId,
                binding.ObservationStreamId,
                new ObservationPosition(Math.Max(mappedHighWater, evaluatedThrough)),
                new ObservationPosition(mappedHighWater),
                new MappingAuthorityRevision(1))
            : null;
        var cut = new CurrentStateAuthorityCut(binding, acquisitionContact, mapping, evaluation);
        var reader = new CurrentMachineStateReader(
            new StubOwnerResolver(binding),
            new StubCutProvider(cut),
            new StubPolicyResolver<CurrentStateContinuityPolicy>(
                new CurrentStateExactlyOnePolicy<CurrentStateContinuityPolicy>(
                    new CurrentStateContinuityPolicy(ContinuityReference, StateContinuityMode.Preserve))),
            new StubPolicyResolver<ICurrentStateFreshnessPolicy>(freshnessResolution),
            timeProvider);

        return new Fixture(binding, reader);
    }

    private static CurrentStatePolicyResolution<ICurrentStateFreshnessPolicy> CreateFreshnessResolution(
        PolicyResolutionKind kind) =>
        kind switch
        {
            PolicyResolutionKind.Missing => new CurrentStateMissingPolicy<ICurrentStateFreshnessPolicy>(),
            PolicyResolutionKind.Ambiguous => new CurrentStateAmbiguousPolicy<ICurrentStateFreshnessPolicy>(),
            PolicyResolutionKind.Unsupported => new CurrentStateUnsupportedPolicy<ICurrentStateFreshnessPolicy>(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static CurrentStateExactlyOnePolicy<ICurrentStateFreshnessPolicy> ExactlyOneFreshness(
        TimeSpan maximumCurrentAge) =>
        new(new StubFreshnessPolicy(maximumCurrentAge));

    private static CurrentStateAuthorityBinding CreateBinding()
    {
        var machineId = MachineId.New();
        return new CurrentStateAuthorityBinding(
            machineId,
            new ObservationStreamId(machineId, "primary"),
            new ObservationProcessorId("mapper"),
            new ObservationProcessorId("state"));
    }

    private static AcquisitionContactAuthority CreateAcquisition(DateTimeOffset contactTime)
    {
        var machineId = MachineId.New();
        return new AcquisitionContactAuthority(
            new ObservationStreamId(machineId, "contact"),
            contactTime,
            null,
            new AcquisitionAuthorityRevision(1));
    }

    private sealed record Fixture(
        CurrentStateAuthorityBinding Binding,
        CurrentMachineStateReader Reader);

    public enum PolicyResolutionKind
    {
        Missing,
        Ambiguous,
        Unsupported,
    }

    private sealed class StubFreshnessPolicy(TimeSpan maximumCurrentAge) : ICurrentStateFreshnessPolicy
    {
        public CurrentStatePolicyReference Reference => FreshnessReference;
        public TimeSpan MaximumCurrentAge => maximumCurrentAge;
    }

    private sealed class StubOwnerResolver(CurrentStateAuthorityBinding binding) : ICurrentStateOwnerResolver
    {
        public CurrentStateOwnerResolution Resolve(MachineId machineId) => new CurrentStateExactlyOneOwner(binding);
    }

    private sealed class StubCutProvider(CurrentStateAuthorityCut cut) : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
            CurrentStateAuthorityBinding binding,
            CancellationToken cancellationToken) =>
            Task.FromResult<CurrentStateAuthorityCutReadResult>(new StableCurrentStateAuthorityCut(cut));
    }

    private sealed class StubPolicyResolver<TPolicy>(CurrentStatePolicyResolution<TPolicy> resolution)
        : ICurrentStatePolicyResolver<TPolicy>
        where TPolicy : class
    {
        public CurrentStatePolicyResolution<TPolicy> Resolve() => resolution;
    }

    private sealed class CountingTimeProvider(DateTimeOffset value) : ICurrentStateTimeProvider
    {
        public int CallCount { get; private set; }

        public DateTimeOffset GetUtcNow()
        {
            CallCount++;
            return value;
        }
    }

    private sealed class ThrowingTimeProvider : ICurrentStateTimeProvider
    {
        public DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("ReadAsOf must not be captured after freshness-policy failure.");
    }
}
