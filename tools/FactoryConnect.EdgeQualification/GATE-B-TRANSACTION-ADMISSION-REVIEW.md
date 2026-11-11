# PR #180 Gate B — transaction-bound admission review (2026-11-11)

**Disposition: implementation design only. No durable writes authorized.**

## Verified production ingestion path

Reviewed `SqlServerObservationIngestionStore.CommitAsync(ObservationIngestionBatch)` on this branch.

- The ingestion method begins a SQL transaction and reads the stream checkpoint using `UPDLOCK, HOLDLOCK`.
- It reads acquisition contact authority, checks evidence replay, and reads the raw accepted frontier.
- For a new publication, it requires `current == batch.ExpectedCheckpoint`; otherwise an evidence-bearing unknown publication is rejected.
- It validates the checkpoint transition, reconciles observations, assigns durable positions, persists checkpoint/observations/contact authority, appends acquisition evidence, then commits. Exceptions trigger rollback.
- The same-instance checkpoint rule rejects decreasing sequences; an instance change is not itself sufficient proof of authorized source-generation provenance.
- The current transaction does not accept the Gate B preflight's approved head identity or a generation admission token. A separate read-only preflight therefore cannot establish an atomic authorization boundary.

## Required gate before enabling RunCycleAsync with an accepting reporter

1. **Fixture-only authority:** require exact SQL server/database, integrated authentication, loopback endpoint, machine, stream, fixture instance and original checkpoint. Keep all production and preserved incident databases excluded.
2. **Atomic binding:** within the same transaction that writes the first new-generation evidence, verify the approved acquisition publication head identity and ordinal, accepted-through frontier, old instance/sequence and approved generation transition. The checkpoint lock must be held throughout. Do not treat a prior preflight result as an authorization token.
3. **Writer isolation:** enforce a fixture-scoped exclusive writer lease or lock that spans the acquisition and its SQL commit, with deterministic conflict rejection. Existing checkpoint CAS remains a second defense, not the sole source-ownership rule.
4. **Evidence contract:** persist and verify the actual `InstanceChanged` recovery evidence and exact first response sequence, rather than silently accepting a new instance ID.
5. **Negative acceptance:** stale publication head, stale checkpoint, wrong stream, wrong database, unexpected instance, duplicate writer and replay conflicts must all reject with no durable change.
6. **Positive acceptance:** only then enable a single bounded `RUNNING` cycle, verify the new publication and checkpoint, verify GG-003 hold, then test process restart, exact replay and second `IDLE` cycle.
7. **Operational boundary:** no general Edge hosted workers or factory endpoint may be started by the fixture harness before separate qualification.

## Existing evidence

The local preflight, recovery rehearsal, denied acquisition and positive read-only generation qualification passed. These establish useful **read-only** behavior only. They do not qualify SQL writes or host processing.

**Gate B OPEN; PR #180 DRAFT; production deployment HOLD.**
