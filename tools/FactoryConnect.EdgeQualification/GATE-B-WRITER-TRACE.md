# PR #180-B — acquisition writer seam trace

Status: SOURCE-VERIFIED TRACE; fixture not implemented or executed.

## Verified production path

1. `src/FactoryConnect.Edge/MtConnectAcquisitionRuntime.cs`: `RunCycleCoreAsync` calls `MtConnectAcquisitionSession.PrepareNextAsync`, retaining `MtConnectSampleResult`, successful-contact timestamp, expected checkpoint and `AcquisitionResponseEvidence` in `PendingAcquisition`. The publication evidence uses a nonempty generated publication GUID and requested/first/last sequences. On durable-write failure the pending batch is retained for retry; on successful commit the session and local checkpoint advance.
2. `src/FactoryConnect.Edge/MtConnectDurableObservationSink.cs`: `WriteWithEvidenceAsync` maps result observations to `SequencedMachineObservation`, constructs `ObservationCheckpoint(stream, instanceId, nextSequence)` and `ObservationIngestionBatch(expected, checkpoint, observations, contactTime, evidence)`.
3. `src/FactoryConnect.Abstractions/IObservationIngestionStore.cs`: `ReadCheckpointAsync`, `ReadAcquisitionContactAuthorityAsync`, `CommitAsync(ObservationIngestionBatch)` are the durable ingestion interface. `ObservationCheckpoint.NextSequence` is a source cursor, **not** the SQL raw `Position`.
4. `src/FactoryConnect.Persistence.SqlServer/SqlServerObservationIngestionStore.cs`: `CommitAsync(ObservationIngestionBatch)` reconciles the expected checkpoint under a SQL transaction, validates transition and replay, assigns raw positions from the accepted frontier, persists checkpoint, observations, contact authority and evidence atomically. Do not assign SQL positions in a fixture.
5. `src/FactoryConnect.Persistence.SqlServer/SqlServerAcquisitionEvidence.cs`: immutable acquisition publication history is checked against persisted observation source identities and prefix continuity; conflicting publication evidence is rejected. A fixture must respect existing publication head and expected checkpoint.

## Safety implication

A synthetic call directly to `IObservationIngestionStore.CommitAsync` exercises durable ingestion, but bypasses the MTConnect parser, session, recovery policy and acquisition runtime. For a claim about the *normal acquisition path*, use a controlled local MTConnect Agent fixture with the actual runtime and `MtConnectDurableObservationSink`, or explicitly label a store-only test as narrower. Never manufacture a publication ID or claim an arbitrary sequence/instance is physically continuous without a controlled source and its provenance.

The existing qualification host deliberately removes `FactoryConnectWorker`; it cannot itself demonstrate live acquisition. A future isolated fixture needs a separate controlled-source producer phase, then a processing-host phase, and restart durability/post-restart liveness as distinct assertions.

## Unresolved before fixture writes

- Trace `MtConnectAcquisitionSession` parsing and continuity/recovery for the exact restored MOXA checkpoint.
- Trace the commissioned `moxa001_status` mapping and choose an *actual valid transition* from the committed activity state; do not guess numeric values.
- Verify current MOXA acquisition checkpoint (InstanceId, NextSequence), acquisition contact authority, publication head, and the existing source-generation identity; retained last row instance/sequence alone is insufficient.
- Decide whether a new controlled Agent generation can be admitted without an explicit recovery boundary; reject if it cannot.
- Use a new isolated disposable SQL clone, never the preserved incident database or the existing qualification baseline. Enforce hard-coded target, source/stream guards and fail-closed write-set checks.
- Verify all source and activity invariants before running a fixture. No direct edits to `MachineObservation`, `ObservationStreamCheckpoint`, `ObservationProcessingCheckpoint` or `MachineStateActivityAuthority`.

## Disposition

Writer seam: traced. Fixture eligibility and status normalization: NOT VERIFIED. No executable SQL writes authorized. GG chronology correction unchanged; PR #180 Draft; production HOLD.
