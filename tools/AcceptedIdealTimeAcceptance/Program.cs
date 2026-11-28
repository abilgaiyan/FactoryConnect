using System.Diagnostics;
using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

if (args.Length != 10)
    throw new ArgumentException("Expected database, machine GUID, assignment, shift, UTC start, UTC end, publication GUID, cycle source, evidence GUID, ideal seconds.");
var connection = Environment.GetEnvironmentVariable("FC_ACCEPTANCE_SQL")
    ?? throw new InvalidOperationException("Set FC_ACCEPTANCE_SQL to an isolated qualification database.");
var builder = new SqlConnectionStringBuilder(connection);
if (!string.Equals(builder.InitialCatalog, args[0], StringComparison.Ordinal) ||
    !args[0].Contains("Qualification", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Explicit qualification database required.");
var machine = Guid.Parse(args[1]);
var start = DateTimeOffset.Parse(args[4], CultureInfo.InvariantCulture);
var end = DateTimeOffset.Parse(args[5], CultureInfo.InvariantCulture);
var publication = Guid.Parse(args[6]);
var source = args[7];
var evidence = Guid.Parse(args[8]);
var seconds = decimal.Parse(args[9], CultureInfo.InvariantCulture);
if (publication == Guid.Empty || evidence == Guid.Empty || seconds < 0 ||
    source is not ("accepted-override" or "operation-default" or "part-default"))
    throw new ArgumentException("Invalid expected evidence.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var reader = new SqlServerSupervisorAcceptedIdealTimeReader(connection);
async Task Check()
{
    var actual = await reader.ReadAsync(machine, args[2], args[3], start, end, timeout.Token);
    if (actual.Status != "accepted-quantity-preview" || actual.PublicationId != publication ||
        actual.CycleSource != source || actual.IdealProductionSeconds != seconds ||
        actual.CycleRevisionId != (source == "accepted-override" ? (Guid?)evidence : null) ||
        actual.DefaultSnapshotId != (source == "accepted-override" ? null : (Guid?)evidence))
        throw new InvalidOperationException($"Unexpected evidence: {actual}");
}
await Check();
var timer = Stopwatch.StartNew();
for (var batch = 0; batch < 5; batch++)
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Check()));
Console.WriteLine($"20 parallel-batch reads passed in {timer.ElapsedMilliseconds} ms.");
var missing = await reader.ReadAsync(Guid.NewGuid(), args[2], args[3], start, end, timeout.Token);
if (missing.Status != "publication-unavailable" || missing.IdealProductionSeconds != null ||
    missing.PublicationId != null)
    throw new InvalidOperationException("Unknown machine must not produce ideal time.");
Console.WriteLine("Exact-scope negative check passed. Diagnostic acceptance only; writer contention not tested.");
