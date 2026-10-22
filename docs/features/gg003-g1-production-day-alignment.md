# GG-003 G1 development change

Baseline: `4ab392752380f4fe206b5fcf736a0eec8a9b965f`.

## Configuration authority

The default Edge configuration carries operational defaults only. Machine acquisition
examples and production assignments reside in `appsettings.Demo.json`, selected by
`DOTNET_ENVIRONMENT=Demo`. Production must supply acquisition inventory, contexts,
planned production, shared shifts and their timezone explicitly. The production
template is a commissioning skeleton; it cannot start without completed assignments.
Deployment and rehearsal scripts already providing complete assignments remain valid.

## Business dates and physical intervals

Shift assignments and planned windows accept `StartDayOffset` 0 or 1 (default 0).
The offset changes local timestamp construction; effectiveness, active weekdays,
overrides, `FactoryDate`, production-day identities and roster ownership use the
original business date. `IsOvernight` retains its existing meaning.

Edge supports either legacy scalar planned times or `PlannedWindows[]`, and rejects
mixed representations. Break windows support the same offset. An omitted break
offset is inferred only when exactly one placement (0 or 1) belongs to one ordinary
planned window. Explicit 0 is distinct from omission for break placement. Overlapping
breaks and ambiguous ownership fail. Replacement overrides inherit resolved ordinary
breaks, ignore nonintersecting breaks and clip intersecting breaks once; a break
intersecting multiple replacement windows fails.

Resolvers inspect two neighboring business dates on each side and reject physical
collisions before returning only requested business dates. Planned collisions are
checked before break subtraction. Shift collection validation includes neighboring
effective assignments even when their business-date effective ranges do not overlap.
The upstream UTC date padding remains a bounded assumption for the Gajra schedule;
this change does not claim universal timezone support for arbitrary offset schedules.

## Review and validation

New tests cover offset bounds; three-shift gross/net duration; midnight and 07:30
adjacency; final effective business date; offset-1 overnight collisions at transitions;
replacement break clipping/exclusion; legacy inference; ambiguous/overlapping breaks;
Edge array binding; effective Production/Demo host configuration; and actual activity
and quantity publication through the roster ownership guard for a historical date.
Existing regression and SQL integration suites remain required gates.

Local validation: `git diff --check` passes. No .NET SDK is installed in the authoring
workspace, so compilation and all test execution are **pending CI**, not reported as
passing. CI completion is managed by the user; do not merge based on this document.

Suggested focused commands:

```powershell
dotnet test tests/FactoryConnect.Core.Tests --filter FullyQualifiedName~ProductionDayOffsetTests
dotnet test tests/FactoryConnect.Edge.Tests --filter "FullyQualifiedName~EdgeConfigurationContractTests|FullyQualifiedName~ProductionConfigurationCompositionTests"
dotnet test FactoryConnect.sln
```

Merge, fresh V2 database creation and production deployment are held. GG-003
acquisition and existing SQL remain unchanged. Confirm the proposed 07:30 business-day
convention with Gajra before cutover.
