# PR #180-B — isolated MOXA progress fixture contract

Status: DESIGN / TRACE REQUIRED. No fixture implementation or SQL mutation authorized by this document.

## Evidence baseline
- GG-003 activity authority: position 513501, projection revision 4091. Exact chronology hold on retained observation 513521.
- MOXA-001 activity authority: position 14324, projection revision 4703.
- MOXA-001 retained frontier and canonical-mapping checkpoint: position 14328.
- MOXA positions 14325–14328 are NULL-valued non-status addresses, not new `moxa001_status` transitions.
- Two independent qualification processes started and shut down normally, with GG held. Both returned exit 3 (no MOXA authority advancement). This is inconclusive for independent-machine liveness.
- The existing qualification harness targets only `DESKTOP-1PJK2OB\FCINVENTORY` / `FactoryConnect_P0_Qualification_20261106`; it removes the MTConnect acquisition hosted service.

## Before implementing a writer
1. Identify the actual production acquisition-to-durable-observation writer entry point, its transaction/checkpoint boundary, deduplication key, stream encoding, sequence/instance continuity and source admission rules. Link source paths and tests.
2. Identify the actual `moxa001_status` normalization semantics and a value that produces a genuine state change from the committed MOXA state. Do not infer values from address names.
3. Determine whether a fresh synthetic acquisition generation is permitted under the existing continuity contract. Never forge a position, sequence, checkpoint or processing authority.
4. Define a new disposable fixture database and independent source-data provenance. Never mutate the preserved incident restore or the existing qualification baseline.
5. Ensure all fixture writes pass through the same durable observation admission contract; direct INSERT/UPDATE of MachineObservation, checkpoints or activity authority is prohibited.
6. Add fail-closed guards for SQL server/database, source machine and stream, commissioned config hash, source baseline and single-writer exclusion. Require a new output report path.
7. Capture complete before/after authority and checkpoint snapshots for both machines, with exact source evidence identity and transaction outcome.

## Acceptance scenarios
A. Existing GG chronology hold persists at 513521; GG committed position/revision remain 513501/4091 throughout.
B. A single valid MOXA status transition is durably admitted and its activity position **and** revision advance during a composed processing-host run.
C. Replayed identical evidence is deduplicated; conflicting same-identity evidence is rejected; no authority or checkpoint drift occurs.
D. A separate fresh process reads the committed MOXA state with no duplicate activity output or regression (restart durability).
E. A second independently eligible MOXA status transition after restart demonstrates new progress (post-restart liveness).
F. Unexpected writes, worker failure, source discontinuity, incorrect target or missing evidence result in a nonzero failure and explicit report.

## Claims not established
- A fixture does not prove real MTConnect acquisition, factory connectivity, production deployment packaging or rollback.
- Startup alone does not prove independent-machine liveness.
- A canonical-mapping checkpoint at the raw frontier does not imply an activity-state transition.
- Exit 3 remains inconclusive, not a Gate B pass.

## Current disposition
Chronology containment and independent-process restart containment: demonstrated.
MOXA progress, fixture implementation, packaged deployment and rollback: OPEN.
PR #180: Draft. Production: HOLD.
