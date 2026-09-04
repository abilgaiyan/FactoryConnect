using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed record LegacyMigrationHistoryIdentity(
    int MigrationId,
    string Name,
    string CanonicalChecksum);

internal static class LegacyPost004MigrationHistory
{
    public static ImmutableArray<LegacyMigrationHistoryIdentity> Entries { get; } =
    [
        new(
            1,
            "InitialObservationIngestion",
            "E1C14282B7A246BBD9D5734370498695721D3F0A78D60F74531E35D5FEDC9057"),
        new(
            2,
            "DurableMetricAggregation",
            "F8DA0AFF348E3ED8964D5ED03042581A55D7C94898AAC739B81E60CA7F5E5113"),
        new(
            3,
            "BindMetricInputFactMachine",
            "98A9635782C4D822441269ECEE8E13BBCDC5A61C07B64608F81A0107133535C6"),
        new(
            4,
            "ProductionContextMetricInputHandoff",
            "786CDD68F66E222A4E4EFB8220595E46390A0F81880D0D45A54FA22DD7A498D5")
    ];

    public static void ValidateExactCatalog(SqlMigrationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.Migrations.Length != Entries.Length)
        {
            throw new SqlMigrationHistoryException(
                "Legacy post-004 adoption requires the repository catalog to be exactly the frozen 001-004 migration baseline.");
        }

        for (var index = 0; index < Entries.Length; index++)
        {
            var expected = Entries[index];
            var actual = catalog.Migrations[index];
            if (actual.MigrationId != expected.MigrationId ||
                !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
                !string.Equals(actual.Sha256Checksum, expected.CanonicalChecksum, StringComparison.Ordinal))
            {
                throw new SqlMigrationHistoryException(
                    $"Legacy post-004 adoption catalog position {index + 1} does not match the frozen historical migration authority.");
            }
        }
    }
}
