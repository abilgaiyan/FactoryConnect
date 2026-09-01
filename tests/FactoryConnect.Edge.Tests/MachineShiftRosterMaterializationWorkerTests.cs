using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MachineShiftRosterMaterializationWorkerTests
{
    [Fact]
    public async Task StartupMaterializesExactConfiguredMachineAndProductionDayHorizon()
    {
        var fixture = CreateFixture(new InMemoryMachineShiftOccurrenceRosterStore());
        var request = new MachineShiftRosterMaterializationRequest(
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3));
        var worker = new MachineShiftRosterMaterializationWorker(
            fixture.Runtimes,
            request);

        await worker.StartAsync(CancellationToken.None);

        foreach (var scope in fixture.Runtimes.Scopes)
        {
            Assert.NotNull(await fixture.Store.ReadAsync(
                scope.MachineId,
                new ProductionDayId(scope.SiteId, new DateOnly(2026, 9, 1)),
                CancellationToken.None));
            Assert.NotNull(await fixture.Store.ReadAsync(
                scope.MachineId,
                new ProductionDayId(scope.SiteId, new DateOnly(2026, 9, 2)),
                CancellationToken.None));
            Assert.Null(await fixture.Store.ReadAsync(
                scope.MachineId,
                new ProductionDayId(scope.SiteId, new DateOnly(2026, 9, 3)),
                CancellationToken.None));
        }
    }

    [Fact]
    public async Task StartupFailureEscapesInsteadOfSilentlyAcceptingIncompleteCoverage()
    {
        var store = new FailingRosterStore(failOnCommit: 2);
        var fixture = CreateFixture(store);
        var worker = new MachineShiftRosterMaterializationWorker(
            fixture.Runtimes,
            new MachineShiftRosterMaterializationRequest(
                new DateOnly(2026, 9, 1),
                new DateOnly(2026, 9, 2)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.StartAsync(CancellationToken.None));

        Assert.Equal(2, store.CommitAttempts);
    }

    private static Fixture CreateFixture(IMachineShiftOccurrenceRosterStore store)
    {
        var siteId = new SiteId("SITE-A");
        var lineId = new ProductionLineId("LINE-1");
        var reader = new InMemoryShiftScheduleReader([
            new ShiftScheduleAssignment
            {
                Id = new ShiftScheduleAssignmentId("SHIFT-SCHEDULE-A"),
                CompanyId = new CompanyId("COMPANY-A"),
                SiteId = siteId,
                ProductionLineId = lineId,
                TimeZoneId = new FactoryTimeZoneId("UTC"),
                ShiftId = new ShiftId("SHIFT-A"),
                Name = "Shift A",
                StartsAtLocal = new TimeOnly(6, 0),
                EndsAtLocal = new TimeOnly(14, 0),
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
        ]);
        var materializer = new MachineShiftOccurrenceRosterMaterializer(
            new ShiftOccurrenceResolver(reader),
            store);
        var scopes = new[]
        {
            new MachineShiftScheduleScope(
                new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                siteId,
                lineId),
            new MachineShiftScheduleScope(
                new MachineId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                siteId,
                lineId),
        };
        return new Fixture(
            store,
            new MachineShiftOccurrenceRosterMaterializationRuntimeSet(
                scopes,
                materializer));
    }

    private sealed record Fixture(
        IMachineShiftOccurrenceRosterStore Store,
        MachineShiftOccurrenceRosterMaterializationRuntimeSet Runtimes);

    private sealed class FailingRosterStore(int failOnCommit) :
        IMachineShiftOccurrenceRosterStore
    {
        private readonly InMemoryMachineShiftOccurrenceRosterStore _inner = new();

        public int CommitAttempts { get; private set; }

        public ValueTask<MachineShiftOccurrenceRoster?> ReadAsync(
            MachineId machineId,
            ProductionDayId productionDayId,
            CancellationToken cancellationToken) =>
            _inner.ReadAsync(machineId, productionDayId, cancellationToken);

        public ValueTask CommitAsync(
            MachineShiftOccurrenceRosterCommit commit,
            CancellationToken cancellationToken)
        {
            CommitAttempts++;
            if (CommitAttempts == failOnCommit)
            {
                throw new InvalidOperationException("Injected roster materialization failure.");
            }

            return _inner.CommitAsync(commit, cancellationToken);
        }
    }
}
