using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

public sealed class ReportingGateDmvOverheadProbeTests
{
    private static readonly string[] ExpectedVariantNames =
        ["sessions", "sessions-requests", "sessions-requests-connections", "full-existing"];
    private const string ValidConnection = @"Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect_GG003_DMV_RCA_UNIT;Integrated Security=true";

    [Fact]
    public void CatalogHasFourOrderedSelectOnlySystemDmvVariants()
    {
        Assert.Equal(ExpectedVariantNames,
            ReportingGateDmvOverheadProbe.Queries.Select(q => q.Name));
        Assert.All(ReportingGateDmvOverheadProbe.Queries, query =>
        {
            Assert.StartsWith("SELECT ", query.Sql);
            Assert.Contains("WHERE LEFT(s.program_name,LEN(@Prefix))=@Prefix", query.Sql);
            Assert.DoesNotContain("dbo.", query.Sql);
            Assert.DoesNotContain("sp_getapplock", query.Sql);
            Assert.DoesNotContain("UPDATE ", query.Sql);
            Assert.DoesNotContain("INSERT ", query.Sql);
            Assert.DoesNotContain("DELETE ", query.Sql);
        });
        Assert.DoesNotContain("sys.dm_exec_requests", ReportingGateDmvOverheadProbe.Queries[0].Sql);
        Assert.DoesNotContain("sys.dm_exec_connections", ReportingGateDmvOverheadProbe.Queries[1].Sql);
        Assert.DoesNotContain("sys.dm_tran_locks", ReportingGateDmvOverheadProbe.Queries[2].Sql);
        Assert.Contains("sys.dm_tran_locks", ReportingGateDmvOverheadProbe.Queries[3].Sql);
    }

    [Fact]
    public void FullVariantPreservesTheExistingSamplerQuery()
    {
        var sql = ReportingGateDmvOverheadProbe.Queries[3].Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal("CEA5BA653CF3C7A109CB3D15933993D407F17CE60E74E75424A898846B411DAB",
            ReportingGateCommandTrace.FingerprintSql(sql));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryCycleGetsAnIndependentFiveSecondParameterizedCommand(int index)
    {
        using var connection = new SqlConnection();
        using var command = ReportingGateDmvOverheadProbe.CreateCommand(connection,
            ReportingGateDmvOverheadProbe.Queries[index], "private-prefix");
        Assert.Equal(5, command.CommandTimeout);
        Assert.Equal("private-prefix", command.Parameters["@Prefix"].Value);
        Assert.DoesNotContain("private-prefix", command.CommandText);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("remote-server")]
    [InlineData("preserved-clone")]
    [InlineData("non-windows")]
    public void UnsafeOrUnauthorizedTargetsFailBeforeOpeningSql(string scenario)
    {
        var connection = scenario switch
        {
            "remote-server" => "Server=localhost;Database=FactoryConnect_GG003_DMV_RCA_UNIT",
            "preserved-clone" => @"Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect_GG003_GATE_ADVANCE_20261024_R31A",
            _ => ValidConnection,
        };
        var authorization = scenario == "authorization" ? "not-authorized" : ReportingGateDmvOverheadProbe.AuthorizationValue;
        Assert.Throws<InvalidOperationException>(() => ReportingGateDmvOverheadProbe.ValidateTarget(
            connection, authorization, scenario != "non-windows"));
    }

    [Fact]
    public void AcceptedTargetUsesReadOnlyIntentAndDoesNotPoolVariantSessions()
    {
        var settings = ReportingGateDmvOverheadProbe.ValidateTarget(ValidConnection,
            ReportingGateDmvOverheadProbe.AuthorizationValue, true);
        Assert.Equal(ApplicationIntent.ReadOnly, settings.ApplicationIntent);
        Assert.False(settings.Pooling);
        Assert.Equal(5, settings.ConnectTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("invalid")]
    public void InvalidRepetitionCountsAreRejected(string value) =>
        Assert.Throws<InvalidOperationException>(() => ReportingGateDmvOverheadProbe.ParseCycles(value));

    [Fact]
    public void RepetitionDefaultAndBoundsAreExplicit()
    {
        Assert.Equal(3, ReportingGateDmvOverheadProbe.ParseCycles(null));
        Assert.Equal(1, ReportingGateDmvOverheadProbe.ParseCycles("1"));
        Assert.Equal(10, ReportingGateDmvOverheadProbe.ParseCycles("10"));
    }

    [Fact]
    public async Task FailedStageRecordsVariantCycleStageAndCannotEmitCompletion()
    {
        var events = new List<object>();
        var error = new TimeoutException("private failure message");
        var caught = await Assert.ThrowsAsync<TimeoutException>(() =>
            ReportingGateDmvOverheadProbe.Stage<int>("full-existing", 2, "query-execute",
                () => Task.FromException<int>(error), events.Add));
        Assert.Same(error, caught);
        var encoded = events.Select(e => JsonSerializer.Serialize(e)).ToArray();
        var failure = Assert.Single(encoded, line => line.Contains("dmv-stage-failure", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(failure);
        Assert.Equal("full-existing", document.RootElement.GetProperty("variant").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("cycle").GetInt32());
        Assert.Equal("query-execute", document.RootElement.GetProperty("stage").GetString());
        Assert.Equal(typeof(TimeoutException).FullName, document.RootElement.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain(encoded, line => line.Contains("dmv-stage-complete", StringComparison.Ordinal));
        Assert.DoesNotContain("private failure message", failure);
    }

    [Fact]
    public async Task SuccessfulStageHasAnIndependentCompletionRecord()
    {
        var events = new List<object>();
        Assert.Equal(7, await ReportingGateDmvOverheadProbe.Stage("sessions", 1, "row-stream",
            () => Task.FromResult(7), events.Add));
        var encoded = events.Select(e => JsonSerializer.Serialize(e)).ToArray();
        Assert.Contains(encoded, line => line.Contains("dmv-stage-complete", StringComparison.Ordinal));
        Assert.DoesNotContain(encoded, line => line.Contains("dmv-stage-failure", StringComparison.Ordinal));
    }

    [Fact]
    public void EstimatedPlanSanitizationRemovesBoundValuesAndProbePrefix()
    {
        const string xml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><ColumnReference ParameterCompiledValue="private-compiled"
              ParameterRuntimeValue="private-runtime" Other="probe-prefix:" /></BatchSequence>
            </ShowPlanXML>
            """;
        var sanitized = ReportingGateDmvOverheadProbe.SanitizeEstimatedPlan(xml, "probe-prefix:");
        Assert.DoesNotContain("private-compiled", sanitized);
        Assert.DoesNotContain("private-runtime", sanitized);
        Assert.DoesNotContain("probe-prefix:", sanitized);
        Assert.Contains("[probe-prefix]", sanitized);
    }
}
