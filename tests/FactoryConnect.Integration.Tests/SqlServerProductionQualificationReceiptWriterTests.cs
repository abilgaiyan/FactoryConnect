using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerProductionQualificationReceiptWriterTests
{
    private static readonly Guid Machine = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Evidence = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Receipt = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Starts = new(2026, 11, 17, 7, 30, 0, TimeSpan.Zero);

    private static ProductionQualificationReceiptRead Valid() => new(
        Receipt, Evidence, "SupervisorRevision", "population-1", Machine,
        "assignment-1", "shift-1", Starts, Starts.AddHours(8),
        null, null, null, "pieces", 100, 95, 5,
        "Qualified", "review-1", Starts.AddHours(9));

    [Fact]
    public void ConstructorRequiresExplicitVerifier()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SqlServerProductionQualificationReceiptWriter(
                "Server=localhost;Database=Unused;Integrated Security=true;", null!));
    }

    [Theory]
    [InlineData("CncCounter", "Unknown")]
    [InlineData("Unknown", "Qualified")]
    public async Task InvalidKindOrDecisionFailsBeforeVerification(string kind, string decision)
    {
        var verifier = new CountingVerifier();
        var writer = CreateWriter(verifier);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.AppendAsync(Valid() with { EvidenceKind = kind, Decision = decision }));
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task InconsistentQuantityFailsBeforeVerification()
    {
        var verifier = new CountingVerifier();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateWriter(verifier).AppendAsync(Valid() with { Rejected = 4 }));
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task NonUtcShiftFailsBeforeVerification()
    {
        var verifier = new CountingVerifier();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateWriter(verifier).AppendAsync(Valid() with
            {
                StartsAtUtc = Starts.ToOffset(TimeSpan.FromHours(5.5))
            }));
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task DeniedVerificationFailsBeforeDatabaseConnection()
    {
        var verifier = new CountingVerifier();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateWriter(verifier).AppendAsync(Valid()));
        Assert.Equal(1, verifier.Calls);
    }

    private static SqlServerProductionQualificationReceiptWriter CreateWriter(
        IProductionQualificationEvidenceVerifier verifier) =>
        new("Server=localhost;Database=Unused;Integrated Security=true;", verifier);

    private sealed class CountingVerifier : IProductionQualificationEvidenceVerifier
    {
        public int Calls { get; private set; }
        public Task<bool> VerifyAsync(ProductionQualificationReceiptRead receipt,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(false);
        }
    }
}
