using System.Data;
using System.Globalization;
using System.Text;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>Fixture-only policy: validates approved predecessor within the actual ingestion transaction.</summary>
internal sealed class GateBIngestionTransactionAdmission : IObservationIngestionTransactionAdmission
{
    private static readonly Guid Machine = Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0");
    private static readonly byte[] Stream = Encoding.BigEndianUnicode.GetBytes("mtconnect:DEWAS-SR-MOXA-001");
    private static readonly byte[] Head = Convert.FromHexString(
        "3C386EF472741E55B178C14159CBBDFFFBFC633C3A7E0F62A8F2B3A86DCB4FF7");

    public async ValueTask ValidateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationIngestionBatch batch,
        ObservationCheckpoint? currentCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Checkpoint.StreamId.MachineId.Value != Machine ||
            !string.Equals(batch.Checkpoint.StreamId.StreamKey,
                "mtconnect:DEWAS-SR-MOXA-001", StringComparison.Ordinal) ||
            currentCheckpoint is null ||
            currentCheckpoint.InstanceId != 1793867780UL ||
            currentCheckpoint.NextSequence != 27UL ||
            batch.ExpectedCheckpoint != currentCheckpoint ||
            batch.Checkpoint.InstanceId != 9000001UL ||
            batch.Checkpoint.NextSequence != 2UL ||
            batch.AcquisitionEvidence is null)
            throw new InvalidOperationException("Gate B ingestion transition is not the approved first generation cut.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.AcquisitionEvidenceHead h WITH (UPDLOCK, HOLDLOCK)
            JOIN dbo.AcquisitionEvidencePublication p WITH (HOLDLOCK)
              ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary
             AND p.IdentityHash=h.IdentityHash
            WHERE h.MachineId=@Machine AND h.StreamKeyBinary=@Stream
              AND p.IdentityHash=@Identity AND p.PublicationOrdinal=104153
              AND p.AcceptedThrough=14328;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = Machine;
        command.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value = Stream;
        command.Parameters.Add("@Identity", SqlDbType.Binary, 32).Value = Head;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Gate B publication ancestry changed before ingestion commit.");
    }
}
