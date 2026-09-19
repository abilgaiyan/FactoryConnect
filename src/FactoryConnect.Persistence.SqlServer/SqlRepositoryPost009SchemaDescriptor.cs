using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost009SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post008)
    {
        ArgumentNullException.ThrowIfNull(post008);

        return new SqlSchemaDescriptor(
            post008.Tables
                .Select(ApplyDurableOutputIdentityCorrection)
                .ToImmutableArray());
    }

    private static SqlTableDescriptor ApplyDurableOutputIdentityCorrection(SqlTableDescriptor table) =>
        table.Name.ObjectName switch
        {
            "MachineStateChangeHistory" => AddDurableOutputIdentity(table),
            "MachineActivityPeriodHistory" => AddDurableOutputIdentity(table),
            _ => table
        };

    private static SqlTableDescriptor AddDurableOutputIdentity(SqlTableDescriptor table) =>
        table with
        {
            Columns =
            [
                .. table.Columns,
                UInt64("InstanceId"),
                UInt64("Sequence")
            ],
            CheckConstraints =
            [
                .. table.CheckConstraints,
                UInt64Check(table.Name.ObjectName, "InstanceId"),
                UInt64Check(table.Name.ObjectName, "Sequence")
            ]
        };

    private static SqlColumnDescriptor UInt64(string name) =>
        new(
            name,
            "decimal",
            MaxLength: null,
            Precision: 20,
            Scale: 0,
            IsNullable: false,
            Collation: null,
            Identity: null);

    private static SqlCheckConstraintDescriptor UInt64Check(string tableName, string columnName) =>
        new(
            $"CK_{tableName}_{columnName}_UInt64",
            $"([{columnName}]>=(0) AND [{columnName}]<=(18446744073709551615.))",
            IsEnabled: true,
            IsTrusted: true,
            IsNotForReplication: false);
}
