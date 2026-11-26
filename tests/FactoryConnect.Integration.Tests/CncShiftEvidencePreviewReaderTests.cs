using FactoryConnect.Persistence.SqlServer;
using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class CncShiftEvidencePreviewReaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(8);

    [Fact]
    public void AutomaticStreamFromDifferentMachineFailsBeforeSqlConnection()
    {
        var reader = new SqlServerCncShiftEvidencePreviewReader(
            "Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");
        var stream = new ObservationStreamId(new MachineId(Guid.NewGuid()), "counter-stream");
        Assert.Throws<ArgumentException>(() => reader.ReadForStreamAsync(Guid.NewGuid(),
            "assignment", "shift", Start, End, "complete-counter", stream));
    }

    [Fact]
    public async Task AutomaticStreamEncodingRespectsSqlIdentityBound()
    {
        var reader = new SqlServerCncShiftEvidencePreviewReader(
            "Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");
        var machine = Guid.NewGuid();
        var stream = new ObservationStreamId(new MachineId(machine), new string('x', 257));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadForStreamAsync(machine,
            "assignment", "shift", Start, End, "complete-counter", stream));
    }

    [Fact]
    public async Task MissingStreamIdentityFailsBeforeSqlConnection()
    {
        var reader = new SqlServerCncShiftEvidencePreviewReader(
            "Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadAsync(Guid.NewGuid(), "assignment", "shift", Start, End,
                "complete-counter", Array.Empty<byte>()));
    }

    [Fact]
    public async Task InvalidOccurrenceIntervalFailsBeforeSqlConnection()
    {
        var reader = new SqlServerCncShiftEvidencePreviewReader(
            "Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadAsync(Guid.NewGuid(), "assignment", "shift", End, Start,
                "complete-counter", new byte[] { 1 }));
    }

    [Fact]
    public async Task OversizedStreamIdentityFailsBeforeSqlConnection()
    {
        var reader = new SqlServerCncShiftEvidencePreviewReader(
            "Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadAsync(Guid.NewGuid(), "assignment", "shift", Start, End,
                "complete-counter", new byte[513]));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InvalidMachineOrNonUtcBoundaryFailsBeforeSqlConnection(int caseNumber)
    {
        var reader = new SqlServerCncShiftEvidencePreviewReader(
            "Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");
        var machine = caseNumber == 0 ? Guid.Empty : Guid.NewGuid();
        var starts = caseNumber == 1 ? Start.ToOffset(TimeSpan.FromHours(5.5)) : Start;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadAsync(machine, "assignment", "shift", starts, End,
                "complete-counter", new byte[] { 1 }));
    }

    [Fact]
    public async Task ShiftLongerThanOneDayFailsBeforeSqlConnection()
    {
        var reader = new SqlServerCncShiftEvidencePreviewReader(
            "Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadAsync(Guid.NewGuid(), "assignment", "shift", Start,
                Start.AddDays(2), "complete-counter", new byte[] { 1 }));
    }

}
