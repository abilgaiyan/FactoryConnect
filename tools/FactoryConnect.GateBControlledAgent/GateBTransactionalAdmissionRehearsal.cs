using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>
/// Read-only transactional admission rehearsal. Holds checkpoint and publication-head
/// update locks in one serializable transaction. Does NOT authorize a separate
/// ingestion transaction or commit source evidence.
/// </summary>
internal static class GateBTransactionalAdmissionRehearsal
{
    private static readonly Guid Machine = Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0");
    private const string Head = "3C386EF472741E55B178C14159CBBDFFFBFC633C3A7E0F62A8F2B3A86DCB4FF7";

    public static async Task<int> RunAsync(bool adversarial = false)
    {
        if (await GateBAdmissionPreflight.RunAsync() != 0)
            return 1;

        var connectionString = Environment.GetEnvironmentVariable("FC_GATEB_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Missing fixture connection string.");
        await using var lease = await GateBExclusiveWriterLease.AcquireAsync(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.Transaction = transaction;
                checkpoint.CommandText = """
                    SELECT InstanceId, NextSequence
                    FROM dbo.ObservationStreamCheckpoint WITH (UPDLOCK, HOLDLOCK)
                    WHERE MachineId=@Machine AND StreamKeyBinary=@Stream;
                    """;
                checkpoint.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = Machine;
                checkpoint.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value =
                    Encoding.BigEndianUnicode.GetBytes("mtconnect:DEWAS-SR-MOXA-001");
                await using var reader = await checkpoint.ExecuteReaderAsync();
                if (!await reader.ReadAsync() ||
                    Convert.ToUInt64(reader.GetDecimal(0), CultureInfo.InvariantCulture) != 1793867780UL ||
                    Convert.ToUInt64(reader.GetDecimal(1), CultureInfo.InvariantCulture) != (adversarial ? 28UL : 27UL) ||
                    await reader.ReadAsync())
                    throw new InvalidDataException("Transactional acquisition checkpoint mismatch.");
            }

            await using (var head = connection.CreateCommand())
            {
                head.Transaction = transaction;
                head.CommandText = """
                    SELECT COUNT_BIG(*)
                    FROM dbo.AcquisitionEvidenceHead h WITH (UPDLOCK, HOLDLOCK)
                    JOIN dbo.AcquisitionEvidencePublication p WITH (HOLDLOCK)
                      ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary
                     AND p.IdentityHash=h.IdentityHash
                    WHERE h.MachineId=@Machine AND h.StreamKeyBinary=@Stream
                      AND p.IdentityHash=@Identity AND p.PublicationOrdinal=104153
                      AND p.AcceptedThrough=14328;
                    """;
                head.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = Machine;
                head.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value =
                    Encoding.BigEndianUnicode.GetBytes("mtconnect:DEWAS-SR-MOXA-001");
                head.Parameters.Add("@Identity", SqlDbType.Binary, 32).Value = Convert.FromHexString(adversarial ? new string('0', 64) : Head);
                if (Convert.ToInt64(await head.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidDataException("Transactional publication head mismatch.");
            }

            // This rehearsal intentionally never commits; a subsequent ingestion
            // transaction must repeat both checks inside its own write boundary.
            await transaction.RollbackAsync();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (await GateBAdmissionPreflight.RunAsync() != 0)
            throw new InvalidDataException("Baseline changed during transaction rehearsal.");
        Console.WriteLine("PASS: serializable checkpoint and publication-head admission predicates matched in one read-only transaction.");
        Console.WriteLine("READ ONLY: transaction rolled back; durable acquisition remains BLOCKED.");
        return 0;
    }
}
