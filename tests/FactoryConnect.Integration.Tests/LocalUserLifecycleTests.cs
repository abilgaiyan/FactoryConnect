using FactoryConnect.Api;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class LocalUserLifecycleTests
{
    [Fact]
    public async Task AccessChangesAndPasswordResetPermanentlyInvalidateOldSessions()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        var users = new LocalUsers(database.ConnectionString);
        var ct = CancellationToken.None;
        const string original = "Original-test-password-1";
        const string replacement = "Replacement-test-password-2";
        await users.CreateAdminAsync("admin", original, ct);
        var admin = (await users.VerifyAsync("admin", original, ct))!.Value;
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.CreateAdminAsync("other-admin", original, ct));
        var id = await users.CreateAsync("supervisor", original, "Supervisor", ct);
        var first = (await users.VerifyAsync("supervisor", original, ct))!.Value;
        Assert.Equal(id, first.Id);
        Assert.True(await users.IsActiveAsync(id, "Supervisor", first.SessionVersion, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => users.ChangeAccessAsync(admin.Id, admin.Id, "Manager", false, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.ChangeAccessAsync(id, admin.Id, "Manager", false, ct));
        Assert.True(await users.ChangeAccessAsync(admin.Id, id, "Manager", false, ct));
        Assert.Null(await users.VerifyAsync("supervisor", original, ct));
        Assert.True(await users.ChangeAccessAsync(admin.Id, id, "Supervisor", true, ct));
        Assert.False(await users.IsActiveAsync(id, "Supervisor", first.SessionVersion, ct));
        var second = (await users.VerifyAsync("supervisor", original, ct))!.Value;
        Assert.NotEqual(first.SessionVersion, second.SessionVersion);
        Assert.True(await users.ResetPasswordAsync(admin.Id, id, replacement, ct));
        Assert.False(await users.IsActiveAsync(id, "Supervisor", second.SessionVersion, ct));
        Assert.Null(await users.VerifyAsync("supervisor", original, ct));
        var third = (await users.VerifyAsync("supervisor", replacement, ct))!.Value;
        Assert.Equal(id, third.Id);
        Assert.True(await users.IsActiveAsync(id, "Supervisor", third.SessionVersion, ct));
        Assert.Equal(2, (await users.ListAsync(ct)).Count);
    }
}
