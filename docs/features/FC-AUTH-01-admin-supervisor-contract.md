# FC-AUTH-01 — Simple Admin / Supervisor / Manager authentication (implementation contract)

Status: proposed next vertical slice after FC-SUP CNC preview PR #333. No production deployment authorization.

## Goal
One FactoryConnect build runs with installation-specific SQL and service configuration. Human users sign in with durable accounts; requests derive their identity and role from the authenticated session rather than startup operator/reviewer IDs.

## Roles (first release)
- Admin: manage users, roles, sites, machines, shift/break/stop-reason masters, CNC mappings, and shift targets; may perform supervisor workflows.
- Supervisor: view commissioned machines and shifts, enter production, review entries submitted by others, inspect provisional CNC evidence, view reporting.
- Manager: read-only access to management OEE dashboards, production/downtime reports, shift targets, machine and shift context, and provisional CNC evidence; may export reports but cannot create, edit, or approve production or master records.
- All three roles: read-only dashboards and reports.
- Neither role may review its own submission, forge an actor ID, bypass evidence qualification, or promote provisional CNC counts to accepted production automatically.

## Minimal end-to-end acceptance
1. One-time initial Admin setup; durable user records and salted password hashes using an established password hasher; never store plaintext or seeded shared credentials.
2. Sign-in and sign-out with a server-side session using Secure/HttpOnly/SameSite cookies, antiforgery for state-changing browser requests, session expiry and account disablement.
3. One Admin creates Supervisor and Manager accounts. Supervisor signs in and submits an entry; actor identity comes from the session.
4. A different authorized account reviews the entry. Self-review is denied on the server.
5. Supervisor and Manager cannot manage user or machine masters; Manager cannot submit or review production; unauthenticated requests cannot access protected routes. API and dashboard enforce the same policy.
6. Local and LAN access work through the configured deployment entry point; no loopback-only bypass or forwarded-header trust shortcut.
7. Existing supervisor data, historical shifts and reporting remain intact. No migration or live production action without explicit approval.

## Implementation sequence
- Inspect existing identity, session, endpoint, dashboard proxy, and SQL user storage patterns before selecting libraries and migration.
- Add durable identity storage and a bootstrap ceremony with a one-time admin credential supplied at installation.
- Add authenticated session endpoint and a minimal sign-in UI.
- Protect one complete Supervisor entry/review flow with role checks and server-derived actor identity; add negative-path tests.
- Extend authorization to master administration and CNC preview; replace development switches only after the protected paths work.
- Validate against a restored development database; keep PR #333 separate until its existing acceptance is complete.

## Configuration boundary
Installation settings contain SQL connectivity, service URLs and secret material, not fixed human identities. SQL master data contains users, role assignments, machines, shifts, breaks, stop reasons and CNC mapping. Never rely on UI-only role checks.
