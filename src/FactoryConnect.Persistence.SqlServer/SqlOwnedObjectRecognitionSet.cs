using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlOwnedObjectRecognitionSet
{
    public SqlOwnedObjectRecognitionSet(IEnumerable<SqlObjectName> ownedTables)
    {
        ArgumentNullException.ThrowIfNull(ownedTables);

        OwnedTables = ownedTables
            .Distinct()
            .OrderBy(static table => table.SchemaName, StringComparer.Ordinal)
            .ThenBy(static table => table.ObjectName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public ImmutableArray<SqlObjectName> OwnedTables { get; }

    public bool ContainsRepositoryIdentity(SqlObjectName repositoryIdentity) =>
        OwnedTables.Contains(repositoryIdentity);
}
