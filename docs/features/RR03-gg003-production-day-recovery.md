# RR03 — GG-003 Production Day Recovery

Status: **Implementation authorized; contract baseline proposed for review.** This document does not assert that implementation or factory acceptance has passed.

## Customer-visible acceptance

Open FactoryConnect → select GG-003 → select production date → view a correct, current Production Day report sourced from durable real FANUC observations. Unsupported metrics remain unavailable with explicit reasons. No synthetic OEE.

## RR03.1 — Gajra break policy

- Three scheduled shifts per production day. Exact clock boundaries, overnight production-day attribution, effective-from date, and timezone MUST be read from approved Gajra configuration; do not assume example shift times.
- Maximum permitted break allowance: **30 minutes per scheduled shift**, flexible timing, no fixed break windows.
- **Until manual entry is available, deduct 30 minutes per eligible scheduled shift** from planned time as an explicit **Gajra-approved policy assumption**. Do not require or fabricate break timestamps.
- Record policy identity/version, effective dates, source `GajraApprovedDefault`, and assumption status `PolicyAssumed` with calculated results.
- The default is a planned-time deduction, **not** evidence of a machine stop, actual operator break, or measured downtime.
- For a fully scheduled 480-minute shift with no other exclusions: planned production time = 480 − 30 = 450 minutes. Three such shifts: 1,350 minutes. These are illustrative until actual Gajra shift boundaries and calendar are verified.
- Calendar overrides, partial shifts, holidays, overlapping exclusions, and future manually approved break entries require explicit precedence and duration-conservation tests. Never double-deduct.
- A future manual entry screen must not silently rewrite historical assessments; re-evaluation must be versioned and auditable.

## RR03.2 — Availability failure containment

Reproduce reported operational-metric revision **534** using a restored Gajra database before fixing. Capture exception and exact machine/processor/stream/revision identities. Define typed numeric vs unavailable outcomes, including reason, evidence reference and revision. Prove processing proceeds to 535+ without classifying failure as zero Availability.

## RR03.3 — Projection version/recovery

Preserve raw observations, projection lineage, failure classification, source revision, policy/rule version, recovery predecessor, and calculation timestamp. No in-place rewriting of failed history, arbitrary latest-wins selection, or stale fallback represented as current.

## RR03.4 — Reporting provenance and freshness

Expose and distinguish source observation time, processed-through time, report calculation time, processing lag, selected projection version/revision, evidence status, and unavailable reasons. Current report generation time does not imply current CNC evidence.

## RR03.5 — API/Dashboard parity

API and Dashboard must select the same authoritative production-day result and shift composition. The Dashboard presents rather than recalculates planned time, Availability, Performance, Quality or OEE. Numeric zero must remain distinguishable from unavailable.

## RR03.6 — Mandatory acceptance tests

1. Three shifts and overnight production-day attribution under approved schedule.
2. Default break deduction once per eligible scheduled shift, with `PolicyAssumed` provenance.
3. No fabricated break intervals or machine downtime; actual activity durations remain independent.
4. Effective-dated policy and calendar selection, overlapping exclusions, partial/unscheduled shifts.
5. Shift/day planned-time and activity duration conservation.
6. Revision 534 reproduction before correction, typed failure after correction, 535+ continuation.
7. Missing evidence unavailable; true numeric zero remains zero; no unsupported OEE.
8. Non-destructive projection recovery and exact-version selection/replay.
9. API/Dashboard parity on exact revision and evidence state.
10. Three independent timestamps and delayed-processing freshness regression.
11. Restored Gajra database GG-003 historical-date browser proof.
12. Current-date GG-003 browser proof while acquisition continues.
13. Build/test/release SHA and restored database identity included in acceptance evidence.

## Delivery slices

RR03.1 schedule/break authority → RR03.2 Availability failure containment → RR03.3 versioned recovery → RR03.4 freshness/API → RR03.5 Dashboard → RR03.6 restored-DB factory release candidate.

## Out of scope

Manual operator/supervisor break-entry UI, scrap/reject entry, forcing Performance/Quality/OEE numeric, monthly reports, multi-machine rollout.

## Open factory-specific inputs

- Exact Gajra shift boundaries and production-day attribution.
- Schedule timezone, calendar exceptions, effective-from date.
- Restored Gajra DB identity and exact revision-534 failure trace.

These must be resolved by evidence before production-day acceptance; they are not permission to invent defaults.
