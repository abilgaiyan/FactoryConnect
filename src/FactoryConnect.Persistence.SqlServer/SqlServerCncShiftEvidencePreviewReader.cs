using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Bounded, read-only preview for an exact commissioned shift occurrence.
/// Requires the Position-ordered observation schema verified by the local diagnostic.
/// Not an accepted-production or OEE authority.
/// </summary>
public sealed class SqlServerCncShiftEvidencePreviewReader
{
    private const int MaximumReadings = 10000;
    private readonly string _connectionString;

    public SqlServerCncShiftEvidencePreviewReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<CncShiftEvidenceClassifier.Preview> ReadAsync(
        Guid machineId, string assignmentId, string shiftId,
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
        string counterAddress, CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty ||
            string.IsNullOrWhiteSpace(assignmentId) || assignmentId.Length > 200 ||
            string.IsNullOrWhiteSpace(shiftId) || shiftId.Length > 200 ||
            string.IsNullOrWhiteSpace(counterAddress) || counterAddress.Length > 512 ||
            startsAtUtc.Offset != TimeSpan.Zero || endsAtUtc.Offset != TimeSpan.Zero ||
            endsAtUtc <= startsAtUtc || endsAtUtc - startsAtUtc > TimeSpan.FromDays(1))
            throw new ArgumentException("Invalid exact CNC shift preview request.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            SET NOCOUNT ON;
            IF NOT EXISTS (
                SELECT 1
                FROM dbo.MachineShiftOccurrenceRosterOccurrence AS o
                INNER JOIN dbo.MachineShiftOccurrenceRoster AS r
                  ON r.MachineShiftOccurrenceRosterRowId = o.MachineShiftOccurrenceRosterRowId
                WHERE r.MachineId = @Machine
                  AND o.ShiftScheduleAssignmentId = @Assignment
                  AND o.ShiftId = @Shift
                  AND o.ShiftStartsAtUtc = @Starts
                  AND o.ShiftEndsAtUtc = @Ends)
                THROW 51120, 'Exact commissioned shift occurrence unavailable.', 1;

            -- Do not silently omit poor-quality or malformed observations and present
            -- the remaining unit transitions as a complete evidence preview.
            IF EXISTS (
                SELECT 1 FROM dbo.MachineObservation
                WHERE MachineId = @Machine AND Address = @Address
                  AND ObservedAt >= @Starts AND ObservedAt < @Ends
                  AND (Quality IS NULL OR Quality <> 0 OR TRY_CONVERT(bigint, ObservationValue) IS NULL))
                THROW 51121, 'Incomplete CNC evidence: unusable observations in shift.', 1;
            -- Read at most one predecessor, plus a bounded number of in-shift rows.
            -- The extra row detects truncation without silently returning a complete result.
            ;WITH PriorReading AS (
                SELECT TOP (1) Position, ObservedAt, InstanceId,
                       TRY_CONVERT(bigint, ObservationValue) AS CounterValue
                FROM dbo.MachineObservation
                WHERE MachineId = @Machine AND Address = @Address AND Quality = 0
                  AND TRY_CONVERT(bigint, ObservationValue) IS NOT NULL
                  AND ObservedAt < @Starts
                ORDER BY Position DESC
            ), ShiftReadings AS (
                SELECT TOP (10001) Position, ObservedAt, InstanceId,
                       TRY_CONVERT(bigint, ObservationValue) AS CounterValue
                FROM dbo.MachineObservation
                WHERE MachineId = @Machine AND Address = @Address AND Quality = 0
                  AND TRY_CONVERT(bigint, ObservationValue) IS NOT NULL
                  AND ObservedAt >= @Starts AND ObservedAt < @Ends
                ORDER BY Position
            )
            SELECT Position, ObservedAt, InstanceId, CounterValue
            FROM (SELECT * FROM PriorReading UNION ALL SELECT * FROM ShiftReadings) AS readings
            ORDER BY Position;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@Assignment", SqlDbType.NVarChar, 200).Value = assignmentId;
        command.Parameters.Add("@Shift", SqlDbType.NVarChar, 200).Value = shiftId;
        command.Parameters.Add("@Address", SqlDbType.NVarChar, 512).Value = counterAddress;
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = startsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = endsAtUtc;

        var readings = new List<CncShiftEvidenceClassifier.Reading>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // InstanceId is decimal(20,0) in the original schema; never narrow to Int64.
            var instance = Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)
                ?? throw new InvalidDataException("Missing acquisition instance.");
            readings.Add(new CncShiftEvidenceClassifier.Reading(
                reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime(),
                reader.GetDecimal(0),
                instance, reader.GetInt64(3), true));
        }
        // A shift's first observed counter cannot establish a produced unit without
        // a preceding usable observation. Never present an unanchored count as complete.
        var inShiftCount = readings.Count(r =>
            r.ObservedAtUtc >= startsAtUtc && r.ObservedAtUtc < endsAtUtc);
        bool truncated = inShiftCount > MaximumReadings;
        if (truncated)
            throw new InvalidDataException("CNC preview exceeds bounded read limit; no partial total returned.");
        if (inShiftCount > 0 && !readings.Any(r => r.ObservedAtUtc < startsAtUtc))
            throw new InvalidDataException("CNC preview has no pre-shift counter baseline; no complete count returned.");
        return CncShiftEvidenceClassifier.Classify(
            readings, startsAtUtc, endsAtUtc, TimeSpan.FromMinutes(30));
    }
}
