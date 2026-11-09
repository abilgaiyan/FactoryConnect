# PR #180-B — controlled-source continuity decision

Status: **BLOCKED for evidence writes**; source trace reviewed 2026-11-10. This is an engineering gate, not a Gate B pass.

## Verified durable boundary

- MOXA machine `bfa0dc97-9a68-45ed-b189-1dacb7d863f0`, stream `mtconnect:DEWAS-SR-MOXA-001`.
- Durable acquisition checkpoint: instance `1793867780`, next sequence `27`.
- Last retained row: position `14328`, instance `1793867780`, sequence `21`; missing retained 22–26 does not establish a gap in MTConnect source sequencing.
- Publication head ordinal `104153`, accepted before/through `14328/14328`, version 1. Tail links are structurally visible; cryptographic chain verification has not been performed.
- MOXA activity authority: position `14324`, revision `4703`, state 0, active start `2026-11-05T08:36:20.736297Z`.
- Last status at position 14324 is uncertain/null. The commissioned mapping binds `moxa001_status` to canonical `state.operational`.

## Code-verified acquisition behavior

`src/FactoryConnect.Protocols.MTConnect/MtConnectAcquisitionSession.cs` requests the current `NextSequence` via `MtConnectSampleClient.AcquireAsync`, checks the response instance, and only advances after a successful acquisition. A changed instance throws `MtConnectInstanceChangedException`.

`src/FactoryConnect.Edge/MtConnectAcquisitionRuntime.cs` invokes the continuity recovery policy for an instance change or out-of-range response, records recovery evidence, and only advances the session/checkpoint after `MtConnectDurableObservationSink.WriteWithEvidenceAsync` succeeds. The durable sink submits `ObservationIngestionBatch` through `IObservationIngestionStore.CommitAsync`.

**Therefore:** a local Agent inventing the *existing* instance `1793867780` and sequence `27` would impersonate the original source and is not an admissible qualification fixture. A local Agent with a **new** instance may be used only if the actual instance-change recovery policy, commissioned source binding, publication ancestry and SQL admission contract permit it. Do not assume that a new instance is automatically safe.

## Required implementation gates

1. Source-review the concrete `MtConnectContinuityRecoveryPolicy.RecoverInstanceChangeAsync` implementation and `MtConnectSampleClient.AcquireAsync` behavior for the response's first/next sequences. Establish a source-owned new instance, not an invented continuation of production.
2. Verify immutable publication head with `IAcquisitionEvidenceReader` / production codec, not merely SQL row linkage. Establish whether the restored prefix can be extended by a controlled source.
3. Create a **new** disposable database restored from the approved backup; never write `FactoryConnect_P0_20261105`, `FactoryConnect_P0_Qualification_20261106`, or `FactoryConnect_P0_GateB_20261109`. Capture baseline hashes and machine/checkpoint snapshots.
4. Configure a loopback-only Agent with fixture-owned device identity and documented provenance. Verify commissioned mapping/stream eligibility before allowing any acquisition. Never point a fixture at a factory endpoint.
5. Fail closed unless SQL target, source URL, machine/stream, config hash, checkpoint, publication head, exclusive writer and source generation all match approved fixture values.
6. Execute the **real acquisition runtime**, parser, durable sink and processing workers. No direct SQL inserts/updates to observations, checkpoints, evidence or activity authority.
7. Test first eligible status transition, GG hold, exact replay idempotence, conflicting replay rejection, full process restart, second eligible post-restart transition and both machine authorities. Preserve complete before/after report and nonzero exit on any unexpected write.

## Disposition

Existing structural evidence supports proceeding with **implementation of the isolated source and verification harness**, but not admitting any new evidence yet. Gate B OPEN. PR #180 draft. Production Edge stopped; deployment HOLD.
