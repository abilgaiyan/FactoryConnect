using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

// Read-only Gate B qualification. Use only the restored disposable clone.
// The production reader verifies the entire publication prefix, canonical
// payload identity/envelope hashes, checkpoint links and retained raw sources.
if (args.Length != 1 || !string.Equals(args[0], "--verify", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: GateBEvidenceVerifier --verify (connection string in FC_GATEB_SQL_CONNECTION)");
    return 2;
}

const string expectedServer = @"DESKTOP-1PJK2OB\FCINVENTORY";
const string expectedDatabase = "FactoryConnect_P0_GateB_Fixture_20261110";
const string expectedIdentity = "3C386EF472741E55B178C14159CBBDFFFBFC633C3A7E0F62A8F2B3A86DCB4FF7";
const long expectedOrdinal = 104153;
const ulong expectedThrough = 14328;

var rawConnection = Environment.GetEnvironmentVariable("FC_GATEB_SQL_CONNECTION");
if (string.IsNullOrWhiteSpace(rawConnection))
{
    Console.Error.WriteLine("BLOCKED: FC_GATEB_SQL_CONNECTION is missing.");
    return 2;
}

try
{
    var builder = new SqlConnectionStringBuilder(rawConnection);
    if (!string.Equals(builder.DataSource, expectedServer, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(builder.InitialCatalog, expectedDatabase, StringComparison.OrdinalIgnoreCase) ||
        builder.AttachDBFilename.Length != 0 || !builder.IntegratedSecurity ||
        builder.UserID.Length != 0 || builder.Password.Length != 0 ||
        builder.FailoverPartner.Length != 0)
        throw new InvalidOperationException("Unexpected SQL target or attached database file.");

    // Validate the actual connected server/database, not only the supplied connection string.
    await using (var connection = new SqlConnection(builder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128), @@SERVERNAME), DB_NAME()";
        await using var rows = await command.ExecuteReaderAsync();
        if (!await rows.ReadAsync() ||
            !string.Equals(rows.GetString(0), expectedServer, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rows.GetString(1), expectedDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connected SQL server or database does not match Gate B clone.");
    }

    var stream = new ObservationStreamId(
        new MachineId(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0")),
        "mtconnect:DEWAS-SR-MOXA-001");
    var reader = new SqlServerAcquisitionEvidenceReader(builder.ConnectionString);
    var result = await reader.ReadExactAsync(stream, expectedIdentity);
    if (result.Status != AcquisitionEvidenceReadStatus.Found || result.Record is null)
        throw new InvalidDataException($"Publication chain verification failed: {result.Status}.");

    var record = result.Record;
    if (record.PublicationOrdinal != expectedOrdinal ||
        record.AcceptedThrough?.Value != expectedThrough ||
        !string.Equals(record.Identity, expectedIdentity, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Verified publication does not match the approved Gate B frontier.");

    Console.WriteLine($"PASS: verified production evidence prefix through ordinal {record.PublicationOrdinal}.");
    Console.WriteLine($"Machine={stream.MachineId.Value}; Stream={stream.StreamKey}");
    Console.WriteLine($"Identity={record.Identity}; AcceptedThrough={record.AcceptedThrough.Value}");
    // Re-check the live head after historical verification. A verified historical
    // identity alone does not establish that the active frontier stayed fixed.
    await using (var connection = new SqlConnection(builder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.PublicationOrdinal, p.AcceptedThrough, p.IdentityHash
            FROM dbo.AcquisitionEvidenceHead h
            JOIN dbo.AcquisitionEvidencePublication p
              ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary
             AND p.IdentityHash=h.IdentityHash
            WHERE h.MachineId=@MachineId AND p.IdentityHash=@Identity;
            """;
        command.Parameters.Add("@MachineId", System.Data.SqlDbType.UniqueIdentifier).Value = stream.MachineId.Value;
        command.Parameters.Add("@Identity", System.Data.SqlDbType.Binary, 32).Value = Convert.FromHexString(expectedIdentity);
        await using var rows = await command.ExecuteReaderAsync();
        if (!await rows.ReadAsync() || rows.GetInt64(0) != expectedOrdinal ||
            rows.IsDBNull(1) || Convert.ToUInt64(rows.GetDecimal(1)) != expectedThrough ||
            !((byte[])rows[2]).AsSpan().SequenceEqual(Convert.FromHexString(expectedIdentity)) ||
            await rows.ReadAsync())
            throw new InvalidDataException("Active MOXA publication head moved or no longer matches the verified identity.");
    }
    Console.WriteLine("PASS: active publication head still matches verified frontier.");
    Console.WriteLine("READ ONLY; NOT FIXTURE ADMISSION OR DEPLOYMENT AUTHORIZATION.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"BLOCKED: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
