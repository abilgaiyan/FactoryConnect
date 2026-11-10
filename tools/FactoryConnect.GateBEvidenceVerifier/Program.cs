using System.Data;
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
        builder.AttachDBFilename.Length != 0)
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
    Console.WriteLine("READ ONLY; NOT FIXTURE ADMISSION OR DEPLOYMENT AUTHORIZATION.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"BLOCKED: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
