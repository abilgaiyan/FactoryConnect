using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class CncShiftEvidencePreviewReaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(8);

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
}
