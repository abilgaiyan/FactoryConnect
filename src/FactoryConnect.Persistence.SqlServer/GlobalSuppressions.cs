using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "The schema metadata reader remains an instance boundary for both ordinary and transaction-aware reads.",
    Scope = "member",
    Target = "~M:FactoryConnect.Persistence.SqlServer.SqlServerSchemaMetadataReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(Microsoft.Data.SqlClient.SqlConnection,Microsoft.Data.SqlClient.SqlTransaction,System.Threading.CancellationToken)~System.Threading.Tasks.Task{FactoryConnect.Persistence.SqlServer.SqlSchemaDescriptor}")]
