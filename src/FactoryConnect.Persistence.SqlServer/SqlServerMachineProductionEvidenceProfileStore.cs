using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Versioned signal configuration only; never qualifies or publishes production quantity.</summary>
public sealed record MachineProductionEvidenceProfile(
    Guid ProfileRevisionId, Guid MachineId, int RevisionNumber, string EvidenceMode,
    string? StateDataItemId, string? CounterDataItemId, string? CounterSemantics,
    DateTimeOffset EffectiveFromUtc, string ConfiguredBy, DateTimeOffset ConfiguredAtUtc);

public sealed class SqlServerMachineProductionEvidenceProfileStore
{
    private readonly string _connectionString;

    public SqlServerMachineProductionEvidenceProfileStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>Appends a revision with optimistic head checking. Identical revision-ID retries return false.</summary>
    public async Task<bool> AppendAsync(MachineProductionEvidenceProfile profile,
        Guid? expectedHeadRevisionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @LockResult int;
            EXEC @LockResult = sp_getapplock @Resource = @LockKey,
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
            IF @LockResult < 0 THROW 51120, 'Machine profile lock unavailable.', 1;

            IF EXISTS (SELECT 1 FROM dbo.MachineProductionEvidenceProfile WHERE ProfileRevisionId = @RevisionId)
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM dbo.MachineProductionEvidenceProfile
                    WHERE ProfileRevisionId = @RevisionId AND MachineId = @Machine
                      AND RevisionNumber = @Number AND EvidenceMode = @Mode
                      AND EffectiveFromUtc = @Effective AND ConfiguredBy = @Actor
                      AND ConfiguredAtUtc = @Recorded
                      AND (StateDataItemId = @State OR (StateDataItemId IS NULL AND @State IS NULL))
                      AND (CounterDataItemId = @Counter OR (CounterDataItemId IS NULL AND @Counter IS NULL))
                      AND (CounterSemantics = @Semantics OR (CounterSemantics IS NULL AND @Semantics IS NULL)))
                    THROW 51121, 'Profile revision ID reused with different contents.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                DECLARE @Head uniqueidentifier, @HeadNumber int, @HeadEffective datetimeoffset(7);
                SELECT TOP (1) @Head = ProfileRevisionId, @HeadNumber = RevisionNumber,
                    @HeadEffective = EffectiveFromUtc
                FROM dbo.MachineProductionEvidenceProfile WITH (UPDLOCK, HOLDLOCK)
                WHERE MachineId = @Machine ORDER BY RevisionNumber DESC;

                IF (@Head IS NULL AND @Expected IS NOT NULL)
                    OR (@Head IS NOT NULL AND (@Expected IS NULL OR @Head <> @Expected))
                    OR @Number <> ISNULL(@HeadNumber, 0) + 1
                    OR (@HeadEffective IS NOT NULL AND @Effective <= @HeadEffective)
                    THROW 51122, 'Stale or non-monotonic machine profile revision.', 1;

                INSERT dbo.MachineProductionEvidenceProfile
                    (ProfileRevisionId, MachineId, RevisionNumber, EvidenceMode,
                     StateDataItemId, CounterDataItemId, CounterSemantics,
                     EffectiveFromUtc, ConfiguredBy, ConfiguredAtUtc)
                VALUES (@RevisionId, @Machine, @Number, @Mode, @State, @Counter,
                        @Semantics, @Effective, @Actor, @Recorded);
                SELECT CAST(1 AS bit);
            END
            """;
        AddParameters(command, profile);
        command.Parameters.Add("@Expected", SqlDbType.UniqueIdentifier).Value =
            (object?)expectedHeadRevisionId ?? DBNull.Value;
        command.Parameters.Add("@LockKey", SqlDbType.NVarChar, 255).Value =
            "MachineEvidenceProfile:" + profile.MachineId.ToString("N");
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Profile append returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    /// <summary>Returns the revision effective at the requested UTC instant, or null if unconfigured.</summary>
    public async Task<MachineProductionEvidenceProfile?> GetEffectiveAsync(
        Guid machineId, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || atUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Machine and UTC instant are required.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) ProfileRevisionId, MachineId, RevisionNumber, EvidenceMode,
                StateDataItemId, CounterDataItemId, CounterSemantics,
                EffectiveFromUtc, ConfiguredBy, ConfiguredAtUtc
            FROM dbo.MachineProductionEvidenceProfile
            WHERE MachineId = @Machine AND EffectiveFromUtc <= @At
            ORDER BY EffectiveFromUtc DESC;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@At", SqlDbType.DateTimeOffset).Value = atUtc;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<MachineProductionEvidenceProfile>> ListAsync(
        Guid machineId, CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty) throw new ArgumentException("Machine is required.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ProfileRevisionId, MachineId, RevisionNumber, EvidenceMode,
                StateDataItemId, CounterDataItemId, CounterSemantics,
                EffectiveFromUtc, ConfiguredBy, ConfiguredAtUtc
            FROM dbo.MachineProductionEvidenceProfile WHERE MachineId = @Machine
            ORDER BY RevisionNumber ASC;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        var profiles = new List<MachineProductionEvidenceProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) profiles.Add(Read(reader));
        return profiles;
    }

    private static MachineProductionEvidenceProfile Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8),
        reader.GetFieldValue<DateTimeOffset>(9));

    private static void Validate(MachineProductionEvidenceProfile p)
    {
        if (p.ProfileRevisionId == Guid.Empty || p.MachineId == Guid.Empty ||
            p.RevisionNumber < 1 || p.EffectiveFromUtc.Offset != TimeSpan.Zero ||
            p.ConfiguredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid machine profile identity or UTC timestamps.");
        Required(p.ConfiguredBy, 200);
        Optional(p.StateDataItemId, 200);
        Optional(p.CounterDataItemId, 200);
        if (p.EvidenceMode == "StateOnly")
        {
            Required(p.StateDataItemId, 200);
            if (p.CounterDataItemId is not null || p.CounterSemantics is not null)
                throw new ArgumentException("State-only machines cannot have counter signals.");
        }
        else if (p.EvidenceMode == "CounterOnly" || p.EvidenceMode == "CncCounterAndState")
        {
            Required(p.CounterDataItemId, 200);
            if (p.EvidenceMode == "CncCounterAndState") Required(p.StateDataItemId, 200);
            else if (p.StateDataItemId is not null)
                throw new ArgumentException("Counter-only machines cannot have state signals.");
            if (p.CounterSemantics is not ("CumulativeResettable" or "CumulativeMonotonic" or "CyclePulse"))
                throw new ArgumentException("Invalid counter semantics.");
        }
        else throw new ArgumentException("Unknown evidence mode.");
    }

    private static void Required(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new ArgumentException("Missing or oversized machine profile field.");
    }

    private static void Optional(string? value, int max)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > max))
            throw new ArgumentException("Invalid optional machine profile field.");
    }

    private static void AddText(SqlCommand command, string name, string? value, int length) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, length).Value =
            (object?)value ?? DBNull.Value;

    private static void AddParameters(SqlCommand command, MachineProductionEvidenceProfile p)
    {
        command.Parameters.Add("@RevisionId", SqlDbType.UniqueIdentifier).Value = p.ProfileRevisionId;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = p.MachineId;
        command.Parameters.Add("@Number", SqlDbType.Int).Value = p.RevisionNumber;
        AddText(command, "@Mode", p.EvidenceMode, 30);
        AddText(command, "@State", p.StateDataItemId, 200);
        AddText(command, "@Counter", p.CounterDataItemId, 200);
        AddText(command, "@Semantics", p.CounterSemantics, 30);
        command.Parameters.Add("@Effective", SqlDbType.DateTimeOffset).Value = p.EffectiveFromUtc;
        AddText(command, "@Actor", p.ConfiguredBy, 200);
        command.Parameters.Add("@Recorded", SqlDbType.DateTimeOffset).Value = p.ConfiguredAtUtc;
    }
}
