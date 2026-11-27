using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorCycleReviewStoreValidationTests
{
    [Fact]
    public async Task InvalidReviewsAreRejectedBeforeOpeningConnection()
    {
        var store = new SqlServerSupervisorCycleReviewStore("invalid");
        var valid = new SupervisorCycleReview(Guid.NewGuid(), Guid.NewGuid(),
            SupervisorCycleReviewDecision.Accepted, "reviewer", DateTimeOffset.UtcNow);
        foreach (var invalid in new[] {
            valid with { ReviewId = Guid.Empty },
            valid with { CycleRevisionId = Guid.Empty },
            valid with { ReviewedBy = " " },
            valid with { Decision = (SupervisorCycleReviewDecision)99 },
            valid with { ReviewedAtUtc = valid.ReviewedAtUtc.ToOffset(TimeSpan.FromHours(1)) } })
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(invalid));
    }
}
