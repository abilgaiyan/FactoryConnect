# V1 Supervisor Production Entry — Local Commissioning

## Current status (after PR #193)

The original storage-foundation note predates the implemented workflow. The following components are now in `main`:

- Migration 024: `dbo.SupervisorProductionEntry` (append-oriented supervisor declarations).
- SQL reader and transactional writer, exact machine-shift roster verification, stable-entry-id idempotency.
- Local GET/POST API at `/api/supervisor/v1/machines/{machineId}/entries`.
- React Supervisor Desk real-data mode: select reporting source, business date, exact shift; enter production order, part number, operation and note; save and refresh declarations.
- The POST returns 201 for an insert, 200 for an identical retry and 409 for roster/identity conflicts.

## Scope and safety

**Local commissioning only. No production deployment or factory-network writes are authorized.**

The API has no authenticated supervisor session. The existing commissioning gate must allow the request (explicit commissioning enablement, loopback client, no forwarded headers), and writes have a second explicit opt-in. The operator identity is configured on the server, not supplied by the browser. Never expose this endpoint through a public reverse proxy or shared factory workstation. A loopback-only gate is not a substitute for authentication.

The SQL declaration does **not** publish production quantity, CNC evidence, Performance, Quality or OEE. Production database migration, machine activation and production use require separate authorization.

## Developer-local prerequisites

1. Use a **local development SQL database**, not the preserved incident or production database. Apply the repository's supported SQL migrations through **024** using the existing migration procedure.
2. Ensure the exact machine/shift roster occurrence exists in the development database; the writer rejects missing rosters.
3. Start the API and dashboard on the development machine, with the browser/API routing configured so `/api/supervisor/v1/...` reaches the API on loopback.
4. Configure the API locally (environment variables or developer-only settings, never commit credentials):

   | Setting | Purpose |
   | --- | --- |
   | `Commissioning:Enabled=true` | Existing local commissioning access gate |
   | `Commissioning:ReadOnlyConnectionString` | SQL connection for saved-entry GET |
   | `SupervisorLocalEntry:EnableWrites=true` | Explicit local write opt-in; default false |
   | `SupervisorLocalEntry:OperatorId` | Non-empty server-side local operator attribution |
   | `SupervisorLocalEntry:WriteConnectionString` | Development SQL connection with permission to insert supervisor entries |

5. In Supervisor Desk, choose **Real read-only** (the existing label refers to reporting inspection), load a configured source/date, select the exact occurrence and submit a declaration.
6. Confirm HTTP 201 on first save and that the saved-entry list shows the declaration. Refresh to confirm durability. Retry the *same entry ID and unchanged fields* to confirm HTTP 200 without a second row. Reusing an ID with different fields must return 409.

## Acceptance limitations

- These instructions do not assert that local factory data, migrations, browser/API proxying or end-to-end commissioning have already been verified.
- The local operator identity is **not authenticated user attribution**.
- Browser refresh does not persist an unsaved form draft.
- The saved-entry list is bounded to 100 entries per exact shift.
- A separate authenticated authorization model is needed before shared factory-floor supervisor entry.
