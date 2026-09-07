using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Acquires the manifest and evidence portions of the FC-030.2C publication
/// lock boundary immediately after C.3 projection acquisition has completed.
/// </summary>
internal static class SqlServerOperationalMetricProjectionPublicationLocks
{
    internal static async Task<PublicationPreparation> PrepareAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);

        var projectionPlan = await SqlServerOperationalMetricProjectionRows.PrepareAsync(
            context,
            commit,
            cancellationToken).ConfigureAwait(false);

        var locks = await AcquireAsync(
            context.Connection,
            context.Transaction,
            context.ProjectionProcessorRowId,
            projectionPlan,
            cancellationToken).ConfigureAwait(false);

        return new PublicationPreparation(projectionPlan, locks);
    }

    internal static async Task<PublicationLockSnapshot> AcquireAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long projectionProcessorRowId,
        SqlServerOperationalMetricProjectionMutationPlan projectionPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(projectionPlan);

        if (projectionProcessorRowId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectionProcessorRowId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // C.1 freezes manifest acquisition after projection acquisition. Lock the
        // complete current manifest prefix in ascending projection-row order. The
        // processor/checkpoint locks already serialize same-processor publication,
        // so proposed rows that do not yet have SQL identity values need no
        // pre-DML child-row key authority.
        var manifestProjectionRowIds = await LockManifestAsync(
            connection,
            transaction,
            projectionProcessorRowId,
            cancellationToken).ConfigureAwait(false);

        // C.3 classifies the complete current durable projection set into rows that
        // survive the proposed publication plus rows that become obsolete. Rebuild
        // that set here without performing another projection-table acquisition.
        var expectedCurrentProjectionRowIds = projectionPlan.ProposedRows
            .Where(static row => row.ExistingProjectionRowId.HasValue)
            .Select(static row => row.ExistingProjectionRowId!.Value)
            .Concat(projectionPlan.ObsoleteProjectionRowIds)
            .Order()
            .ToArray();

        if (!manifestProjectionRowIds.SequenceEqual(expectedCurrentProjectionRowIds))
        {
            throw new InvalidOperationException(
                "Operational metric projection manifest does not exactly match the current durable projection set.");
        }

        // Evidence acquisition is deliberately after the manifest prefix. The
        // forced ordinal authority gives deterministic projection/kind/ordinal
        // acquisition and range protection for every currently published row.
        var evidenceRows = await LockEvidenceAsync(
            connection,
            transaction,
            manifestProjectionRowIds,
            cancellationToken).ConfigureAwait(false);

        return new PublicationLockSnapshot(manifestProjectionRowIds, evidenceRows);
    }

    private static async Task<long[]> LockManifestAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long projectionProcessorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT OperationalMetricProjectionRowId
            FROM dbo.OperationalMetricProjectionManifest WITH
            (
                UPDLOCK,
                HOLDLOCK,
                INDEX(PK_OperationalMetricProjectionManifest)
            )
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY OperationalMetricProjectionRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", System.Data.SqlDbType.BigInt).Value =
            projectionProcessorRowId;

        var rowIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowIds.Add(reader.GetInt64(0));
        }

        return [.. rowIds];
    }

    private static async Task<IReadOnlyList<LockedEvidenceRow>> LockEvidenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<long> projectionRowIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<LockedEvidenceRow>();

        // Resolve one projection evidence prefix at a time so projection IDs are
        // acquired in ascending order. Each query uses the frozen ordinal unique
        // authority and orders kind/ordinal within the projection.
        foreach (var projectionRowId in projectionRowIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    OperationalMetricProjectionEvidenceRowId,
                    EvidenceKind,
                    EvidenceOrdinal,
                    OperandName,
                    OperandNameOrderKey
                FROM dbo.OperationalMetricProjectionEvidence WITH
                (
                    UPDLOCK,
                    HOLDLOCK,
                    INDEX(UQ_OperationalMetricProjectionEvidence_Ordinal)
                )
                WHERE OperationalMetricProjectionRowId = @ProjectionRowId
                ORDER BY EvidenceKind, EvidenceOrdinal;
                """;
            command.Parameters.Add("@ProjectionRowId", System.Data.SqlDbType.BigInt).Value =
                projectionRowId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new LockedEvidenceRow(
                    projectionRowId,
                    reader.GetInt64(0),
                    reader.GetByte(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    (byte[])reader.GetValue(4)));
            }
        }

        return rows;
    }

    internal sealed record PublicationPreparation(
        SqlServerOperationalMetricProjectionMutationPlan ProjectionPlan,
        PublicationLockSnapshot Locks);

    internal sealed record PublicationLockSnapshot(
        IReadOnlyList<long> ManifestProjectionRowIds,
        IReadOnlyList<LockedEvidenceRow> EvidenceRows);

    internal sealed record LockedEvidenceRow(
        long OperationalMetricProjectionRowId,
        long OperationalMetricProjectionEvidenceRowId,
        byte EvidenceKind,
        int EvidenceOrdinal,
        string OperandName,
        byte[] OperandNameOrderKey);
}
