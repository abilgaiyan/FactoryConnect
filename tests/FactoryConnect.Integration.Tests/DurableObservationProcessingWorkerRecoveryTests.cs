using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class DurableObservationProcessingWorkerRecoveryTests
{
    [Fact]
    public async Task WorkerSurvivesRealSqlReadTimeoutAndAdvancesOnlyAfterLockRelease()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using (var migration = new SqlConnection(database.ConnectionString))
        {
            await migration.OpenAsync();
            await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
                migration, TimeSpan.FromMinutes(2), CancellationToken.None);
        }

        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:gg003-worker-recovery");
        var processor = new RecordingProcessor();
        var store = new SqlServerObservationIngestionStore(
            database.ConnectionString, observationReadCommandTimeoutSeconds: 1);
        await store.CommitAsync(new ObservationIngestionBatch(
            null,
            new ObservationCheckpoint(stream, 1, 2),
            [
                new SequencedMachineObservation(
                    1,
                    new MachineObservation
                    {
                        MachineId = stream.MachineId,
                        Source = "mtconnect",
                        Address = "part_count",
                        Type = SignalType.Numeric,
                        Value = 1m,
                        Quality = ObservationQuality.Good,
                        Timestamp = DateTimeOffset.UnixEpoch,
                    }),
            ],
            DateTimeOffset.UnixEpoch));

        var options = new ObservationProcessingRuntimeOptions(
            10, TimeSpan.FromMilliseconds(50));
        var pipeline = new DurableObservationProcessingPipeline(
            new ObservationProcessingRuntime(store, store, processor, stream, options),
            new MappedObservationProcessingRuntime(
                new EmptyMappedReader(stream),
                new EmptyCursorReader(),
                new EmptyMappedProcessor(),
                stream,
                options),
            TimeSpan.FromMilliseconds(50));
        var set = new DurableObservationProcessingPipelineSet(
            [pipeline], TimeSpan.FromMilliseconds(50));
        using var worker = new DurableObservationProcessingWorker(
            set, NullLogger<DurableObservationProcessingWorker>.Instance);

        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(IsolationLevel.Serializable);
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText =
                "SELECT COUNT_BIG(*) FROM dbo.MachineObservation WITH (TABLOCKX,HOLDLOCK);";
            await lockCommand.ExecuteScalarAsync();
        }

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(worker.ExecuteTask?.IsCompleted ?? true);
            Assert.Null(await store.ReadCheckpointAsync(processor.ProcessorId, stream));
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            ObservationProcessingCheckpoint? checkpoint = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                Assert.False(worker.ExecuteTask?.IsFaulted ?? false);
                checkpoint = await store.ReadCheckpointAsync(processor.ProcessorId, stream);
                if (checkpoint?.Position == new ObservationPosition(1))
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.Equal(new ObservationPosition(1), checkpoint?.Position);
            Assert.Equal(1, processor.ProcessedCount);
            Assert.False(worker.ExecuteTask?.IsCompleted ?? true);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingProcessor : IObservationProcessor
    {
        public ObservationProcessorId ProcessorId { get; } = new("gg003-worker-recovery");
        public int ProcessedCount { get; private set; }

        public ValueTask ProcessAsync(
            IReadOnlyList<DurableMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessedCount += observations.Count;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyMappedReader(ObservationStreamId stream)
        : IDurableMappedObservationReader
    {
        public ValueTask<MappedObservationReadBatch> ReadAsync(
            MappedObservationReadRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MappedObservationReadBatch(
                stream, [], false));
    }

    private sealed class EmptyCursorReader : IMachineStateActivityCursorReader
    {
        public ValueTask<ObservationPosition?> ReadAsync(
            ObservationProcessorId stateProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ObservationPosition?>(null);
    }

    private sealed class EmptyMappedProcessor : IMappedMachineObservationProcessor
    {
        public ObservationProcessorId ProcessorId { get; } = new("gg003-mapped-noop");

        public ValueTask ProcessAsync(
            IReadOnlyList<DurableMappedMachineObservation> observations,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
