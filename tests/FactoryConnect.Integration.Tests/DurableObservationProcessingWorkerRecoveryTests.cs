using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
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
        var logger = new TimeoutRecordingLogger();
        using var worker = new DurableObservationProcessingWorker(set, logger);

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
            var timeout = await logger.TimeoutObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(-2, timeout.Number);
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


    [Fact]
    public async Task WorkerCancellationInterruptsSqlTimeoutRetryDelayWithoutAnotherRead()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:gg003-cancel-retry");
        var processor = new RecordingProcessor();
        var store = new SqlServerObservationIngestionStore(database.ConnectionString);
        var reader = new SqlCommandReader(
            database.ConnectionString, "WAITFOR DELAY '00:00:10';", commandTimeoutSeconds: 1);
        var logger = new TimeoutRecordingLogger();
        using var worker = CreateWorker(reader, store, processor, stream, logger);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            var timeout = await logger.TimeoutObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(-2, timeout.Number);
            Assert.False(worker.ExecuteTask!.IsCompleted);

            // Bound the entire stop below the worker's five-second retry delay.
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(1, logger.TimeoutCount);
            Assert.Equal(0, processor.ProcessedCount);
            Assert.Null(await store.ReadCheckpointAsync(processor.ProcessorId, stream));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task WorkerPropagatesNonTimeoutSqlFailureWithoutRetryOrCheckpointAdvance()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:gg003-non-timeout");
        var processor = new RecordingProcessor();
        var store = new SqlServerObservationIngestionStore(database.ConnectionString);
        var reader = new SqlCommandReader(
            database.ConnectionString,
            "THROW 50001, 'GG003 acceptance: unrelated SQL failure', 1;",
            commandTimeoutSeconds: 10);
        var logger = new TimeoutRecordingLogger();
        using var worker = CreateWorker(reader, store, processor, stream, logger);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            var exception = await Assert.ThrowsAsync<SqlException>(
                () => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(50001, exception.Number);
            Assert.Same(reader.Failure, exception);
            Assert.True(worker.ExecuteTask!.IsFaulted);
            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(0, logger.TimeoutCount);
            Assert.False(logger.TimeoutObserved.Task.IsCompleted);
            Assert.Equal(0, processor.ProcessedCount);
            Assert.Null(await store.ReadCheckpointAsync(processor.ProcessorId, stream));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<SqlStartupIsolatedDatabase> CreateMigratedDatabaseAsync()
    {
        var database = await SqlStartupIsolatedDatabase.CreateAsync();
        try
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
                connection, TimeSpan.FromMinutes(2), CancellationToken.None);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static DurableObservationProcessingWorker CreateWorker(
        IDurableObservationReader reader,
        IObservationProcessingCheckpointStore checkpoints,
        RecordingProcessor processor,
        ObservationStreamId stream,
        TimeoutRecordingLogger logger)
    {
        var options = new ObservationProcessingRuntimeOptions(
            10, TimeSpan.FromMilliseconds(50));
        var pipeline = new DurableObservationProcessingPipeline(
            new ObservationProcessingRuntime(reader, checkpoints, processor, stream, options),
            new MappedObservationProcessingRuntime(
                new EmptyMappedReader(stream), new EmptyCursorReader(),
                new EmptyMappedProcessor(), stream, options),
            TimeSpan.FromMilliseconds(50));
        return new DurableObservationProcessingWorker(
            new DurableObservationProcessingPipelineSet(
                [pipeline], TimeSpan.FromMilliseconds(50)),
            logger);
    }

    // Exercise the real hosted worker/pipeline with genuine SqlClient exceptions.
    // The existing recovery test separately covers the actual observation-store query.
    private sealed class SqlCommandReader(
        string connectionString, string commandText, int commandTimeoutSeconds)
        : IDurableObservationReader
    {
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public SqlException? Failure { get; private set; }

        public async ValueTask<ObservationReadBatch> ReadAsync(
            ObservationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.CommandTimeout = commandTimeoutSeconds;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException exception)
            {
                Failure = exception;
                throw;
            }

            throw new InvalidOperationException("The acceptance SQL command must fail.");
        }
    }

    private sealed class TimeoutRecordingLogger : ILogger<DurableObservationProcessingWorker>
    {
        private int _timeoutCount;
        public int TimeoutCount => Volatile.Read(ref _timeoutCount);
        public TaskCompletionSource<SqlException> TimeoutObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning &&
                eventId.Name == "ProcessingSqlTimeout" &&
                exception is SqlException sqlException)
            {
                Interlocked.Increment(ref _timeoutCount);
                TimeoutObserved.TrySetResult(sqlException);
            }
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
