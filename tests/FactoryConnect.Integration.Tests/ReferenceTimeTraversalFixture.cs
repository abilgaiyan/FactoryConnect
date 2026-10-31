using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

internal sealed record ReferenceTimeTraversalFixture(
    PositionedMetricInputFact[] Inputs, ProductionQuantitySourceEntry[] Sources,
    ProductionReferenceTimePublicationTransition[] Transitions, PublishedProductionReferenceTimeOutcome[] Outcomes,
    ProductionStandardVersion[] Versions)
{
    internal long Reference => Transitions[^1].CompletedReferenceTimeRevision!.Value.Value;
    internal Task<ProductionStandardAuthorityCut> ReadStandard(long revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(ProductionStandardAuthorityCut.Materialize(revision, Versions.Take(checked((int)revision))));
    }

    internal static ReferenceTimeTraversalFixture Create(int count, int seed, int background, string pattern, bool changing)
    {
        var machine = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var site = new SiteId("synthetic-site");
        var start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var shift = new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId("synthetic-shift"), new ShiftId("S1"), start, start.AddHours(8));
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 25));
        var stream = new MetricInputStreamId(machine, "synthetic-metrics");
        var processor = new MetricAggregationProcessorId("synthetic-aggregation");
        var facts = new List<PositionedMetricInputFact>();
        var transitions = new List<ProductionReferenceTimePublicationTransition>();
        var outcomes = new List<PublishedProductionReferenceTimeOutcome>();
        var versions = new List<ProductionStandardVersion>();
        long reference = 0;
        for (var i = 1; i <= count; i++)
        {
            var low = checked((ulong)(10 * (i - 1)));
            var position = low;
            var time = start.AddTicks(i);
            for (var b = 0; b < background; b++) Add(MetricInputFactKeys.RunningDuration, null, b, 1);
            var quantity = pattern switch
            {
                "empty" => 0,
                "sparse" => i % 10 == (1 + seed % 10) % 10 ? 1 : 0,
                "dense2" => 2,
                _ => 1,
            };
            long? standard = null;
            var previousR = reference;
            if (quantity > 0)
            {
                if (changing || versions.Count == 0)
                {
                    var revision = versions.Count + 1L;
                    versions.Add(new ProductionStandardVersion { VersionId = $"std-{seed}-{revision}", CompanyId = new("company"),
                        SiteId = site, PartId = new("unused-part"), OperationId = new("unused-operation"), SecondsPerUnit = 1,
                        EffectiveFromUtc = start, SourceReference = "synthetic", PublishedRevision = revision });
                }
                standard = versions.Count;
            }
            for (var q = 0; q < quantity; q++)
            {
                var source = new ProductionQuantityEvidenceId($"source-{seed}-{i}-{q}");
                var memberStart = facts.Count;
                Add(MetricInputFactKeys.PartCountIncrement, source, q, 10);
                if (pattern is "multi2" or "multi3") Add(MetricInputFactKeys.GoodQuantity, source, q, 8);
                if (pattern == "multi3") Add(MetricInputFactKeys.RejectedQuantity, source, q, 2);
                var members = facts.GetRange(memberStart, facts.Count - memberStart).ToArray();
                var reconstructed = SqlServerMetricAggregationStore.ReconstructSource(source, members)!;
                // MissingIdentity is a legitimate canonical outcome; it is not a fabricated duration.
                var cut = ProductionStandardAuthorityCut.Materialize(standard!.Value, []);
                var resolution = ProductionStandardResolver.Resolve(reconstructed.Evidence, shift, day, cut);
                outcomes.Add(new(new(++reference), resolution));
            }
            transitions.Add(new(processor, new MetricInputPosition(checked((ulong)(10 * i))),
                i == 1 ? null : new MetricInputPosition(low), standard, new(previousR), new(reference), true));

            void Add(string key, ProductionQuantityEvidenceId? source, int ordinal, decimal value)
            {
                var fact = new DurableMetricInputFact { Id = new($"fact-{seed}-{i}-{ordinal}-{key}"), Key = key, Value = value,
                    Unit = source is null ? MetricInputFactUnits.Seconds : MetricInputFactUnits.Count,
                    StartsAtUtc = time, EndsAtUtc = source is null ? time.AddSeconds((double)value) : time, CompanyId = new("company"), SiteId = site,
                    ProductionLineId = new("line"), MachineId = machine, ShiftId = shift.ShiftId,
                    ShiftScheduleAssignmentId = shift.ShiftScheduleAssignmentId, SourceQuantityEvidenceId = source };
                facts.Add(new(stream, new(++position), fact, shift, day));
            }
        }
        var inputs = facts.ToArray();
        var sources = inputs.Where(static item => item.Fact.SourceQuantityEvidenceId is not null)
            .GroupBy(static item => item.Fact.SourceQuantityEvidenceId!.Value)
            .Select(group => SqlServerMetricAggregationStore.ReconstructSource(group.Key, group.OrderBy(static item => item.Position).ToArray())!).ToArray();
        return new(inputs, sources, transitions.ToArray(), outcomes.ToArray(), versions.ToArray());
    }
}
