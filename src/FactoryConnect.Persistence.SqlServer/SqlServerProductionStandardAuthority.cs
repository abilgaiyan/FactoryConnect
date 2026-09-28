using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed class SqlServerProductionStandardAuthority
{
    private readonly string _connectionString;

    public SqlServerProductionStandardAuthority(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<long> PublishAsync(
        ProductionStandardVersion approvedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedVersion);
        approvedVersion.Validate();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await ReadVersionAsync(
                connection,
                transaction,
                approvedVersion.VersionId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing == approvedVersion)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return existing.PublishedRevision;
                }

                throw new InvalidOperationException("An approved standard version cannot be replaced.");
            }

            var current = await ReadCurrentRevisionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var next = checked(current + 1L);
            if (approvedVersion.PublishedRevision != next)
            {
                throw new InvalidOperationException("Published revision must be the next authority revision.");
            }

            await InsertRevisionAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
            await InsertVersionAsync(connection, transaction, approvedVersion, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return next;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ProductionStandardAuthorityCut> ReadCutAsync(
        long revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var existence = connection.CreateCommand())
        {
            existence.CommandText = """
                SELECT COUNT(*)
                FROM dbo.ProductionStandardAuthorityRevision
                WHERE ProductionStandardAuthorityRevision = @Revision;
                """;
            existence.Parameters.AddWithValue("@Revision", revision);
            if (Convert.ToInt32(await existence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException("The requested production-standard authority revision does not exist.");
            }
        }

        var versions = new List<ProductionStandardVersion>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT VersionId, ProductionStandardAuthorityRevision, CompanyId, SiteId, PartId, OperationId,
                   MachineId, SecondsPerUnit, EffectiveFromUtc, EffectiveToUtc, SourceReference
            FROM dbo.ProductionStandardVersion
            WHERE ProductionStandardAuthorityRevision <= @Revision
            ORDER BY ProductionStandardAuthorityRevision;
            """;
        command.Parameters.AddWithValue("@Revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(Materialize(reader));
        }

        return ProductionStandardAuthorityCut.Materialize(revision, versions);
    }

    public async Task<ProductionStandardAuthorityCut> ReadCurrentCutAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(ProductionStandardAuthorityRevision) FROM dbo.ProductionStandardAuthorityRevision;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var revision = Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
        return await ReadCutAsync(revision, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadCurrentRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(ProductionStandardAuthorityRevision)
            FROM dbo.ProductionStandardAuthorityRevision WITH (UPDLOCK, HOLDLOCK);
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ProductionStandardVersion?> ReadVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT VersionId, ProductionStandardAuthorityRevision, CompanyId, SiteId, PartId, OperationId,
                   MachineId, SecondsPerUnit, EffectiveFromUtc, EffectiveToUtc, SourceReference
            FROM dbo.ProductionStandardVersion WITH (UPDLOCK, HOLDLOCK)
            WHERE VersionId = @VersionId;
            """;
        command.Parameters.AddWithValue("@VersionId", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Materialize(reader) : null;
    }

    private static async Task InsertRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.ProductionStandardAuthorityRevision (ProductionStandardAuthorityRevision)
            VALUES (@Revision);
            """;
        command.Parameters.AddWithValue("@Revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProductionStandardVersion version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.ProductionStandardVersion
                (VersionId, ProductionStandardAuthorityRevision, CompanyId, SiteId, PartId, OperationId,
                 MachineId, SecondsPerUnit, EffectiveFromUtc, EffectiveToUtc, SourceReference)
            VALUES
                (@VersionId, @Revision, @CompanyId, @SiteId, @PartId, @OperationId,
                 @MachineId, @SecondsPerUnit, @EffectiveFromUtc, @EffectiveToUtc, @SourceReference);
            """;
        command.Parameters.AddWithValue("@VersionId", version.VersionId);
        command.Parameters.AddWithValue("@Revision", version.PublishedRevision);
        command.Parameters.AddWithValue("@CompanyId", version.CompanyId.Value);
        command.Parameters.AddWithValue("@SiteId", version.SiteId.Value);
        command.Parameters.AddWithValue("@PartId", version.PartId.Value);
        command.Parameters.AddWithValue("@OperationId", version.OperationId.Value);
        command.Parameters.AddWithValue("@MachineId", version.MachineId is null ? DBNull.Value : version.MachineId.Value.Value);
        command.Parameters.AddWithValue("@SecondsPerUnit", version.SecondsPerUnit);
        command.Parameters.AddWithValue("@EffectiveFromUtc", version.EffectiveFromUtc);
        command.Parameters.AddWithValue("@EffectiveToUtc", version.EffectiveToUtc is null ? DBNull.Value : version.EffectiveToUtc.Value);
        command.Parameters.AddWithValue("@SourceReference", version.SourceReference);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProductionStandardVersion Materialize(SqlDataReader reader) => new()
    {
        VersionId = reader.GetString(0),
        PublishedRevision = checked((long)reader.GetDecimal(1)),
        CompanyId = new(reader.GetString(2)),
        SiteId = new(reader.GetString(3)),
        PartId = new(reader.GetString(4)),
        OperationId = new(reader.GetString(5)),
        MachineId = reader.IsDBNull(6) ? null : new MachineId(reader.GetGuid(6)),
        SecondsPerUnit = reader.GetDecimal(7),
        EffectiveFromUtc = reader.GetFieldValue<DateTimeOffset>(8),
        EffectiveToUtc = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        SourceReference = reader.GetString(10),
    };
}
