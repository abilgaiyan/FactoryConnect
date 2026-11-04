using System.Diagnostics;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class HistoricalLoadDiagnosticsTests
{
    [Fact]
    public void SuccessfulLoadSeparatesReturnedAndAddedRowsAndEmitsAfterCleanupOnce()
    {
        var records = new List<string>();
        var load = Create(records.Add);
        load.EndRead(load.BeginRead(), true);
        load.EndMaterialization(load.BeginMaterialization());
        load.RowAdded();
        load.EndRead(load.BeginRead(), false);
        load.Cleanup(Stopwatch.GetTimestamp(), null, default);
        Assert.Empty(records);
        load.Dispose(); load.Dispose();
        var record = Assert.Single(records);
        Assert.Contains("ReadCalls=2;ReadCompleted=2;ReadInterrupted=0;RowsReturned=1;RowsAdded=1;MaterializationAttempts=1", record, StringComparison.Ordinal);
        Assert.Contains("EnumerationCompleted=True", record, StringComparison.Ordinal);
        Assert.Contains("CleanupOutcome=Completed", record, StringComparison.Ordinal);
        Assert.Contains("AggregationProcessor=aggregation;Checkpoint=6050;Outcome=Completed", record, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledReadRetainsInterruptedCallTimeAndOriginalException()
    {
        var records = new List<string>();
        var expected = new OperationCanceledException("authoritative");
        var actual = Assert.Throws<OperationCanceledException>((Action)(() =>
        {
            using var load = Create(records.Add);
            try
            {
                _ = load.BeginRead();
                try { throw expected; }
                finally { load.EndRead(Stopwatch.GetTimestamp() - Stopwatch.Frequency, null); }
            }
            catch (Exception error) { load.Fail(error, default); throw; }
            finally { load.Cleanup(Stopwatch.GetTimestamp(), null, default); }
        }));
        Assert.Same(expected, actual);
        var record = Assert.Single(records);
        Assert.Contains("Outcome=Cancelled", record, StringComparison.Ordinal);
        Assert.Contains("ReadCalls=1;ReadCompleted=0;ReadInterrupted=1;RowsReturned=0;RowsAdded=0", record, StringComparison.Ordinal);
        Assert.True(Number(record, "ReadElapsedMs") >= 1000);
        Assert.True(Number(record, "ReadMaxMs") >= 1000);
    }

    [Fact]
    public void FailedMaterializationRetainsTimeAndReturnedRowWithoutClaimingAddedInput()
    {
        var records = new List<string>();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var expected = new ContainmentHistoricalIntegrityException("authoritative");
        var actual = Assert.Throws<ContainmentHistoricalIntegrityException>((Action)(() =>
        {
            using var load = Create(records.Add);
            try
            {
                load.EndRead(load.BeginRead(), true);
                _ = load.BeginMaterialization();
                try { throw expected; }
                finally { load.EndMaterialization(Stopwatch.GetTimestamp() - Stopwatch.Frequency); }
            }
            catch (Exception error) { load.Fail(error, cancellation.Token); throw; }
        }));
        Assert.Same(expected, actual);
        var record = Assert.Single(records);
        Assert.Contains("Outcome=Failed", record, StringComparison.Ordinal);
        Assert.Contains("RowsReturned=1;RowsAdded=0;MaterializationAttempts=1", record, StringComparison.Ordinal);
        Assert.True(Number(record, "MaterializeValidateAddElapsedMs") >= 1000);
        Assert.True(Number(record, "MaterializeValidateAddMaxMs") >= 1000);
    }

    [Fact]
    public void CleanupFailureAndBothSinkFailuresDoNotReplaceAuthoritativeException()
    {
        var expected = new InvalidOperationException("authoritative");
        var fallbackCalls = 0;
        var actual = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var load = new HistoricalLoadDiagnostics(_ => throw new IOException("sink"),
                _ => { fallbackCalls++; throw new IOException("fallback"); }, "trace", "attempt", "aggregation", "6050");
            try { throw expected; }
            catch (Exception error) { load.Fail(error, default); throw; }
            finally { load.Cleanup(Stopwatch.GetTimestamp(), expected, default); }
        }));
        Assert.Same(expected, actual);
        Assert.Equal(1, fallbackCalls);
        var records = new List<string>();
        using (var load = Create(records.Add)) load.Cleanup(Stopwatch.GetTimestamp(), expected, default);
        Assert.Contains("CleanupOutcome=Failed", Assert.Single(records), StringComparison.Ordinal);
    }

    [Fact]
    public void SessionSummarySurvivesDetailExhaustionAndDisabledSessionAllocatesNoLoad()
    {
        var cut = new MetricAggregationCheckpoint(new MetricAggregationProcessorId("aggregation"),
            new MetricInputStreamId(new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f")), "metric-inputs"), new MetricInputPosition(6050));
        Assert.Null(ReportingDiagnosticSession.BeginHistoricalLoad(cut));
        var loads = new List<string>();
        using (var session = new ReportingDiagnosticSession(_ => { }, _ => { }, historicalLoadSink: loads.Add))
        {
            for (var i = 0; i < ReportingDiagnosticSession.SharedLimit + 1; i++) session.Admit(null);
            using var load = ReportingDiagnosticSession.BeginHistoricalLoad(cut);
            Assert.NotNull(load);
            load.EndRead(load.BeginRead(), false);
        }
        Assert.Contains("Checkpoint=6050", Assert.Single(loads), StringComparison.Ordinal);
        Assert.Null(ReportingDiagnosticSession.BeginHistoricalLoad(cut));
    }

    [Fact]
    public void SlowReadSamplesAreCappedButMaximumAndInterruptedOrdinalRemainVisible()
    {
        var records = new List<string>();
        using (var load = Create(records.Add))
        {
            for (var i = 0; i < HistoricalLoadDiagnostics.SlowReadSampleLimit + 2; i++)
            {
                _ = load.BeginRead();
                load.EndRead(Stopwatch.GetTimestamp() - Stopwatch.Frequency, true);
            }
            _ = load.BeginRead();
            load.EndRead(Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency, null);
        }
        var record = Assert.Single(records);
        Assert.Contains("ReadMaxOrdinal=7;", record, StringComparison.Ordinal);
        Assert.Contains("ReadMaxOutcome=Interrupted;", record, StringComparison.Ordinal);
        Assert.Contains("SlowReadCount=7;", record, StringComparison.Ordinal);
        Assert.Contains("SqlSessionId=Unavailable;", record, StringComparison.Ordinal);
        var sample = record.Split(';').Single(field => field.StartsWith("SlowReadSamples=", StringComparison.Ordinal));
        Assert.Equal(HistoricalLoadDiagnostics.SlowReadSampleLimit, sample.Split('|').Length);
        var fields = sample["SlowReadSamples=".Length..].Split('|')[0].Split(',');
        Assert.Equal("1", fields[0]);
        var start = DateTimeOffset.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.True(end >= start);
    }

    [Fact]
    public void NoReadsHaveNoInventedOrdinalOrTimestamp()
    {
        var records = new List<string>();
        using (var load = Create(records.Add)) { }
        var record = Assert.Single(records);
        Assert.Contains("ReadMaxOrdinal=0;ReadMaxStartUtc=Unavailable;ReadMaxEndUtc=Unavailable", record, StringComparison.Ordinal);
        Assert.Contains("SlowReadCount=0;", record, StringComparison.Ordinal);
    }

    private static HistoricalLoadDiagnostics Create(Action<string> sink) =>
        new(sink, _ => { }, "trace", "attempt", "aggregation", "6050");
    private static double Number(string record, string key) => double.Parse(
        record.Split(';').Single(field => field.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..],
        System.Globalization.CultureInfo.InvariantCulture);
}
