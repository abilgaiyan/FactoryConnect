using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

// Consistent diagnostic read; not a canonical reference-time publication.
public sealed class SqlServerSupervisorAcceptedIdealTimeReader(string connectionString)
{
    public async Task<SupervisorAcceptedIdealTimeResult> ReadAsync(Guid machineId, string assignmentId,
        string shiftId, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        SqlServerSupervisorCycleDefaultStore.Validate(new SupervisorCycleContext(machineId,
            assignmentId, shiftId, startsAtUtc, endsAtUtc, null, "validation", null));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var cut = DateTimeOffset.UtcNow;
        var production = await SqlServerAcceptedShiftProductionReader.ReadCoreAsync(connection, transaction,
            machineId, assignmentId, shiftId, startsAtUtc, endsAtUtc, cancellationToken);
        var result = await CalculateAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;

        async Task<SupervisorAcceptedIdealTimeResult> CalculateAsync()
        {
            if (production is null)
                return SupervisorAcceptedIdealTime.Calculate(null, Array.Empty<SupervisorShiftIdealCycleRevision>(),
                    Array.Empty<SupervisorCycleReview>(), null, cut);
            if (production.MachineId != machineId || production.ShiftScheduleAssignmentId != assignmentId ||
                production.ShiftId != shiftId || production.ShiftStartsAtUtc != startsAtUtc ||
                production.ShiftEndsAtUtc != endsAtUtc || production.PublishedAtUtc > cut)
                throw new InvalidOperationException("Accepted production scope or time mismatch.");
            if (string.IsNullOrWhiteSpace(production.PartNumber))
                return new(production.PublicationId, null, null, "publication-inapplicable");
            var context = new SupervisorCycleContext(machineId, assignmentId, shiftId, startsAtUtc, endsAtUtc,
                production.ProductionOrder, production.PartNumber, production.Operation);
            try { SqlServerSupervisorCycleDefaultStore.Validate(context); }
            catch (ArgumentException) { return new(production.PublicationId, null, null, "publication-inapplicable"); }
            var cycles = await SqlServerSupervisorShiftIdealCycleStore.ListCoreAsync(connection, transaction,
                machineId, assignmentId, startsAtUtc, endsAtUtc, cancellationToken);
            var reviews = await SqlServerSupervisorCycleReviewStore.ListCoreAsync(connection, transaction,
                machineId, assignmentId, shiftId, startsAtUtc, endsAtUtc, cut, cancellationToken);
            var snapshot = await SqlServerSupervisorCycleDefaultStore.ReadCoreAsync(connection, transaction,
                context, cancellationToken);
            return SupervisorAcceptedIdealTime.Calculate(production, cycles, reviews, snapshot, cut);
        }
    }
}
