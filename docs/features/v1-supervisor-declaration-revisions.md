# SUP-3B — Supervisor declaration revision and audit contract

Status: implementation workstream, local commissioning only. Builds on merged PR #207.

## Product behavior

A supervisor can correct an existing declaration by submitting a **new immutable revision**. The original entry and its review decision remain intact. Corrections are not UPDATEs to `SupervisorProductionEntry` and do not silently replace accepted production facts.

- Scope revisions to the same exact machine, entry, shift schedule assignment, shift and UTC shift boundaries.
- Require a nonempty correction reason and a stable revision operation ID for retry/idempotency.
- Record author, UTC time, prior revision identity, changed production context and quantities.
- Keep a complete ordered audit trail; distinguish the original declaration from subsequent revisions.
- A correction to an approved or rejected declaration must be **pending review** as a new revision. Previous review decisions remain attached to their exact historical revision.
- Only one current revision head per declaration; concurrent edits with a stale expected revision must conflict, never silently overwrite.
- Preserve the existing local commissioning gate and default-disabled write configuration. No new production write route without shared identity and permission enforcement.
- Neither an approved original declaration nor an approved revision automatically qualifies CNC counts, publishes accepted production quantity, or makes OEE numeric.

## Vertical implementation order

1. Append-only SQL revision table and exact revision-head concurrency contract; runtime schema descriptor and migration history updated in the same change.
2. Local machine-bound API: list revision history and submit correction with expected head and idempotency ID.
3. Supervisor UI: correction form, required reason, revision timeline and pending review visibility.
4. Focused SQL/API/UI acceptance for wrong-machine correction, stale head, retry, original immutability, review reset and history.

## Boundary

Issue #171 SUP-3; PR #207 remains the baseline for review of original declarations. Production rollout, production SQL and machine activation are excluded.
