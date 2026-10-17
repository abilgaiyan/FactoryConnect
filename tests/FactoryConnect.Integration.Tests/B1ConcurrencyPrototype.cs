using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Validation executable only. JSON receipts and B1-only digest domains intentionally
// cannot be mistaken for FCWE/FCDS production codecs or publication identities.
internal sealed class B1ConcurrencyPrototype : IAsyncDisposable
{
    internal const string Policy = AvailabilityContainmentV1.PolicyId;
    private static readonly string[] PublicationPrefixTables = ["OperationalMetricProjection", "OperationalMetricProjectionManifest", "OperationalMetricProjectionEvidence"];
    private static readonly JsonSerializerOptions ReceiptOptions = CreateReceiptOptions();
    private readonly SqlStartupIsolatedDatabase database;
    private readonly ITestOutputHelper output;
    private readonly string eventSession = "B1_" + Guid.NewGuid().ToString("N");
    private bool eventsStarted;
    internal readonly ConcurrentQueue<object> Events = new();
    internal string ConnectionString => database.ConnectionString;
    internal readonly MachineId Machine = new(Guid.NewGuid());
    internal readonly MetricAggregationProcessorId Aggregation = new("b1-concurrency-source");
    internal MetricAggregationCheckpoint Initial = null!, Bound = null!;
    internal readonly List<PositionedMetricInputFact> Inputs = [];
    internal ProductionStandardAuthorityCut Standards = null!;
    internal PublishedProductionReferenceTimeOutcome Outcome = null!;
    internal PublishedProductionReferenceTimeOutcome[] AllOutcomes = [];
    internal long ReferenceRevision => AllOutcomes.LongLength;
    internal ulong WitnessPosition => Inputs.First(x => x.Fact.Key == MetricInputFactKeys.RunningDuration).Position.Value;
    internal long AggregationRow, StreamRow, TemplateRow;
    internal byte[] KeyHash = [], KeyBytes = [];
    internal OperationalMetricEvaluationKey Key = null!;
    internal OperationalMetricProjection Template = null!;
    internal SqlServerMetricAggregationStore AggregationStore => new(ConnectionString);
    internal SqlServerProductionReferenceTimePublicationTransitionStore Transitions => new(ConnectionString);
    internal SqlServerProductionReferenceTimeTransitionOutcomeStore Admissions => new(ConnectionString);
    internal SqlServerProductionReferenceTimeOutcomeStore Legacy => new(ConnectionString);
    internal readonly SiteId Site = new("B1-SITE");
    internal readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
    internal ShiftOccurrenceId Shift => new(Site, new("B1-SCHEDULE"), new("B1-SHIFT"), Start, Start.AddHours(8));
    private B1ConcurrencyPrototype(SqlStartupIsolatedDatabase database, ITestOutputHelper output)
    { this.database = database; this.output = output; }

    internal static async Task<B1ConcurrencyPrototype> Create(ITestOutputHelper output, bool complete = true, int quantities = 1)
    {
        var f = new B1ConcurrencyPrototype(await SqlStartupIsolatedDatabase.CreateAsync(), output);
        try { await f.Initialize(complete, quantities); return f; }
        catch { await f.DisposeAsync(); throw; }
    }
    private async Task Initialize(bool complete, int quantities)
    {
        await using var c = await Open();
        Events.Enqueue(new { FixtureDatabase = c.Database, Machine = Machine.Value, Runtime = Environment.Version.ToString(), SqlVersion = await Scalar(c, null, "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'));") });
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(c, TimeSpan.FromMinutes(2), CancellationToken.None);
        var authority = new SqlServerProductionStandardAuthority(ConnectionString);
        await authority.PublishAsync(new ProductionStandardVersion
        {
            VersionId = "b1-approved-standard", CompanyId = new("B1-COMPANY"), SiteId = Site,
            PartId = new("B1-PART"), OperationId = new("B1-OP"), MachineId = Machine,
            SecondsPerUnit = 5m, EffectiveFromUtc = Start.AddDays(-1), SourceReference = "synthetic-approved-fixture", PublishedRevision = 1,
        });
        Standards = await authority.ReadCutAsync(1);
        var padding = await Append("b1.padding", 1, null);
        Initial = new(Aggregation, padding.StreamId, padding.Position);
        await AggregationStore.CommitAsync(new(Aggregation, null, Initial, [padding]), CancellationToken.None);
        Assert.True((await new SqlServerProductionReferenceTimeConvergenceCoordinator(ConnectionString)
            .ConvergeAsync(Initial, Machine, null)).IsCompleted);
        var quantityInputs = new List<PositionedMetricInputFact>();
        for (var i = 0; i < quantities; i++) quantityInputs.Add(await Append(MetricInputFactKeys.PartCountIncrement, 2, null));
        var running = await Append(MetricInputFactKeys.RunningDuration, 3, false);
        var planned = await Append(MetricInputFactKeys.PlannedProductionDuration, 4, true);
        Bound = new(Aggregation, planned.StreamId, planned.Position);
        await AggregationStore.CommitAsync(new(Aggregation, Initial, Bound, quantityInputs.Concat([running, planned]).ToArray()), CancellationToken.None);
        var sourceSet = await AggregationStore.ReadProductionQuantityTransitionSourcesAsync(Bound, Machine, Initial.Position);
        Assert.Equal(quantities, sourceSet.Count);
        AllOutcomes = sourceSet.Select((source, index) => new PublishedProductionReferenceTimeOutcome(new(index + 1),
            ProductionStandardResolver.Resolve(source.Evidence, source.ShiftOccurrenceId, source.ProductionDayId, Standards))).ToArray();
        Outcome = AllOutcomes[0];
        if (complete) { await Begin(); foreach (var outcome in AllOutcomes) await Admissions.AdmitAsync(Aggregation, Bound.Position, outcome); await Transitions.CompleteAsync(Aggregation, Bound.Position); }
        Key = new(Machine, new OperationalMetricPeriodId.Shift(Shift), BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned);
        var templateProcessor = new OperationalMetricProjectionProcessorId("b1-legacy-template");
        Template = new(templateProcessor, Key, OperationalMetricEvaluationStatus.Calculated, 0.75m, "ratio", null, null, Bound,
            Snapshot(Inputs).Components.Select(component => new OperationalMetricComponentProjectionEvidence(component.OperandName,
                component.SourceIdentity, Bound, component.Dimension, component.Aggregate.Value, component.Aggregate.Unit,
                component.Aggregate.InputCount, component.Aggregate.FirstInputTimestamp, component.Aggregate.LastInputTimestamp)));
        var commit = new OperationalMetricProjectionCommit(templateProcessor, null,
            new(templateProcessor, Bound, new([Key])), [Template]);
        await new SqlServerOperationalMetricProjectionCommitTransaction(ConnectionString).ExecuteAsync(commit,
            async (context, token) => await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(context, commit, token), CancellationToken.None);
        AggregationRow = (long)(await Scalar(c, null, "SELECT MetricAggregationProcessorRowId FROM dbo.MetricAggregationProcessor;"))!;
        StreamRow = (long)(await Scalar(c, null, "SELECT MetricInputStreamRowId FROM dbo.MetricInputStream;"))!;
        TemplateRow = (long)(await Scalar(c, null, "SELECT OperationalMetricProjectionRowId FROM dbo.OperationalMetricProjection;"))!;
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT EvaluationKeyHash,EvaluationKeyBinary FROM dbo.OperationalMetricProjection;";
            await using var r = await cmd.ExecuteReaderAsync(); await r.ReadAsync(); KeyHash = (byte[])r[0]; KeyBytes = (byte[])r[1];
        }
        using var resource = typeof(B1ConcurrencyPrototype).Assembly.GetManifestResourceStream("B1RC2.ProposedDdl")!;
        using var reader = new StreamReader(resource);
        await using var t = c.BeginTransaction();
        foreach (var batch in B1ProposalBatchReader.Split(await reader.ReadToEndAsync())) await Exec(c, t, batch);
        await t.CommitAsync();
        await Exec(c, null, "CREATE TABLE dbo.B1MutationAudit (Id bigint IDENTITY, TableName nvarchar(128));");
        foreach (var table in new[] { "OperationalMetricConflictWitnessArchive", "OperationalMetricPublicationResultArchive", "OperationalMetricResultWitnessReference", "OperationalMetricProjection", "OperationalMetricProjectionEvidence", "OperationalMetricProjectionManifest", "OperationalMetricProjectionCheckpoint" })
            await Exec(c, null, $"CREATE TRIGGER dbo.B1Audit_{table} ON dbo.{table} AFTER INSERT,UPDATE,DELETE AS BEGIN SET NOCOUNT ON; INSERT dbo.B1MutationAudit(TableName) VALUES (N'{table}'); END;");
        await Exec(c, null, "CREATE TRIGGER dbo.B1CheckpointFault ON dbo.OperationalMetricProjectionCheckpoint AFTER UPDATE AS BEGIN SET NOCOUNT ON; IF CONVERT(nvarchar(64),SESSION_CONTEXT(N'b1-fault'))=N'checkpoint' THROW 51088,'B1 checkpoint fault',1; END;");
        var dbid = (int)(await Scalar(c, null, "SELECT DB_ID();"))!;
        await Exec(c, null, $"CREATE EVENT SESSION [{eventSession}] ON SERVER ADD EVENT sqlserver.xml_deadlock_report, ADD EVENT sqlserver.sql_statement_completed(ACTION(sqlserver.session_id,sqlserver.transaction_id) WHERE ([sqlserver].[database_id]=({dbid}))), ADD EVENT sqlserver.sp_statement_completed(ACTION(sqlserver.session_id,sqlserver.transaction_id) WHERE ([sqlserver].[database_id]=({dbid}))) ADD TARGET package0.ring_buffer WITH(MAX_DISPATCH_LATENCY=1 SECONDS); ALTER EVENT SESSION [{eventSession}] ON SERVER STATE=START;");
        eventsStarted = true;
    }
    internal Task<ProductionReferenceTimePublicationTransition> Begin() => Transitions.BeginAsync(Aggregation, Bound.Position, Initial.Position, new(0), 1);
    internal async Task<PositionedMetricInputFact> Append(string key, decimal value, bool? planned)
    {
        var n = Inputs.Count + 1;
        var fact = new DurableMetricInputFact
        {
            Id = new($"b1-fact-{n}"), Key = key, Value = value, Unit = key == MetricInputFactKeys.PartCountIncrement ? MetricInputFactUnits.Count : MetricInputFactUnits.Seconds,
            StartsAtUtc = Start.AddMinutes(n), EndsAtUtc = Start.AddMinutes(n).AddSeconds((double)value),
            CompanyId = new("B1-COMPANY"), SiteId = Site, MachineId = Machine, ShiftId = Shift.ShiftId, ShiftScheduleAssignmentId = Shift.ShiftScheduleAssignmentId,
            PartId = new("B1-PART"), OperationId = new("B1-OP"), IsPlannedProductionTime = planned,
            PlannedProductionScheduleAssignmentId = planned == true ? new("B1-PLAN") : null,
            SourceEligibilityIntervalId = planned is not null ? new($"b1-eligibility-{n}") : null,
            SourceContextualizedActivityIntervalId = planned is not null ? new($"b1-activity-{n}") : null,
            SourceQuantityEvidenceId = key == MetricInputFactKeys.PartCountIncrement ? new($"b1-quantity-{n}") : null,
        };
        var input = await new SqlServerMetricInputStore(ConnectionString).AppendAsync(new(MetricInputStreamId.ForMachine(Machine), fact, Shift, new(Site, new(2026, 10, 16))), CancellationToken.None);
        Inputs.Add(input); return input;
    }
    internal OperationalMetricComponentSnapshot Snapshot(IReadOnlyList<PositionedMetricInputFact> inputs, OperationalMetricEvaluationKey? key = null)
    {
        key ??= Key;
        var request = new OperationalMetricComponentSnapshotRequest(key, Aggregation,
            BuiltInOperationalMetricDefinitions.All.Single(d => d.Id == BuiltInOperationalMetricDefinitions.AvailabilityId).Operands, true);
        var set = MetricInputContributionAggregator.Aggregate(Bound.StreamId, inputs);
        var components = request.Operands.Select(operand =>
        {
            var componentKey = OperationalMetricComponentKeyMapping.AggregateKey(((OperationalMetricOperandSource.Component)operand.Source).ComponentKey);
            var aggregate = (MetricAggregateValue)Invoke(typeof(SqlServerMetricAggregationStore), "ReadHistoricalAggregate", set, key, componentKey)!;
            return new OperationalMetricComponent(operand.OperandName, new(Aggregation, Machine, key.PeriodId, componentKey), operand.RequiredDimension, aggregate);
        }).ToArray();
        return new(key, Bound, components, AvailabilityScopeConflictEvidenceBuilder.Build(request, Bound, inputs, components));
    }
    internal byte[] Receipt(string processor, bool reference, OperationalMetricComponentSnapshot? snapshot = null, IReadOnlyList<ProductionReferenceTimeResolution>? outcomes = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        ValidationOnly = true, Policy, Processor = processor, Source = Bound, Key, Status = "Unavailable", Reason = "RunningOutsidePlannedProduction",
        Snapshot = snapshot ?? Snapshot(Inputs.Where(x => x.Position <= Bound.Position).ToArray()),
        R = reference ? (long?)ReferenceRevision : null, Outcomes = reference ? outcomes ?? AllOutcomes.Select(x => x.Resolution).ToArray() : [],
    }, ReceiptOptions);

    internal static byte[] WitnessBytes(AvailabilityScopeConflictEvidenceV1 witness) => JsonSerializer.SerializeToUtf8Bytes(witness, ReceiptOptions);
    private static JsonSerializerOptions CreateReceiptOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(OperationalMetricPeriodId)) return;
            info.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$period",
                DerivedTypes =
                {
                    new JsonDerivedType(typeof(OperationalMetricPeriodId.Shift), "shift"),
                    new JsonDerivedType(typeof(OperationalMetricPeriodId.ProductionDay), "production-day"),
                },
            };
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }
    internal async Task WriteWitnessSet(SqlConnection c, SqlTransaction t, IEnumerable<AvailabilityScopeConflictEvidenceV1> incoming, Func<Task>? afterFirst = null)
    {
        var entries = incoming.Select(witness => new { Witness = witness, Payload = WitnessBytes(witness) })
            .Select(x => new { x.Witness, x.Payload, Id = Digest("B1ValidationOnly/Witness/", x.Payload) })
            .OrderBy(x => Convert.ToHexString(x.Id), StringComparer.Ordinal).ToArray();
        var first = true;
        foreach (var entry in entries)
        {
            Assert.Equal(Bound, entry.Witness.SourceRevision);
            var position = (decimal)entry.Witness.Contribution.Position.Value;
            var fact = await Scalar(c, t, "SELECT MetricInputFactRowId FROM dbo.MetricInputFact WITH(HOLDLOCK) WHERE MetricInputStreamRowId=@S AND Position=@Position;", ("@S", StreamRow), ("@Position", position));
            var existing = await Scalar(c, t, "SELECT WitnessPayload FROM dbo.OperationalMetricConflictWitnessArchive WITH(UPDLOCK,HOLDLOCK) WHERE WitnessId=@W;", ("@W", entry.Id));
            if (existing is byte[] bytes)
            { if (!bytes.SequenceEqual(entry.Payload)) throw new InvalidDataException("StoredEvidenceIntegrityFailure: witness identity collision"); }
            else await Exec(c, t, "INSERT dbo.OperationalMetricConflictWitnessArchive VALUES(@W,1,@Payload,@A,@S,@Cut,@Fact,@Position);",
                ("@W", entry.Id), ("@Payload", entry.Payload), ("@A", AggregationRow), ("@S", StreamRow), ("@Cut", (decimal)Bound.Position.Value), ("@Fact", fact!), ("@Position", position));
            await Trace("archive-key:" + Convert.ToHexString(entry.Id), c, t);
            if (first && afterFirst is not null) await afterFirst();
            first = false;
        }
    }

    private async Task VerifyStored(SqlConnection c, SqlTransaction t, Publisher publisher, byte[] payload)
    {
        var witnessObject = Snapshot(Inputs.Where(x => x.Position <= Bound.Position).ToArray()).AvailabilityScopeConflictEvidence!;
        var witnessPayload = WitnessBytes(witnessObject);
        var witness = Digest("B1ValidationOnly/Witness/", witnessPayload);
        var result = Digest("B1ValidationOnly/Result/", payload);
        var count = await Scalar(c, t, """
            SELECT COUNT(*) FROM dbo.OperationalMetricPublicationResultArchive WITH(HOLDLOCK)
            WHERE OperationalMetricProjectionProcessorRowId=@P AND EvaluationKeyHash=@Key AND SourceRevisionPosition=@Cut
            AND ResultId=@Result AND EvaluationKeyBinary=@KeyBytes AND EvaluationPolicyId=@Policy
            AND MetricAggregationProcessorRowId=@A AND MetricInputStreamRowId=@S
            AND PublicationFormatVersion=2 AND ResultCodecVersion=2 AND Status=1 AND ReasonCode=6 AND ConflictWitnessId=@W;
            """, ("@P", publisher.Row), ("@Key", KeyHash), ("@Cut", (decimal)Bound.Position.Value), ("@Result", result), ("@KeyBytes", KeyBytes), ("@Policy", Policy), ("@A", AggregationRow), ("@S", StreamRow), ("@W", witness));
        if (!Equals(count, 1)) throw new InvalidDataException("StoredEvidenceIntegrityFailure: result metadata");
        var archivedWitness = await Scalar(c, t, "SELECT WitnessPayload FROM dbo.OperationalMetricConflictWitnessArchive WITH(HOLDLOCK) WHERE WitnessId=@W AND WitnessCodecVersion=1 AND MetricAggregationProcessorRowId=@A AND MetricInputStreamRowId=@S AND SourceRevisionPosition=@Cut AND ContributionPosition=@Position;",
            ("@W", witness), ("@A", AggregationRow), ("@S", StreamRow), ("@Cut", (decimal)Bound.Position.Value), ("@Position", (decimal)WitnessPosition));
        if (archivedWitness is not byte[] bytes || !bytes.SequenceEqual(witnessPayload)) throw new InvalidDataException("StoredEvidenceIntegrityFailure: witness payload");
        var references = await Scalar(c, t, "SELECT COUNT(*) FROM dbo.OperationalMetricResultWitnessReference WITH(HOLDLOCK) WHERE ResultId=@Result;", ("@Result", result));
        var root = await Scalar(c, t, "SELECT COUNT(*) FROM dbo.OperationalMetricResultWitnessReference WITH(HOLDLOCK) WHERE ResultId=@Result AND NodePath=0x00000000 AND WitnessId=@W;", ("@Result", result), ("@W", witness));
        if (!Equals(references, 1) || !Equals(root, 1)) throw new InvalidDataException("StoredEvidenceIntegrityFailure: reference closure");
        var retained = await Scalar(c, t, """
            SELECT COUNT(*) FROM dbo.OperationalMetricProjection p JOIN dbo.OperationalMetricProjectionManifest m ON m.OperationalMetricProjectionRowId=p.OperationalMetricProjectionRowId
            JOIN dbo.OperationalMetricProjectionCheckpoint cp ON cp.OperationalMetricProjectionProcessorRowId=m.OperationalMetricProjectionProcessorRowId
            WHERE p.OperationalMetricProjectionProcessorRowId=@P AND p.PublicationFormatVersion=2 AND p.EvaluationPolicyId=@Policy
            AND p.ContainmentResultId=@Result AND p.ConflictWitnessId=@W AND p.Status=1 AND p.ReasonCode=6 AND p.MetricValue IS NULL
            AND p.EvaluationKeyBinary=@KeyBytes AND p.SourceRevisionPosition=@Cut AND cp.Position=@Cut;
            """, ("@P", publisher.Row), ("@Policy", Policy), ("@Result", result), ("@W", witness), ("@KeyBytes", KeyBytes), ("@Cut", (decimal)Bound.Position.Value));
        if (!Equals(retained, 1)) throw new InvalidDataException("StoredEvidenceIntegrityFailure: retained binding");
        foreach (var component in Snapshot(Inputs.Where(x => x.Position <= Bound.Position).ToArray()).Components)
        {
            var evidence = await Scalar(c, t, """
                SELECT COUNT(*) FROM dbo.OperationalMetricProjectionEvidence e JOIN dbo.OperationalMetricProjection p ON p.OperationalMetricProjectionRowId=e.OperationalMetricProjectionRowId
                WHERE p.OperationalMetricProjectionProcessorRowId=@P AND e.EvidenceKind=1 AND e.OperandName=@Operand AND e.ComponentKey=@Component
                AND e.MetricDimension=@Dimension AND e.ComponentValue=@Value AND e.ComponentUnit=@Unit AND e.InputCount=@Count AND e.FirstInputTimestamp=@First AND e.LastInputTimestamp=@Last;
                """, ("@P", publisher.Row), ("@Operand", component.OperandName), ("@Component", component.SourceIdentity.ComponentKey), ("@Dimension", (byte)component.Dimension),
                ("@Value", SqlServerCanonicalDecimalCodec.Serialize(component.Aggregate.Value)), ("@Unit", component.Aggregate.Unit), ("@Count", component.Aggregate.InputCount), ("@First", component.Aggregate.FirstInputTimestamp), ("@Last", component.Aggregate.LastInputTimestamp));
            if (!Equals(evidence, 1)) throw new InvalidDataException("StoredEvidenceIntegrityFailure: component evidence");
        }
    }
    internal async Task<Publisher> NewPublisher(string name, bool reference = false)
    {
        var id = new OperationalMetricProjectionProcessorId("b1-validation-only:" + name);
        await using var c = await Open();
        var row = (long)(await Scalar(c, null, """
            INSERT dbo.OperationalMetricProjectionProcessor(ProcessorKeyBinary,ProcessorKey,MetricAggregationProcessorRowId,MetricInputStreamRowId,PublicationFormatVersion,EvaluationPolicyId)
            OUTPUT INSERTED.OperationalMetricProjectionProcessorRowId VALUES(@Key,@Name,@A,@S,2,@Policy);
            """, ("@Key", StringOrderKeyV2Codec.Encode(id.Value)), ("@Name", id.Value), ("@A", AggregationRow), ("@S", StreamRow), ("@Policy", Policy)))!;
        await Exec(c, null, "INSERT dbo.OperationalMetricProjectionCheckpoint VALUES(@P,@A);", ("@P", row), ("@A", (decimal)Initial.Position.Value));
        return new Publisher(this, id, row, reference);
    }

    // Bounded source order under caller locks: exact revision -> exact ledger ->
    // target terminal transition -> exact (A,R) pair -> bound R/outcomes -> standards.
    internal async Task<byte[]> Validate(SqlConnection c, SqlTransaction t, Publisher publisher, CancellationToken token)
    {
        if ((int)(await Scalar(c, t, "SELECT COUNT(*) FROM dbo.MetricAggregationRevision WITH(HOLDLOCK) WHERE MetricAggregationProcessorRowId=@A AND Position=@Cut;", ("@A", AggregationRow), ("@Cut", (decimal)Bound.Position.Value)))! != 1) throw new InvalidDataException("HistoricalEvidenceUnavailable");
        var sql = LedgerSelect;
        var inputs = new List<PositionedMetricInputFact>();
        await using (var cmd = Command(c, t, sql, ("@A", AggregationRow), ("@S", StreamRow), ("@Cut", (decimal)Bound.Position.Value)))
        await using (var r = await cmd.ExecuteReaderAsync(token))
            while (await r.ReadAsync(token)) inputs.Add((PositionedMetricInputFact)Invoke(typeof(SqlServerMetricAggregationStore), "MaterializeHistoricalInput", r)!);
        var snapshot = Snapshot(inputs);
        Assert.Equal(3m, snapshot.Components.Single(x => x.OperandName == "ActualProductionTime").Aggregate.Value);
        Assert.Equal(WitnessPosition, snapshot.AvailabilityScopeConflictEvidence!.Contribution.Position.Value);
        var evaluation = await new OperationalMetricEvaluator(new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All),
            new TransactionSnapshot(snapshot), Aggregation, useAvailabilityContainmentV1: true).EvaluateAsync(Key, token);
        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, evaluation.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, evaluation.ReasonCode);
        Assert.Equal(snapshot.AvailabilityScopeConflictEvidence, evaluation.AvailabilityScopeConflictEvidence);
        var outcomes = new List<ProductionReferenceTimeResolution>();
        if (publisher.Reference)
        {
            await Scalar(c, t, "SELECT COUNT_BIG(*) FROM dbo.ProductionReferenceTimePublicationTransition WITH(HOLDLOCK) WHERE MetricAggregationProcessorRowId=@A AND TargetMetricAggregationPosition=@Cut;", ("@A", AggregationRow), ("@Cut", (decimal)Bound.Position.Value));
            var transition = (ProductionReferenceTimePublicationTransition?)await (Task<ProductionReferenceTimePublicationTransition?>)Invoke(typeof(SqlServerProductionReferenceTimePublicationTransitionStore), "ReadAsync",
                c, t, Aggregation, AggregationRow, Bound.Position, false, token)!;
            if (transition is not { IsCompleted: true, CompletedReferenceTimeRevision: not null }) throw new InvalidDataException("HistoricalEvidenceUnavailable");
            if (transition.CompletedReferenceTimeRevision.Value.Value != ReferenceRevision) throw new InvalidDataException("StoredEvidenceIntegrityFailure");
            await Scalar(c, t, "SELECT COUNT_BIG(*) FROM dbo.ProductionReferenceTimeRevision WITH(HOLDLOCK) WHERE MetricAggregationProcessorRowId=@A AND ProductionReferenceTimeRevision=@R;", ("@A", AggregationRow), ("@R", ReferenceRevision));
            Assert.Equal(1, (int)(await Scalar(c, t, "SELECT COUNT(*) FROM dbo.ProductionReferenceTimePublicationCut WITH(HOLDLOCK) WHERE MetricAggregationProcessorRowId=@A AND MetricAggregationPosition=@Cut AND ProductionReferenceTimeRevision=@R;", ("@A", AggregationRow), ("@Cut", (decimal)Bound.Position.Value), ("@R", ReferenceRevision)))!);
            var select = (string)typeof(SqlServerProductionReferenceTimeOutcomeStore).GetField("OutcomeSelect", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            select = select.Replace("ProductionReferenceTimeOutcome o", "ProductionReferenceTimeOutcome o WITH(HOLDLOCK)", StringComparison.Ordinal)
                .Replace("ProductionReferenceTimeOutcomeConflict c", "ProductionReferenceTimeOutcomeConflict c WITH(HOLDLOCK)", StringComparison.Ordinal);
            await using (var cmd = Command(c, t, select + " WHERE o.MetricAggregationProcessorRowId=@A AND o.ProductionReferenceTimeRevision<=@R ORDER BY o.ProductionReferenceTimeRevision,o.SourceQuantityEvidenceId;", ("@A", AggregationRow), ("@R", ReferenceRevision)))
            await using (var r = await cmd.ExecuteReaderAsync(token))
                while (await r.ReadAsync(token)) outcomes.Add(((PublishedProductionReferenceTimeOutcome)Invoke(typeof(SqlServerProductionReferenceTimeOutcomeStore), "Materialize", r)!).Resolution);
            Assert.Equal(1, (int)(await Scalar(c, t, "SELECT COUNT(*) FROM dbo.ProductionStandardAuthorityRevision WITH(HOLDLOCK) WHERE ProductionStandardAuthorityRevision=1;"))!);
            var versions = new List<ProductionStandardVersion>();
            await using (var cmd = Command(c, t, "SELECT VersionId,ProductionStandardAuthorityRevision,CompanyId,SiteId,PartId,OperationId,MachineId,SecondsPerUnit,EffectiveFromUtc,EffectiveToUtc,SourceReference FROM dbo.ProductionStandardVersion WITH(HOLDLOCK) WHERE ProductionStandardAuthorityRevision<=1 ORDER BY ProductionStandardAuthorityRevision;"))
            await using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) versions.Add((ProductionStandardVersion)Invoke(typeof(SqlServerProductionStandardAuthority), "Materialize", r)!);
            var standards = ProductionStandardAuthorityCut.Materialize(1, versions);
            var sources = inputs.Where(x => x.Fact.SourceQuantityEvidenceId is not null).GroupBy(x => x.Fact.SourceQuantityEvidenceId!.Value)
                .Select(group => (ProductionQuantitySourceEntry)Invoke(typeof(SqlServerMetricAggregationStore), "ReconstructSource", group.Key, group.OrderBy(x => x.Position).ToArray())!).ToArray();
            Assert.Equal(sources.Length, outcomes.Count);
            foreach (var source in sources)
            {
                var expected = ProductionStandardResolver.Resolve(source.Evidence, source.ShiftOccurrenceId, source.ProductionDayId, standards);
                var actual = outcomes.Single(x => x.SourceQuantityEvidenceId == source.Evidence.Id);
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            }
        }
        return Receipt(publisher.Id.Value, publisher.Reference, snapshot, outcomes);
    }

    // Adapter over an already materialized, transaction-protected SQL snapshot;
    // no manufactured evaluation or authority outcome is supplied to the evaluator.
    private sealed class TransactionSnapshot(OperationalMetricComponentSnapshot snapshot) : IOperationalMetricComponentSnapshotReader
    {
        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(OperationalMetricComponentSnapshotRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(snapshot.EvaluationKey, request.EvaluationKey);
            var components = request.Operands.Select(operand =>
            {
                var key = OperationalMetricComponentKeyMapping.AggregateKey(((OperationalMetricOperandSource.Component)operand.Source).ComponentKey);
                var component = snapshot.Components.Single(x => x.SourceIdentity.ComponentKey == key);
                return new OperationalMetricComponent(operand.OperandName, component.SourceIdentity, component.Dimension, component.Aggregate);
            });
            return ValueTask.FromResult(new OperationalMetricComponentSnapshot(request.EvaluationKey, snapshot.Revision, components, snapshot.AvailabilityScopeConflictEvidence));
        }
    }

    internal sealed class Publisher(B1ConcurrencyPrototype f, OperationalMetricProjectionProcessorId id, long row, bool reference)
    {
        internal OperationalMetricProjectionProcessorId Id => id;
        internal long Row => row;
        internal bool Reference => reference;
        internal Func<string, SqlConnection, SqlTransaction, Task>? AtStage;
        internal string? Fault;
        internal async Task<string> Publish(byte[]? replacementPayload = null, CancellationToken token = default)
        {
            var payload = replacementPayload ?? f.Receipt(id.Value, reference);
            var result = Digest("B1ValidationOnly/Result/", payload);
            var witnessObject = f.Snapshot(f.Inputs.Where(x => x.Position <= f.Bound.Position).ToArray()).AvailabilityScopeConflictEvidence!;
            var witnessPayload = WitnessBytes(witnessObject);
            var witness = Digest("B1ValidationOnly/Witness/", witnessPayload);
            var intent = new OperationalMetricProjection(id, f.Key, f.Template.Status, f.Template.Value, f.Template.Unit,
                f.Template.ReasonCode, f.Template.ReasonOperandName, f.Bound, f.Template.OperandEvidence);
            var commit = new OperationalMetricProjectionCommit(id, new(id, f.Initial, new([])), new(id, f.Bound, new([f.Key])), [intent]);
            var outcome = "Published";
            await new SqlServerOperationalMetricProjectionCommitTransaction(f.ConnectionString).ExecuteAsync(commit, async (context, ct) =>
            {
                var c = context.Connection; var t = context.Transaction;
                await Exec(c, t, "EXEC sys.sp_set_session_context @key=N'b1-fault',@value=NULL;");
                // Retain frozen prefix after the real gate/processor/checkpoint path.
                foreach (var table in PublicationPrefixTables)
                {
                    var predicate = table == "OperationalMetricProjectionEvidence"
                        ? "OperationalMetricProjectionRowId IN (SELECT OperationalMetricProjectionRowId FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=@P)"
                        : "OperationalMetricProjectionProcessorRowId=@P";
                    await Scalar(c, t, $"SELECT COUNT_BIG(*) FROM dbo.{table} WITH(UPDLOCK,HOLDLOCK) WHERE {predicate};", ("@P", row));
                }
                await Stage("prefix", c, t);
                await using (var cmd = Command(c, t, "SELECT EvaluationKeyBinary,ResultPayload FROM dbo.OperationalMetricPublicationResultArchive WITH(UPDLOCK,HOLDLOCK) WHERE OperationalMetricProjectionProcessorRowId=@P AND EvaluationKeyHash=@Key AND SourceRevisionPosition=@Cut;", ("@P", row), ("@Key", f.KeyHash), ("@Cut", (decimal)f.Bound.Position.Value)))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        if (!((byte[])r[0]).SequenceEqual(f.KeyBytes)) throw new InvalidDataException("StoredEvidenceIntegrityFailure: key collision");
                        if (!((byte[])r[1]).SequenceEqual(payload)) throw new InvalidDataException("PublicationConflict");
                        outcome = "Replayed";
                    }
                }
                await Stage("slot", c, t);
                if (outcome == "Replayed")
                {
                    await f.VerifyStored(c, t, this, payload);
                    Assert.Equal(payload, await f.Validate(c, t, this, ct));
                    await Stage("replay", c, t); return;
                }
                await f.WriteWitnessSet(c, t, [witnessObject]);
                await Stage("witness", c, t);
                await Exec(c, t, """
                    INSERT dbo.OperationalMetricPublicationResultArchive(ResultId,OperationalMetricProjectionProcessorRowId,PublicationFormatVersion,MetricAggregationProcessorRowId,MetricInputStreamRowId,SourceRevisionPosition,EvaluationKeyHash,EvaluationKeyBinary,EvaluationPolicyId,ResultCodecVersion,ResultPayload,Status,ReasonCode,ConflictWitnessId)
                    VALUES(@Result,@P,2,@A,@S,@Cut,@Key,@KeyBytes,@Policy,2,@Payload,1,6,@W);
                    """, ("@Result", result), ("@P", row), ("@A", f.AggregationRow), ("@S", f.StreamRow), ("@Cut", (decimal)f.Bound.Position.Value), ("@Key", f.KeyHash), ("@KeyBytes", f.KeyBytes), ("@Policy", Policy), ("@Payload", payload), ("@W", witness));
                await Stage("result", c, t);
                await Exec(c, t, "INSERT dbo.OperationalMetricResultWitnessReference VALUES(@Result,0x00000000,@W);", ("@Result", result), ("@W", witness));
                await Stage("references", c, t);
                Assert.Equal(payload, await f.Validate(c, t, this, ct));
                await Stage("validated", c, t);
                // Clone the structurally valid legacy key/context columns; the prototype
                // supplies only the new containment fields, never a production publisher.
                var columns = new List<string>();
                await using (var cmd = Command(c, t, "SELECT name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.OperationalMetricProjection') AND is_identity=0 AND is_computed=0 ORDER BY column_id;"))
                await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) columns.Add(r.GetString(0));
                var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OperationalMetricProjectionProcessorRowId"]="@P",["PublicationFormatVersion"]="2",["EvaluationPolicyId"]="@Policy",
                    ["ContainmentResultId"]="@Result",["ConflictWitnessId"]="@W",["Status"]="1",["MetricValue"]="NULL",["ReasonCode"]="6",["ReasonOperandName"]="NULL",
                };
                var names = string.Join(',', columns.Select(x => "[" + x + "]"));
                var values = string.Join(',', columns.Select(x => overrides.GetValueOrDefault(x, "[" + x + "]")));
                var projection = (long)(await Scalar(c, t, $"DECLARE @Inserted table(Id bigint); INSERT dbo.OperationalMetricProjection({names}) OUTPUT INSERTED.OperationalMetricProjectionRowId INTO @Inserted SELECT {values} FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionRowId=@Template; SELECT Id FROM @Inserted;",
                    ("@P", row), ("@Policy", Policy), ("@Result", result), ("@W", witness), ("@Template", f.TemplateRow)))!;
                await Stage("projection", c, t);
                await Exec(c, t, """
                    INSERT dbo.OperationalMetricProjectionEvidence(OperationalMetricProjectionRowId,EvidenceKind,EvidenceOrdinal,OperandName,OperandNameOrderKey,ComponentKey,MetricDimension,ComponentValue,ComponentUnit,InputCount,FirstInputTimestamp,LastInputTimestamp)
                    SELECT @Row,EvidenceKind,EvidenceOrdinal,OperandName,OperandNameOrderKey,ComponentKey,MetricDimension,ComponentValue,ComponentUnit,InputCount,FirstInputTimestamp,LastInputTimestamp
                    FROM dbo.OperationalMetricProjectionEvidence WHERE OperationalMetricProjectionRowId=@Template;
                    """, ("@Row", projection), ("@Template", f.TemplateRow));
                await Stage("evidence", c, t);
                await Exec(c, t, "INSERT dbo.OperationalMetricProjectionManifest VALUES(@P,@Row);", ("@P", row), ("@Row", projection));
                await Stage("manifest", c, t);
                if (Fault == "checkpoint") await Exec(c, t, "EXEC sys.sp_set_session_context @key=N'b1-fault',@value=N'checkpoint';");
                await Stage("before-checkpoint", c, t);
            }, token);
            return outcome;
        }
        private async Task Stage(string stage, SqlConnection c, SqlTransaction t)
        {
            await f.Trace(stage, c, t);
            if (AtStage is not null) await AtStage(stage, c, t);
            if (Fault == stage) throw new IOException("B1 injected failure: " + stage);
        }
        internal async Task<byte[]> Read()
        {
            await using var c = await f.Open();
            return await SqlServerOperationalMetricProjectionStableRead.ExecuteAsync(c, id, async (connection, t, token) =>
            {
                await f.Trace("read-gate", connection, t);
                if (AtStage is not null) await AtStage("read-gate", connection, t);
                var stored = await Scalar(connection, t, "SELECT a.ResultPayload FROM dbo.OperationalMetricPublicationResultArchive a JOIN dbo.OperationalMetricProjection p ON p.ContainmentResultId=a.ResultId JOIN dbo.OperationalMetricProjectionManifest m ON m.OperationalMetricProjectionRowId=p.OperationalMetricProjectionRowId JOIN dbo.OperationalMetricProjectionCheckpoint cp ON cp.OperationalMetricProjectionProcessorRowId=m.OperationalMetricProjectionProcessorRowId WHERE p.OperationalMetricProjectionProcessorRowId=@P AND cp.Position=p.SourceRevisionPosition;", ("@P", row));
                if (stored is not byte[] payload)
                {
                    var position = await Scalar(connection, t, "SELECT Position FROM dbo.OperationalMetricProjectionCheckpoint WHERE OperationalMetricProjectionProcessorRowId=@P;", ("@P", row));
                    if (Equals(position, (decimal)f.Initial.Position.Value)) return [];
                    throw new InvalidDataException("StoredEvidenceIntegrityFailure: missing manifested result");
                }
                await f.VerifyStored(connection, t, this, payload);
                Assert.Equal(payload, await f.Validate(connection, t, this, token));
                await f.Trace("read-validated", connection, t);
                if (AtStage is not null) await AtStage("read-validated", connection, t);
                return payload;
            }, CancellationToken.None);
        }
    }

    internal async Task Trace(string stage, SqlConnection c, SqlTransaction t)
    {
        var tx = await Scalar(c, t, "SELECT transaction_id FROM sys.dm_tran_current_transaction;");
        Events.Enqueue(new { Stage = stage, Spid = c.ServerProcessId, Transaction = tx, ReviewedHead = Environment.GetEnvironmentVariable("B1_VALIDATION_HEAD"), BuildSha = Environment.GetEnvironmentVariable("GITHUB_SHA") });
        await Capture("stage:" + stage);
    }
    internal async Task Capture(string schedule)
    {
        await using var c = await Open();
        var requests = await Rows(c, "SELECT r.session_id,r.blocking_session_id,r.transaction_id,r.wait_type,r.wait_resource,t.text FROM sys.dm_exec_requests r OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.database_id=DB_ID() AND r.session_id<>@@SPID;");
        var locks = await Rows(c, "SELECT l.request_session_id,l.request_mode,l.request_status,l.resource_type,l.resource_associated_entity_id,l.resource_description,OBJECT_NAME(CASE WHEN l.resource_type='OBJECT' THEN CONVERT(int,l.resource_associated_entity_id) ELSE p.object_id END) ObjectName FROM sys.dm_tran_locks l LEFT JOIN sys.partitions p ON p.hobt_id=l.resource_associated_entity_id AND l.resource_type IN ('KEY','PAGE','RID','HOBT') WHERE l.resource_database_id=DB_ID() AND l.request_session_id<>@@SPID;");
        Events.Enqueue(new { Schedule = schedule, Database = c.Database, BoundA = Bound.Position.Value, R = ReferenceRevision, Requests = requests, Locks = locks });
    }
    internal async Task<int?> WaitFor(int blocker, Task contender, bool completionAllowed = false)
    {
        await using var c = await Open();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!bound.IsCancellationRequested)
        {
            var session = await Scalar(c, null, "SELECT TOP(1) CONVERT(int,session_id) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND blocking_session_id=@Blocker;", ("@Blocker", blocker));
            if (session is int id) { await Capture("observed-wait"); return id; }
            if (contender.IsCompleted)
            {
                await contender;
                if (completionAllowed) { await Capture("completed-without-observed-wait"); return null; }
                throw new InvalidOperationException("Required SQL wait was not observed.");
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("SQL barrier was not observed in 15 seconds.");
    }
    internal async Task<object[]> State(Publisher publisher)
    {
        await using var c = await Open();
        var rows = await Rows(c, """
            SELECT (SELECT COUNT(*) FROM dbo.OperationalMetricConflictWitnessArchive) Witnesses,
            (SELECT COUNT(*) FROM dbo.OperationalMetricPublicationResultArchive WHERE OperationalMetricProjectionProcessorRowId=@P) Results,
            (SELECT COUNT(*) FROM dbo.OperationalMetricResultWitnessReference r JOIN dbo.OperationalMetricPublicationResultArchive a ON a.ResultId=r.ResultId WHERE a.OperationalMetricProjectionProcessorRowId=@P) Refs,
            (SELECT COUNT(*) FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=@P) Projections,
            (SELECT COUNT(*) FROM dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionProcessorRowId=@P) Manifest,
            (SELECT COUNT(*) FROM dbo.OperationalMetricProjectionEvidence e JOIN dbo.OperationalMetricProjection p ON p.OperationalMetricProjectionRowId=e.OperationalMetricProjectionRowId WHERE p.OperationalMetricProjectionProcessorRowId=@P) Evidence,
            (SELECT Position FROM dbo.OperationalMetricProjectionCheckpoint WHERE OperationalMetricProjectionProcessorRowId=@P) Checkpoint,
            (SELECT COUNT(*) FROM dbo.B1MutationAudit) AuditWrites,
            (SELECT Position FROM dbo.MetricAggregationCheckpoint WHERE MetricAggregationProcessorRowId=@A) SourceCheckpoint,
            (SELECT COUNT(*) FROM dbo.MetricAggregationContribution WHERE MetricAggregationProcessorRowId=@A AND Position<=@Cut) BoundMembership,
            (SELECT COUNT(*) FROM dbo.MetricAggregationContribution WHERE MetricAggregationProcessorRowId=@A AND Position>@Cut) LaterMembership;
            """, ("@P", publisher.Row), ("@A", AggregationRow), ("@Cut", (decimal)Bound.Position.Value));
        var witnesses = await Rows(c, "SELECT CONVERT(varchar(64),WitnessId,2) WitnessId,WitnessCodecVersion,MetricAggregationProcessorRowId,MetricInputStreamRowId,SourceRevisionPosition,ContributionFactRowId,ContributionPosition,DATALENGTH(WitnessPayload) PayloadLength,CONVERT(varchar(64),HASHBYTES('SHA2_256',WitnessPayload),2) PayloadDigest FROM dbo.OperationalMetricConflictWitnessArchive;");
        var results = await Rows(c, "SELECT CONVERT(varchar(64),ResultId,2) ResultId,OperationalMetricProjectionProcessorRowId,PublicationFormatVersion,MetricAggregationProcessorRowId,MetricInputStreamRowId,SourceRevisionPosition,CONVERT(varchar(64),EvaluationKeyHash,2) KeyHash,EvaluationPolicyId,ResultCodecVersion,Status,ReasonCode,CONVERT(varchar(64),ConflictWitnessId,2) WitnessId,DATALENGTH(ResultPayload) PayloadLength,CONVERT(varchar(64),HASHBYTES('SHA2_256',ResultPayload),2) PayloadDigest FROM dbo.OperationalMetricPublicationResultArchive;");
        Events.Enqueue(new { Stage = "archive-checkpoint-state", Publisher = publisher.Id.Value, State = rows, WitnessArchive = witnesses, ResultArchive = results });
        return Assert.Single(rows).Values.Select(x => x!).ToArray();
    }
    internal async Task<SqlBarrier> Barrier(string table, string operation)
    {
        var c = await Open();
        await Exec(c, null, "EXEC sys.sp_getapplock @Resource=N'B1-source-barrier',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=15000;");
        await Exec(c, null, $"CREATE TRIGGER dbo.B1SourceBarrier ON dbo.{table} AFTER {operation} AS BEGIN SET NOCOUNT ON; DECLARE @Result int; EXEC @Result=sys.sp_getapplock @Resource=N'B1-source-barrier',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000; IF @Result<0 THROW 51089,'B1 source barrier timed out',1; END;");
        return new SqlBarrier(c);
    }
    internal sealed class SqlBarrier(SqlConnection connection) : IAsyncDisposable
    {
        private bool released;
        internal int Spid => connection.ServerProcessId;
        internal async Task Release()
        { if (!released) { released = true; await Exec(connection, null, "EXEC sys.sp_releaseapplock @Resource=N'B1-source-barrier',@LockOwner='Session';"); } }
        public async ValueTask DisposeAsync()
        { await Release(); await Exec(connection, null, "DROP TRIGGER dbo.B1SourceBarrier;"); await connection.DisposeAsync(); }
    }
    internal static async Task<List<Dictionary<string, object?>>> Rows(SqlConnection c, string sql, params (string, object)[] parameters)
    {
        await using var cmd = Command(c, null, sql, parameters);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < r.FieldCount; i++) row.Add(r.GetName(i), r.IsDBNull(i) ? null : r.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }
    internal async Task<SqlConnection> Open()
    { var c = new SqlConnection(ConnectionString); await c.OpenAsync(); return c; }
    internal static SqlCommand Command(SqlConnection c, SqlTransaction? t, string sql, params (string, object)[] parameters)
    { var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.CommandTimeout = 20; foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value); return cmd; }
    internal static async Task<object?> Scalar(SqlConnection c, SqlTransaction? t, string sql, params (string, object)[] parameters)
    { await using var cmd = Command(c, t, sql, parameters); return await cmd.ExecuteScalarAsync(); }
    internal static async Task Exec(SqlConnection c, SqlTransaction? t, string sql, params (string, object)[] parameters)
    { await using var cmd = Command(c, t, sql, parameters); await cmd.ExecuteNonQueryAsync(); }
    internal static byte[] Digest(string domain, byte[] bytes) => SHA256.HashData(Encoding.UTF8.GetBytes(domain).Concat(bytes).ToArray());
    private static object? Invoke(Type type, string name, params object?[] args) => type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (eventsStarted)
            {
                await using var c = await Open();
                var dbid = Convert.ToString(await Scalar(c, null, "SELECT DB_ID();"), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    await Exec(c, null, "SELECT N'B1TRACE_END';");
                    XDocument? document = null;
                    for (var i = 0; i < 50; i++)
                    {
                        var xml = (string?)await Scalar(c, null, "SELECT CONVERT(nvarchar(max),t.target_data) FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address=s.address WHERE s.name=@Name;", ("@Name", eventSession));
                        if (xml is not null)
                        {
                            document = XDocument.Parse(xml);
                            if (document.Descendants("event").Any(e => EventField(e, "data", "statement")?.Contains("B1TRACE_END", StringComparison.Ordinal) == true)) break;
                        }
                        await Task.Delay(100);
                    }
                    Assert.NotNull(document);
                    var graphs = document.Descendants("deadlock").Where(graph => graph.Descendants().Attributes("dbid").Any(attribute => attribute.Value == dbid)).Select(graph => graph.ToString()).ToArray();
                    var statements = document.Descendants("event").Where(e => (string?)e.Attribute("name") is "sql_statement_completed" or "sp_statement_completed")
                        .Select(e => new { Timestamp = (string?)e.Attribute("timestamp"), Spid = EventField(e, "action", "session_id"), Transaction = EventField(e, "action", "transaction_id"), Statement = EventField(e, "data", "statement") })
                        .Where(e => e.Statement is not null && (e.Statement.Contains("MetricAggregation", StringComparison.Ordinal) || e.Statement.Contains("ProductionReferenceTime", StringComparison.Ordinal) || e.Statement.Contains("ProductionStandard", StringComparison.Ordinal) || e.Statement.Contains("OperationalMetric", StringComparison.Ordinal)))
                        .ToArray();
                    var dropped = Convert.ToInt64(await Scalar(c, null, "SELECT dropped_event_count FROM sys.dm_xe_sessions WHERE name=@Name;", ("@Name", eventSession)), System.Globalization.CultureInfo.InvariantCulture);
                    var truncated = (string?)document.Root?.Attribute("truncated");
                    var targetDropped = (string?)document.Root?.Attribute("droppedCount");
                    Events.Enqueue(new { DeadlockGraphs = graphs, MatchingGraphCount = graphs.Length, SqlStatements = statements, SessionDroppedEvents = dropped, TargetTruncated = truncated, TargetDropped = targetDropped,
                        Meaning = "Zero matching graphs is not a universal deadlock-freedom proof; any SQL 1205 still fails. Statements retain source-writer SPID/transaction IDs even for fast replay." });
                    Assert.Equal(0L, dropped);
                    Assert.NotEqual("1", truncated);
                    Assert.True(targetDropped is null or "0", "XE target dropped events.");
                    Assert.Contains(document.Descendants("event"), e => EventField(e, "data", "statement")?.Contains("B1TRACE_END", StringComparison.Ordinal) == true);
                }
                finally
                {
                    await Exec(c, null, $"ALTER EVENT SESSION [{eventSession}] ON SERVER STATE=STOP; DROP EVENT SESSION [{eventSession}] ON SERVER;");
                }
            }
        }
        finally
        {
            output.WriteLine("B1_CONCURRENCY_EVENTS " + JsonSerializer.Serialize(Events.ToArray()));
            await database.DisposeAsync();
        }
    }

    private static string? EventField(XElement element, string kind, string name) => element.Elements(kind).FirstOrDefault(e => (string?)e.Attribute("name") == name)?.Element("value")?.Value;

    private const string LedgerSelect = """
        SELECT s.MachineId,s.StreamKey,f.Position,f.FactId,f.MetricInputKey,f.MetricValue,f.Unit,f.StartsAtUtc,f.EndsAtUtc,f.CompanyId,f.SiteId,
        f.ProductionLineId,f.MachineId,f.ShiftId,f.ShiftScheduleAssignmentId,f.ProductionContextAssignmentId,f.ProductionOrderId,f.OperationId,f.PartId,f.OperatorId,
        f.IsPlannedProductionTime,f.PlannedProductionScheduleAssignmentId,f.SourceContextualizedActivityIntervalId,f.SourceEligibilityIntervalId,f.SourceQuantityEvidenceId,
        f.OccurrenceSiteId,f.OccurrenceShiftScheduleAssignmentId,f.OccurrenceShiftId,f.OccurrenceStartsAtUtc,f.OccurrenceEndsAtUtc,f.ProductionDaySiteId,f.ProductionBusinessDate
        FROM dbo.MetricAggregationContribution c WITH(HOLDLOCK)
        JOIN dbo.MetricInputFact f WITH(HOLDLOCK) ON f.MetricInputFactRowId=c.MetricInputFactRowId AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
        JOIN dbo.MetricInputStream s WITH(HOLDLOCK) ON s.MetricInputStreamRowId=f.MetricInputStreamRowId
        WHERE c.MetricAggregationProcessorRowId=@A AND c.MetricInputStreamRowId=@S AND c.Position<=@Cut ORDER BY c.Position;
        """;
}
