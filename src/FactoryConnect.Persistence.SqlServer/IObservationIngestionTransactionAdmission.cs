using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Optional, fail-closed ingestion admission policy executed inside the same
/// SQL transaction as checkpoint, raw observations and evidence publication.
/// Implementations must not commit, roll back or dispose the supplied transaction.
/// The default production store has no additional policy.
/// </summary>
public interface IObservationIngestionTransactionAdmission
{
    ValueTask ValidateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationIngestionBatch batch,
        ObservationCheckpoint? currentCheckpoint,
        CancellationToken cancellationToken = default);
}
