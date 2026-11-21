using System.Data;
using System.Diagnostics;
using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Mutates only the explicitly acknowledged disposable A2 clone. No fixture creation,
// migration, history truncation, constraint changes or automatic cleanup is performed.
[Trait("Category", "SqlServerIntegration")]
[Collection(Gg003AcquisitionCloneBenchmarkDefinition.CollectionName)]
public sealed class Gg003AcquisitionCloneBenchmarkTests(ITestOutputHelper output)
{
    private const string Prefix = "FACTORYCONNECT_GG003_ACQ_A2_";
    private const string Database = "GG003_ACQ_A2_20261026";
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));

    [A2CloneFact]
    public async Task ExistingFullHistoryAcceptsOneContinuationAndIdenticalReplay()
    {
        Assert.Equal(Database, Required("DISPOSABLE_DATABASE"));
        var settings = new SqlConnectionStringBuilder(Required("CONNECTION_STRING"));
        Assert.Equal(Database, settings.InitialCatalog);
        Assert.True(settings.DataSource.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase),
            "Only a developer LocalDB instance is allowed.");
        Assert.True(string.IsNullOrEmpty(settings.AttachDBFilename), "Attached files are not allowed.");
        var stream = new ObservationStreamId(Machine, Required("STREAM_KEY"));
        var expectedIdentity = Convert.ToHexString(Convert.FromHexString(Required("EXPECTED_HEAD")));
        Assert.Equal(64, expectedIdentity.Length);
        var expectedCount = long.Parse(Required("EXPECTED_OBSERVATION_COUNT"), CultureInfo.InvariantCulture);
        var expectedThrough = decimal.Parse(Required("EXPECTED_ACCEPTED_THROUGH"), CultureInfo.InvariantCulture);
        Assert.True(expectedCount > 0 && expectedThrough > 0);
        Assert.Equal(decimal.Truncate(expectedThrough), expectedThrough);
        Assert.True(expectedThrough < ulong.MaxValue);
        var seconds = int.Parse(Environment.GetEnvironmentVariable(Prefix + "TIMEOUT_SECONDS") ?? "600", CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 30, 1800);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var token = deadline.Token;
        using var capture = new AcquisitionPersistenceCapture(settings.ConnectionString);
        var store = new SqlServerObservationIngestionStore(capture.ConnectionString);
        var before = await ReadStateAsync(capture.ConnectionString, stream, token);
        Assert.Equal(expectedIdentity, before.Identity);
        Assert.Equal(40545L, before.Ordinal);
        Assert.Equal(before.Ordinal, before.Publications);
        Assert.Equal(expectedCount, before.Observations);
        Assert.Equal(expectedThrough, before.AcceptedThrough);
        Assert.Equal(1792061301m, before.Instance);
        Assert.Equal(482204m, before.NextSequence);
        var expectedCheckpoint = new ObservationCheckpoint(stream, 1792061301, 482204);

        try
        {
            var clock = Stopwatch.StartNew();
            var commands = capture.SourceCommands;
            var prior = await store.ReadExactAsync(stream, before.Identity, token);
            output.WriteLine("Baseline exact read: {0:F3} ms; source SQL commands={1}",
                clock.Elapsed.TotalMilliseconds, capture.SourceCommands - commands);
            Assert.Equal(AcquisitionEvidenceReadStatus.Found, prior.Status);
            var priorRecord = Assert.IsType<AcquisitionEvidenceRecord>(prior.Record);
            Assert.Equal(expectedCheckpoint, priorRecord.Batch.Checkpoint);
            Assert.Equal(before.Ordinal, priorRecord.PublicationOrdinal);
            Assert.Equal((ulong)expectedThrough, priorRecord.AcceptedThrough!.Value);
            // Recheck after the full read. The clone must be exclusive to this test.
            Assert.Equal(before, await ReadStateAsync(capture.ConnectionString, stream, token));

            var timestamp = priorRecord.Batch.SuccessfulContactTime.AddSeconds(1);
            var observation = new MachineObservation
            {
                MachineId = Machine, Source = "gg003-disposable-benchmark", Address = "r4-continuation",
                Type = SignalType.Text, Value = "Disposable clone benchmark observation",
                Quality = ObservationQuality.Good, Timestamp = timestamp
            };
            var publication = Guid.NewGuid();
            var batch = new ObservationIngestionBatch(expectedCheckpoint,
                new ObservationCheckpoint(stream, 1792061301, 482205),
                [new SequencedMachineObservation(482204, observation)], timestamp,
                new AcquisitionResponseEvidence(publication, 482204, 482204, 482204, []));
            // Runs the real codec validator before opening the append transaction.
            var payload = AcquisitionEvidenceCodec.Encode(batch);
            var identity = Convert.ToHexString(AcquisitionEvidenceCodec.Identity(batch, payload));
            output.WriteLine("Continuation publication={0}; identity={1}; baseline ordinal={2}", publication, identity, before.Ordinal);
            commands = capture.SourceCommands;
            clock.Restart();
            try { await store.CommitAsync(batch, token); }
            catch
            {
                // Use a fresh bound after cancellation to verify atomic rollback.
                using var rollbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Assert.Equal(before, await ReadStateAsync(capture.ConnectionString, stream, rollbackDeadline.Token));
                output.WriteLine("Failed append left checkpoint, head, counts and contact authority unchanged.");
                throw;
            }
            output.WriteLine("Append: {0:F3} ms; historical source SQL commands={1}",
                clock.Elapsed.TotalMilliseconds, capture.SourceCommands - commands);
            var after = await ReadStateAsync(capture.ConnectionString, stream, token);
            Assert.Equal(identity, after.Identity);
            Assert.Equal(before.Ordinal + 1, after.Ordinal);
            Assert.Equal(before.Publications + 1, after.Publications);
            Assert.Equal(before.Observations + 1, after.Observations);
            Assert.Equal(before.AcceptedThrough + 1, after.AcceptedThrough);
            Assert.Equal(before.Instance, after.Instance);
            Assert.Equal(482205m, after.NextSequence);
            Assert.Equal(batch.Checkpoint, await store.ReadCheckpointAsync(stream, token));
            var authority = await store.ReadAcquisitionContactAuthorityAsync(stream, token);
            Assert.NotNull(authority);
            Assert.Equal(timestamp, authority.SuccessfulContactTime);

            var restarted = new SqlServerObservationIngestionStore(capture.ConnectionString);
            var verified = await restarted.ReadExactAsync(stream, identity, token);
            Assert.Equal(AcquisitionEvidenceReadStatus.Found, verified.Status);
            var record = Assert.IsType<AcquisitionEvidenceRecord>(verified.Record);
            Assert.Equal(before.Identity, record.PreviousIdentity);
            Assert.Equal(after.Ordinal, record.PublicationOrdinal);
            Assert.Equal(payload, AcquisitionEvidenceCodec.Encode(record.Batch));
            var read = await restarted.ReadAsync(new ObservationReadRequest(stream,
                new ObservationPosition((ulong)before.AcceptedThrough), 1), token);
            Assert.Equal(observation, Assert.Single(read.Observations).Observation);
            Assert.False(read.HasMore);

            commands = capture.SourceCommands;
            clock.Restart();
            await restarted.CommitAsync(batch, token);
            output.WriteLine("Identical replay: {0:F3} ms; source SQL commands={1}",
                clock.Elapsed.TotalMilliseconds, capture.SourceCommands - commands);
            Assert.Equal(after, await ReadStateAsync(capture.ConnectionString, stream, token));
            Assert.Null(capture.Error);
            var recorded = capture.Spans.Where(span => span.OperationName == "acquisition.evidence.sources.verify")
                .Sum(span => Convert.ToInt64(span.GetTagItem("sql.command.count"), CultureInfo.InvariantCulture));
            Assert.Equal(capture.SourceCommands, recorded);
            output.WriteLine("PASS: one observation/publication appended; restart read and identical replay verified. Clone is now advanced; restore before another baseline run.");
        }
        finally
        {
            foreach (var span in capture.Spans)
                output.WriteLine("{0}: {1:F3} ms publications={2} observations={3} sqlCommands={4} completed={5}",
                    span.OperationName, span.Duration.TotalMilliseconds, span.GetTagItem("publication.count"),
                    span.GetTagItem("observation.count"), span.GetTagItem("sql.command.count"), span.GetTagItem("stage.completed"));
        }
    }

    private static string Required(string suffix) => Environment.GetEnvironmentVariable(Prefix + suffix)
        is { Length: > 0 } value ? value : throw new InvalidOperationException("Missing " + Prefix + suffix);

    private sealed record CloneState(string Identity, long Ordinal, decimal AcceptedThrough, decimal Instance,
        decimal NextSequence, long Observations, long Publications, string Contact);

    private static async Task<CloneState> ReadStateAsync(string connectionString, ObservationStreamId stream,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        Assert.Equal(Database, connection.Database);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            IF DB_NAME() <> N'GG003_ACQ_A2_20261026' THROW 51090, 'Unexpected benchmark database', 1;
            IF (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.MachineObservation')
                AND name IN (N'CK_MachineObservation_SignalType',N'CK_MachineObservation_Quality')
                AND is_disabled=0 AND is_not_trusted=0) <> 2
                THROW 51091, 'Observation enum constraints must be enabled and trusted', 1;
            SELECT p.IdentityHash,p.PublicationOrdinal,p.AcceptedThrough,c.InstanceId,c.NextSequence,
                (SELECT COUNT_BIG(*) FROM dbo.MachineObservation WHERE MachineId=@Machine AND StreamKeyBinary=@Stream),
                (SELECT COUNT_BIG(*) FROM dbo.AcquisitionEvidencePublication WHERE MachineId=@Machine AND StreamKeyBinary=@Stream),
                (SELECT * FROM dbo.AcquisitionContactAuthority WHERE MachineId=@Machine AND StreamKeyBinary=@Stream
                    FOR JSON PATH,INCLUDE_NULL_VALUES)
            FROM dbo.AcquisitionEvidenceHead h
            JOIN dbo.AcquisitionEvidencePublication p ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary AND p.IdentityHash=h.IdentityHash
            JOIN dbo.ObservationStreamCheckpoint c ON c.MachineId=h.MachineId AND c.StreamKeyBinary=h.StreamKeyBinary
            WHERE h.MachineId=@Machine AND h.StreamKeyBinary=@Stream;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = stream.MachineId.Value;
        command.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(stream.StreamKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken), "Expected exact stream head and checkpoint are missing.");
        var state = new CloneState(Convert.ToHexString((byte[])reader[0]), reader.GetInt64(1), reader.GetDecimal(2),
            reader.GetDecimal(3), reader.GetDecimal(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7));
        Assert.False(await reader.ReadAsync(cancellationToken), "Expected one exact stream head.");
        return state;
    }

    public sealed class A2CloneFactAttribute : FactAttribute
    {
        public A2CloneFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING")))
                Skip = "Opt-in disposable GG-003 A2 clone benchmark; connection not supplied.";
        }
    }
}

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class Gg003AcquisitionCloneBenchmarkDefinition
{
    public const string CollectionName = "GG-003 acquisition clone benchmark";
}
