using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerProductionReferenceTimeOutcomeStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerProductionReferenceTimeOutcomeStoreIntegrationTests(
        SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExactRevisionPreservesEarlierOutcomesAndOrdinaryReplay()
    {
        var processor = await CreateAuthorityAsync();
        var store = new SqlServerProductionReferenceTimeOutcomeStore(_fixture.ConnectionString);
        var first = CreateOutcome("source-a", 1, ProductionReferenceTimeResolutionStatus.Resolved);
        var second = CreateOutcome("source-b", 2, ProductionReferenceTimeResolutionStatus.AmbiguousStandard);

        Assert.Equal(first, await store.PublishAsync(processor, first, CancellationToken.None));
        Assert.Single(await store.ReadAtRevisionAsync(processor, new(1), CancellationToken.None));
        Assert.Equal(second, await store.PublishAsync(processor, second, CancellationToken.None));
        var old = await store.ReadAtRevisionAsync(processor, new(1), CancellationToken.None);
        Assert.Equal("source-a", Assert.Single(old).SourceQuantityEvidenceId.Value);
        var current = await store.ReadAtRevisionAsync(processor, new(2), CancellationToken.None);
        Assert.Equal(2, current.Count);
        Assert.Equal(first.Resolution.IdealDurationSeconds, current[0].Resolution.IdealDurationSeconds);
        Assert.Equal(second.Resolution.ConflictingStandardVersionIds,
            current[1].Resolution.ConflictingStandardVersionIds);

        Assert.Equal(first, await store.PublishAsync(processor, first, CancellationToken.None));
        var changed = new PublishedProductionReferenceTimeOutcome(new(3),
            first.Resolution with { IdealDurationSeconds = 99m });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PublishAsync(processor, changed, CancellationToken.None));
        Assert.Equal(2, (await store.ReadAtRevisionAsync(processor, new(2), CancellationToken.None)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAtRevisionAsync(processor, new(3), CancellationToken.None));
    }

    private async Task<MetricAggregationProcessorId> CreateAuthorityAsync()
    {
        var processor = new MetricAggregationProcessorId($"reference-time-{Guid.NewGuid():N}");
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.MetricInputStream (MachineId, StreamKeyBinary, StreamKey)
            VALUES (@Machine, @StreamBinary, @Stream);
            DECLARE @StreamRowId bigint = SCOPE_IDENTITY();
            INSERT INTO dbo.MetricAggregationProcessor
                (ProcessorKeyBinary, ProcessorKey, MetricInputStreamRowId)
            VALUES (@ProcessorBinary, @Processor, @StreamRowId);
            DECLARE @ProcessorRowId bigint = SCOPE_IDENTITY();
            INSERT INTO dbo.ProductionReferenceTimeRevision
                (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
            VALUES (@ProcessorRowId, 0);
            """;
        command.Parameters.AddWithValue("@Machine", Guid.NewGuid());
        command.Parameters.AddWithValue("@StreamBinary", Guid.NewGuid().ToByteArray());
        command.Parameters.AddWithValue("@Stream", $"stream-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@ProcessorBinary", Guid.NewGuid().ToByteArray());
        command.Parameters.AddWithValue("@Processor", processor.Value);
        await command.ExecuteNonQueryAsync();
        return processor;
    }

    private static PublishedProductionReferenceTimeOutcome CreateOutcome(
        string source, long revision, ProductionReferenceTimeResolutionStatus status)
    {
        var site = new SiteId("SITE-1");
        var start = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        var shift = new ShiftOccurrenceId(site, new("schedule-1"), new("shift-1"), start, start.AddHours(8));
        var resolved = status == ProductionReferenceTimeResolutionStatus.Resolved;
        return new PublishedProductionReferenceTimeOutcome(new(revision),
            new ProductionReferenceTimeResolution
            {
                SourceQuantityEvidenceId = new(source),
                CompanyId = new("company-1"),
                SiteId = site,
                MachineId = new(Guid.NewGuid()),
                PartId = new("part-1"),
                OperationId = new("operation-1"),
                ShiftOccurrenceId = shift,
                ProductionDayId = new(site, new DateOnly(2026, 9, 26)),
                OccurredAtUtc = start.AddMinutes(1),
                ProducedUnits = 2,
                AuthorityRevision = 4,
                Status = status,
                SelectedStandardVersionId = resolved ? "standard-1" : null,
                SelectedStandardSourceReference = resolved ? "engineering-approval-1" : null,
                IdealDurationSeconds = resolved ? 10m : null,
                ConflictingStandardVersionIds = resolved ? [] : ["standard-1", "standard-2"],
            });
    }
}
