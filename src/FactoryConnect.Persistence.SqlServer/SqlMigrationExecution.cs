using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class MigrationExecutionException : Exception
{
    public MigrationExecutionException(
        int migrationId,
        string migrationName,
        SqlException innerException)
        : base(
            $"SQL migration '{migrationId:000}_{migrationName}' failed.",
            innerException)
    {
        MigrationId = migrationId;
        MigrationName = migrationName;
    }

    public int MigrationId { get; }

    public string MigrationName { get; }
}

internal static class SqlServerMigrationLedgerCreator
{
    public static async Task CreateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE dbo.FactoryConnectMigrationHistory
            (
                MigrationId int NOT NULL,
                Name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CanonicalChecksum char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                AppliedAtUtc datetimeoffset(7) NOT NULL,
                CONSTRAINT PK_FactoryConnectMigrationHistory
                    PRIMARY KEY CLUSTERED (MigrationId ASC)
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class SqlServerMigrationExecutor
{
    public static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlMigrationDescriptor migration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(migration);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.CanonicalSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            throw new MigrationExecutionException(
                migration.MigrationId,
                migration.Name,
                exception);
        }
    }
}
