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
                VALUES (NEWID(), @username, @salt, @hash, @iterations, 'Administrator', 1)
                """, db);
            cmd.Parameters.Add("@username", System.Data.SqlDbType.NVarChar, 64).Value = username;
            cmd.Parameters.Add("@salt", System.Data.SqlDbType.VarBinary, SaltBytes).Value = salt;
            cmd.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, HashBytes).Value = hash;
            cmd.Parameters.Add("@iterations", System.Data.SqlDbType.Int).Value = Iterations;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { CryptographicOperations.ZeroMemory(salt); CryptographicOperations.ZeroMemory(hash); }
    }

    internal async Task<(Guid Id, string Username, string Role)?> VerifyAsync(string username, string password, CancellationToken ct)
    {
        if (!ValidUsername(username) || password.Length is < 1 or > 256) return null;
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT UserId, Username, PasswordSalt, PasswordHash, Iterations, RoleName, IsActive,
                   FailedAttempts, LockedUntilUtc
            FROM dbo.LocalUser WITH (UPDLOCK, ROWLOCK)
            WHERE Username = @username
            """, db, tx);
        cmd.Parameters.Add("@username", System.Data.SqlDbType.NVarChar, 64).Value = username;
        Guid id;
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
        return valid ? (id, canonicalUsername, role) : null;
    }

    internal async Task<bool> IsActiveAsync(Guid userId, string role, CancellationToken ct)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.LocalUser
            WHERE UserId = @id AND RoleName = @role AND IsActive = 1
            """, db);
        cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = userId;
        cmd.Parameters.Add("@role", System.Data.SqlDbType.NVarChar, 32).Value = role;
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L) == 1;
    }

    private static bool ValidUsername(string username) =>
        username.Length is >= 3 and <= 64 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
