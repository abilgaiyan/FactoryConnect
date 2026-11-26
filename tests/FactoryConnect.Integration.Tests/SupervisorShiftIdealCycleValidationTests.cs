using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorShiftIdealCycleValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
    private static SupervisorShiftIdealCycleRevision Revision(decimal seconds) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "assignment", "shift", Start, Start.AddHours(8),
            1, seconds, null, null, null, null, "supervisor", Start);

    private static SqlServerSupervisorShiftIdealCycleStore Store() =>
        new("Server=127.0.0.1,1;Database=not-used;Connect Timeout=1;TrustServerCertificate=True");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    public async Task InvalidSecondsRejectedBeforeSql(int seconds)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().AppendAsync(Revision(seconds), null));
    }

    [Fact]
    public async Task ExcessPrecisionRejectedBeforeSql()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().AppendAsync(Revision(0.0001m), null));
    }

    [Fact]
    public async Task CorrectionRequiresReasonBeforeSql()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().AppendAsync(
            Revision(600m) with { RevisionNumber = 2 }, Guid.NewGuid()));
    }
}
