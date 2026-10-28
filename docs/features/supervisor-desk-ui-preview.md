# Supervisor Desk UI first implementation slice

Initial screen implementation authorized by Ajay on 29 October 2026, based on main
8061eab01f2c6d03b1b4b491fc69456b2a74683d. This PR implements a development-only screen
review. It does not approve the still-pending authentication/session authority,
canonical request encodings, rejection classification or transaction-aware resolver
SQL. No backend mutations, migration, production enablement or deployment is included.

## Review the screens

Use the repository-pinned Node version. From src/FactoryConnect.Dashboard/ClientApp:

```sh
npm ci
npm run build
npm run typecheck
npm test
npm run dev -- --host 127.0.0.1
```

Open http://127.0.0.1:5173/supervisor-preview (use the actual Vite port if occupied).
Direct development navigation mounts the sample desk without loading dashboard
configuration or contacting a .NET API. Navigation from the development dashboard
also exposes the preview. Production builds omit the sample component/data and
preview navigation; the route displays a non-enabled notice instead of an editor.
No environment/configuration change on Gajra is part of this review.

## Included behavior

- Shift start, Event explanations, Shift summary and a separate Reviewer queue.
- Two illustrative machine types with visibly distinct sources and sample identities.
- Three explicit sample shift occurrences including an overnight occurrence. These
  are not commissioned factory calendars or proof of production-day semantics.
- Original closed event identity and source bounds remain read-only. Overlaps are
  labelled and do not split or replace the source event. Open activity is read-only.
- Reference publication revision zero and a value beyond JavaScript's safe integer
  range remain strings; annotation revision is a separate value.
- Independent threads, separate event/thread counts, creator-only simulated edits,
  new local draft revisions, simulated submission and independent acceptance.
- Existing note field carries action taken; no structured-field amendment. Team
  involved/follow-up team permits Not established; cause need not be asserted.
- Retained sample evidence is labelled when simulated reads are unavailable or
  conflicting; creation/submission/acceptance then remain blocked.
- Stale-revision simulation preserves unsaved input. Unsaved input prevents internal
  event/shift/persona/view changes until saved locally or discarded.
- Historical local revisions remain visible; a successor draft inherits no approval.

All personas, permissions, timestamps and transitions are illustrative. The local
state model is not an authentication/authorization service, resolver, durable audit
store, operation ledger or production protocol implementation. No network mutation,
browser storage or server persistence is used. Leaving/reloading resets the entire
preview; no user should rely on it to record factory work. Permission revocation,
atomic retries, exact resolution and lost acknowledgements require the separately
reviewed backend, not this UI reducer.

Changes-requested interaction is shown as pending rather than inventing the unresolved
self-review permission rule. The model's status vocabulary includes Changes requested,
but this slice does not simulate that transition. Targets/achievement, quantity and
quality declarations, context assignment, general handover notes, reason-master
maintenance, machine control and OEE changes are excluded. No 100% default is invented.

## Validation

Build and typecheck precede targeted and full client tests. New tests cover independent
threads, original/overlap identity, overnight scope, exact revision strings, creator-only
editing, stale revisions, self-approval, unavailable evidence, no inherited approval,
unchanged sample evidence, no network/storage and unsaved-input handling. These are UI
tests; they do not constitute backend security, SQL or factory acceptance.

Perform Chromium review at 1440px and 390px for all four views, event editing, conflict
and evidence-unavailable behavior. Verify contained table scrolling and no page overflow.
Check the production bundle excludes sample fixture/editor strings. Remote CI remains
the authority for the complete current-main .NET/client/SQL validation.

Next approval gate: review the screens and finish the pending backend technical contract
before authorizing persistence, authenticated writes or production configuration changes.
