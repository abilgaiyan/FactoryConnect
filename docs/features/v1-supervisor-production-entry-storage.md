# V1 Supervisor Production Entry — Storage Foundation

This branch adds Migration 024 only. It is a repository development slice, not an enabled production-entry workflow.

## Business purpose

A supervisor can eventually declare the machine, exact shift occurrence, production order, part number, operation and note. The database retains the original declaration with a unique entry ID, entered-by attribution and recorded UTC timestamp. These are supervisor declarations, **not verified machine observations** or approved production quantities.

## Current implementation

- New additive `dbo.SupervisorProductionEntry` table with append-oriented identity and exact shift bounds.
- Required fields, UTC bounds, ordered shift window and machine/shift retrieval index.
- Embedded migration resource and migration-count test expectations updated.

## Not implemented yet

- API create/list endpoints and SQL repository, including idempotent retries and transaction semantics.
- Authentication/actor attribution, machine/shift authorization and roster verification.
- Dashboard form wired to the real API.
- SQL integration acceptance, deployment qualification and factory commissioning.

Do not expose a write endpoint until the actor identity, machine eligibility and shift occurrence are verified server-side. Do not use client-provided identity as authenticated actor. Never alter CNC raw evidence or OEE from these entries.

**Production migration/deployment is not authorized by this PR.**
