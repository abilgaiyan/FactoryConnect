using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Explicit opt-in construction for an ingestion store with a transaction-bound
/// admission policy. Normal persistence registration remains unchanged.
/// </summary>
public static class SqlServerAdmissionBoundIngestionStoreFactory
{
    public static IObservationIngestionStore Create(
        string connectionString,
        IObservationIngestionTransactionAdmission admission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(admission);
        return new SqlServerObservationIngestionStore(connectionString, admission);
    }
}
