# RR03.1 — Assumed break duration: integration contract and expected results

Status: **PROPOSED / REVIEW REQUIRED**. This contract does **not** authorize reporting integration, release, or migration. PR #107 remains draft.

## 1. Effective-dated policy provenance

A result MUST identify a policy using `PolicyId`, `PolicyVersion`, `EffectiveFrom`, optional `EffectiveTo`, `SiteId` and optional `ProductionLineId`. Select the single applicable policy by factory production date, with line-specific precedence over site-wide and no ambiguous competing effective policies. A request without a matching approved/effective policy returns **Unavailable(PolicyNotEstablished)**; no implicit 30-minute production fallback.

The customer-relayed demo instruction is 30 minutes per scheduled shift. Its written approval reference and effective date remain unverified. Until an approved effective-dated authority is recorded, policy source must remain `CustomerRelayedGajraDemoDefault` and the result is `PolicyAssumed`; do not label it as a formally approved policy. Whether such a provisional policy is admitted into demo-only reporting must be an explicit, versioned configuration decision, never inferred from the presence of a constant.

Use `PolicyAssumed` as provenance, **not** as a substitute for policy identity or approval. Identical replay returns the same policy binding; a changed version triggers an explicitly versioned re-evaluation and never silently changes historical assessments.

## 2. Exclusions and conservation

`otherExcludedDuration` is not an untrusted aggregate from independent sources. It MUST be formed from a validated union of half-open, timestamped intervals within the exact scheduled shift. Exclusions must have source identity and classification. Duplicate or overlapping intervals are unioned once. An exclusion representing an actual break already deducted by this allowance is rejected or marked unavailable rather than charged again.

The existing duration-only calculator assumes **the caller already supplied** that validated duration. Integration MUST add a typed validation boundary and never pass a raw sum of intervals. Ambiguous classification or overlapping break authority returns **Unavailable(ExclusionAuthorityAmbiguous)**. No exclusion is inferred from CNC Stopped/Idle machine activity.

Example expected results (completed scheduled shift):
- 480 scheduled, zero other exclusions, 30 assumed break => **450 planned minutes**.
- 480 scheduled, two overlapping shutdown intervals 10:00–10:20 and 10:10–10:30 (union 30), 30 break => **420 planned minutes**.
- 60 scheduled, 45 other excluded, 15 assumed break => **0 planned minutes**.
- 20 scheduled, no exclusions, capped 20 assumed break => **0 planned minutes** (Availability unavailable when denominator is zero).
- 480 scheduled, an interval explicitly classified as actual break => **unavailable pending precedence**, not an additional 30-minute deduction.
- An unscheduled period => **zero default deduction**.

## 3. Forecast consumption safety

A result with `IsForecast=true` is display-only and MUST NOT enter elapsed Availability evaluation, published numeric planned-time facts or authoritative completed-shift metrics. The consuming API must require a validated **CompletedShiftPlannedTime** value (or equivalent discriminated completed/forecast result), not merely inspect a caller-optional flag.

Current-shift forecast may be shown distinctly, but numeric *elapsed* Availability is **Unavailable(BreakTimingNotEstablished)** until the shift closes or approved actual-break evidence supports an exact interval calculation. The shift-start and mid-shift cases cannot inherit a future 30-minute deduction as already elapsed.

Expected results:
- Shift start, eight-hour schedule: forecast **450**, elapsed Availability **unavailable**.
- Mid-shift, eight-hour schedule: forecast **450**, elapsed Availability **unavailable**.
- Completed eight-hour shift: planned **450**, calculation eligible only with coherent activity/evidence and policy binding.
- Missing CNC evidence after shift end: Availability **unavailable**, never zero.
- Completed 20-minute shift: planned **0**, Availability **unavailable (ZeroDenominator)** rather than an unsupported percentage.
- Replaying a completed shift under the same policy and exclusion evidence reproduces identical output.

## 4. Acceptance gates

Before wiring into SQL reporting or Dashboard:
1. Add policy selection/validation and provenance to the actual authoritative consumer contract.
2. Validate non-overlapping *union* of classified exclusions and break-duplication rejection at that consumer.
3. Make forecast-to-elapsed-Availability admission impossible by type or checked invariant.
4. Add executable expected-result tests for all cases listed here and full policy/evidence lineage.
5. Confirm CI and restored-Gajra-data behavior. No simulated timestamps or manufactured CNC evidence.

This document specifies integration behavior; it is not evidence that the existing PR #107 helper enforces these guarantees.
