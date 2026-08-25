using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerUInt64
{
    public static SqlParameter CreateParameter(
        string name,
        ulong value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = 20,
            Scale = 0,
            Value = checked((decimal)value),
        };
    }

    public static ulong Materialize(decimal value) =>
        checked((ulong)value);
}
