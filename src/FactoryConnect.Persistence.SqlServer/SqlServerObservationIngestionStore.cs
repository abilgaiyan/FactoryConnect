using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerObservationIngestionStore :
    IObservationIngestionStore
{
    internal string ConnectionString { get; }

    public SqlServerObservationIngestionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
    }

    public ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "SQL Server checkpoint reads are implemented in FC-023.4.");
    }

    public ValueTask CommitAsync(
        ObservationIngestionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "SQL Server ingestion commits are implemented in FC-023.5.");
    }
}
