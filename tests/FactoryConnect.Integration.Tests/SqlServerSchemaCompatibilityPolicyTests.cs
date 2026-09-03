using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerSchemaCompatibilityPolicyContractTests
{
    [Fact]
    public void ColumnDescriptorDoesNotOwnPhysicalOrdinal()
    {
        Assert.DoesNotContain(
            typeof(SqlColumnDescriptor).GetProperties(),
            static property => string.Equals(property.Name, "Ordinal", StringComparison.Ordinal));
    }

    [Fact]
    public void Post004RepositoryAuthorityRequiresEnabledPrimaryAndUniqueKeys()
    {
        var descriptors = new[]
        {
            SqlRepositorySchemaDescriptors.LegacyPost004,
            SqlRepositorySchemaDescriptors.Current
        };

        foreach (var descriptor in descriptors)
        {
            Assert.All(
                descriptor.Tables.Where(static table => table.PrimaryKey is not null),
                static table => Assert.True(table.PrimaryKey!.IsEnabled));
            Assert.All(
                descriptor.Tables.SelectMany(static table => table.UniqueConstraints),
                static constraint => Assert.True(constraint.IsEnabled));
        }
    }
}

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerKeyConstraintStateIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerKeyConstraintStateIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReaderProjectsEnabledPrimaryAndUniqueKeyBackingIndexes()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var reader = new SqlServerSchemaMetadataReader();

        var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
            connection,
            CancellationToken.None);

        var machineObservation = snapshot.Tables.Single(
            static table => table.Name.ObjectName == "MachineObservation");
        var shiftAggregate = snapshot.Tables.Single(
            static table => table.Name.ObjectName == "ShiftMetricAggregate");

        Assert.True(Assert.IsType<SqlPrimaryKeyDescriptor>(machineObservation.PrimaryKey).IsEnabled);
        Assert.True(shiftAggregate.UniqueConstraints.Single(
            static constraint => constraint.Name == "UQ_ShiftMetricAggregate_IdentityHash").IsEnabled);
    }

    [Fact]
    public async Task ReaderProjectsDisabledPrimaryKeyBackingIndex()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            "ALTER INDEX [PK_MachineObservation] ON [dbo].[MachineObservation] DISABLE;");

        try
        {
            var reader = new SqlServerSchemaMetadataReader();
            var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
                connection,
                CancellationToken.None);
            var machineObservation = snapshot.Tables.Single(
                static table => table.Name.ObjectName == "MachineObservation");

            Assert.False(Assert.IsType<SqlPrimaryKeyDescriptor>(machineObservation.PrimaryKey).IsEnabled);
        }
        finally
        {
            await ExecuteAsync(
                connection,
                "ALTER INDEX [PK_MachineObservation] ON [dbo].[MachineObservation] REBUILD;");
        }
    }

    [Fact]
    public async Task ReaderProjectsDisabledUniqueConstraintBackingIndex()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            "ALTER INDEX [UQ_ShiftMetricAggregate_IdentityHash] ON [dbo].[ShiftMetricAggregate] DISABLE;");

        try
        {
            var reader = new SqlServerSchemaMetadataReader();
            var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
                connection,
                CancellationToken.None);
            var shiftAggregate = snapshot.Tables.Single(
                static table => table.Name.ObjectName == "ShiftMetricAggregate");
            var unique = shiftAggregate.UniqueConstraints.Single(
                static constraint => constraint.Name == "UQ_ShiftMetricAggregate_IdentityHash");

            Assert.False(unique.IsEnabled);
        }
        finally
        {
            await ExecuteAsync(
                connection,
                "ALTER INDEX [UQ_ShiftMetricAggregate_IdentityHash] ON [dbo].[ShiftMetricAggregate] REBUILD;");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}
