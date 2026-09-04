using System.Collections.Immutable;
using System.Reflection;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlMigrationTransactionPolicy
{
    EngineOwned,
    LegacyMigration003Embedded
}

internal sealed record SqlMigrationDescriptor(
    int MigrationId,
    string Name,
    string ResourceName,
    SqlMigrationTransactionPolicy TransactionPolicy,
    string CanonicalSql,
    ImmutableArray<byte> CanonicalBytes,
    string Sha256Checksum);

internal sealed class SqlMigrationCatalog
{
    internal const string ResourcePrefix = "FactoryConnect.Persistence.SqlServer.Sql.";
    private const string SqlExtension = ".sql";
    private const int LegacyEmbeddedTransactionMigrationId = 3;
    private const string LegacyEmbeddedTransactionMigrationName = "BindMetricInputFactMachine";

    private SqlMigrationCatalog(ImmutableArray<SqlMigrationDescriptor> migrations)
    {
        Migrations = migrations;
    }

    public ImmutableArray<SqlMigrationDescriptor> Migrations { get; }

    public static SqlMigrationCatalog Load(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlMigrationCatalog).Assembly;

        var resourceNames = assembly
            .GetManifestResourceNames()
            .Where(IsMigrationNamespaceSqlResource)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var descriptors = resourceNames
            .Select(resourceName => LoadDescriptor(assembly, resourceName));

        return Create(descriptors);
    }

    internal static SqlMigrationCatalog Create(IEnumerable<SqlMigrationDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var ordered = descriptors
            .OrderBy(static descriptor => descriptor.MigrationId)
            .ThenBy(static descriptor => descriptor.Name, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.ResourceName, StringComparer.Ordinal)
            .ToImmutableArray();

        ValidateCatalog(ordered);
        return new SqlMigrationCatalog(ordered);
    }

    private static bool IsMigrationNamespaceSqlResource(string resourceName) =>
        resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
        resourceName.EndsWith(SqlExtension, StringComparison.OrdinalIgnoreCase);

    private static SqlMigrationDescriptor LoadDescriptor(Assembly assembly, string resourceName)
    {
        var identity = ParseResourceIdentity(resourceName);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration resource '{resourceName}' could not be opened.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        var canonical = SqlMigrationCanonicalizer.Canonicalize(memory.ToArray());
        var transactionPolicy = GetTransactionPolicy(identity.MigrationId, identity.Name);
        SqlMigrationLexicalPolicy.Validate(canonical.Text, transactionPolicy);

        return new SqlMigrationDescriptor(
            identity.MigrationId,
            identity.Name,
            resourceName,
            transactionPolicy,
            canonical.Text,
            canonical.Bytes,
            canonical.Sha256Checksum);
    }

    internal static (int MigrationId, string Name) ParseResourceIdentity(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
            !resourceName.EndsWith(SqlExtension, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Migration resource '{resourceName}' is outside the frozen naming grammar.");
        }

        var fileName = resourceName[ResourcePrefix.Length..^SqlExtension.Length];
        if (fileName.Length < 5 || fileName[3] != '_')
        {
            throw new InvalidOperationException($"Migration resource '{resourceName}' must use '<three-digit-id>_<name>.sql'.");
        }

        var idText = fileName[..3];
        if (idText.Any(static character => character < '0' || character > '9'))
        {
            throw new InvalidOperationException($"Migration resource '{resourceName}' must begin with exactly three ASCII digits.");
        }

        var name = fileName[4..];
        if (!IsValidMigrationName(name))
        {
            throw new InvalidOperationException($"Migration resource '{resourceName}' contains an invalid migration name component.");
        }

        return (int.Parse(idText, System.Globalization.CultureInfo.InvariantCulture), name);
    }

    internal static bool IsValidMigrationName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static SqlMigrationTransactionPolicy GetTransactionPolicy(int migrationId, string name)
    {
        if (migrationId == LegacyEmbeddedTransactionMigrationId &&
            string.Equals(name, LegacyEmbeddedTransactionMigrationName, StringComparison.Ordinal))
        {
            return SqlMigrationTransactionPolicy.LegacyMigration003Embedded;
        }

        return SqlMigrationTransactionPolicy.EngineOwned;
    }

    private static void ValidateCatalog(ImmutableArray<SqlMigrationDescriptor> descriptors)
    {
        if (descriptors.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("No SQL migration resources were discovered.");
        }

        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var resources = new HashSet<string>(StringComparer.Ordinal);

        var previousId = -1;
        foreach (var descriptor in descriptors)
        {
            if (!ids.Add(descriptor.MigrationId))
            {
                throw new InvalidOperationException($"Duplicate migration id '{descriptor.MigrationId:000}'.");
            }

            if (!names.Add(descriptor.Name))
            {
                throw new InvalidOperationException($"Duplicate migration name '{descriptor.Name}'.");
            }

            if (!resources.Add(descriptor.ResourceName))
            {
                throw new InvalidOperationException($"Duplicate migration resource '{descriptor.ResourceName}'.");
            }

            if (descriptor.MigrationId <= previousId)
            {
                throw new InvalidOperationException("Migration catalog ids must be strictly increasing.");
            }

            previousId = descriptor.MigrationId;
        }

        var legacy = descriptors.SingleOrDefault(static descriptor => descriptor.MigrationId == LegacyEmbeddedTransactionMigrationId);
        if (legacy is null ||
            !string.Equals(legacy.Name, LegacyEmbeddedTransactionMigrationName, StringComparison.Ordinal) ||
            legacy.TransactionPolicy != SqlMigrationTransactionPolicy.LegacyMigration003Embedded)
        {
            throw new InvalidOperationException("Migration 003 must be exactly '003_BindMetricInputFactMachine'.");
        }

        foreach (var descriptor in descriptors.Where(static descriptor => descriptor.MigrationId != LegacyEmbeddedTransactionMigrationId))
        {
            if (descriptor.TransactionPolicy != SqlMigrationTransactionPolicy.EngineOwned)
            {
                throw new InvalidOperationException("Only migration 003 may use the legacy embedded transaction policy.");
            }
        }
    }
}
