using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerMachineShiftOccurrenceRosterStore :
    IMachineShiftOccurrenceRosterStore
{
    private readonly string _connectionString;

    public SqlServerMachineShiftOccurrenceRosterStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask<MachineShiftOccurrenceRoster?> ReadAsync(
        MachineId machineId,
        ProductionDayId productionDayId,
        CancellationToken cancellationToken)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(productionDayId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var expectedSiteOrderKey = StringOrderKeyV2Codec.Encode(productionDayId.SiteId.Value);
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    r.MachineShiftOccurrenceRosterRowId,
                    r.MachineId,
                    r.ProductionDaySiteId,
                    r.ProductionDaySiteOrderKey,
                    r.ProductionBusinessDate,
                    r.ProductionLineId,
                    r.Revision,
                    o.ShiftScheduleAssignmentId,
                    o.ShiftScheduleAssignmentOrderKey,
                    o.ShiftId,
                    o.ShiftOrderKey,
                    o.ShiftStartsAtUtc,
                    o.ShiftEndsAtUtc,
                    CASE
                        WHEN o.MachineShiftOccurrenceRosterRowId IS NULL THEN CAST(0 AS bit)
                        WHEN EXISTS
                        (
                            SELECT 1
                            FROM dbo.MachineShiftOccurrenceRosterOccurrence AS otherOccurrence
                            INNER JOIN dbo.MachineShiftOccurrenceRoster AS otherRoster
                                ON otherRoster.MachineShiftOccurrenceRosterRowId = otherOccurrence.MachineShiftOccurrenceRosterRowId
                            WHERE otherOccurrence.ShiftScheduleAssignmentOrderKey = o.ShiftScheduleAssignmentOrderKey
                              AND otherOccurrence.ShiftOrderKey = o.ShiftOrderKey
                              AND otherOccurrence.ShiftStartsAtUtc = o.ShiftStartsAtUtc
                              AND otherOccurrence.ShiftEndsAtUtc = o.ShiftEndsAtUtc
                              AND otherRoster.ProductionDaySiteOrderKey = r.ProductionDaySiteOrderKey
                              AND otherRoster.ProductionBusinessDate <> r.ProductionBusinessDate
                        ) THEN CAST(1 AS bit)
                        ELSE CAST(0 AS bit)
                    END AS HasConflictingOwnership
                FROM dbo.MachineShiftOccurrenceRoster AS r
                LEFT JOIN dbo.MachineShiftOccurrenceRosterOccurrence AS o
                    ON o.MachineShiftOccurrenceRosterRowId = r.MachineShiftOccurrenceRosterRowId
                WHERE r.MachineId = @MachineId
                  AND r.ProductionBusinessDate = @ProductionBusinessDate
                  AND
                  (
                      r.ProductionDaySiteOrderKey = @ProductionDaySiteOrderKey
                      OR r.ProductionDaySiteId = @ProductionDaySiteId
                  )
                ORDER BY
                    r.MachineShiftOccurrenceRosterRowId,
                    o.ShiftStartsAtUtc,
                    o.ShiftEndsAtUtc,
                    o.ShiftScheduleAssignmentOrderKey,
                    o.ShiftOrderKey;
                """;
            command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId.Value;
            command.Parameters.Add(
                "@ProductionDaySiteOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value = expectedSiteOrderKey;
            AddString(command, "@ProductionDaySiteId", productionDayId.SiteId.Value);
            command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
                productionDayId.BusinessDate.ToDateTime(TimeOnly.MinValue);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            try
            {
                var rosterRowId = reader.GetInt64(0);
                var persistedMachineId = new MachineId(reader.GetGuid(1));
                var persistedSiteId = reader.GetString(2);
                var persistedSiteOrderKey = (byte[])reader[3];
                var persistedBusinessDate = DateOnly.FromDateTime(reader.GetDateTime(4));
                var productionLineId = new ProductionLineId(reader.GetString(5));
                var revision = new MachineShiftOccurrenceRosterRevision(
                    SqlServerUInt64.Materialize(reader.GetDecimal(6)));

                ValidateParentIdentity(
                    machineId,
                    productionDayId,
                    expectedSiteOrderKey,
                    persistedMachineId,
                    persistedSiteId,
                    persistedSiteOrderKey,
                    persistedBusinessDate,
                    productionLineId);

                var occurrences = new List<MachineShiftOccurrenceOwnership>();
                do
                {
                    if (reader.GetInt64(0) != rosterRowId)
                    {
                        throw Corruption(
                            "Persisted machine-shift occurrence roster has conflicting rows for one semantic identity.");
                    }

                    if (reader.IsDBNull(7))
                    {
                        if (!reader.IsDBNull(8) ||
                            !reader.IsDBNull(9) ||
                            !reader.IsDBNull(10) ||
                            !reader.IsDBNull(11) ||
                            !reader.IsDBNull(12))
                        {
                            throw Corruption(
                                "Persisted roster occurrence has an incomplete identity.");
                        }

                        continue;
                    }

                    var assignmentIdText = reader.GetString(7);
                    var assignmentOrderKey = (byte[])reader[8];
                    var shiftIdText = reader.GetString(9);
                    var shiftOrderKey = (byte[])reader[10];
                    ValidateOrderKey(
                        assignmentIdText,
                        assignmentOrderKey,
                        "shift schedule assignment");
                    ValidateOrderKey(shiftIdText, shiftOrderKey, "shift");

                    if (reader.GetBoolean(13))
                    {
                        throw Corruption(
                            "Persisted shift occurrence is owned by conflicting production days.");
                    }

                    var occurrenceId = new ShiftOccurrenceId(
                        productionDayId.SiteId,
                        new ShiftScheduleAssignmentId(assignmentIdText),
                        new ShiftId(shiftIdText),
                        reader.GetFieldValue<DateTimeOffset>(11),
                        reader.GetFieldValue<DateTimeOffset>(12));
                    occurrences.Add(new MachineShiftOccurrenceOwnership(
                        machineId,
                        productionLineId,
                        occurrenceId,
                        productionDayId));
                }
                while (await reader.ReadAsync(cancellationToken));

                return new MachineShiftOccurrenceRoster(
                    machineId,
                    productionLineId,
                    productionDayId,
                    revision,
                    occurrences);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                OverflowException or
                InvalidCastException)
            {
                throw Corruption(
                    "Persisted machine-shift occurrence roster is invalid.",
                    exception);
            }
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Machine-shift occurrence roster read was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async ValueTask CommitAsync(
        MachineShiftOccurrenceRosterCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        var proposed = commit.ProposedRoster;
        var siteOrderKey = StringOrderKeyV2Codec.Encode(proposed.ProductionDayId.SiteId.Value);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var sqlTransaction = (SqlTransaction)transaction;

            try
            {
                var current = await ReadCurrentForUpdateAsync(
                    connection,
                    sqlTransaction,
                    proposed.MachineId,
                    proposed.ProductionDayId,
                    siteOrderKey,
                    cancellationToken);

                ValidateCommitAgainstCurrent(commit, current);
                await ValidateNoConflictingOwnershipAsync(
                    connection,
                    sqlTransaction,
                    proposed,
                    siteOrderKey,
                    cancellationToken);

                long rosterRowId;
                if (current is null)
                {
                    rosterRowId = await InsertRosterAsync(
                        connection,
                        sqlTransaction,
                        proposed,
                        siteOrderKey,
                        cancellationToken);
                }
                else
                {
                    rosterRowId = current.RowId;
                    await AdvanceRevisionAsync(
                        connection,
                        sqlTransaction,
                        current,
                        proposed.Revision.Value,
                        cancellationToken);
                    await DeleteOccurrencesAsync(
                        connection,
                        sqlTransaction,
                        rosterRowId,
                        cancellationToken);
                }

                foreach (var ownership in proposed.Occurrences)
                {
                    await InsertOccurrenceAsync(
                        connection,
                        sqlTransaction,
                        rosterRowId,
                        ownership,
                        cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Machine-shift occurrence roster commit was cancelled.",
                exception,
                cancellationToken);
        }
    }

    private static async Task<PersistedRoster?> ReadCurrentForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MachineId machineId,
        ProductionDayId productionDayId,
        byte[] expectedSiteOrderKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                MachineShiftOccurrenceRosterRowId,
                MachineId,
                ProductionDaySiteId,
                ProductionDaySiteOrderKey,
                ProductionBusinessDate,
                ProductionLineId,
                Revision
            FROM dbo.MachineShiftOccurrenceRoster WITH (UPDLOCK, HOLDLOCK)
            WHERE MachineId = @MachineId
              AND ProductionBusinessDate = @ProductionBusinessDate
              AND
              (
                  ProductionDaySiteOrderKey = @ProductionDaySiteOrderKey
                  OR ProductionDaySiteId = @ProductionDaySiteId
              )
            ORDER BY MachineShiftOccurrenceRosterRowId;
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId.Value;
        command.Parameters.Add(
            "@ProductionDaySiteOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value = expectedSiteOrderKey;
        AddString(command, "@ProductionDaySiteId", productionDayId.SiteId.Value);
        command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
            productionDayId.BusinessDate.ToDateTime(TimeOnly.MinValue);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var persisted = new PersistedRoster(
            reader.GetInt64(0),
            new MachineId(reader.GetGuid(1)),
            reader.GetString(2),
            (byte[])reader[3],
            DateOnly.FromDateTime(reader.GetDateTime(4)),
            new ProductionLineId(reader.GetString(5)),
            SqlServerUInt64.Materialize(reader.GetDecimal(6)));

        ValidateParentIdentity(
            machineId,
            productionDayId,
            expectedSiteOrderKey,
            persisted.MachineId,
            persisted.SiteId,
            persisted.SiteOrderKey,
            persisted.BusinessDate,
            persisted.ProductionLineId);

        if (await reader.ReadAsync(cancellationToken))
        {
            throw Corruption(
                "Persisted machine-shift occurrence roster has conflicting rows for one semantic identity.");
        }

        return persisted;
    }

    private static void ValidateCommitAgainstCurrent(
        MachineShiftOccurrenceRosterCommit commit,
        PersistedRoster? current)
    {
        var proposed = commit.ProposedRoster;
        if (current is not null &&
            current.ProductionLineId != proposed.ProductionLineId)
        {
            throw Corruption(
                "Persisted machine-shift occurrence roster production line does not match the proposed roster identity state.");
        }

        var expectedRevision = commit.ExpectedRevision?.Value;
        if (current?.Revision != expectedRevision)
        {
            throw new InvalidOperationException(
                "Machine-shift occurrence roster revision conflict.");
        }

        if (current is null)
        {
            if (proposed.Revision.Value != 1)
            {
                throw new InvalidOperationException(
                    "An initial machine-shift occurrence roster must use revision one.");
            }

            return;
        }

        if (current.Revision == ulong.MaxValue ||
            proposed.Revision.Value != current.Revision + 1)
        {
            throw new InvalidOperationException(
                "A replacement machine-shift occurrence roster must use the next revision.");
        }
    }

    private static async Task ValidateNoConflictingOwnershipAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MachineShiftOccurrenceRoster proposed,
        byte[] proposedSiteOrderKey,
        CancellationToken cancellationToken)
    {
        foreach (var ownership in proposed.Occurrences)
        {
            var occurrence = ownership.ShiftOccurrenceId;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT TOP (1)
                    r.ProductionDaySiteId,
                    r.ProductionDaySiteOrderKey
                FROM dbo.MachineShiftOccurrenceRosterOccurrence AS o WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.MachineShiftOccurrenceRoster AS r WITH (UPDLOCK, HOLDLOCK)
                    ON r.MachineShiftOccurrenceRosterRowId = o.MachineShiftOccurrenceRosterRowId
                WHERE o.ShiftScheduleAssignmentOrderKey = @AssignmentOrderKey
                  AND o.ShiftOrderKey = @ShiftOrderKey
                  AND o.ShiftStartsAtUtc = @StartsAtUtc
                  AND o.ShiftEndsAtUtc = @EndsAtUtc
                  AND r.ProductionDaySiteOrderKey = @ProductionDaySiteOrderKey
                  AND r.ProductionBusinessDate <> @ProductionBusinessDate;
                """;
            command.Parameters.Add(
                "@AssignmentOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(occurrence.ShiftScheduleAssignmentId.Value);
            command.Parameters.Add(
                "@ShiftOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(occurrence.ShiftId.Value);
            command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value =
                occurrence.StartsAtUtc;
            command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value =
                occurrence.EndsAtUtc;
            command.Parameters.Add(
                "@ProductionDaySiteOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value = proposedSiteOrderKey;
            command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
                proposed.ProductionDayId.BusinessDate.ToDateTime(TimeOnly.MinValue);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                continue;
            }

            ValidateOrderKey(
                reader.GetString(0),
                (byte[])reader[1],
                "production-day site");
            throw new InvalidOperationException(
                "A shift occurrence cannot belong to conflicting production days.");
        }
    }

    private static async Task<long> InsertRosterAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MachineShiftOccurrenceRoster roster,
        byte[] siteOrderKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.MachineShiftOccurrenceRoster
                (MachineId, ProductionDaySiteId, ProductionDaySiteOrderKey,
                 ProductionBusinessDate, ProductionLineId, Revision)
            OUTPUT INSERTED.MachineShiftOccurrenceRosterRowId
            VALUES
                (@MachineId, @ProductionDaySiteId, @ProductionDaySiteOrderKey,
                 @ProductionBusinessDate, @ProductionLineId, @Revision);
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = roster.MachineId.Value;
        AddString(command, "@ProductionDaySiteId", roster.ProductionDayId.SiteId.Value);
        command.Parameters.Add(
            "@ProductionDaySiteOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value = siteOrderKey;
        command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
            roster.ProductionDayId.BusinessDate.ToDateTime(TimeOnly.MinValue);
        AddString(command, "@ProductionLineId", roster.ProductionLineId.Value);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", roster.Revision.Value));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task AdvanceRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PersistedRoster current,
        ulong nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.MachineShiftOccurrenceRoster
            SET Revision = @NextRevision
            WHERE MachineShiftOccurrenceRosterRowId = @RosterRowId
              AND Revision = @ExpectedRevision;
            """;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@NextRevision", nextRevision));
        command.Parameters.Add("@RosterRowId", SqlDbType.BigInt).Value = current.RowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ExpectedRevision", current.Revision));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Machine-shift occurrence roster revision conflict.");
        }
    }

    private static async Task DeleteOccurrencesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long rosterRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM dbo.MachineShiftOccurrenceRosterOccurrence
            WHERE MachineShiftOccurrenceRosterRowId = @RosterRowId;
            """;
        command.Parameters.Add("@RosterRowId", SqlDbType.BigInt).Value = rosterRowId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOccurrenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long rosterRowId,
        MachineShiftOccurrenceOwnership ownership,
        CancellationToken cancellationToken)
    {
        var occurrence = ownership.ShiftOccurrenceId;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.MachineShiftOccurrenceRosterOccurrence
                (MachineShiftOccurrenceRosterRowId,
                 ShiftScheduleAssignmentId, ShiftScheduleAssignmentOrderKey,
                 ShiftId, ShiftOrderKey, ShiftStartsAtUtc, ShiftEndsAtUtc)
            VALUES
                (@RosterRowId,
                 @ShiftScheduleAssignmentId, @ShiftScheduleAssignmentOrderKey,
                 @ShiftId, @ShiftOrderKey, @ShiftStartsAtUtc, @ShiftEndsAtUtc);
            """;
        command.Parameters.Add("@RosterRowId", SqlDbType.BigInt).Value = rosterRowId;
        AddString(
            command,
            "@ShiftScheduleAssignmentId",
            occurrence.ShiftScheduleAssignmentId.Value);
        command.Parameters.Add(
            "@ShiftScheduleAssignmentOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(occurrence.ShiftScheduleAssignmentId.Value);
        AddString(command, "@ShiftId", occurrence.ShiftId.Value);
        command.Parameters.Add(
            "@ShiftOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(occurrence.ShiftId.Value);
        command.Parameters.Add("@ShiftStartsAtUtc", SqlDbType.DateTimeOffset).Value =
            occurrence.StartsAtUtc;
        command.Parameters.Add("@ShiftEndsAtUtc", SqlDbType.DateTimeOffset).Value =
            occurrence.EndsAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateParentIdentity(
        MachineId expectedMachineId,
        ProductionDayId expectedProductionDayId,
        byte[] expectedSiteOrderKey,
        MachineId persistedMachineId,
        string persistedSiteId,
        byte[] persistedSiteOrderKey,
        DateOnly persistedBusinessDate,
        ProductionLineId persistedProductionLineId)
    {
        if (persistedMachineId != expectedMachineId ||
            !string.Equals(
                persistedSiteId,
                expectedProductionDayId.SiteId.Value,
                StringComparison.Ordinal) ||
            !persistedSiteOrderKey.AsSpan().SequenceEqual(expectedSiteOrderKey) ||
            !persistedSiteOrderKey.AsSpan().SequenceEqual(
                StringOrderKeyV2Codec.Encode(persistedSiteId)) ||
            persistedBusinessDate != expectedProductionDayId.BusinessDate ||
            persistedProductionLineId.IsEmpty)
        {
            throw Corruption(
                "Persisted machine-shift occurrence roster identity is inconsistent.");
        }
    }

    private static void ValidateOrderKey(
        string text,
        byte[] persistedOrderKey,
        string identityKind)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !persistedOrderKey.AsSpan().SequenceEqual(StringOrderKeyV2Codec.Encode(text)))
        {
            throw Corruption(
                $"Persisted machine-shift occurrence roster {identityKind} order key does not match its textual identity.");
        }
    }

    private static InvalidOperationException Corruption(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);

    private static void AddString(SqlCommand command, string name, string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, 256).Value = value;

    private sealed record PersistedRoster(
        long RowId,
        MachineId MachineId,
        string SiteId,
        byte[] SiteOrderKey,
        DateOnly BusinessDate,
        ProductionLineId ProductionLineId,
        ulong Revision);
}
