# FC-GAJRA-P0-20261105 — chronology containment

Draft. Restored-SQL acceptance and production validation remain pending.
Deployed baseline: 85f56f32880dd1b47be1a605c85da2a86d4fc304.
PR base: 088bf52492546a2a79029cf366a22f7a4d7e8d44.

## Exact boundary

GG-003, stream mtconnect:DEWAS-SR-GG-003, processor machine-state-activity:

- Authority position 513501, revision 4091; active state Unknown.
- Active start 2026-11-05T08:35:08.9193810Z; last consumed instance 1793867708.
- Execution position 513521, same instance, sequence 103, STOPPED/Good at 2026-11-05T08:26:00.4417150Z.
- Policy continuity/preserve 1.0. Earlier quality 1 is named Uncertain in code.

The actual normalizer maps STOPPED to false; the evaluator transitions Unknown to Stopped. The deployed calculator proposes a negative Unknown activity period. A frozen, test-only copy of that calculator provides the reproduction oracle. Synthetic execution is not restored-SQL evidence.

## Containment and checkpoint ownership

A state transition that would close an active period before its start returns a typed chronology hold. The complete calculation batch is rejected, including earlier tentative outputs. The processor does not call PublishAsync. Runtime/pipeline handle the outcome explicitly; no broad exception handler is introduced. Legacy void/result-less entry points throw on a hold so callers cannot mistake it for accepted processing.

The held activity cursor is MachineStateActivityAuthority.Position (513501 here). ProjectionRevision, evaluation authority, signals and histories stay unchanged. There is no separate activity checkpoint update in this runtime. Canonical mapping owns the separate ObservationProcessingCheckpoint entry canonical-mapping; that checkpoint can advance. Acquisition, quantity and metric processors receive no hold. This does not guarantee downstream metrics can advance without new activity input.

The pipeline continues to MOXA after the typed hold. Other exceptions retain their failure behavior. A hold does not count as progress for polling delay. Edge logs a new/changed hold and logs it again after process restart; identical polls do not flood logs.

Diagnostics retain mapped value/type/quality, position, instance, sequence, timestamp, source/address/signal, active start/state, proposed state, committed position/revision and policy. Original raw STOPPED remains in MachineObservation; its mapped value is false. No timestamp or raw value is rewritten.

## Restart reconstruction

The canonical sink is in memory while mapping progress is durable. Restart could otherwise omit the conflict after mapping advanced. Edge activity reads reconstruct mapped input from retained raw evidence after the activity cursor, bounded by the canonical-mapping checkpoint pinned for that read. The same configured normalizer and mapper run against a private in-memory coverage store. Reconstruction writes no SQL state or checkpoint.

Returned pages contain at most the configured batch size; unmapped pages are scanned until mapped input or the frontier. This adds raw reads/remapping and assumes the same commissioned configuration. Repeated sparse-suffix scans and latency require acceptance observation. This change does not establish physical source completeness or authorize configuration changes. No schema, SQL writer validation, isolation, break, OEE calculation or deployment changes are included.

## Verification commands

Keep Edge/acquisition stopped against the restored database. From the PR checkout:

```powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter "FullyQualifiedName~GajraChronologyHoldTests"

$env:FACTORYCONNECT_P0_CHRONOLOGY_CONNECTION = "Server=DESKTOP-1PJK2OB\FCINVENTORY;Database=FactoryConnect_P0_20261105;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"

dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter "FullyQualifiedName~GajraRestoredChronologyAcceptanceTests" --logger "console;verbosity=detailed"
```

The opt-in restored test permits only the exact server/database. It reads actual authority/raw observations, checks captured identity and timestamps, reconstructs mapped input, captures the deployed negative proposal and executes the corrected activity runtime twice with fresh reconstruction state. SQL operations are SELECT-only and an authority proxy rejects every writer call. Complete authority/signals/state-history/activity-history/processing-checkpoint captures must match before/after. No Edge host, migration, recovery, DML or checkpoint intervention runs.

Synthetic tests separately prove whole-batch rejection, equality timestamp behavior, distinct instance reset/preserve semantics, unchanged activity cursor, advancing mapping checkpoint, actual MOXA status normalization/independent advancement, restart reconstruction, notification behavior and propagation of unrelated failures. Live MOXA SQL advancement remains a separate gate; the read-only test does not claim it.

## Release gates

Focused and relevant existing regression tests must pass, followed by exact restored-SQL acceptance with console evidence. Review replay cost and deployment compatibility before a separately approved production activation. GG-003 evidence resolution/resumption remain unapproved. No skipping, timestamp clamping, reordering, history rewrite or checkpoint repair is supplied. OEE Help Page inclusion belongs to the later exact-commit release; this PR does not build that release or activate breaks.

## Workspace validation

- Focused and related integration regression selection: 35 passed; restored-SQL acceptance skipped because its opt-in connection is unavailable in this workspace.
- Edge test project: 242 passed. The Linux workspace needed its missing Asia/Calcutta timezone alias supplied outside the repository; no application timezone logic was changed.
- Core and affected projects compile with analyzers enabled. SQL acceptance on the developer restore remains pending.
