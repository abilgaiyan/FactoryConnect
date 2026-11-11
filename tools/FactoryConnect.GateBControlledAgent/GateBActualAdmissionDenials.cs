using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>
/// Exercises the real ingestion admission policy against deliberately invalid batches.
/// Every scenario runs inside a rolled-back SQL transaction; no ingestion commit is called.
/// </summary>
internal static class GateBActualAdmissionDenials
{
    public static async Task<int> RunAsync()
    {
        if (await GateBAdmissionPreflight.RunAsync() != 0)
            return 1;

        var connectionString = Environment.GetEnvironmentVariable("FC_GATEB_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Missing fixture connection string.");
        await using var lease = await GateBExclusiveWriterLease.AcquireAsync(connectionString);
        var stream = new ObservationStreamId(
            new MachineId(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0")),
            "mtconnect:DEWAS-SR-MOXA-001");
        var current = new ObservationCheckpoint(stream, 1793867780UL, 27UL);
        var policy = new GateBIngestionTransactionAdmission();

        foreach (var scenario in new[] { "missing-evidence", "stale-checkpoint", "wrong-generation", "wrong-sequence", "wrong-machine" })
        {
            var actualStream = scenario == "wrong-machine"
                ? new ObservationStreamId(new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111")), stream.StreamKey)
                : stream;
            var actualCurrent = scenario == "stale-checkpoint"
                ? new ObservationCheckpoint(stream, 1793867780UL, 28UL)
                : current;
            var next = new ObservationCheckpoint(
                actualStream,
                scenario == "wrong-generation" ? 9000002UL : 9000001UL,
                scenario == "wrong-sequence" ? 3UL : 2UL);
            var expected = scenario == "wrong-machine" ? null : current;
            var batch = new ObservationIngestionBatch(
                expected, next, Array.Empty<SequencedMachineObservation>(),
                new DateTimeOffset(2026, 11, 5, 8, 36, 22, TimeSpan.Zero));

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction =
                (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            var rejected = false;
            try
            {
                await policy.ValidateAsync(connection, transaction, batch, actualCurrent);
            }
            catch (InvalidOperationException error)
                when (error.Message == "Gate B ingestion transition is not the approved first generation cut.")
            {
                rejected = true;
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            if (!rejected)
                throw new InvalidDataException($"Actual admission policy accepted {scenario}.");
            if (await GateBAdmissionPreflight.RunAsync() != 0)
                throw new InvalidDataException($"Fixture changed after {scenario} denial.");
            Console.WriteLine($"PASS: actual transaction admission rejected {scenario}; rollback and baseline verified.");
        }

        Console.WriteLine("READ ONLY: actual policy rejection verified; no ingestion commit attempted.");
        return 0;
    }
}
