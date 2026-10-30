using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Integration.Tests;
using FactoryConnect.Persistence.SqlServer;

var full = args.Contains("--full", StringComparer.Ordinal);
if (args.Any(static argument => argument != "--full")) throw new ArgumentException("Use no arguments for smoke, or --full for the frozen full synthetic matrix. No database option exists.");
int[] sizes = full ? [100, 500, 1000, 5000, 10000] : [100, 500, 1000];
int[] seeds = full ? [16901, 16902, 16903] : [16901];
int[] backgrounds = full ? [0, 2, 4] : [4];
string[] patterns = full ? ["empty", "dense1", "dense2", "sparse", "multi2", "multi3"] : ["empty", "dense1"];
bool[] standardPatterns = full ? [false, true] : [false];
Console.WriteLine(JsonSerializer.Serialize(new { Kind = "manifest", Baseline = "a91618de8d358474834845f3665012129ddd5273", Generator = "v1.1-implementation-1",
    FullMatrix = full, Sizes = sizes, Seeds = seeds, Backgrounds = backgrounds, Patterns = patterns,
    StandardPatterns = standardPatterns, Warmup = 3, Measurements = 10, Runtime = Environment.Version.ToString(),
    Note = "Synthetic only. Traversal visits exclude sort comparisons. Complete validation uses in-memory authority loading, not SQL. No HTTP/replay result is established." }));
var previousMeasurements = new Dictionary<string, (int Count, double Median)>();
foreach (var count in sizes)
foreach (var seed in seeds)
foreach (var background in backgrounds)
foreach (var pattern in patterns)
foreach (var changing in standardPatterns)
{
    var data = ReferenceTimeTraversalFixture.Create(count, seed, background, pattern, changing);
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
    await ReferenceTimeSemanticOracle.ValidateAsync(data.Inputs, data.Sources, data.Transitions, data.Outcomes, data.Reference, data.ReadStandard, default);
    await SqlContainmentReferenceTimeAuthorityValidator.ValidateSemanticsAsync(data.Inputs, data.Sources, data.Transitions, data.Outcomes, data.Reference, data.ReadStandard, default);
    if (BaselineTraversal() != CandidateTraversal()) throw new InvalidOperationException("Traversal inventory differs.");
    foreach (var stage in new[] { "traversal", "complete-in-memory-semantics" })
    foreach (var candidate in new[] { false, true })
    {
        async Task Execute()
        {
            if (stage == "traversal")
            {
                _ = candidate ? CandidateTraversal() : BaselineTraversal();
            }
            else if (candidate)
                await SqlContainmentReferenceTimeAuthorityValidator.ValidateSemanticsAsync(data.Inputs, data.Sources, data.Transitions, data.Outcomes, data.Reference, data.ReadStandard, default);
            else
                await ReferenceTimeSemanticOracle.ValidateAsync(data.Inputs, data.Sources, data.Transitions, data.Outcomes, data.Reference, data.ReadStandard, default);
        }
        for (var i = 0; i < 3; i++) await Execute();
        var timings = new double[10]; var allocations = new long[10];
        for (var i = 0; i < 10; i++)
        {
            var before = GC.GetTotalAllocatedBytes(true); var time = Stopwatch.GetTimestamp();
            await Execute();
            timings[i] = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
            allocations[i] = GC.GetTotalAllocatedBytes(true) - before;
        }
        Array.Sort(timings); Array.Sort(allocations);
        var traversal = new ReferenceTimeTransitionPartitioner(data.Inputs, data.Outcomes);
        foreach (var transition in data.Transitions) _ = traversal.Next(transition);
        var median = (timings[4] + timings[5]) / 2;
        var measurementKey = $"{seed}:{background}:{pattern}:{changing}:{stage}:{candidate}";
        var previous = previousMeasurements.GetValueOrDefault(measurementKey);
        previousMeasurements[measurementKey] = (count, median);
        Console.WriteLine(JsonSerializer.Serialize(new { Count = count, Seed = seed, Background = background, Pattern = pattern, ChangingStandards = changing,
            Facts = data.Inputs.Length, Outcomes = data.Outcomes.Length, Digest = digest, Stage = stage, Candidate = candidate,
            MedianMs = median, GrowthVsPrevious = previous.Median > 0 ? median / previous.Median : (double?)null,
            SizeGrowth = previous.Count > 0 ? count / (double)previous.Count : (double?)null, MinMs = timings[0], MaxMs = timings[^1],
            MedianAllocatedBytes = (allocations[4] + allocations[5]) / 2, MinAllocatedBytes = allocations[0], MaxAllocatedBytes = allocations[^1],
            StructuralVisits = candidate ? traversal.FactVisits + traversal.OutcomeVisits : (long)count * (data.Inputs.Length + data.Outcomes.Length),
            StandardLoads = stage == "traversal" ? 0 : data.Transitions.Where(static transition => transition.ProductionStandardAuthorityRevision is not null)
                .Select(static transition => transition.ProductionStandardAuthorityRevision).Distinct().Count(),
            CanonicalResolutions = stage == "traversal" ? 0 : data.Outcomes.Length }));
    }
    long BaselineTraversal()
    {
        long sum = 0;
        foreach (var transition in data.Transitions)
        {
            var sources = data.Inputs.Where(item => item.Fact.Key == FactoryConnect.Abstractions.MetricInputFactKeys.PartCountIncrement &&
                (transition.ExpectedPreviousAggregationPosition is null || item.Position > transition.ExpectedPreviousAggregationPosition) && item.Position <= transition.TargetAggregationPosition)
                .Select(static item => item.Fact.SourceQuantityEvidenceId!.Value).ToHashSet();
            var outcomes = data.Outcomes.Where(item => item.PublicationRevision.Value > transition.StartingReferenceTimeRevision.Value &&
                item.PublicationRevision.Value <= transition.CompletedReferenceTimeRevision!.Value.Value).ToArray();
            sum += sources.Count + outcomes.Length;
        }
        return sum;
    }
    long CandidateTraversal()
    {
        long sum = 0;
        var traversal = new ReferenceTimeTransitionPartitioner(data.Inputs, data.Outcomes);
        foreach (var transition in data.Transitions)
        {
            var (sources, outcomes) = traversal.Next(transition); sum += sources.Count + outcomes.Length;
        }
        return sum;
    }
}
