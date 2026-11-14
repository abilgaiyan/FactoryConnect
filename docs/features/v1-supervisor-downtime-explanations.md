# SUP-4 — Supervisor downtime explanation vertical slice

Parent: #171. Baseline: main after squash merge #208 (`a2394170`).

## User workflow
A supervisor opens the machine activity view, selects an exact **durable** stopped activity period, chooses a configured reason, writes an explanation, and submits it. A different authorized reviewer approves or rejects the exact explanation. The screen displays Pending, Approved, or Rejected and the immutable decision history.

## Non-negotiable acceptance
- Resolve machine, state processor, durable observation position, and exact UTC interval against authoritative activity storage **before accepting** a write. Do not trust a client-supplied interval as proof.
- Require stopped activity (not running or unknown); reject missing, truncated, unstable, or ambiguous activity.
- Preserve machine observation, activity projection, and raw counters unchanged.
- Enforce local commissioning gates initially; production writes require the shared authenticated authorization foundation and separate approval.
- Require explicit reason code and explanation; validate reason against active reason master when available. Until then, no claim of production-ready reason qualification.
- Store immutable explanation and independent review; prevent self-review, stale decisions, and duplicate conflicting writes.
- No automatic downtime reclassification, availability recalculation, or OEE publication from unapproved explanations.
- Add one focused SQL/API integration path and a supervisor UI action; use existing CI.

## Increment currently staged
Migration 029 is an initial storage contract only. **It is not registered for execution, does not yet verify authoritative period identity, and is not a usable write workflow.** Next implement authoritative lookup and the minimal working end-to-end flow before merge. Do not deploy to Gajra.
