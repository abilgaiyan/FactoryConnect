using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed partial class SqlServerMetricAggregationStore
{
    private static async Task InsertRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition position,
        IEnumerable<PositionedMetricInputFact> stagedInputs,
        CancellationToken cancellationToken)
    {
        var inputs = stagedInputs.ToArray();
        var shifts = inputs.Select(static input => input.ShiftOccurrenceId).Distinct().ToArray();
        var productionDays = inputs.Select(static input => input.ProductionDayId).Distinct().ToArray();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO dbo.MetricAggregationRevision " +
                "(MetricAggregationProcessorRowId, Position) VALUES (@ProcessorRowId, @Position);";
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Metric aggregation revision publication failed.");
            }
        }

        foreach (var occurrence in shifts)
        {
            var canonicalIdentity = SqlServerShiftOccurrenceIdentityCodec.Encode(occurrence);
            var identityHash = SqlServerShiftOccurrenceIdentityCodec.Hash(canonicalIdentity);

            await using var collision = connection.CreateCommand();
            collision.Transaction = transaction;
            collision.CommandText =
                "SELECT ShiftOccurrenceIdentityBinary FROM dbo.MetricAggregationRevisionShiftOccurrence " +
                "WITH (UPDLOCK, HOLDLOCK) " +
                "WHERE MetricAggregationProcessorRowId = @ProcessorRowId " +
                "AND Position = @Position AND ShiftOccurrenceIdentityHash = @IdentityHash;";
            collision.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
            collision.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
            collision.Parameters.Add("@IdentityHash", SqlDbType.Binary, 32).Value = identityHash;
            var existing = await collision.ExecuteScalarAsync(cancellationToken);
            if (existing is byte[] existingIdentity &&
                !existingIdentity.AsSpan().SequenceEqual(canonicalIdentity))
            {
                throw new InvalidOperationException(
                    "Metric aggregation revision shift-occurrence identity hash collision detected.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO dbo.MetricAggregationRevisionShiftOccurrence " +
                "(MetricAggregationProcessorRowId, Position, ShiftOccurrenceIdentityHash, " +
                "ShiftOccurrenceIdentityBinary, SiteId, SiteOrderKey, ShiftScheduleAssignmentId, " +
                "ShiftScheduleAssignmentOrderKey, ShiftId, ShiftOrderKey, ShiftStartsAtUtc, ShiftEndsAtUtc) " +
                "VALUES (@ProcessorRowId, @Position, @IdentityHash, @IdentityBinary, @SiteId, @SiteOrderKey, " +
                "@AssignmentId, @AssignmentOrderKey, @ShiftId, @ShiftOrderKey, @StartsAtUtc, @EndsAtUtc);";
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
            command.Parameters.Add("@IdentityHash", SqlDbType.Binary, 32).Value = identityHash;
            command.Parameters.Add("@IdentityBinary", SqlDbType.VarBinary, -1).Value = canonicalIdentity;
            AddString(command, "@SiteId", occurrence.SiteId.Value, 256);
            command.Parameters.Add("@SiteOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
                .Value = StringOrderKeyV2Codec.Encode(occurrence.SiteId.Value);
            AddString(command, "@AssignmentId", occurrence.ShiftScheduleAssignmentId.Value, 256);
            command.Parameters.Add("@AssignmentOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
                .Value = StringOrderKeyV2Codec.Encode(occurrence.ShiftScheduleAssignmentId.Value);
            AddString(command, "@ShiftId", occurrence.ShiftId.Value, 256);
            command.Parameters.Add("@ShiftOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
                .Value = StringOrderKeyV2Codec.Encode(occurrence.ShiftId.Value);
            command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value = occurrence.StartsAtUtc;
            command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value = occurrence.EndsAtUtc;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var productionDay in productionDays)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO dbo.MetricAggregationRevisionProductionDay " +
                "(MetricAggregationProcessorRowId, Position, SiteId, SiteOrderKey, ProductionBusinessDate) " +
                "VALUES (@ProcessorRowId, @Position, @SiteId, @SiteOrderKey, @BusinessDate);";
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
            AddString(command, "@SiteId", productionDay.SiteId.Value, 256);
            command.Parameters.Add("@SiteOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
                .Value = StringOrderKeyV2Codec.Encode(productionDay.SiteId.Value);
            command.Parameters.Add("@BusinessDate", SqlDbType.Date).Value =
                productionDay.BusinessDate.ToDateTime(TimeOnly.MinValue);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
