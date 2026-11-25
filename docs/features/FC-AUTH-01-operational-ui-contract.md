# Operational workspace simplification

Related: FC-AUTH-01 (#335), CNC preview (#333).

## Acceptance

- Display readable site and machine names; filter machines by selected site. Use searchable selectors for large master lists.
- Select production date and commissioned shift occurrence; display site-local times and planned breaks. Preserve exact UTC occurrence identity in requests.
- Use existing master-backed part, operation and stop-reason selectors where available; implement missing master capabilities in focused slices. Filter dependent options and clear stale selections.
- Resolve counter address and stream identity server-side from the selected machine's configured evidence profile. Missing or ambiguous configuration fails closed with readable guidance. Do not require supervisor hex or GUID input.
- Derive operator/reviewer identity from authenticated session; preserve self-review restrictions.
- Keep production entry, downtime, review, targets and OEE accessible with consistent layout, loading and error states. Hide technical provenance behind expandable details, not by deleting evidence.
- Distinguish provisional CNC increments, accepted production, missing operands and valid zero. Never publish diagnostic counts into OEE automatically.
- Add focused UI/API regression tests for selection, missing configuration, role enforcement and exact shift identity.

Deliver after local-user foundation in small reviewable PRs. No production deployment authorized.
