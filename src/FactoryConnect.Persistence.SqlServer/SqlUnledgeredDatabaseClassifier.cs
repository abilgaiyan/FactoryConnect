namespace FactoryConnect.Persistence.SqlServer;

internal enum UnledgeredDatabaseClassification
{
    Uninitialized,
    LegacyAdoptable,
    PartialOrIncompatibleLegacy
}

internal static class SqlUnledgeredDatabaseClassifier
{
    public static UnledgeredDatabaseClassification Classify(SqlSchemaDescriptor ownedSchemaSnapshot)
    {
        ArgumentNullException.ThrowIfNull(ownedSchemaSnapshot);

        if (ownedSchemaSnapshot.Tables.IsEmpty)
        {
            return UnledgeredDatabaseClassification.Uninitialized;
        }

        return SqlSchemaComparator.Compare(
                SqlRepositorySchemaDescriptors.LegacyPost004,
                ownedSchemaSnapshot)
            .IsExactMatch
                ? UnledgeredDatabaseClassification.LegacyAdoptable
                : UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy;
    }
}
