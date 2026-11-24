using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

internal sealed class LocalUsers(string connectionString)
{
    private const int Iterations = 310_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;

    internal static string? ConnectionString(IConfiguration config) =>
        config["PersistenceProviders:SqlServer:ConnectionString"];

    internal async Task CreateAdminAsync(string username, string password, CancellationToken ct)
    {
        if (!ValidUsername(username) || password.Length < 14 || password.Length > 256)
            throw new ArgumentException("Username must be 3-64 ASCII letters/digits/._-; password must be 14-256 characters.");
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        try
        {
            await using var db = new SqlConnection(connectionString);
            await db.OpenAsync(ct);
            await using var cmd = new SqlCommand("""
                INSERT INTO dbo.LocalUser (UserId, Username, PasswordSalt, PasswordHash, Iterations, RoleName, IsActive)
                SELECT NEWID(), @username, @salt, @hash, @iterations, 'Administrator', 1
                WHERE NOT EXISTS (SELECT 1 FROM dbo.LocalUser WITH (UPDLOCK, HOLDLOCK))
                """, db);
            cmd.Parameters.Add("@username", System.Data.SqlDbType.NVarChar, 64).Value = username;
            cmd.Parameters.Add("@salt", System.Data.SqlDbType.VarBinary, SaltBytes).Value = salt;
            cmd.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, HashBytes).Value = hash;
            cmd.Parameters.Add("@iterations", System.Data.SqlDbType.Int).Value = Iterations;
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Initial administrator provisioning is only available for an empty user master.");
        }
        finally { CryptographicOperations.ZeroMemory(salt); CryptographicOperations.ZeroMemory(hash); }
    }

    internal async Task<(Guid Id, string Username, string Role, Guid SessionVersion)?> VerifyAsync(string username, string password, CancellationToken ct)
    {
        if (!ValidUsername(username) || password.Length is < 1 or > 256) return null;
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT UserId, Username, PasswordSalt, PasswordHash, Iterations, RoleName, IsActive,
                   FailedAttempts, LockedUntilUtc, SessionVersion
            FROM dbo.LocalUser WITH (UPDLOCK, ROWLOCK)
            WHERE Username = @username
            """, db, tx);
        cmd.Parameters.Add("@username", System.Data.SqlDbType.NVarChar, 64).Value = username;
        Guid id, sessionVersion;
        string canonicalUsername, role;
        byte[] salt, storedHash;
        int iterations, failed;
        bool active;
        DateTime? locked;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            id = reader.GetGuid(0);
            canonicalUsername = reader.GetString(1);
            salt = (byte[])reader[2];
            storedHash = (byte[])reader[3];
            iterations = reader.GetInt32(4);
            role = reader.GetString(5);
            active = reader.GetBoolean(6);
            failed = reader.GetInt32(7);
            locked = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
            sessionVersion = reader.GetGuid(9);
        }
        if (!active || locked is not null && locked.Value > DateTime.UtcNow) return null;
        if (iterations is < 100_000 or > 1_000_000 || salt.Length != SaltBytes || storedHash.Length != HashBytes)
            throw new InvalidOperationException("Invalid stored password parameters.");
        byte[] computed = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        bool valid = CryptographicOperations.FixedTimeEquals(computed, storedHash);
        CryptographicOperations.ZeroMemory(computed);
        await using var update = new SqlCommand(valid
            ? "UPDATE dbo.LocalUser SET FailedAttempts = 0, LockedUntilUtc = NULL WHERE UserId = @id"
            : """
              UPDATE dbo.LocalUser SET FailedAttempts = CASE WHEN FailedAttempts >= 4 THEN 0 ELSE FailedAttempts + 1 END,
                  LockedUntilUtc = CASE WHEN FailedAttempts >= 4 THEN DATEADD(minute, 15, SYSUTCDATETIME()) ELSE NULL END
              WHERE UserId = @id
              """, db, tx);
        update.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = id;
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return valid ? (id, canonicalUsername, role, sessionVersion) : null;
    }

    internal async Task<bool> IsActiveAsync(Guid userId, string role, Guid sessionVersion, CancellationToken ct)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.LocalUser
            WHERE UserId = @id AND RoleName = @role AND IsActive = 1 AND SessionVersion = @version
            """, db);
        cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = userId;
        cmd.Parameters.Add("@role", System.Data.SqlDbType.NVarChar, 32).Value = role;
        cmd.Parameters.Add("@version", System.Data.SqlDbType.UniqueIdentifier).Value = sessionVersion;
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L) == 1;
    }

    internal sealed record UserSummary(Guid UserId, string Username, string Role, bool IsActive);

    internal async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct)
    {
        var users = new List<UserSummary>();
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT UserId, Username, RoleName, IsActive FROM dbo.LocalUser ORDER BY Username", db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            users.Add(new UserSummary(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return users;
    }

    internal async Task<Guid> CreateAsync(string username, string password, string role, CancellationToken ct)
    {
        if (!ValidUsername(username) || password.Length is < 14 or > 256 ||
            role is not ("Administrator" or "Supervisor" or "Manager"))
            throw new ArgumentException("Invalid username, password or role.");
        var id = Guid.NewGuid();
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        try
        {
            await using var db = new SqlConnection(connectionString);
            await db.OpenAsync(ct);
            await using var cmd = new SqlCommand("""
                INSERT INTO dbo.LocalUser (UserId, Username, PasswordSalt, PasswordHash, Iterations, RoleName, IsActive)
                VALUES (@id, @username, @salt, @hash, @iterations, @role, 1)
                """, db);
            cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = id;
            cmd.Parameters.Add("@username", System.Data.SqlDbType.NVarChar, 64).Value = username;
            cmd.Parameters.Add("@salt", System.Data.SqlDbType.VarBinary, SaltBytes).Value = salt;
            cmd.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, HashBytes).Value = hash;
            cmd.Parameters.Add("@iterations", System.Data.SqlDbType.Int).Value = Iterations;
            cmd.Parameters.Add("@role", System.Data.SqlDbType.NVarChar, 32).Value = role;
            await cmd.ExecuteNonQueryAsync(ct);
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(salt); CryptographicOperations.ZeroMemory(hash); }
    }

    internal async Task<bool> ChangeAccessAsync(Guid actorId, Guid userId, string role, bool active, CancellationToken ct)
    {
        if (actorId == userId || role is not ("Administrator" or "Supervisor" or "Manager"))
            throw new ArgumentException("Cannot change your own access or assign an invalid role.");
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await using var guard = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.LocalUser WITH (UPDLOCK, HOLDLOCK)
            WHERE UserId = @actor AND RoleName = N'Administrator' AND IsActive = 1
            """, db, tx);
        guard.Parameters.Add("@actor", System.Data.SqlDbType.UniqueIdentifier).Value = actorId;
        if ((long)(await guard.ExecuteScalarAsync(ct) ?? 0L) != 1)
            throw new InvalidOperationException("Administrator access is no longer active.");
        await using var cmd = new SqlCommand("""
            UPDATE dbo.LocalUser SET RoleName = @role, IsActive = @active, SessionVersion = NEWID()
            WHERE UserId = @id
            """, db, tx);
        cmd.Parameters.Add("@role", System.Data.SqlDbType.NVarChar, 32).Value = role;
        cmd.Parameters.Add("@active", System.Data.SqlDbType.Bit).Value = active;
        cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = userId;
        var changed = await cmd.ExecuteNonQueryAsync(ct) == 1;
        await tx.CommitAsync(ct);
        return changed;
    }

    private static bool ValidUsername(string username) =>
        username.Length is >= 3 and <= 64 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
