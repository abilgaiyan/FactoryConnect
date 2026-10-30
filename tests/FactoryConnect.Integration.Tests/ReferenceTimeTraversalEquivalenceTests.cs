using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class ReferenceTimeTraversalEquivalenceTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("dense1")]
    [InlineData("dense2")]
    [InlineData("sparse")]
    [InlineData("multi2")]
    [InlineData("multi3")]
    public async Task ValidPatternsAndPermutedInputEnumerationMatchPinnedOracle(string pattern)
    {
        foreach (var changing in new[] { false, true })
        {
            var fixture = ReferenceTimeTraversalFixture.Create(100, 16901, 4, pattern, changing);
            Assert.Null(await Compare(fixture));
            Assert.Null(await Compare(fixture with { Inputs = fixture.Inputs.Reverse().ToArray() }));
            foreach (var published in fixture.Outcomes)
            {
                var source = Assert.Single(fixture.Sources, source => source.Evidence.Id == published.SourceQuantityEvidenceId);
                var cut = await fixture.ReadStandard(published.StandardAuthorityRevision, default);
                ProductionStandardResolver.ValidateCanonicalOutcome(source.Evidence, source.ShiftOccurrenceId, source.ProductionDayId, cut, published.Resolution);
            }
        }
    }

    [Theory]
    [InlineData("O01", "Duplicate historical source outcomes.")]
    [InlineData("O02", "The terminal R lacks its complete outcome history.")]
    [InlineData("O03A", "The historical outcome revision chain is incomplete.")]
    [InlineData("O04", "An outcome is outside its exact aggregation delta.")]
    [InlineData("O05", "Completed transition lacks a required produced-source outcome.")]
    [InlineData("C01", "Outcome contradicts its transition S or source membership.")]
    [InlineData("C02", "Outcome contradicts its independently resolved historical source/standard.")]
    [InlineData("STD01", "A nonempty transition has no standard authority cut.")]
    [InlineData("STD04", "A referenced historical standard version is absent.")]
    [InlineData("MP04", "Duplicate historical source outcomes.")]
    [InlineData("MP05", "An outcome is outside its exact aggregation delta.")]
    public async Task MalformedHistoriesMatchExceptionTypeReasonAndPrecedence(string id, string message)
    {
        var fixture = ReferenceTimeTraversalFixture.Create(3, 16901, 0, "dense1", false);
        var outcomes = fixture.Outcomes.ToArray();
        var transitions = fixture.Transitions.ToArray();
        switch (id)
        {
            case "O01":
            case "MP04":
                outcomes[1] = new(outcomes[1].PublicationRevision, outcomes[0].Resolution);
                if (id == "MP04") outcomes = outcomes.Take(2).ToArray();
                break;
            case "O02": outcomes = outcomes.Take(2).ToArray(); break;
            case "O03A": outcomes = outcomes.Reverse().ToArray(); break;
            case "O04":
            case "MP05":
                // Q3 is independently valid outside T1. Swapping first/third admissions
                // preserves unique sources and contiguous R, while T1 lacks Q1 and admits Q2.
                outcomes[0] = new(new(1), fixture.Outcomes[2].Resolution);
                outcomes[2] = new(new(3), fixture.Outcomes[0].Resolution);
                break;
            case "O05":
                outcomes = outcomes.Take(2).ToArray();
                transitions[2] = transitions[2] with { CompletedReferenceTimeRevision = new(2), ProductionStandardAuthorityRevision = null };
                break;
            case "C01": outcomes[0] = new(new(1), outcomes[0].Resolution with { AuthorityRevision = 0 }); break;
            case "C02": outcomes[0] = new(new(1), outcomes[0].Resolution with { ProducedUnits = 9 }); break;
            case "STD01": transitions[0] = transitions[0] with { ProductionStandardAuthorityRevision = null }; break;
            case "STD04": outcomes[0] = new(new(1), outcomes[0].Resolution with { Status = ProductionReferenceTimeResolutionStatus.Resolved,
                SelectedStandardVersionId = "missing", SelectedStandardSourceReference = "synthetic", IdealDurationSeconds = 10 }); break;
        }
        var result = await Compare(fixture with { Outcomes = outcomes, Transitions = transitions });
        Assert.NotNull(result);
        Assert.Equal(message, result.Message);
        Assert.Equal(id is "O02" or "O03A" or "O05" or "STD01" or "STD04"
            ? typeof(MissingContainmentHistoricalAuthorityException).FullName : typeof(ContainmentHistoricalIntegrityException).FullName, result.Type);
    }

    [Fact]
    public void S03OperationMismatchAndDuplicateKeysRetainSourceReconstructionRejections()
    {
        var fixture = ReferenceTimeTraversalFixture.Create(1, 16901, 0, "multi3", false);
        var produced = fixture.Inputs[0];
        var sibling = fixture.Inputs[1];
        var changed = new PositionedMetricInputFact(sibling.StreamId, sibling.Position,
            sibling.Fact with { OperationId = new OperationId("different") }, sibling.ShiftOccurrenceId, sibling.ProductionDayId);
        var failure = Assert.Throws<InvalidOperationException>(() => SqlServerMetricAggregationStore.ReconstructSource(
            fixture.Sources[0].Evidence.Id, [produced, changed]));
        Assert.Equal("Sibling quantity facts with the same source evidence id disagree on canonical identity or period attribution.", failure.Message);
        Assert.Throws<InvalidOperationException>(() => SqlServerMetricAggregationStore.ReconstructSource(
            fixture.Sources[0].Evidence.Id, [produced, produced]));
    }

    [Fact]
    public async Task CancellationAtStandardReadDoesNotReturnSemanticSuccess()
    {
        var fixture = ReferenceTimeTraversalFixture.Create(100, 16901, 0, "dense1", false);
        using var cancelled = new CancellationTokenSource();
        Task<ProductionStandardAuthorityCut> Cancel(long revision, CancellationToken token)
        {
            cancelled.Cancel();
            return fixture.ReadStandard(revision, CancellationToken.None);
        }
        var baseline = await Capture(() => ReferenceTimeSemanticOracle.ValidateAsync(fixture.Inputs, fixture.Sources, fixture.Transitions,
            fixture.Outcomes, fixture.Reference, Cancel, cancelled.Token));
        var candidate = await Capture(() => SqlContainmentReferenceTimeAuthorityValidator.ValidateSemanticsAsync(fixture.Inputs, fixture.Sources,
            fixture.Transitions, fixture.Outcomes, fixture.Reference, Cancel, cancelled.Token));
        Assert.Equal(baseline, candidate);
        Assert.Equal(typeof(OperationCanceledException).FullName, candidate!.Type);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public void StructuralVisitsScaleWithCollectionsRatherThanTransitionProduct(int count)
    {
        var fixture = ReferenceTimeTraversalFixture.Create(count, 16901, 4, "multi3", false);
        var traversal = new ReferenceTimeTransitionPartitioner(fixture.Inputs, fixture.Outcomes);
        foreach (var transition in fixture.Transitions) _ = traversal.Next(transition);
        Assert.True(traversal.FactVisits <= fixture.Inputs.Length + 2L * fixture.Sources.Length + fixture.Transitions.Length);
        Assert.True(traversal.OutcomeVisits <= fixture.Outcomes.Length + fixture.Transitions.Length);
    }

    [Fact]
    public async Task V02FrozenCommonHistoryHasEmptyMiddleDeltaAndExactA30R2S2()
    {
        var fixture = ReferenceTimeTraversalFixture.Create(3, 16901, 1, "dense1", true);
        var first = fixture.Outcomes[0];
        var last = new PublishedProductionReferenceTimeOutcome(new(2), fixture.Outcomes[2].Resolution with { AuthorityRevision = 2 });
        var middleSource = fixture.Sources[1].Evidence.Id;
        var transitions = fixture.Transitions.ToArray();
        transitions[1] = new(transitions[1].ProcessorId, new(20), new(10), null, new(1), new(1), true);
        transitions[2] = new(transitions[2].ProcessorId, new(30), new(20), 2, new(1), new(2), true);
        Assert.Null(await Compare(fixture with { Inputs = fixture.Inputs.Where(item => item.Fact.SourceQuantityEvidenceId != middleSource).ToArray(),
            Sources = fixture.Sources.Where(source => source.Evidence.Id != middleSource).ToArray(), Transitions = transitions,
            Outcomes = [first, last], Versions = fixture.Versions.Take(2).ToArray() }));
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("missing-standard")]
    [InlineData("ambiguous")]
    public async Task IndependentlyResolvedStandardOutcomesRemainEquivalent(string mode)
    {
        var fixture = ReferenceTimeTraversalFixture.Create(1, 16901, 0, "dense1", false);
        var source = fixture.Sources[0] with { Evidence = fixture.Sources[0].Evidence with
            { PartId = new PartId(mode == "missing-standard" ? "unmatched" : "unused-part"), OperationId = new OperationId("unused-operation") } };
        var versions = fixture.Versions;
        if (mode == "ambiguous") versions = [versions[0], versions[0] with { VersionId = "second", PublishedRevision = 2 }];
        var standard = ProductionStandardAuthorityCut.Materialize(versions.Length, versions);
        var resolution = ProductionStandardResolver.Resolve(source.Evidence, source.ShiftOccurrenceId, source.ProductionDayId, standard);
        Assert.Equal(mode switch { "resolved" => ProductionReferenceTimeResolutionStatus.Resolved,
            "ambiguous" => ProductionReferenceTimeResolutionStatus.AmbiguousStandard, _ => ProductionReferenceTimeResolutionStatus.MissingStandard }, resolution.Status);
        Assert.Null(await Compare(fixture with { Inputs = fixture.Inputs.Select(item => new PositionedMetricInputFact(item.StreamId, item.Position,
                item.Fact with { PartId = source.Evidence.PartId, OperationId = source.Evidence.OperationId }, item.ShiftOccurrenceId, item.ProductionDayId)).ToArray(), Sources = [source], Versions = versions,
            Transitions = [fixture.Transitions[0] with { ProductionStandardAuthorityRevision = versions.Length }],
            Outcomes = [new(new(1), resolution)] }));
    }

    [Fact]
    public async Task UInt64PositionsAreNeverNarrowedToIndexesOrFloatingPoint()
    {
        var fixture = ReferenceTimeTraversalFixture.Create(3, 16901, 4, "dense1", false);
        const ulong offset = ulong.MaxValue - 1000;
        var inputs = fixture.Inputs.Select(item => new PositionedMetricInputFact(item.StreamId, new(offset + item.Position.Value), item.Fact,
            item.ShiftOccurrenceId, item.ProductionDayId)).ToArray();
        var transitions = fixture.Transitions.Select(transition => transition with
        {
            TargetAggregationPosition = new(offset + transition.TargetAggregationPosition.Value),
            ExpectedPreviousAggregationPosition = transition.ExpectedPreviousAggregationPosition is { } previous ? new(offset + previous.Value) : null,
        }).ToArray();
        Assert.Null(await Compare(fixture with { Inputs = inputs, Transitions = transitions, Sources = fixture.Sources.Select(source => source with
            { ContributingPositions = source.ContributingPositions.Select(position => new MetricInputPosition(offset + position.Value)).ToArray() }).ToArray() }));
    }

    [Theory]
    [InlineData("The historical standard authority revision chain is incomplete.")]
    [InlineData("The historical standard cut lacks its complete version inventory.")]
    public async Task StandardLoaderFailurePreservesExactFailure(string reason)
    {
        var fixture = ReferenceTimeTraversalFixture.Create(3, 16901, 0, "dense1", false);
        Task<ProductionStandardAuthorityCut> Missing(long revision, CancellationToken token) => throw new MissingContainmentHistoricalAuthorityException(reason);
        var before = await Capture(() => ReferenceTimeSemanticOracle.ValidateAsync(fixture.Inputs, fixture.Sources, fixture.Transitions, fixture.Outcomes,
            fixture.Reference, Missing, default));
        var after = await Capture(() => SqlContainmentReferenceTimeAuthorityValidator.ValidateSemanticsAsync(fixture.Inputs, fixture.Sources, fixture.Transitions,
            fixture.Outcomes, fixture.Reference, Missing, default));
        Assert.Equal(before, after);
        Assert.Equal(reason, after!.Message);
    }

    [Theory]
    [InlineData(16901)]
    [InlineData(16902)]
    [InlineData(16903)]
    public void DatasetConstructionMatchesFrozenCountsPositionsAndQuantitySemantics(int seed)
    {
        foreach (var background in new[] { 0, 2, 4 })
        foreach (var pattern in new[] { "empty", "dense1", "dense2", "sparse", "multi2", "multi3" })
        {
            var fixture = ReferenceTimeTraversalFixture.Create(100, seed, background, pattern, false);
            var expectedSources = pattern switch { "empty" => 0, "dense2" => 200, "sparse" => 10, _ => 100 };
            var members = pattern switch { "multi2" => 2, "multi3" => 3, _ => 1 };
            Assert.Equal(expectedSources, fixture.Sources.Length);
            Assert.Equal(expectedSources, fixture.Outcomes.Length);
            Assert.Equal(background * 100 + members * expectedSources, fixture.Inputs.Length);
            Assert.Equal(fixture.Inputs.Length, fixture.Inputs.Select(static item => item.Position).Distinct().Count());
            foreach (var source in fixture.Sources)
            {
                Assert.Equal(members, source.ContributingPositions.Count);
                Assert.Equal(10, source.Evidence.PartCountIncrement);
                if (members >= 2) Assert.Equal(8, source.Evidence.GoodQuantity);
                if (members == 3) Assert.Equal(2, source.Evidence.RejectedQuantity);
                if (pattern == "sparse")
                {
                    var transitionIndex = checked((int)((source.ContributingPositions[0].Value - 1) / 10 + 1));
                    Assert.Equal((1 + seed % 10) % 10, transitionIndex % 10);
                }
            }
            Assert.Equal(JsonSerializer.Serialize(fixture), JsonSerializer.Serialize(ReferenceTimeTraversalFixture.Create(100, seed, background, pattern, false)));
        }
    }

    private sealed record Failure(string? Type, string Message, string? InnerType, string? InnerMessage);
    private static async Task<Failure?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return new(error.GetType().FullName, error.Message, error.InnerException?.GetType().FullName, error.InnerException?.Message); }
    }
    private static async Task<Failure?> Compare(ReferenceTimeTraversalFixture fixture)
    {
        var original = JsonSerializer.Serialize(fixture);
        var oracleLoads = new List<long>();
        var candidateLoads = new List<long>();
        Task<ProductionStandardAuthorityCut> ReadOracle(long revision, CancellationToken token) { oracleLoads.Add(revision); return fixture.ReadStandard(revision, token); }
        Task<ProductionStandardAuthorityCut> ReadCandidate(long revision, CancellationToken token) { candidateLoads.Add(revision); return fixture.ReadStandard(revision, token); }
        var oracle = await Capture(() => ReferenceTimeSemanticOracle.ValidateAsync(fixture.Inputs, fixture.Sources, fixture.Transitions, fixture.Outcomes,
            fixture.Reference, ReadOracle, default));
        var candidate = await Capture(() => SqlContainmentReferenceTimeAuthorityValidator.ValidateSemanticsAsync(fixture.Inputs, fixture.Sources, fixture.Transitions,
            fixture.Outcomes, fixture.Reference, ReadCandidate, default));
        Assert.Equal(oracle, candidate);
        Assert.Equal(oracleLoads, candidateLoads);
        Assert.Equal(original, JsonSerializer.Serialize(fixture));
        return candidate;
    }
}
