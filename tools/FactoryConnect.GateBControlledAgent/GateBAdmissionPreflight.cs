using System.Globalization;
using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>Strictly read-only admission preflight. Never grants write authority.</summary>
internal static class GateBAdmissionPreflight
{
    private const string Server = @"DESKTOP-1PJK2OB\FCINVENTORY";
    private const string Database = "FactoryConnect_P0_GateB_Fixture_20261110";
    private const string HeadIdentity = "3C386EF472741E55B178C14159CBBDFFFBFC633C3A7E0F62A8F2B3A86DCB4FF7";
    private static readonly Guid Moxa = Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0");
    private static readonly Guid Gg = Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f");

    public static async Task<int> RunAsync()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("FC_GATEB_SQL_CONNECTION");
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("FC_GATEB_SQL_CONNECTION is required.");
            var builder = new SqlConnectionStringBuilder(raw);
            if (!string.Equals(builder.DataSource, Server, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(builder.InitialCatalog, Database, StringComparison.Ordinal) ||
                !builder.IntegratedSecurity || builder.AttachDBFilename.Length != 0 ||
                builder.FailoverPartner.Length != 0 ||
                builder.UserID.Length != 0 || builder.Password.Length != 0)
                throw new InvalidOperationException("Wrong SQL target or authentication.");

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using (var identity = connection.CreateCommand())
            {
                identity.CommandText = "SELECT CONVERT(nvarchar(128), @@SERVERNAME), DB_NAME()";
                await using var reader = await identity.ExecuteReaderAsync();
                if (!await reader.ReadAsync() ||
                    !string.Equals(reader.GetString(0), Server, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(reader.GetString(1), Database, StringComparison.Ordinal))
                    throw new InvalidOperationException("Connected SQL target mismatch.");
            }

            var stream = new ObservationStreamId(new MachineId(Moxa), "mtconnect:DEWAS-SR-MOXA-001");
            var evidence = new SqlServerAcquisitionEvidenceReader(builder.ConnectionString);
            var verified = await evidence.ReadExactAsync(stream, HeadIdentity);
            if (verified.Status != AcquisitionEvidenceReadStatus.Found || verified.Record is null ||
                verified.Record.PublicationOrdinal != 104153 ||
                verified.Record.AcceptedThrough?.Value != 14328 ||
                !string.Equals(verified.Record.Identity, HeadIdentity, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Production evidence prefix verification failed.");

            await using (var head = connection.CreateCommand())
            {
                head.CommandText = """
                    SELECT COUNT_BIG(*)
                    FROM dbo.AcquisitionEvidenceHead h
                    JOIN dbo.AcquisitionEvidencePublication p
                      ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary
                     AND p.IdentityHash=h.IdentityHash
                    WHERE h.MachineId=@MachineId AND p.IdentityHash=@Identity
                      AND p.PublicationOrdinal=104153 AND p.AcceptedThrough=14328;
                    """;
                head.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = Moxa;
                head.Parameters.Add("@Identity", SqlDbType.Binary, 32).Value = Convert.FromHexString(HeadIdentity);
                if (Convert.ToInt64(await head.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidDataException("Active publication head differs from approved baseline.");
            }

            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = """
                    SELECT InstanceId, NextSequence FROM dbo.ObservationStreamCheckpoint
                    WHERE MachineId=@MachineId AND StreamKeyBinary=@Stream;
                    """;
                checkpoint.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = Moxa;
                checkpoint.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value =
                    System.Text.Encoding.BigEndianUnicode.GetBytes(stream.StreamKey);
                await using var rows = await checkpoint.ExecuteReaderAsync();
                if (!await rows.ReadAsync() ||
                    Convert.ToUInt64(rows.GetDecimal(0)) != 1793867780UL ||
                    Convert.ToUInt64(rows.GetDecimal(1)) != 27UL ||
                    await rows.ReadAsync())
                    throw new InvalidDataException("MOXA acquisition checkpoint changed.");
            }

            await using (var gg = connection.CreateCommand())
            {
                gg.CommandText = """
                    SELECT Position, ProjectionRevision FROM dbo.MachineStateActivityAuthority
                    WHERE MachineId=@MachineId AND StateProcessorId=N'machine-state-activity';
                    """;
                gg.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = Gg;
                await using var rows = await gg.ExecuteReaderAsync();
                if (!await rows.ReadAsync() ||
                    Convert.ToUInt64(rows.GetDecimal(0)) != 513501UL ||
                    Convert.ToInt64(rows.GetValue(1), CultureInfo.InvariantCulture) != 4091 ||
                    await rows.ReadAsync())
                    throw new InvalidDataException("GG-003 held authority baseline changed.");
            }

            Console.WriteLine("PASS: SQL identity, verified MOXA evidence prefix/head, acquisition checkpoint and GG hold match baseline.");
            Console.WriteLine("READ ONLY: source-generation admission and durable SQL writes remain BLOCKED.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }
}
