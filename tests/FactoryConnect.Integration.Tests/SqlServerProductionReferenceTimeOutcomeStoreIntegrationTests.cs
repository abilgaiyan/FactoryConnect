using FactoryConnect.Abstractions;
using FactoryConnect.Core;
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

        await store.PublishAsync(processor, new(1), first, CancellationToken.None);
        Assert.Single(await store.ReadAtRevisionAsync(processor, new(1), CancellationToken.None));
        await store.PublishAsync(processor, new(2), second, CancellationToken.None);
        var old = await store.ReadAtRevisionAsync(processor, new(1), CancellationToken.None);
        Assert.Equal("source-a", Assert.Single(old).SourceQuantityEvidenceId.Value);
        var current = await store.ReadAtRevisionAsync(processor, new(2), CancellationToken.None);
        Assert.Equal(2, current.Count);
        Assert.Equal(first.Resolution.IdealDurationSeconds, current[0].Resolution.IdealDurationSeconds);
        Assert.Equal(second.Resolution.ConflictingStandardVersionIds,
            current[1].Resolution.ConflictingStandardVersionIds);
        Assert.Equal(1, await CountCutAsync(processor, 1, 1));
        Assert.Equal(1, await CountCutAsync(processor, 2, 2));

        var replay = await store.PublishAsync(processor, new(1), first, CancellationToken.None);
        Assert.Equal(first.PublicationRevision, replay.PublicationRevision);
        Assert.Equal(first.SourceQuantityEvidenceId, replay.SourceQuantityEvidenceId);
        Assert.Equal(first.Resolution.IdealDurationSeconds, replay.Resolution.IdealDurationSeconds);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PublishAsync(processor, new(2), first, CancellationToken.None));
        Assert.Equal(0, await CountCutAsync(processor, 2, 1));
        Assert.Equal(1, await CountCutAsync(processor, 1, 1));

        var changed = new PublishedProductionReferenceTimeOutcome(new(3),
            first.Resolution with { IdealDurationSeconds = 99m });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PublishAsync(processor, new(2), changed, CancellationToken.None));
        Assert.Equal(2, (await store.ReadAtRevisionAsync(processor, new(2), CancellationToken.None)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAtRevisionAsync(processor, new(3), CancellationToken.None));
    }

    [Fact]
    public async Task MissingAggregationRevisionRollsBackReferenceTimePublication()
    {
        var processor = await CreateAuthorityAsync();
        var store = new SqlServerProductionReferenceTimeOutcomeStore(_fixture.ConnectionString);
        var proposed = CreateOutcome("source-rollback", 1, ProductionReferenceTimeResolutionStatus.Resolved);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PublishAsync(processor, new(3), proposed, CancellationToken.None));

        Assert.Empty(await store.ReadAtRevisionAsync(processor, new(0), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAtRevisionAsync(processor, new(1), CancellationToken.None));
        Assert.Equal(0, await CountOutcomeAsync(processor, "source-rollback"));
        Assert.Equal(0, await CountCutAsync(processor, 3, 1));
    }

    [Fact]
    public async Task CanonicalPublisherRejectsTamperingAndChangedAuthorityCutWithoutDurablePublication()
    {
        var processor = await CreateAuthorityAsync();
        var publisher = new SqlServerCanonicalProductionReferenceTimePublisher(_fixture.ConnectionString);
        var store = new SqlServerProductionReferenceTimeOutcomeStore(_fixture.ConnectionString);
        var site = new SiteId("SITE-1");
        var machine = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var start = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        var shift = new ShiftOccurrenceId(site, new("schedule-1"), new("shift-1"), start, start.AddHours(8));
        var day = new ProductionDayId(site, new DateOnly(2026, 9, 26));
        var evidence = new ProductionQuantityEvidence
        {
            Id = new("source-canonical"),
            CompanyId = new("company-1"),
            SiteId = site,
            MachineId = machine,
            ShiftId = shift.ShiftId,
            PartId = new("part-1"),
            OperationId = new("operation-1"),
            OccurredAtUtc = start.AddMinutes(1),
            PartCountIncrement = 2,
        };
        var standards = new InMemoryProductionStandardAuthority();
        standards.Publish(new ProductionStandardVersion
        {
            VersionId = "standard-1",
            CompanyId = evidence.CompanyId,
            SiteId = site,
            PartId = evidence.PartId!,
            OperationId = evidence.OperationId!,
            SecondsPerUnit = 5m,
            EffectiveFromUtc = start,
            SourceReference = "engineering-approval-1",
            PublishedRevision = 1,
        });
        var originalCut = standards.ReadCurrentCut();
        var canonical = ProductionStandardResolver.Resolve(evidence, shift, day, originalCut);
        var proposed = new PublishedProductionReferenceTimeOutcome(new(1), canonical);

        async Task AssertRejectedWithoutPublicationAsync(
            ProductionStandardAuthorityCut cut,
            ProductionReferenceTimeResolution resolution)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
                processor,
                new(1),
                evidence,
                shift,
                day,
                cut,
                proposed with { Resolution = resolution },
                CancellationToken.None));

            Assert.Empty(await store.ReadAtRevisionAsync(processor, new(0), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ReadAtRevisionAsync(processor, new(1), CancellationToken.None));
            Assert.Equal(0, await CountOutcomeAsync(processor, evidence.Id.Value));
            Assert.Equal(0, await CountCutAsync(processor, 1, 1));
        }

        await AssertRejectedWithoutPublicationAsync(
            originalCut,
            canonical with { SelectedStandardVersionId = "tampered-version" });
        await AssertRejectedWithoutPublicationAsync(
            originalCut,
            canonical with { SelectedStandardSourceReference = "tampered-source" });
        await AssertRejectedWithoutPublicationAsync(
            originalCut,
            canonical with { IdealDurationSeconds = canonical.IdealDurationSeconds!.Value + 1m });

        standards.Publish(new ProductionStandardVersion
        {
            VersionId = "machine-standard-2",
            CompanyId = evidence.CompanyId,
            SiteId = site,
            PartId = evidence.PartId!,
            OperationId = evidence.OperationId!,
            MachineId = machine,
            SecondsPerUnit = 4m,
            EffectiveFromUtc = start,
            SourceReference = "engineering-approval-2",
            PublishedRevision = 2,
        });
        await AssertRejectedWithoutPublicationAsync(standards.ReadCurrentCut(), canonical);

        var published = await publisher.PublishAsync(
            processor,
            new(1),
            evidence,
            shift,
            day,
            originalCut,
            proposed,
            CancellationToken.None);

        Assert.Equal(proposed.PublicationRevision, published.PublicationRevision);
        Assert.Equal(proposed.SourceQuantityEvidenceId, published.SourceQuantityEvidenceId);
        Assert.Equal(canonical.IdealDurationSeconds, published.Resolution.IdealDurationSeconds);
        Assert.Single(await store.ReadAtRevisionAsync(processor, new(1), CancellationToken.None));
        Assert.Equal(1, await CountOutcomeAsync(processor, evidence.Id.Value));
        Assert.Equal(1, await CountCutAsync(processor, 1, 1));
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
            INSERT INTO dbo.MetricAggregationRevision
                (MetricAggregationProcessorRowId, Position)
            VALUES (@ProcessorRowId, 1), (@ProcessorRowId, 2);
            INSERT INTO dbo.ProductionReferenceTimeRevision
                (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
            VALUES (@ProcessorRowId, 0);
            INSERT INTO dbo.ProductionReferenceTimePublicationCut
                (MetricAggregationProcessorRowId, MetricAggregationPosition, ProductionReferenceTimeRevision)
            VALUES (@ProcessorRowId, 1, 0), (@ProcessorRowId, 2, 0);
            """;
        command.Parameters.AddWithValue("@Machine", Guid.NewGuid());
        command.Parameters.AddWithValue("@StreamBinary", Guid.NewGuid().ToByteArray());
        command.Parameters.AddWithValue("@Stream", $"stream-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@ProcessorBinary", Guid.NewGuid().ToByteArray());
        command.Parameters.AddWithValue("@Processor", processor.Value);
        await command.ExecuteNonQueryAsync();
        return processor;
    }

    private async Task<int> CountCutAsync(
        MetricAggregationProcessorId processor, long aggregationPosition, long referenceTimeRevision)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.ProductionReferenceTimePublicationCut c
            INNER JOIN dbo.MetricAggregationProcessor p
                ON p.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
            WHERE p.ProcessorKey = @Processor
              AND c.MetricAggregationPosition = @Position
              AND c.ProductionReferenceTimeRevision = @Revision;
            """;
        command.Parameters.AddWithValue("@Processor", processor.Value);
        command.Parameters.AddWithValue("@Position", aggregationPosition);
        command.Parameters.AddWithValue("@Revision", referenceTimeRevision);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> CountOutcomeAsync(MetricAggregationProcessorId processor, string source)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.ProductionReferenceTimeOutcome o
            INNER JOIN dbo.MetricAggregationProcessor p
                ON p.MetricAggregationProcessorRowId = o.MetricAggregationProcessorRowId
            WHERE p.ProcessorKey = @Processor AND o.SourceQuantityEvidenceId = @Source;
            """;
        command.Parameters.AddWithValue("@Processor", processor.Value);
        command.Parameters.AddWithValue("@Source", source);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
