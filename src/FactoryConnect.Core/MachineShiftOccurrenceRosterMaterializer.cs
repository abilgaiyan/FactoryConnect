using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class MachineShiftOccurrenceRosterMaterializer
{
    private readonly ShiftOccurrenceResolver _resolver;
    private readonly IMachineShiftOccurrenceRosterStore _store;

    public MachineShiftOccurrenceRosterMaterializer(
        ShiftOccurrenceResolver resolver,
        IMachineShiftOccurrenceRosterStore store)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(store);
        _resolver = resolver;
        _store = store;
    }

    public async ValueTask<MachineShiftOccurrenceRoster> MaterializeAsync(
        MachineShiftScheduleScope scope,
        ProductionDayId productionDayId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(productionDayId);

        if (scope.SiteId != productionDayId.SiteId)
        {
            throw new ArgumentException(
                "Machine scheduling scope and production day must belong to the same site.",
                nameof(productionDayId));
        }

        if (productionDayId.BusinessDate == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(productionDayId),
                "The maximum production day cannot form an exclusive resolution boundary.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var occurrences = await _resolver.ResolveAsync(
            scope.SiteId,
            scope.ProductionLineId,
            productionDayId.BusinessDate,
            productionDayId.BusinessDate.AddDays(1),
            cancellationToken).ConfigureAwait(false);
        ValidateResolvedOccurrences(scope, productionDayId, occurrences);

        var ownership = occurrences
            .Select(occurrence => new MachineShiftOccurrenceOwnership(
                scope.MachineId,
                scope.ProductionLineId,
                new ShiftOccurrenceId(
                    occurrence.SiteId,
                    occurrence.SourceAssignmentId,
                    occurrence.ShiftId,
                    occurrence.StartsAtUtc,
                    occurrence.EndsAtUtc),
                productionDayId))
            .ToArray();
        var current = await _store.ReadAsync(
            scope.MachineId,
            productionDayId,
            cancellationToken).ConfigureAwait(false);

        if (current is not null && current.ProductionLineId != scope.ProductionLineId)
        {
            throw new InvalidDataException(
                "Existing roster coverage belongs to a different machine scheduling line.");
        }

        if (current is not null && IsEquivalent(current, scope, ownership))
        {
            return current;
        }

        if (current?.Revision.Value == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Machine-shift occurrence roster revision cannot advance beyond its maximum value.");
        }

        var revision = new MachineShiftOccurrenceRosterRevision(
            current is null ? 1 : current.Revision.Value + 1);
        var proposed = new MachineShiftOccurrenceRoster(
            scope.MachineId,
            scope.ProductionLineId,
            productionDayId,
            revision,
            ownership);
        await _store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(current?.Revision, proposed),
            cancellationToken).ConfigureAwait(false);
        return proposed;
    }

    private static void ValidateResolvedOccurrences(
        MachineShiftScheduleScope scope,
        ProductionDayId productionDayId,
        IReadOnlyList<ShiftOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);

        if (occurrences.Any(static occurrence => occurrence is null))
        {
            throw new InvalidDataException(
                "Shift resolution returned a null occurrence.");
        }

        if (occurrences.Any(occurrence =>
                occurrence.SiteId != scope.SiteId ||
                occurrence.FactoryDate != productionDayId.BusinessDate ||
                (occurrence.ProductionLineId is not null &&
                    occurrence.ProductionLineId != scope.ProductionLineId)))
        {
            throw new InvalidDataException(
                "Shift resolution returned an occurrence outside the requested machine scheduling scope or production day.");
        }
    }

    private static bool IsEquivalent(
        MachineShiftOccurrenceRoster current,
        MachineShiftScheduleScope scope,
        MachineShiftOccurrenceOwnership[] ownership)
    {
        if (current.ProductionLineId != scope.ProductionLineId ||
            current.Occurrences.Count != ownership.Length)
        {
            return false;
        }

        var proposed = new MachineShiftOccurrenceRoster(
            scope.MachineId,
            scope.ProductionLineId,
            current.ProductionDayId,
            current.Revision,
            ownership);
        return current.Occurrences.SequenceEqual(proposed.Occurrences);
    }
}

public sealed class MachineShiftOccurrenceRosterMaterializationRuntimeSet
{
    private readonly ReadOnlyCollection<MachineShiftScheduleScope> _scopes;
    private readonly MachineShiftOccurrenceRosterMaterializer _materializer;

    public MachineShiftOccurrenceRosterMaterializationRuntimeSet(
        IEnumerable<MachineShiftScheduleScope> scopes,
        MachineShiftOccurrenceRosterMaterializer materializer)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(materializer);

        var snapshot = scopes.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one machine scheduling scope is required.",
                nameof(scopes));
        }

        if (snapshot.Any(static scope => scope is null))
        {
            throw new ArgumentException(
                "Machine scheduling scopes cannot contain null values.",
                nameof(scopes));
        }

        if (snapshot.GroupBy(static scope => scope.MachineId).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Machine scheduling scopes must contain unique machine identities.",
                nameof(scopes));
        }

        _scopes = Array.AsReadOnly(snapshot);
        _materializer = materializer;
    }

    public IReadOnlyList<MachineShiftScheduleScope> Scopes => _scopes;

    public ValueTask<MachineShiftOccurrenceRoster> MaterializeAsync(
        MachineId machineId,
        ProductionDayId productionDayId,
        CancellationToken cancellationToken)
    {
        var scope = _scopes.SingleOrDefault(candidate => candidate.MachineId == machineId);
        if (scope is null)
        {
            throw new ArgumentException(
                "No authoritative scheduling scope is configured for the requested machine.",
                nameof(machineId));
        }

        return _materializer.MaterializeAsync(scope, productionDayId, cancellationToken);
    }

    public async Task<IReadOnlyList<MachineShiftOccurrenceRoster>> MaterializeAsync(
        MachineShiftRosterMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<MachineShiftOccurrenceRoster> materialized = [];
        foreach (var scope in _scopes)
        {
            for (var businessDate = request.FromProductionDayInclusive;
                 businessDate < request.ToProductionDayExclusive;
                 businessDate = businessDate.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                materialized.Add(await _materializer.MaterializeAsync(
                    scope,
                    new ProductionDayId(scope.SiteId, businessDate),
                    cancellationToken).ConfigureAwait(false));
            }
        }

        return new ReadOnlyCollection<MachineShiftOccurrenceRoster>(materialized);
    }
}
