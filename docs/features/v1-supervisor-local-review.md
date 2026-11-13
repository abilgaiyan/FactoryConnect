# V1 local supervisor declaration review (SUP-3A)

This is a **local commissioning prototype**, not production supervisor approval. It remains behind the existing `CommissioningAccess.Allows` loopback commissioning gate. Do not enable on the Gajra production host or expose through a factory-network reverse proxy.

## Operator workflow

Open the local supervisor workspace for an exact machine/shift. The declaration review panel shows Pending, Approved and Rejected entries, reasons, reviewer identity and decision timestamp. Select an undecided entry, enter a reason, and choose **Approve locally** or **Reject locally**. Decisions are final for this slice. Use Refresh reviews to reconcile uncertain network outcomes. A retry of the same decision reuses the decision ID.

## Configuration

The review GET endpoint uses `Commissioning:ReadOnlyConnectionString`. POST remains disabled unless all of the following are configured explicitly:

- `SupervisorLocalReview:EnableWrites=true`
- `SupervisorLocalReview:ReviewerId` — server-configured local reviewer identity, distinct from the declaration author
- `SupervisorLocalReview:WriteConnectionString` — database writer connection

The schema requires migration `026_SupervisorProductionEntryReview.sql`. Review decisions are tied to an existing declaration, are immutable, and have a globally unique decision ID. Duplicate requests with identical decision payloads return an idempotent result; competing decisions are rejected.

## Not implemented / not authorized

There is no authenticated factory reviewer session or role grant in this slice; PR #172 is a separate draft. No review revision/supersession, cross-shift review queue, production approval, evidence reconciliation, accepted quantity, CNC processing, or OEE publication is performed. Existing declaration totals remain **unqualified sums**. Follow-on work must integrate a production-ready identity/permission system before promotion beyond local commissioning.

No production deployment, migration, machine activation or recovery is authorized by this PR.
