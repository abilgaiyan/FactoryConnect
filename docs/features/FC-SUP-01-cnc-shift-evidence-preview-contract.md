# FC-SUP-01 — CNC shift evidence preview contract (draft)

## Purpose
Show observed CNC COMPLETE-counter changes beside commissioned supervisor shift occurrences, without publishing production quantity or OEE inputs. The GG-003 local restore is the diagnostic sample, not an application-wide machine mapping.

## Read path
- Reuse the existing machine/shift occurrence roster for exact effective-dated UTC intervals; do not hard-code Gajra shift hours in production code.
- Read immutable raw observations only through a bounded SQL query with machine, address, UTC range, deterministic acquisition order, and pagination or row limit.
- Load one preceding numeric reading when available to classify the first transition. Include observation quality and instance continuity.
- Attribute unit increments provisionally to observation time, never silently to physical completion time.
- Mark a +1 increment boundary-ambiguous if the preceding numeric reading is outside the target shift occurrence, including across missing shift rows.
- Report long observation gaps separately; the diagnostic 30-minute threshold is not a qualification criterion. Flags may overlap.
- Return explicit no-evidence/unavailable status for roster occurrences with no usable readings. Do not infer zero.
- Reject negative, non-unit, or reset transitions as unqualified; never silently normalize.
- Preserve timestamp offsets and the exact occurrence identifiers. Do not use current shift schedule to reinterpret historical shifts.

## Response semantics (proposed)
Per machine/roster occurrence: machineId, assignmentId, shiftId, startsAtUtc, endsAtUtc, evidenceStatus, observedUnitIncrements, boundaryAmbiguousIncrements, gapFlaggedIncrements, nonUnitTransitions, firstObservedAtUtc, lastObservedAtUtc. Counts are provisional and must not be labelled actual/accepted production.

## Authorization and safety
Use existing supervisor read permissions and site/machine scoping. Do not expose an unrestricted machine-observation search. Do not write to SupervisorAcceptedShiftProduction, quantity metric inputs, projections, or shift targets. No migration, checkpoint mutation, adapter change, or live factory operation.

## Acceptance tests
1. Two adjacent shift occurrences with one transition straddling the boundary: exactly one observed +1, boundary-ambiguous.
2. Shift with no numeric evidence: no-evidence status, not zero.
3. Quality!=0, null and malformed values: excluded from numeric increments and reflected as incomplete evidence.
4. Instance change and long gap: independent, potentially overlapping flags.
5. Counter reset and jump >1: not accepted as unit production.
6. Overnight occurrence uses actual UTC roster boundaries; historical schedule revision is respected.
7. Unauthorized site/machine request is denied.
8. Bounded SQL reader and API cancellation/timeout behavior.
9. No writes to accepted-production or OEE tables.

## Local diagnostic baseline
GG-003, 28–31 October 2026: eight returned shift rows; 657 observed +1 increments; eight boundary-ambiguous; 19 gap-flagged; zero non-unit transitions. These are user-run restored-database observations, not physical output verification.

## Status
SQL diagnostic validated by user. API, UI and automated tests not yet implemented. Keep PR draft until all required checks pass.
