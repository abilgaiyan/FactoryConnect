using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionProcessorGateIntegrationTests(SqlServerTestDatabaseFixture fixture)
    : IClassFixture<SqlServerTestDatabaseFixture>
{
    [Fact]
    public void ResourceIsBoundedOrdinalAndUsesExistingCanonicalBytes()
    {
        var id = new OperationalMetricProjectionProcessorId(new string('x', 256));
        var resource = SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(id);
        Assert.Equal(120, resource.Length);
        Assert.Equal(resource, SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new(id.Value)));
        Assert.NotEqual(resource, SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new(new string('X', 256))));
        Assert.NotEqual(SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new("é")),
            SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new("e\u0301")));
        Assert.NotEqual(SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new("a")),
            SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new("a\0")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new(new string('x', 257))));
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 0, 97, 0 }));
        Assert.EndsWith(expected, SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(new("a")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedLocksCoexistAndDifferentProcessorDoesNotContend()
    {
        var id = new OperationalMetricProjectionProcessorId($"gate-{Guid.NewGuid():N}");
        await using var c1 = new SqlConnection(fixture.ConnectionString); await c1.OpenAsync();
        await using var t1 = (SqlTransaction)await c1.BeginTransactionAsync();
        await using var c2 = new SqlConnection(fixture.ConnectionString); await c2.OpenAsync();
        await using var t2 = (SqlTransaction)await c2.BeginTransactionAsync();
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(c1, t1, id,
            OperationalMetricProjectionProcessorGateMode.Shared, CancellationToken.None, 0);
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(c2, t2, id,
            OperationalMetricProjectionProcessorGateMode.Shared, CancellationToken.None, 0);
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(c2, t2, new(id.Value + "-other"),
            OperationalMetricProjectionProcessorGateMode.Exclusive, CancellationToken.None, 0);
        await t2.RollbackAsync(); await t1.RollbackAsync();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public async Task IncompatibleModesTimeoutAndRollbackReleasesGate(int heldMode, int requestedMode)
    {
        var held = (OperationalMetricProjectionProcessorGateMode)heldMode;
        var requested = (OperationalMetricProjectionProcessorGateMode)requestedMode;
        var id = new OperationalMetricProjectionProcessorId($"gate-{Guid.NewGuid():N}");
        await using var c1 = new SqlConnection(fixture.ConnectionString); await c1.OpenAsync();
        await using var t1 = (SqlTransaction)await c1.BeginTransactionAsync();
        await using var c2 = new SqlConnection(fixture.ConnectionString); await c2.OpenAsync();
        await using var t2 = (SqlTransaction)await c2.BeginTransactionAsync();
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(c1, t1, id, held, CancellationToken.None, 0);
        await Assert.ThrowsAsync<TimeoutException>(() => SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(
            c2, t2, id, requested, CancellationToken.None, 0));
        await t1.RollbackAsync();
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(c2, t2, id, requested, CancellationToken.None, 0);
        await t2.CommitAsync();
    }

    [Fact]
    public async Task PreCancellationRemainsOperationalFailure()
    {
        await using var c = new SqlConnection(fixture.ConnectionString); await c.OpenAsync();
        await using var t = (SqlTransaction)await c.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(
            c, t, new("cancel-gate"), OperationalMetricProjectionProcessorGateMode.Exclusive, new CancellationToken(true)));
        await t.RollbackAsync();
    }
}
