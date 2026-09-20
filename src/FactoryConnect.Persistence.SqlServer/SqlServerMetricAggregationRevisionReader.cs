using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed partial class SqlServerMetricAggregationStore
{
    public async ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(
        MetricAggregationProcessorId processorId,
        MetricInputStreamId streamId,
        MetricAggregationCheckpoint? afterRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);

        if (afterRevision is not null &&
            (afterRevision.ProcessorId != processorId || afterRevision.StreamId != streamId))
        {
            throw new ArgumentException(
                "Aggregation revision cursor must belong to the requested processor and stream.",
                nameof(afterRevision));
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var processor = await FindProcessorAsync(
            connection, transaction: null, processorId, cancellationToken);
        if (processor is null)
        {
            return null;
        }

        await ValidateProcessorStreamAsync(
            connection, processor.Value.StreamRowId, streamId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TOP (1) Position FROM dbo.MetricAggregationRevision " +
            "WHERE MetricAggregationProcessorRowId = @ProcessorRowId " +
            "AND (@AfterPosition IS NULL OR Position > @AfterPosition) ORDER BY Position ASC;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processor.Value.RowId;
        var after = command.Parameters.Add("@AfterPosition", SqlDbType.Decimal);
        after.Precision = 20;
        after.Scale = 0;
        after.Value = afterRevision is null
            ? DBNull.Value
            : SqlServerUInt64.ToDecimal(afterRevision.Position.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            return null;
        }

        return await ReadRevisionChangeAsync(
            connection,
            processor.Value.RowId,
            new MetricAggregationCheckpoint(
                processorId,
                streamId,
                new MetricInputPosition(SqlServerUInt64.Materialize((decimal)result))),
            cancellationToken);
    }

    public async ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(
        MetricAggregationCheckpoint revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var processor = await FindProcessorAsync(
            connection, transaction: null, revision.ProcessorId, cancellationToken);
        if (processor is null)
        {
            return null;
        }

        await ValidateProcessorStreamAsync(
            connection, processor.Value.StreamRowId, revision.StreamId, cancellationToken);

        if (!await RevisionExistsAsync(
                connection,
                transaction: null,
                processor.Value.RowId,
                revision.Position,
                cancellationToken))
        {
            return null;
        }

        return await ReadRevisionChangeAsync(
            connection,
            processor.Value.RowId,
            revision,
            cancellationToken);
    }

    private static async Task<bool> RevisionExistsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long processorRowId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = transaction is null ? string.Empty : " WITH (UPDLOCK, HOLDLOCK)";
        command.CommandText =
            "SELECT 1 FROM dbo.MetricAggregationRevision" + hint +
            " WHERE MetricAggregationProcessorRowId = @ProcessorRowId AND Position = @Position;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ValidateProcessorStreamAsync(
        SqlConnection connection,
        long persistedStreamRowId,
        MetricInputStreamId requestedStream,
        CancellationToken cancellationToken)
    {
        var requestedStreamRowId = await FindStreamAsync(
            connection, transaction: null, requestedStream, cancellationToken);
        if (requestedStreamRowId is null || requestedStreamRowId.Value != persistedStreamRowId)
        {
            throw new InvalidOperationException(
                "Aggregation processor revision belongs to a different metric input stream.");
        }
    }

    private static async Task<MetricAggregationRevisionChange> ReadRevisionChangeAsync(
        SqlConnection connection,
        long processorRowId,
        MetricAggregationCheckpoint revision,
        CancellationToken cancellationToken)
    {
        var shifts = await ReadRevisionShiftsAsync(
            connection, processorRowId, revision.Position, cancellationToken);
        var days = await ReadRevisionProductionDaysAsync(
            connection, processorRowId, revision.Position, cancellationToken);
        return new MetricAggregationRevisionChange(revision, shifts, days);
    }

    private static async Task<IReadOnlyList<ShiftOccurrenceId>> ReadRevisionShiftsAsync(
        SqlConnection connection,
        long processorRowId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ShiftOccurrenceIdentityHash, ShiftOccurrenceIdentityBinary, " +
            "SiteId, SiteOrderKey, ShiftScheduleAssignmentId, ShiftScheduleAssignmentOrderKey, " +
            "ShiftId, ShiftOrderKey, ShiftStartsAtUtc, ShiftEndsAtUtc " +
            "FROM dbo.MetricAggregationRevisionShiftOccurrence " +
            "WHERE MetricAggregationProcessorRowId = @ProcessorRowId AND Position = @Position " +
            "ORDER BY ShiftOccurrenceIdentityHash ASC;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));

        var shifts = new List<ShiftOccurrenceId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var persistedHash = (byte[])reader.GetValue(0);
            var persistedIdentity = (byte[])reader.GetValue(1);
            var site = reader.GetString(2);
            var siteOrderKey = (byte[])reader.GetValue(3);
            var assignment = reader.GetString(4);
            var assignmentOrderKey = (byte[])reader.GetValue(5);
            var shift = reader.GetString(6);
            var shiftOrderKey = (byte[])reader.GetValue(7);
            var startsAt = reader.GetFieldValue<DateTimeOffset>(8);
            var endsAt = reader.GetFieldValue<DateTimeOffset>(9);

            ValidateOrderKey(site, siteOrderKey, "site");
            ValidateOrderKey(assignment, assignmentOrderKey, "shift schedule assignment");
            ValidateOrderKey(shift, shiftOrderKey, "shift");

            ShiftOccurrenceId occurrence;
            try
            {
                occurrence = new ShiftOccurrenceId(
                    new SiteId(site),
                    new ShiftScheduleAssignmentId(assignment),
                    new ShiftId(shift),
                    startsAt,
                    endsAt);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Persisted metric aggregation revision shift occurrence is invalid.",
                    exception);
            }

            var canonicalIdentity = SqlServerShiftOccurrenceIdentityCodec.Encode(occurrence);
            var expectedHash = SqlServerShiftOccurrenceIdentityCodec.Hash(canonicalIdentity);
            if (!persistedIdentity.AsSpan().SequenceEqual(canonicalIdentity) ||
                !persistedHash.AsSpan().SequenceEqual(expectedHash))
            {
                throw new InvalidDataException(
                    "Persisted metric aggregation revision shift occurrence identity is corrupt.");
            }

            shifts.Add(occurrence);
        }

        return shifts;
    }

    private static async Task<IReadOnlyList<ProductionDayId>> ReadRevisionProductionDaysAsync(
        SqlConnection connection,
        long processorRowId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SiteId, SiteOrderKey, ProductionBusinessDate " +
            "FROM dbo.MetricAggregationRevisionProductionDay " +
            "WHERE MetricAggregationProcessorRowId = @ProcessorRowId AND Position = @Position " +
            "ORDER BY SiteOrderKey ASC, ProductionBusinessDate ASC;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));

        var days = new List<ProductionDayId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var site = reader.GetString(0);
            ValidateOrderKey(site, (byte[])reader.GetValue(1), "production-day site");
            try
            {
                days.Add(new ProductionDayId(
                    new SiteId(site),
                    DateOnly.FromDateTime(reader.GetDateTime(2))));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Persisted metric aggregation revision production day is invalid.",
                    exception);
            }
        }

        return days;
    }

    private static void ValidateOrderKey(string value, byte[] persisted, string identityName)
    {
        var expected = StringOrderKeyV2Codec.Encode(value);
        if (!persisted.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"Persisted metric aggregation revision {identityName} order key is corrupt.");
        }
    }
}
