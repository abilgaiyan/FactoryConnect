using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

public sealed class ReportingGateCommandTraceTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.MetricAggregationContribution c JOIN dbo.MetricInputFact f ON 1=1", "historical-contribution-sql")]
    [InlineData("select * from dbo.productionreferencetimepublicationtransition", "reference-time-transition-sql")]
    [InlineData("SELECT * FROM dbo.ProductionReferenceTimePublicationCut", "reference-time-cut-sql")]
    [InlineData("SELECT * FROM dbo.ProductionReferenceTimeOutcome", "reference-time-outcome-sql")]
    [InlineData("SELECT * FROM dbo.MetricAggregationRevisionProductionDay", "aggregation-day-revision-sql")]
    [InlineData("SELECT * FROM dbo.OperationalMetricProjectionManifest", "publication-manifest-sql")]
    [InlineData("UPDATE dbo.OperationalMetricProjectionCheckpoint SET Position=@P", "checkpoint-write-sql")]
    [InlineData("SELECT * FROM dbo.OperationalMetricProjectionCheckpoint", "checkpoint-sql")]
    [InlineData("SELECT * FROM dbo.UnrecognizedTable", "other-sql")]
    public void CategoriesDistinguishOverlappingTableNamesAndKeepFallback(string sql, string expected) =>
        Assert.Equal(expected, ReportingGateCommandTrace.ClassifySql(sql));

    [Fact]
    public void InMemoryDiagnosticEventsRetainShapeFingerprintWithoutParameterValues()
    {
        var application = "FC-Trace-Unit-" + Guid.NewGuid().ToString("N");
        using var trace = new ReportingGateCommandTrace(application);
        using var connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = "not-opened", InitialCatalog = "not-opened", ApplicationName = application + ":initial-checkpoint",
        }.ConnectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM dbo.MetricAggregationContribution WHERE Position=@Position";
        command.Parameters.AddWithValue("@Position", "private-value-one");
        var first = Guid.NewGuid();
        trace.OnNext(new("Microsoft.Data.SqlClient.WriteCommandBefore", new { Command = command, OperationId = first, TransactionId = 123L }));
        trace.OnNext(new("Microsoft.Data.SqlClient.WriteCommandAfter", new { Command = command, OperationId = first, TransactionId = 123L }));
        command.Parameters["@Position"].Value = "private-value-two";
        var second = Guid.NewGuid();
        trace.OnNext(new("Microsoft.Data.SqlClient.WriteCommandBefore", new { Command = command, OperationId = second, TransactionId = 123L }));
        trace.OnNext(new("Microsoft.Data.SqlClient.WriteCommandAfter", new { Command = command, OperationId = second, TransactionId = 123L }));
        var entries = trace.Entries;
        Assert.Collection(entries,
            entry => Assert.Equal(first, entry.OperationId),
            entry => Assert.Equal(second, entry.OperationId));
        Assert.All(entries, entry =>
        {
            Assert.Equal("initial-checkpoint", entry.Role);
            Assert.Equal("historical-contribution-sql", entry.Kind);
            Assert.Matches("^[0-9A-F]{64}$", entry.CommandFingerprint!);
            Assert.DoesNotContain("private-value", System.Text.Json.JsonSerializer.Serialize(entry));
        });
        Assert.Equal(entries[0].CommandFingerprint, entries[1].CommandFingerprint);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }
}
