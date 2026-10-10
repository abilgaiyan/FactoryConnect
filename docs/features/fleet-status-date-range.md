# Fleet status and date-range dashboard

Entry baseline: `9d35a9c923c0cab8d57978fb9429a891da21cdfd`.

The dashboard adds `/fleet` and `/reports/range` using existing APIs. No backend contracts, SQL, producers, evaluators, machine mapping or operational calculations change.

## Fleet

Configured reporting sources are grouped by machine GUID, case-insensitively. Each machine is queried once per fleet refresh. Different processor bindings do not add machines. Conflicting display name, site, line, group or display order are flagged and all configured values are visible.

Each machine occupies one bucket in each independent state, freshness, usability and request group. These groups overlap; their totals must not be added. Reported state is shown alongside coverage/usability, including stale and behind evidence. A failed or loading attempt has no current-attempt state evidence. Successful no-evidence is separate from request failure and Unknown. Idle is displayed only when returned by the authoritative API.

Refresh preserves the prior successful response as separately labeled retained evidence. Its `Read as of` timestamp is unchanged and is not an observation/contact timestamp. Failed attempts retain it without counting it as current evidence. Generations, abort signals and disposal prevent superseded responses from publishing. Fleet reads are individual authority reads, not a simultaneous fleet snapshot.

## Date range

The selected end date is inclusive. Checked date-only next-day conversion supplies exclusive API boundaries, including leap days. The existing paginated production-day loader is invoked for every selected source/day; all pages must succeed before any completed result is exposed. This deliberately favors existing validated traversal behavior over a new cursor mechanism. Large selections require more requests. Token cycles and traversal-limit exhaustion fail the selection.

Each configured machine/processor has a separately labeled series. Rows preserve site, business date, unpartitioned context, metric/definition identity and full aggregation revision. Results outside the requested identity fail explicitly. Identical duplicate results reconcile; differing content for one exact reporting identity fails the selection. Only successful complete traversal can produce `No reporting result`, including missing selected metrics. Request failure never becomes absence or zero.

Calculated results alone are plotted. Missing, insufficient and unavailable values create breaks. Genuine calculated zero remains zero. Decimal strings remain exact in tables and point details; finite numerical conversion supplies approximate graph coordinates. Unrepresentable/underflowing nonzero values are omitted from coordinates and retained in the table. API status, reason code and reason operand name remain visible. Range reads do not assert a shared aggregation revision across days or sources.

Ongoing day is `Undetermined`: this API surface has no sufficient versioned site-calendar provenance. Range ratios are withheld: numerator/denominator operand evidence is unavailable. Neither elapsed dates nor calculated reporting values establish coverage completeness.

## Conformance

Focused model and mounted-route fixtures cover duplicate machine sources, metadata conflicts, independent count reconciliation, successful no-evidence, retained timestamps after failure, superseded refresh responses, response identity mismatch, inclusive leap-day boundaries, complete pagination, later-page failure, separate processor series, conflicting exact reporting results, canceled range loading, exact decimal/unsigned revision display and graph gaps. Existing traversal tests cover cycles and page limits.

Remote workspace validation: TypeScript checks PASS; frontend production build PASS; complete frontend suite 369/369 PASS (18 new tests), zero skipped. Diff whitespace check PASS. .NET Release/Core/non-SQL integration and OpenAPI contract regeneration require local verification; no .NET SDK is available in the remote workspace. No SQL execution or factory operation is part of this change.

Run `tests/deployment/Invoke-FleetRangeDashboardConformance.ps1 -ExpectedSha <exact implementation SHA>` from a clean checkout with .NET 10 and the repository's supported Node version. Review the exact implementation diff against the entry baseline. Synthetic tests do not establish factory authority. R3243 remains Unproven.
