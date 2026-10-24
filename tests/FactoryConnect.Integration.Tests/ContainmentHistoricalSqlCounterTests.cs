using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

// Synthetic diagnostics only: these tests never open a SQL connection or execute a command.
public sealed class ContainmentHistoricalSqlCounterTests
{
    private const string HistoricalSql = "SELECT s.MachineId, s.StreamKey, f.Position, f.FactId, f.MetricInputKey, " +
        "f.MetricValue FROM dbo.MetricAggregationContribution c WHERE c.MetricAggregationProcessorRowId=@ProcessorRowId " +
        "AND c.Position <= @ThroughPosition ORDER BY c.Position ASC;";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordsCompletionsAndErrorsSeparatelyFromStartedCommands(bool failed)
    {
        using var counter = new ContainmentHistoricalSqlCounter("Server=(local);Database=counter;Integrated Security=true");
        using var connection = new SqlConnection(counter.ConnectionString);
        using var command = HistoricalCommand(connection);
        using var listener = new DiagnosticListener("SqlClientDiagnosticListener");
        listener.Write("Microsoft.Data.SqlClient.WriteCommandBefore", new { Command = command });
        Assert.Empty(counter.Executions);
        Assert.Equal(1, counter.PendingCount);
        listener.Write(failed ? "Microsoft.Data.SqlClient.WriteCommandError" : "Microsoft.Data.SqlClient.WriteCommandAfter", new { Command = command });
        var observed = Assert.Single(counter.Executions);
        Assert.Equal(534ul, observed.Position);
        Assert.Equal(7, observed.Processor);
        Assert.Equal(9, observed.Stream);
        Assert.Equal(failed, observed.Failed);
        Assert.False(observed.HadTransactionAtStart);
        Assert.False(observed.TransactionBoundToConnectionAtStart);
        Assert.Equal(0L, observed.TransactionOrdinal);
        Assert.NotNull(observed.Elapsed);
        Assert.True(observed.Elapsed.Value >= TimeSpan.Zero);
        Assert.Equal(0, counter.PendingCount);
        Assert.Null(counter.ListenerError);
    }

    [Fact]
    public void FiltersOtherApplicationsAndNonHistoricalCommandsAndStopsAfterDisposal()
    {
        using var counter = new ContainmentHistoricalSqlCounter("Server=(local);Database=counter;Integrated Security=true");
        using var other = new ContainmentHistoricalSqlCounter(counter.ConnectionString);
        using var connection = new SqlConnection(counter.ConnectionString);
        using var command = HistoricalCommand(connection);
        using var listener = new DiagnosticListener("SqlClientDiagnosticListener");
        listener.Write("Microsoft.Data.SqlClient.WriteCommandBefore", new { Command = command });
        listener.Write("Microsoft.Data.SqlClient.WriteCommandAfter", new { Command = command });
        Assert.Single(counter.Executions);
        Assert.NotNull(counter.Executions[0].Elapsed);
        Assert.Empty(other.Executions);
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.MetricAggregationRevision;";
        listener.Write("Microsoft.Data.SqlClient.WriteCommandBefore", new { Command = command });
        listener.Write("Microsoft.Data.SqlClient.WriteCommandAfter", new { Command = command });
        Assert.Single(counter.Executions);
        counter.Dispose();
        command.CommandText = HistoricalSql;
        listener.Write("Microsoft.Data.SqlClient.WriteCommandBefore", new { Command = command });
        listener.Write("Microsoft.Data.SqlClient.WriteCommandAfter", new { Command = command });
        Assert.Single(counter.Executions);
        Assert.Equal(0, counter.PendingCount);
    }


    [Fact]
    public async Task OverlappingCommandsPreserveIdentityAndDuration()
    {
        using var counter = new ContainmentHistoricalSqlCounter("Server=(local);Database=counter;Integrated Security=true");
        using var connection = new SqlConnection(counter.ConnectionString);
        using var first = HistoricalCommand(connection);
        using var second = HistoricalCommand(connection);
        second.Parameters["@ThroughPosition"].Value = 537m;
        second.Parameters["@ProcessorRowId"].Value = 8L;
        using var started = new Barrier(2);
        async Task Emit(SqlCommand command, bool failed)
        {
            await Task.Run(() =>
            {
                counter.OnNext(new KeyValuePair<string, object?>("Microsoft.Data.SqlClient.WriteCommandBefore", new { Command = command }));
                Assert.True(started.SignalAndWait(TimeSpan.FromSeconds(10)));
                counter.OnNext(new KeyValuePair<string, object?>(failed ? "Microsoft.Data.SqlClient.WriteCommandError" :
                    "Microsoft.Data.SqlClient.WriteCommandAfter", new { Command = command }));
            });
        }
        await Task.WhenAll(Emit(first, false), Emit(second, true));
        var executions = counter.Executions;
        Assert.Equal(2, executions.Length);
        var a = Assert.Single(executions, e => e.Position == 534);
        var b = Assert.Single(executions, e => e.Position == 537);
        Assert.Equal(7L, a.Processor); Assert.Equal(8L, b.Processor);
        Assert.Equal(9L, a.Stream); Assert.Equal(9L, b.Stream);
        Assert.All(executions, e =>
        {
            Assert.False(e.HadTransactionAtStart);
            Assert.False(e.TransactionBoundToConnectionAtStart);
            Assert.Equal(0L, e.TransactionOrdinal);
        });
        Assert.False(a.Failed); Assert.True(b.Failed);
        Assert.All(executions, e => { Assert.NotNull(e.Elapsed); Assert.True(e.Elapsed.Value >= TimeSpan.Zero); });
        Assert.Equal(0, counter.PendingCount);
        Assert.Null(counter.ListenerError);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CapturesMissingAndMismatchedBindingWithoutInventingIdentity(bool hasTransaction, bool bound)
    {
        using var counter = new ContainmentHistoricalSqlCounter("Server=(local);Database=counter;Integrated Security=true");
        object? transaction = hasTransaction ? new object() : null;
        var connection = new object();
        var snapshot = counter.CaptureTransactionStart(transaction, connection, bound ? connection : null);
        Assert.Equal(hasTransaction, snapshot.HadTransaction);
        Assert.Equal(hasTransaction && bound, snapshot.BoundToConnection);
        if (hasTransaction) Assert.True(snapshot.Ordinal > 0);
        else Assert.Equal(0L, snapshot.Ordinal);
        // Later loss of the live binding does not mutate an already captured snapshot.
        var later = counter.CaptureTransactionStart(transaction, connection, null);
        Assert.Equal(snapshot.Ordinal, later.Ordinal);
        Assert.False(later.BoundToConnection);
        Assert.Equal(hasTransaction && bound, snapshot.BoundToConnection);
    }

    [Fact]
    public async Task ConcurrentSnapshotsKeepReferenceIdentityDistinctAndStable()
    {
        using var counter = new ContainmentHistoricalSqlCounter("Server=(local);Database=counter;Integrated Security=true");
        var connection = new object();
        var first = new object(); var second = new object();
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            (First: i % 2 == 0, Snapshot: counter.CaptureTransactionStart(i % 2 == 0 ? first : second, connection, connection)))));
        var firstIds = snapshots.Where(s => s.First).Select(s => s.Snapshot.Ordinal).Distinct().ToArray();
        var secondIds = snapshots.Where(s => !s.First).Select(s => s.Snapshot.Ordinal).Distinct().ToArray();
        Assert.Single(firstIds); Assert.Single(secondIds);
        Assert.NotEqual(firstIds[0], secondIds[0]);
        Assert.All(snapshots, s => { Assert.True(s.Snapshot.Ordinal > 0); Assert.True(s.Snapshot.HadTransaction); Assert.True(s.Snapshot.BoundToConnection); });
    }

    private static SqlCommand HistoricalCommand(SqlConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = HistoricalSql;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = 7L;
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = 9L;
        command.Parameters.Add("@ThroughPosition", SqlDbType.Decimal).Value = 534m;
        return command;
    }
}
