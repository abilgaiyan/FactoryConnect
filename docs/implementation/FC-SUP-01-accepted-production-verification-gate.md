# FC-SUP-01 — Accepted-production verification gate

Status: **development-only; verification issuance and production publication are not authorized**.

## Existing evidence boundaries

- Migration 024 stores supervisor production entries; migration 025 adds optional good/rejected quantities.
- Migration 026 stores local commissioning review decisions, explicitly **not authenticated factory approval**.
- Migration 027 stores append-only supervisor revisions.
- Migration 034 stores qualification receipts. A receipt's `Qualified` decision alone does not establish complete coverage or accepted production.
- Migrations 033/035 store accepted shift production and explicit receipt ancestry.
- Migration 036 inspects conflicts but does not qualify evidence or publish.
- Migration 037 guards source linkage.
- PR #303 migration 038 introduces a verification record and a serializable transactional writer. It **does not issue verification records or expose publication to the API**.

## Required end-to-end issuance slice (not implemented in PR #303)

1. Authenticate a factory reviewer through the existing production identity/permission boundary; do not reuse local commissioning `Approved` as authority.
2. Bind the review to the exact immutable source evidence and its current revision, machine, shift occurrence, operation, quantity unit, and production population. Reject superseded or rejected evidence.
3. Independently establish CNC counter continuity or an explicitly approved supervisor-only quantity basis. Verify complete shift coverage and disjointness/reconciliation against every competing source for that population. Unknown coverage or overlap must remain insufficient.
4. Verify quantity decomposition and applicable quality/reference-time evidence separately. A verified produced count alone must **not** turn Performance, Quality, or OEE numeric.
5. Persist an auditable, immutable verification with reviewer identity, source revision, coverage evidence, reconciliation decision and effective authority. Bind verification issuance and publication to a single authorized service boundary; ordinary callers must not insert verification rows or execute the writer directly.
6. Revalidate the verification and publication conflicts under locks in one transaction; preserve receipt and population uniqueness and exact ancestry. Define revocation/correction behavior before enabling reporting.
7. Feed accepted quantities into the existing reporting authority only after qualification, with explicit missing-evidence states and regression tests for replay, concurrency, stale revisions, revocation, overlapping populations and partial coverage.

## PR #303 merge and rollout boundaries

PR #303's SQL writer and tests are a **non-exposed foundation**, not a usable production publishing workflow. Its test fixture directly seeds synthetic verification records solely to exercise SQL behavior. A green test run does not establish independent verification authority. Do not grant application roles direct INSERT on `SupervisorAcceptedProductionVerification` or EXECUTE on `PublishSupervisorAcceptedProduction`; do not deploy to the Gajra production database without explicit approval.

Next functional implementation: trace existing production authorization and source-evidence contracts, then implement the smallest authenticated verification issuance path with real source binding and fail-closed coverage checks. Avoid substituting an arbitrary `AuthorityReference` string for authenticated authorization.
